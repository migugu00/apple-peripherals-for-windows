using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using MagicTrackpad.Interop;

namespace MagicTrackpad.Hid;

public sealed record BatteryStatus(int? Percent, bool? Charging, string Source, DateTimeOffset UpdatedAt);

public static partial class DeviceBattery
{
    private const int HidpStatusSuccess = 0x00110000;
    private const byte BluetoothBatteryReport = 0x90;
    private const byte LegacyTrackpadBatteryReport = 0x47;
    private static readonly object CacheLock = new();

    public static BatteryStatus Unknown(string source = "No battery data") =>
        new(null, null, source, DateTimeOffset.Now);

    public static IReadOnlyDictionary<string, BatteryStatus> QueryApplePeripheralBatteries(IEnumerable<HidDeviceInfo> devices)
    {
        var appleDevices = devices
            .Where(device => device.IsAppleMagicTrackpad || device.IsAppleKeyboard)
            .GroupBy(device => DeviceActions.PhysicalKey(device.Name))
            .Select(group => group.First())
            .ToList();

        IReadOnlyList<PnpBatteryRecord>? pnpRecords = null;
        var cache = ReadCache();
        var result = new Dictionary<string, BatteryStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in appleDevices)
        {
            var key = DeviceActions.PhysicalKey(device.Name);
            var status = QueryHidBattery(device);
            if (status != null)
            {
                WriteCacheRecord(key, device, status);
                result[key] = status;
                continue;
            }

            if (cache.TryGetValue(key, out var cached) && DateTimeOffset.Now - cached.UpdatedAt < TimeSpan.FromDays(7))
            {
                result[key] = cached with { Source = "Last device battery report" };
                continue;
            }

            pnpRecords ??= QueryWindowsPnpBatteryRecords();
            status = MatchWindowsPnpBattery(device, pnpRecords);
            result[key] = status ?? Unknown("Windows did not report battery");
        }

        return result;
    }

    public static void TryCacheReport(HidDeviceInfo device, byte[] report)
    {
        if (!device.IsAppleMagicTrackpad && !device.IsAppleKeyboard)
        {
            return;
        }

        if (!TryParseBatteryReport(report, out var percent, out var charging))
        {
            return;
        }

        var status = new BatteryStatus(percent, charging, "Device battery report", DateTimeOffset.Now);
        WriteCacheRecord(DeviceActions.PhysicalKey(device.Name), device, status);
    }

    internal static bool TryParseBatteryReport(ReadOnlySpan<byte> report, out int percent, out bool? charging)
    {
        percent = 0;
        charging = null;
        if (report.Length < 2)
        {
            return false;
        }

        if (report[0] == BluetoothBatteryReport)
        {
            if (report.Length >= 3 && report[2] <= 100)
            {
                percent = report[2];
                charging = (report[1] & 0x02) != 0;
                return true;
            }

            if (report[1] <= 100)
            {
                percent = report[1];
                return true;
            }
        }

        if (report[0] == LegacyTrackpadBatteryReport && report[1] <= 100)
        {
            percent = report[1];
            return true;
        }

        return false;
    }

    private static BatteryStatus? QueryHidBattery(HidDeviceInfo device)
    {
        if (!CanQueryHidBattery(device))
        {
            return null;
        }

        var accessAttempts = new[] { NativeMethods.GenericRead | NativeMethods.GenericWrite, NativeMethods.GenericRead, 0u };
        foreach (var access in accessAttempts)
        {
            var handle = NativeMethods.CreateFile(
                device.Name,
                access,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                NativeMethods.FileAttributeNormal,
                IntPtr.Zero);

            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                continue;
            }

            try
            {
                var lengths = ReportLengths(handle);
                foreach (var reportId in BatteryReportIds(device))
                {
                    var input = QueryReport(handle, reportId, lengths.Input, NativeMethods.HidD_GetInputReport);
                    if (input != null)
                    {
                        return input;
                    }

                    var feature = QueryReport(handle, reportId, lengths.Feature, NativeMethods.HidD_GetFeature);
                    if (feature != null)
                    {
                        return feature;
                    }
                }
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        return null;
    }

    private static BatteryStatus? QueryReport(IntPtr handle, byte reportId, int preferredLength, Func<IntPtr, byte[], uint, bool> getter)
    {
        foreach (var length in ReportLengthsToTry(preferredLength))
        {
            var report = new byte[length];
            report[0] = reportId;
            if (!getter(handle, report, (uint)report.Length))
            {
                continue;
            }

            if (TryParseBatteryReport(report, out var percent, out var charging))
            {
                return new BatteryStatus(percent, charging, "Device HID battery report", DateTimeOffset.Now);
            }
        }

        return null;
    }

    private static IEnumerable<int> ReportLengthsToTry(int preferredLength)
    {
        if (preferredLength >= 3)
        {
            yield return preferredLength;
        }

        yield return 64;
        yield return 16;
        yield return 8;
        yield return 3;
    }

    private static (int Input, int Feature) ReportLengths(IntPtr handle)
    {
        if (!NativeMethods.HidD_GetPreparsedData(handle, out var preparsedData))
        {
            return (64, 64);
        }

        try
        {
            return NativeMethods.HidP_GetCaps(preparsedData, out var caps) == HidpStatusSuccess
                ? (Math.Max(3, (int)caps.InputReportByteLength), Math.Max(3, (int)caps.FeatureReportByteLength))
                : (64, 64);
        }
        finally
        {
            NativeMethods.HidD_FreePreparsedData(preparsedData);
        }
    }

    private static bool CanQueryHidBattery(HidDeviceInfo device) =>
        device.IsAppleMagicTrackpad || device.IsAppleKeyboard;

    private static IEnumerable<byte> BatteryReportIds(HidDeviceInfo device)
    {
        yield return BluetoothBatteryReport;
        if (device.ProductId == DeviceCatalog.MagicTrackpad)
        {
            yield return LegacyTrackpadBatteryReport;
        }
    }

    private static BatteryStatus? MatchWindowsPnpBattery(HidDeviceInfo device, IReadOnlyList<PnpBatteryRecord> records)
    {
        var record = records.FirstOrDefault(item =>
            item.BatteryLevel is >= 0 and <= 100 &&
            PnpRecordMatches(device, item));

        return record?.BatteryLevel is int level
            ? new BatteryStatus(level, null, "Windows battery property", DateTimeOffset.Now)
            : null;
    }

    private static bool PnpRecordMatches(HidDeviceInfo device, PnpBatteryRecord record)
    {
        if (device.ProductId != null && record.ProductId == device.ProductId)
        {
            return true;
        }

        var text = $"{record.InstanceId} {record.FriendlyName} {record.Name}".ToLowerInvariant();
        if (device.IsAppleMagicTrackpad)
        {
            return text.Contains("trackpad", StringComparison.OrdinalIgnoreCase);
        }

        return device.IsAppleKeyboard && text.Contains("keyboard", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<PnpBatteryRecord> QueryWindowsPnpBatteryRecords()
    {
        try
        {
            var script = """
$ErrorActionPreference = 'SilentlyContinue'
$devices = Get-PnpDevice -PresentOnly | Where-Object {
    $_.InstanceId -match 'VID_05AC|VID&0001004C|VID_004C|PID_0324|PID_0321|PID_0320|PID_0265|PID_030E' -or
    $_.FriendlyName -match 'Magic (Trackpad|Keyboard)|Apple Wireless Keyboard'
}
$devices | ForEach-Object {
    $level = $null
    try {
        $level = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_BatteryLevel').Data
    } catch {}
    [pscustomobject]@{
        InstanceId = [string]$_.InstanceId
        FriendlyName = [string]$_.FriendlyName
        Name = [string]$_.Name
        Class = [string]$_.Class
        Manufacturer = [string]$_.Manufacturer
        BatteryLevel = $level
    }
} | ConvertTo-Json -Compress
""";

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -Command -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process == null)
            {
                return [];
            }

            process.StandardInput.Write(script);
            process.StandardInput.Close();

            // Drain stdout while waiting: with several matching devices the JSON can exceed the pipe
            // buffer, which would block PowerShell on write and trip the 4 second timeout.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(4000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort cleanup; battery fallback will simply be unavailable.
                }

                return [];
            }

            var output = outputTask.GetAwaiter().GetResult().Trim();
            return ParsePnpRecords(output);
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<PnpBatteryRecord> ParsePnpRecords(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                return document.RootElement.EnumerateArray()
                    .Select(ParsePnpRecord)
                    .Where(record => record != null)
                    .Cast<PnpBatteryRecord>()
                    .ToList();
            }

            var record = ParsePnpRecord(document.RootElement);
            return record != null ? [record] : [];
        }
        catch
        {
            return [];
        }
    }

    private static PnpBatteryRecord? ParsePnpRecord(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var instanceId = JsonString(element, "InstanceId");
        var friendlyName = JsonString(element, "FriendlyName");
        var name = JsonString(element, "Name");
        return new PnpBatteryRecord(
            instanceId,
            friendlyName,
            name,
            ProductIdFromText($"{instanceId} {friendlyName} {name}"),
            JsonInt(element, "BatteryLevel"));
    }

    private static string JsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";

    private static int? JsonInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => null,
        };
    }

    private static IReadOnlyDictionary<string, BatteryStatus> ReadCache()
    {
        lock (CacheLock)
        {
            try
            {
                if (!File.Exists(CachePath()))
                {
                    return new Dictionary<string, BatteryStatus>(StringComparer.OrdinalIgnoreCase);
                }

                var records = JsonSerializer.Deserialize<List<CachedBatteryRecord>>(File.ReadAllText(CachePath())) ?? [];
                return records
                    .Where(record => record.Key.Length > 0 && record.Percent is >= 0 and <= 100)
                    .GroupBy(record => record.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group =>
                        {
                            var latest = group.OrderByDescending(record => record.UpdatedAt).First();
                            return new BatteryStatus(latest.Percent, latest.Charging, latest.Source, latest.UpdatedAt);
                        },
                        StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, BatteryStatus>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private static void WriteCacheRecord(string key, HidDeviceInfo device, BatteryStatus status)
    {
        if (status.Percent is not (>= 0 and <= 100))
        {
            return;
        }

        lock (CacheLock)
        {
            try
            {
                var path = CachePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var records = File.Exists(path)
                    ? JsonSerializer.Deserialize<List<CachedBatteryRecord>>(File.ReadAllText(path)) ?? []
                    : [];
                records.RemoveAll(record => string.Equals(record.Key, key, StringComparison.OrdinalIgnoreCase));
                records.Add(new CachedBatteryRecord(key, device.ProductId, status.Percent.Value, status.Charging, status.Source, status.UpdatedAt));
                File.WriteAllText(path, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // Battery cache writes are non-critical.
            }
        }
    }

    private static string CachePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApplePeripheralsForWindows", "battery-cache.json");

    private static int? ProductIdFromText(string text)
    {
        var match = ProductIdRegex().Match(text);
        return match.Success ? Convert.ToInt32(match.Groups[1].Value, 16) : null;
    }

    [GeneratedRegex(@"(?:PID|DEV)[_&]([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex ProductIdRegex();

    private sealed record PnpBatteryRecord(string InstanceId, string FriendlyName, string Name, int? ProductId, int? BatteryLevel);

    private sealed record CachedBatteryRecord(string Key, int? ProductId, int Percent, bool? Charging, string Source, DateTimeOffset UpdatedAt);
}
