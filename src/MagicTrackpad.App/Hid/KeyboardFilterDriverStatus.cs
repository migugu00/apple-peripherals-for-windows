using Microsoft.Win32;
using System.Diagnostics;

namespace MagicTrackpad.Hid;

public sealed record KeyboardFilterDriverState(
    bool AppleKeyboardPresent,
    bool FilterTargetPresent,
    bool DriverStoreInstalled,
    bool FilterBound,
    bool MicrosoftSigned,
    string Diagnosis,
    string? TargetDriverInfPath,
    string? TargetService,
    IReadOnlyList<KeyboardFilterTargetState> Targets,
    IReadOnlyList<KeyboardFilterDriverPackageState> DriverStorePackages)
{
    public bool Ready => AppleKeyboardPresent && FilterTargetPresent && DriverStoreInstalled && FilterBound;

    public bool ReleaseReady => Ready && MicrosoftSigned;
}

public sealed record KeyboardFilterTargetState(
    string InstanceId,
    bool FilterBound,
    string? DriverInfPath,
    string? Service,
    IReadOnlyList<string> LowerFilters);

public sealed record KeyboardFilterDriverPackageState(
    string? PublishedName,
    string? OriginalName,
    string? ProviderName,
    string? DriverVersion,
    string? SignerName,
    string? CatalogFile);

public static class KeyboardFilterDriverStatus
{
    private const string FilterServiceName = "AppleKeyboardFilter";
    private const string BluetoothHidServiceId = "{00001124-0000-1000-8000-00805f9b34fb}";
    private static readonly string[] BluetoothPids = ["0320", "0267", "026C"];
    private static readonly string[] UsbPids = ["0321", "0267", "026C"];

    public const int DefaultGlobeHidCode = 0x72;

    // Mirrors the driver's GlobeHidCode service value, including its fallback for
    // values that do not fit in a one-byte key slot.
    public static int ConfiguredGlobeHidCode()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{FilterServiceName}");
            return key?.GetValue("GlobeHidCode") is int value and >= 0 and <= 0xFF ? value : DefaultGlobeHidCode;
        }
        catch
        {
            return DefaultGlobeHidCode;
        }
    }

    public static KeyboardFilterDriverState Query(bool includeDriverStorePackages = false)
    {
        var appleKeyboardPresent = false;
        try
        {
            appleKeyboardPresent = DeviceCatalog.FindAppleKeyboards(DeviceActions.EnumerateRawInputDevices()).Count > 0;
        }
        catch
        {
            appleKeyboardPresent = false;
        }

        var targets = EnumerateTargets().ToList();
        var packages = includeDriverStorePackages ? DriverStorePackages() : [];
        var driverStoreInstalled = packages.Count > 0 || DriverStoreInstalled();
        var microsoftSigned = packages.Any(item => item.SignerName?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true);
        return Evaluate(appleKeyboardPresent, targets, driverStoreInstalled, microsoftSigned, packages);
    }

    internal static KeyboardFilterDriverState Evaluate(
        bool appleKeyboardPresent,
        IReadOnlyList<KeyboardFilterTargetState> targets,
        bool driverStoreInstalled,
        bool microsoftSigned = false,
        IReadOnlyList<KeyboardFilterDriverPackageState>? driverStorePackages = null)
    {
        var target = targets.FirstOrDefault(item => item.FilterBound) ?? targets.FirstOrDefault();
        var filterTargetPresent = targets.Count > 0;
        var filterBound = targets.Any(item => item.FilterBound);
        var diagnosis = DiagnosisFor(appleKeyboardPresent, filterTargetPresent, driverStoreInstalled, filterBound);

        return new KeyboardFilterDriverState(
            appleKeyboardPresent,
            filterTargetPresent,
            driverStoreInstalled,
            filterBound,
            microsoftSigned,
            diagnosis,
            target?.DriverInfPath,
            target?.Service,
            targets,
            driverStorePackages ?? []);
    }

    private static string DiagnosisFor(
        bool appleKeyboardPresent,
        bool filterTargetPresent,
        bool driverStoreInstalled,
        bool filterBound)
    {
        if (!appleKeyboardPresent)
        {
            return "Magic Keyboard not detected.";
        }

        if (!filterTargetPresent)
        {
            return "Keyboard driver target not found.";
        }

        if (!driverStoreInstalled)
        {
            return "Globe/Fn driver is not installed.";
        }

        if (!filterBound)
        {
            return "Globe/Fn driver is waiting for reconnect or restart.";
        }

        return "Globe/Fn driver is active.";
    }

    private static IEnumerable<KeyboardFilterTargetState> EnumerateTargets()
    {
        foreach (var target in EnumerateBluetoothTargets())
        {
            yield return target;
        }

        foreach (var target in EnumerateUsbTargets())
        {
            yield return target;
        }
    }

    private static IEnumerable<KeyboardFilterTargetState> EnumerateBluetoothTargets()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\BTHENUM");
        if (root == null)
        {
            yield break;
        }

        foreach (var parentName in root.GetSubKeyNames())
        {
            if (!parentName.Contains(BluetoothHidServiceId, StringComparison.OrdinalIgnoreCase) ||
                !BluetoothPids.Any(pid => parentName.Contains($"VID&0001004C_PID&{pid}", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            using var parent = root.OpenSubKey(parentName);
            if (parent == null)
            {
                continue;
            }

            foreach (var instanceName in parent.GetSubKeyNames())
            {
                using var instance = parent.OpenSubKey(instanceName);
                if (instance != null)
                {
                    yield return TargetFromRegistryKey($"BTHENUM\\{parentName}\\{instanceName}", instance);
                }
            }
        }
    }

    private static IEnumerable<KeyboardFilterTargetState> EnumerateUsbTargets()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (root == null)
        {
            yield break;
        }

        foreach (var parentName in root.GetSubKeyNames())
        {
            if (!UsbPids.Any(pid => parentName.Contains($"VID_05AC&PID_{pid}", StringComparison.OrdinalIgnoreCase)) ||
                !parentName.Contains("MI_01", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var parent = root.OpenSubKey(parentName);
            if (parent == null)
            {
                continue;
            }

            foreach (var instanceName in parent.GetSubKeyNames())
            {
                using var instance = parent.OpenSubKey(instanceName);
                if (instance != null)
                {
                    yield return TargetFromRegistryKey($"USB\\{parentName}\\{instanceName}", instance);
                }
            }
        }
    }

    private static KeyboardFilterTargetState TargetFromRegistryKey(string instanceId, RegistryKey instance)
    {
        var lowerFilters = RegistryStringArray(instance.GetValue("LowerFilters"));
        var driverKey = instance.GetValue("Driver") as string;
        var service = instance.GetValue("Service") as string;
        var driverInfPath = DriverInfPath(driverKey);
        var filterBound = lowerFilters.Any(item => string.Equals(item, FilterServiceName, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(driverInfPath, "AppleKeyboardFilter.inf", StringComparison.OrdinalIgnoreCase);

        return new KeyboardFilterTargetState(instanceId, filterBound, driverInfPath, service, lowerFilters);
    }

    private static string? DriverInfPath(string? driverKey)
    {
        if (string.IsNullOrWhiteSpace(driverKey))
        {
            return null;
        }

        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driverKey}");
        return key?.GetValue("InfPath") as string;
    }

    private static bool DriverStoreInstalled()
    {
        var repository = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "DriverStore",
            "FileRepository");
        try
        {
            return Directory.Exists(repository) &&
                Directory.EnumerateDirectories(repository, "applekeyboardfilter.inf_*").Any();
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<KeyboardFilterDriverPackageState> DriverStorePackages()
    {
        var pnputil = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "pnputil.exe");
        if (!File.Exists(pnputil))
        {
            return [];
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = pnputil,
                ArgumentList = { "/enum-drivers" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (process == null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10000);
            if (!process.HasExited)
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // Best effort only; a status query should not crash the app.
                }
                return [];
            }

            return ParseDriverStorePackages(output);
        }
        catch
        {
            return [];
        }
    }

    internal static IReadOnlyList<KeyboardFilterDriverPackageState> ParseDriverStorePackages(string output)
    {
        var packages = new List<KeyboardFilterDriverPackageState>();
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void AddCurrent()
        {
            if (current.Count == 0)
            {
                return;
            }

            if (IsKeyboardFilterPackage(current))
            {
                packages.Add(new KeyboardFilterDriverPackageState(
                    current.GetValueOrDefault("Published Name"),
                    current.GetValueOrDefault("Original Name"),
                    current.GetValueOrDefault("Provider Name"),
                    current.GetValueOrDefault("Driver Version"),
                    current.GetValueOrDefault("Signer Name"),
                    current.GetValueOrDefault("Catalog File")));
            }

            current.Clear();
        }

        foreach (var rawLine in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                AddCurrent();
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            current[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        AddCurrent();
        return packages;
    }

    private static bool IsKeyboardFilterPackage(IReadOnlyDictionary<string, string> package) =>
        package.TryGetValue("Original Name", out var originalName) &&
            string.Equals(originalName, "AppleKeyboardFilter.inf", StringComparison.OrdinalIgnoreCase) ||
        package.TryGetValue("Provider Name", out var providerName) &&
            string.Equals(providerName, "Apple Peripherals for Windows", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> RegistryStringArray(object? value)
    {
        return value switch
        {
            string[] array => array,
            string text when !string.IsNullOrWhiteSpace(text) => [text],
            _ => [],
        };
    }
}
