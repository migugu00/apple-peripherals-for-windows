using MagicTrackpad.Configuration;
using MagicTrackpad.Hid;
using MagicTrackpad.Keyboard;
using MagicTrackpad.Runtime;
using MagicTrackpad.SelfTest;
using MagicTrackpad.Ui;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MagicTrackpad;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var parsed = CommandLine.Parse(args);
        var configPath = parsed.ConfigPath ?? ConfigStore.DefaultConfigPath;

        try
        {
            if (parsed.Mode == AppMode.SelfTest)
            {
                return SelfTests.Run();
            }

            if (parsed.Mode == AppMode.Enable)
            {
                var ok = DeviceActions.EnableAllMagicTrackpads();
                return ok ? 0 : 2;
            }

            if (parsed.Mode == AppMode.DiagnoseHid)
            {
                return HidDiagnostics.Run(
                    parsed.DiagnosticsPath ?? Path.Combine(AppContext.BaseDirectory, "diagnostics", "hid-diagnostics.json"),
                    parsed.Seconds ?? 10);
            }

            if (parsed.Mode == AppMode.KeyboardFilterStatus)
            {
                return KeyboardFilterStatusCommand.Run(parsed);
            }

            if (parsed.Mode == AppMode.TestGlobe)
            {
                return GlobeKeyTestCommand.Run(parsed);
            }

            if (parsed.Mode == AppMode.WriteConfig)
            {
                ConfigStore.Save(configPath, new AppConfig());
                return 0;
            }

            if (parsed.Mode == AppMode.MigrateConfig)
            {
                ConfigStore.Save(configPath, ConfigStore.Load(configPath));
                return 0;
            }

            if (parsed.Mode == AppMode.Bridge)
            {
                Application.Run(new BridgeApplicationContext(configPath, parsed.DryRun, parsed.Seconds));
                return 0;
            }

            Application.Run(new SettingsForm(configPath));
            return 0;
        }
        catch (Exception ex)
        {
            if (parsed.Mode == AppMode.Settings)
            {
                MessageBox.Show(ex.Message, "Apple Peripherals", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return 1;
        }
    }
}

internal enum AppMode
{
    Settings,
    Bridge,
    Enable,
    DiagnoseHid,
    KeyboardFilterStatus,
    TestGlobe,
    SelfTest,
    WriteConfig,
    MigrateConfig,
}

internal sealed class CommandLine
{
    public AppMode Mode { get; init; } = AppMode.Settings;
    public string? ConfigPath { get; init; }
    public string? DiagnosticsPath { get; init; }
    public string? OutputPath { get; init; }
    public bool DryRun { get; init; }
    public bool Json { get; init; }
    public bool RequireReady { get; init; }
    public bool RequireMicrosoftSigner { get; init; }
    public double? Seconds { get; init; }

    public static CommandLine Parse(string[] args)
    {
        var mode = AppMode.Settings;
        string? configPath = null;
        string? diagnosticsPath = null;
        string? outputPath = null;
        var dryRun = false;
        var json = false;
        var requireReady = false;
        var requireMicrosoftSigner = false;
        double? seconds = null;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg.ToLowerInvariant())
            {
                case "--settings":
                    mode = AppMode.Settings;
                    break;
                case "--bridge":
                case "run":
                    mode = AppMode.Bridge;
                    break;
                case "--enable":
                case "enable":
                    mode = AppMode.Enable;
                    break;
                case "--diagnose-hid":
                case "diagnose-hid":
                    mode = AppMode.DiagnoseHid;
                    break;
                case "--keyboard-filter-status":
                case "--check-keyboard-filter":
                case "keyboard-filter-status":
                case "check-keyboard-filter":
                    mode = AppMode.KeyboardFilterStatus;
                    break;
                case "--test-globe":
                case "--globe-test":
                case "test-globe":
                case "globe-test":
                    mode = AppMode.TestGlobe;
                    break;
                case "--self-test":
                    mode = AppMode.SelfTest;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--require-ready":
                    requireReady = true;
                    break;
                case "--require-microsoft-signer":
                    requireMicrosoftSigner = true;
                    break;
                case "--write-config":
                case "write-config":
                    mode = AppMode.WriteConfig;
                    break;
                case "--migrate-config":
                case "migrate-config":
                    mode = AppMode.MigrateConfig;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--config":
                    configPath = index + 1 < args.Length ? args[++index] : configPath;
                    break;
                case "--diagnostics-path":
                    diagnosticsPath = index + 1 < args.Length ? args[++index] : diagnosticsPath;
                    break;
                case "--output":
                case "--output-path":
                    outputPath = index + 1 < args.Length ? args[++index] : outputPath;
                    break;
                case "--seconds":
                    if (index + 1 < args.Length && double.TryParse(args[++index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
                    {
                        seconds = value;
                    }
                    break;
            }
        }

        return new CommandLine
        {
            Mode = mode,
            ConfigPath = configPath,
            DiagnosticsPath = diagnosticsPath,
            OutputPath = outputPath,
            DryRun = dryRun,
            Json = json,
            RequireReady = requireReady,
            RequireMicrosoftSigner = requireMicrosoftSigner,
            Seconds = seconds,
        };
    }
}

internal static class KeyboardFilterStatusCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static int Run(CommandLine parsed)
    {
        var status = KeyboardFilterDriverStatus.Query(includeDriverStorePackages: true);
        var ready = parsed.RequireMicrosoftSigner ? status.ReleaseReady : status.Ready;
        var output = parsed.Json ? JsonSerializer.Serialize(status, JsonOptions) : PlainText(status, parsed.RequireMicrosoftSigner);
        CommandOutput.Write(output, parsed.OutputPath);
        return parsed.RequireReady && !ready ? 2 : 0;
    }

    private static string PlainText(KeyboardFilterDriverState status, bool requireMicrosoftSigner)
    {
        var lines = new List<string>
        {
            $"Ready: {status.Ready}",
            $"Release ready: {status.ReleaseReady}",
            $"Microsoft signed: {status.MicrosoftSigned}",
            $"Diagnosis: {status.Diagnosis}",
            $"Target driver: {status.TargetDriverInfPath ?? "not bound"}",
            $"Target service: {status.TargetService ?? "unknown"}",
            $"Driver packages: {status.DriverStorePackages.Count}",
        };

        if (requireMicrosoftSigner && status.Ready && !status.MicrosoftSigned)
        {
            lines.Add("Required signer: Microsoft driver-signing certificate was not found.");
        }

        foreach (var package in status.DriverStorePackages)
        {
            lines.Add($"Package: {package.PublishedName ?? "unknown"} {package.OriginalName ?? "unknown"} signer={package.SignerName ?? "unknown"}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

internal static class GlobeKeyTestCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static int Run(CommandLine parsed)
    {
        var timeout = TimeSpan.FromSeconds(parsed.Seconds ?? 8);
        var result = GlobeKeyProbe.Run(timeout);
        var output = parsed.Json ? JsonSerializer.Serialize(result, JsonOptions) : PlainText(result);
        CommandOutput.Write(output, parsed.OutputPath);
        return result.Observed ? 0 : 2;
    }

    private static string PlainText(GlobeKeyProbeResult result)
    {
        var lines = new List<string>
        {
            $"Observed: {result.Observed}",
            $"Apple keyboard present: {result.AppleKeyboardPresent}",
            $"Filter ready: {result.FilterReady}",
            $"Diagnosis: {result.Diagnosis}",
        };

        if (result.Sources.Count > 0)
        {
            lines.Add($"Sources: {string.Join(", ", result.Sources)}");
        }

        foreach (var status in result.ReaderStatuses)
        {
            lines.Add($"Reader: {status.ProductName} usage={status.UsagePage:X}/{status.Usage:X} state={status.State} length={status.ReportLength?.ToString() ?? "n/a"} error={status.Error?.ToString() ?? "n/a"}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

internal static class CommandOutput
{
    private const int AttachParentProcess = -1;

    public static void Write(string text, string? outputPath)
    {
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var path = Path.IsPathRooted(outputPath)
                ? outputPath
                : Path.Combine(Environment.CurrentDirectory, outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
            File.WriteAllText(path, text + Environment.NewLine);
            return;
        }

        AttachConsole(AttachParentProcess);
        Console.WriteLine(text);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
}
