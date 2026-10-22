using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ApplePeripherals.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var parsed = SetupOptions.Parse(args);
        try
        {
            if (parsed.UninstallWorker)
            {
                Installer.Uninstall();
                return 0;
            }

            if (parsed.Uninstall)
            {
                return Installer.StartUninstall(parsed.Quiet);
            }

            if (parsed.Install || parsed.Quiet)
            {
                var result = Installer.Install(parsed.InstallDriver, new ConsoleProgress());
                if (!parsed.Quiet)
                {
                    MessageBox.Show(result.Message, Installer.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                return 0;
            }

            Application.Run(new SetupForm());
            return 0;
        }
        catch (Exception ex)
        {
            if (parsed.Quiet)
            {
                Console.Error.WriteLine(ex.Message);
            }
            else
            {
                MessageBox.Show(ex.Message, Installer.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return 1;
        }
    }
}

internal sealed class SetupForm : Form
{
    private readonly CheckBox driverCheck = new();
    private readonly Button installButton = new();
    private readonly Button cancelButton = new();
    private readonly TextBox log = new();

    public SetupForm()
    {
        Text = "Apple Peripherals Setup";
        Width = 560;
        Height = 420;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.White;

        var title = new Label
        {
            Text = "Apple Peripherals for Windows",
            Font = new Font("Segoe UI Semibold", 16),
            AutoSize = false,
            Height = 42,
            Dock = DockStyle.Top,
            Padding = new Padding(18, 16, 18, 0),
        };

        var body = new Label
        {
            Text = Installer.HasBundledKeyboardDriver
                ? "Install the settings app, background bridge, bundled Magic Trackpad driver, and bundled Magic Keyboard Globe/Fn driver."
                : "Install the settings app, background bridge, and bundled Magic Trackpad driver for Magic Trackpad and Magic Keyboard support.",
            AutoSize = false,
            Height = 64,
            Dock = DockStyle.Top,
            Padding = new Padding(18, 10, 18, 0),
        };

        driverCheck.Text = Installer.IsAdministrator
            ? Installer.HasBundledKeyboardDriver
                ? "Install or update bundled trackpad and keyboard drivers"
                : "Install or update the Magic Trackpad Precision Touchpad driver (included)"
            : "Precision Touchpad driver requires Administrator access";
        driverCheck.Checked = Installer.IsAdministrator;
        driverCheck.Enabled = Installer.IsAdministrator;
        driverCheck.AutoSize = false;
        driverCheck.Height = 34;
        driverCheck.Dock = DockStyle.Top;
        driverCheck.Padding = new Padding(18, 0, 18, 0);

        log.Multiline = true;
        log.ReadOnly = true;
        log.ScrollBars = ScrollBars.Vertical;
        log.BorderStyle = BorderStyle.FixedSingle;
        log.Font = new Font("Consolas", 9);
        log.Dock = DockStyle.Fill;
        log.Margin = new Padding(18);

        var buttonPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Height = 58,
            Padding = new Padding(18, 10, 18, 12),
        };

        installButton.Text = "Install";
        installButton.Width = 104;
        installButton.Height = 32;
        installButton.Click += async (_, _) => await InstallAsync();

        cancelButton.Text = "Cancel";
        cancelButton.Width = 104;
        cancelButton.Height = 32;
        cancelButton.Click += (_, _) => Close();

        buttonPanel.Controls.Add(installButton);
        buttonPanel.Controls.Add(cancelButton);

        var logWrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 8, 18, 8) };
        logWrap.Controls.Add(log);

        Controls.Add(logWrap);
        Controls.Add(driverCheck);
        Controls.Add(body);
        Controls.Add(title);
        Controls.Add(buttonPanel);
    }

    private async Task InstallAsync()
    {
        installButton.Enabled = false;
        cancelButton.Enabled = false;
        driverCheck.Enabled = false;
        try
        {
            var progress = new UiProgress(message =>
            {
                log.AppendText(message + Environment.NewLine);
            });
            var result = await Task.Run(() => Installer.Install(driverCheck.Checked, progress));
            MessageBox.Show(result.Message, Installer.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Installer.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            installButton.Enabled = true;
            cancelButton.Enabled = true;
            driverCheck.Enabled = Installer.IsAdministrator;
        }
    }
}

internal static class Installer
{
    public const string ProductName = "Apple Peripherals for Windows";
    private const string Publisher = "Apple Peripherals for Windows";
    private static readonly string Version =
        typeof(Installer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Installer).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
    private const string TaskName = "ApplePeripheralsBridge";
    private const string DriverPackageUrl = "https://github.com/vitoplantamura/MagicTrackpad2ForWindows/releases/download/v2.0/MT2FW11-20260223-MSSigned.zip";
    private const string DriverResourceName = "MagicTrackpad2ForWindows-MSSigned.zip";
    private const string KeyboardDriverResourceName = "AppleKeyboardFilterDriver.zip";
    private const string UninstallRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ApplePeripheralsForWindows";

    private static readonly string InstallRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ApplePeripheralsForWindows");
    private static readonly string AppDir = Path.Combine(InstallRoot, "app");
    private static readonly string InstalledSetupPath = Path.Combine(InstallRoot, "ApplePeripheralsSetup.exe");
    private static readonly string InstallStatePath = Path.Combine(InstallRoot, "install-state.json");
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".magictrackpad-bridge.json");
    private static readonly string StartMenuDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft",
        "Windows",
        "Start Menu",
        "Programs",
        ProductName);
    private static readonly string StartupShortcutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        "Apple Peripherals.lnk");

    public static bool IsAdministrator
    {
        get
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }

    public static bool HasBundledKeyboardDriver =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Any(name => string.Equals(name, KeyboardDriverResourceName, StringComparison.Ordinal));

    public static InstallResult Install(bool installDriver, IProgress<string> progress)
    {
        progress.Report("Stopping existing bridge...");
        StopBridgeProcesses();
        DeleteScheduledTask("MagicTrackpadBridge");
        DeleteScheduledTask(TaskName);

        progress.Report("Extracting app files...");
        Directory.CreateDirectory(InstallRoot);
        ReplaceAppPayload();
        CopySetupExecutable();

        var appExe = Path.Combine(AppDir, "MagicTrackpad.exe");
        if (!File.Exists(appExe))
        {
            throw new InvalidOperationException("Installer payload did not contain MagicTrackpad.exe.");
        }

        progress.Report("Preparing configuration...");
        RunRequired(appExe, File.Exists(ConfigPath)
            ? $"--migrate-config --config \"{ConfigPath}\""
            : $"--write-config --config \"{ConfigPath}\"");

        progress.Report("Creating shortcuts...");
        Directory.CreateDirectory(StartMenuDir);
        CreateShortcut(
            Path.Combine(StartMenuDir, "Apple Peripherals Settings.lnk"),
            appExe,
            $"--settings --config \"{ConfigPath}\"",
            AppDir,
            "Configure Apple keyboard and trackpad support on Windows");
        CreateShortcut(
            Path.Combine(StartMenuDir, "Run Apple Peripherals Bridge.lnk"),
            appExe,
            $"--bridge --config \"{ConfigPath}\"",
            AppDir,
            "Start Apple keyboard and trackpad support on Windows");

        progress.Report("Registering startup...");
        var taskInstalled = TryRegisterStartupTask(appExe);
        if (!taskInstalled)
        {
            CreateShortcut(
                StartupShortcutPath,
                appExe,
                $"--bridge --config \"{ConfigPath}\"",
                AppDir,
                "Start Apple keyboard and trackpad support at sign in");
        }
        else
        {
            DeleteFileIfExists(StartupShortcutPath);
            TryRunProcess("schtasks.exe", $"/Run /TN \"{TaskName}\"", wait: true, out _);
        }

        if (!Process.GetProcessesByName("MagicTrackpad").Any())
        {
            StartBridge(appExe);
        }

        var rebootRequired = false;
        var keyboardDriverPending = false;
        var keyboardDriverUnavailable = false;
        if (installDriver)
        {
            if (!IsAdministrator)
            {
                throw new InvalidOperationException("Installing the Precision Touchpad driver requires running setup as Administrator.");
            }

            progress.Report("Installing Precision Touchpad driver...");
            rebootRequired = InstallPrecisionDriver(progress);
            if (HasBundledKeyboardDriver)
            {
                progress.Report("Installing Magic Keyboard Globe/Fn driver...");
                var keyboardDriverRebootRequired = InstallKeyboardFilterDriver(progress);
                keyboardDriverRebootRequired |= TryRestartKeyboardFilterTargets(appExe, progress);
                rebootRequired |= keyboardDriverRebootRequired;
                if (!VerifyKeyboardFilterReady(appExe, progress, out var keyboardStatus))
                {
                    keyboardDriverPending = true;
                    progress.Report(keyboardStatus);
                }
            }
            else
            {
                keyboardDriverUnavailable = true;
                progress.Report("Magic Keyboard Globe/Fn driver is not bundled in this installer.");
            }
        }

        progress.Report("Registering uninstaller...");
        RegisterUninstaller(appExe);
        WriteInstallState();

        var messages = new List<string> { "Apple Peripherals was installed." };
        if (rebootRequired)
        {
            messages.Add("Windows reported that a restart is required to finish driver installation.");
        }
        if (keyboardDriverPending)
        {
            messages.Add("The Magic Keyboard Globe/Fn driver is not active yet; reconnect the keyboard or restart Windows, then check driver status in the app.");
        }
        if (keyboardDriverUnavailable)
        {
            messages.Add("This installer does not include the signed Magic Keyboard Globe/Fn driver package.");
        }

        var message = string.Join(" ", messages);
        return new InstallResult(message, rebootRequired);
    }

    public static int StartUninstall(bool quiet)
    {
        if (!quiet)
        {
            var response = MessageBox.Show(
                "Uninstall Apple Peripherals for Windows?",
                ProductName,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (response != DialogResult.Yes)
            {
                return 0;
            }
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"ApplePeripheralsUninstall-{Guid.NewGuid():N}.exe");
        File.Copy(Environment.ProcessPath ?? InstalledSetupPath, tempPath, overwrite: true);
        Process.Start(new ProcessStartInfo
        {
            FileName = tempPath,
            Arguments = "/uninstall-worker",
            UseShellExecute = false,
        });
        return 0;
    }

    public static void Uninstall()
    {
        Thread.Sleep(1000);
        StopBridgeProcesses();
        DeleteScheduledTask("MagicTrackpadBridge");
        DeleteScheduledTask(TaskName);
        DeleteFileIfExists(StartupShortcutPath);
        DeleteDirectoryIfExists(StartMenuDir);
        Registry.CurrentUser.DeleteSubKey(UninstallRegistryKey, throwOnMissingSubKey: false);
        DeleteDirectoryIfExists(InstallRoot);
    }

    private static void ReplaceAppPayload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ApplePeripheralsPayload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("ApplePeripheralsPayload.zip");
            if (payload == null)
            {
                throw new InvalidOperationException("Setup payload is missing. Rebuild with scripts\\build-installer.ps1.");
            }

            ZipFile.ExtractToDirectory(payload, tempDir, overwriteFiles: true);
            DeleteDirectoryIfExists(AppDir);
            Directory.CreateDirectory(AppDir);
            CopyDirectory(tempDir, AppDir);
        }
        finally
        {
            DeleteDirectoryIfExists(tempDir);
        }
    }

    private static void CopySetupExecutable()
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current))
        {
            return;
        }

        if (!string.Equals(current, InstalledSetupPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(current, InstalledSetupPath, overwrite: true);
        }
    }

    private static void RegisterUninstaller(string appExe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallRegistryKey);
        key.SetValue("DisplayName", ProductName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", Publisher);
        key.SetValue("InstallLocation", InstallRoot);
        key.SetValue("DisplayIcon", appExe);
        key.SetValue("UninstallString", $"\"{InstalledSetupPath}\" /uninstall");
        key.SetValue("QuietUninstallString", $"\"{InstalledSetupPath}\" /uninstall /quiet");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", EstimateSizeKb(InstallRoot), RegistryValueKind.DWord);
        key.SetValue("URLInfoAbout", "https://github.com/sheehanmunim/apple-peripherals-for-windows");
    }

    private static void WriteInstallState()
    {
        var state = new InstallState(
            Version,
            HasBundledPrecisionTrackpadDriver: true,
            HasBundledKeyboardDriver,
            DateTimeOffset.Now);
        File.WriteAllText(InstallStatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool TryRegisterStartupTask(string appExe)
    {
        var runLevel = IsAdministrator ? "HIGHEST" : "LIMITED";
        // schtasks parses its own command line, so the quotes around each path inside /TR must be
        // backslash-escaped; otherwise a profile path containing a space splits the argument.
        var taskRun = $"\\\"{appExe}\\\" --bridge --config \\\"{ConfigPath}\\\"";
        var args = $"/Create /TN \"{TaskName}\" /TR \"{taskRun}\" /SC ONLOGON /RL {runLevel} /F";
        return TryRunProcess("schtasks.exe", args, wait: true, out var exitCode) && exitCode == 0;
    }

    private static bool InstallPrecisionDriver(IProgress<string> progress)
    {
        var driversDir = Path.Combine(InstallRoot, "drivers");
        Directory.CreateDirectory(driversDir);
        var zipPath = Path.Combine(driversDir, "MagicTrackpad2ForWindows-MSSigned.zip");
        var extractRoot = Path.Combine(driversDir, "MagicTrackpad2ForWindows-MSSigned");

        using (var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream(DriverResourceName))
        {
            if (embedded != null)
            {
                progress.Report("Extracting bundled Precision Touchpad driver package...");
                using var target = File.Create(zipPath);
                embedded.CopyTo(target);
            }
            else
            {
                progress.Report("Downloading Precision Touchpad driver package...");
                using var client = new HttpClient();
                using var response = client.GetAsync(DriverPackageUrl).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                using var source = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                using var target = File.Create(zipPath);
                source.CopyTo(target);
            }
        }

        DeleteDirectoryIfExists(extractRoot);
        ZipFile.ExtractToDirectory(zipPath, extractRoot, overwriteFiles: true);
        var packageRoot = Directory.GetDirectories(extractRoot).FirstOrDefault()
            ?? throw new InvalidOperationException("Could not find extracted driver package root.");
        var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "ARM64" : "AMD64";
        var driverDir = Path.Combine(packageRoot, architecture);
        var infPath = Path.Combine(driverDir, "AmtPtpDevice.inf");
        if (!File.Exists(infPath))
        {
            throw new InvalidOperationException($"Could not find {architecture} driver INF.");
        }

        foreach (var file in Directory.EnumerateFiles(driverDir).Where(path => path.EndsWith(".cat", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sys", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            AssertValidSignature(file);
        }

        var controlPanel = Path.Combine(packageRoot, "AmtPtpControlPanel.exe");
        if (File.Exists(controlPanel))
        {
            AssertValidSignature(controlPanel);
        }

        progress.Report("Adding Precision Touchpad driver package...");
        var pnputil = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "pnputil.exe");
        var process = RunProcess(pnputil, $"/add-driver \"{infPath}\" /install", wait: true);
        if (process.ExitCode is not 0 and not 3010)
        {
            throw new InvalidOperationException($"pnputil failed with exit code {process.ExitCode}.");
        }

        return process.ExitCode == 3010;
    }

    private static bool InstallKeyboardFilterDriver(IProgress<string> progress)
    {
        using var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream(KeyboardDriverResourceName);
        if (embedded == null)
        {
            return false;
        }

        var driversDir = Path.Combine(InstallRoot, "drivers");
        Directory.CreateDirectory(driversDir);
        var zipPath = Path.Combine(driversDir, "AppleKeyboardFilterDriver.zip");
        var extractRoot = Path.Combine(driversDir, "AppleKeyboardFilterDriver");

        progress.Report("Extracting bundled Magic Keyboard driver package...");
        using (var target = File.Create(zipPath))
        {
            embedded.CopyTo(target);
        }

        DeleteDirectoryIfExists(extractRoot);
        ZipFile.ExtractToDirectory(zipPath, extractRoot, overwriteFiles: true);

        var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "ARM64" : "AMD64";
        var driverDir = Directory.EnumerateDirectories(extractRoot, architecture, SearchOption.AllDirectories).FirstOrDefault()
            ?? extractRoot;
        var infPath = Directory.EnumerateFiles(driverDir, "AppleKeyboardFilter.inf", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException($"Could not find {architecture} Apple Keyboard Filter INF.");

        var packageDir = Path.GetDirectoryName(infPath)
            ?? throw new InvalidOperationException("Could not resolve Apple Keyboard Filter driver directory.");
        var catalog = Directory.EnumerateFiles(packageDir, "*.cat").FirstOrDefault()
            ?? throw new InvalidOperationException("Could not find Apple Keyboard Filter driver catalog.");
        foreach (var file in new[] { catalog })
        {
            AssertValidSignature(file, requireMicrosoftSigner: true);
        }

        progress.Report("Adding Magic Keyboard driver package...");
        var pnputil = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "pnputil.exe");
        var process = RunProcess(pnputil, $"/add-driver \"{infPath}\" /install", wait: true);
        if (process.ExitCode is not 0 and not 3010)
        {
            throw new InvalidOperationException($"pnputil failed with exit code {process.ExitCode}.");
        }

        return process.ExitCode == 3010;
    }

    private static void AssertValidSignature(string path, bool requireMicrosoftSigner = false)
    {
        var command = "$sig = Get-AuthenticodeSignature -LiteralPath " + PowerShellQuote(path) + "; if ($sig.Status -ne 'Valid') { exit 1 }; if (" + (requireMicrosoftSigner ? "$true" : "$false") + " -and (!$sig.SignerCertificate -or $sig.SignerCertificate.Subject -notmatch 'Microsoft')) { exit 2 }";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var process = RunProcess("powershell.exe", $"-NoLogo -NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}", wait: true);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(requireMicrosoftSigner
                ? $"Microsoft driver signature check failed for {path}."
                : $"Signature check failed for {path}.");
        }
    }

    private static string PowerShellQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static bool VerifyKeyboardFilterReady(string appExe, IProgress<string> progress, out string statusText)
    {
        var statusPath = Path.Combine(Path.GetTempPath(), $"AppleKeyboardFilterStatus-{Guid.NewGuid():N}.txt");
        try
        {
            progress.Report("Verifying Magic Keyboard Globe/Fn driver status...");
            var process = RunProcess(
                appExe,
                $"--keyboard-filter-status --require-ready --require-microsoft-signer --output \"{statusPath}\"",
                wait: true);

            statusText = File.Exists(statusPath)
                ? File.ReadAllText(statusPath).Trim()
                : $"Magic Keyboard Globe/Fn driver status command exited with code {process.ExitCode}.";

            return process.ExitCode == 0;
        }
        finally
        {
            DeleteFileIfExists(statusPath);
        }
    }

    private static bool TryRestartKeyboardFilterTargets(string appExe, IProgress<string> progress)
    {
        var status = QueryKeyboardFilterStatus(appExe, progress);
        if (status == null || status.ReleaseReady)
        {
            return false;
        }

        var targets = (status.Targets ?? [])
            .Where(target => !string.IsNullOrWhiteSpace(target.InstanceId))
            .ToList();
        if (targets.Count == 0)
        {
            progress.Report("No Magic Keyboard driver target was found to restart.");
            return false;
        }

        var rebootRequired = false;
        var pnputil = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "pnputil.exe");
        foreach (var target in targets)
        {
            progress.Report("Restarting Magic Keyboard driver target...");
            var process = RunProcess(pnputil, $"/restart-device \"{target.InstanceId}\"", wait: true);
            if (process.ExitCode == 3010)
            {
                rebootRequired = true;
            }
            else if (process.ExitCode != 0)
            {
                progress.Report($"Could not restart Magic Keyboard target. pnputil exited with {process.ExitCode}.");
            }
        }

        return rebootRequired;
    }

    private static KeyboardFilterStatusDocument? QueryKeyboardFilterStatus(string appExe, IProgress<string> progress)
    {
        var statusPath = Path.Combine(Path.GetTempPath(), $"AppleKeyboardFilterStatus-{Guid.NewGuid():N}.json");
        try
        {
            var process = RunProcess(
                appExe,
                $"--keyboard-filter-status --json --output \"{statusPath}\"",
                wait: true);

            if (process.ExitCode != 0 || !File.Exists(statusPath))
            {
                progress.Report($"Could not query Magic Keyboard driver status. Status command exited with {process.ExitCode}.");
                return null;
            }

            return JsonSerializer.Deserialize<KeyboardFilterStatusDocument>(File.ReadAllText(statusPath));
        }
        catch (Exception ex)
        {
            progress.Report($"Could not query Magic Keyboard driver status. {ex.Message}");
            return null;
        }
        finally
        {
            DeleteFileIfExists(statusPath);
        }
    }

    private static void StartBridge(string appExe)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = appExe,
            Arguments = $"--bridge --config \"{ConfigPath}\"",
            WorkingDirectory = AppDir,
            UseShellExecute = true,
        });
    }

    private static void RunRequired(string fileName, string args)
    {
        var process = RunProcess(fileName, args, wait: true);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} failed with exit code {process.ExitCode}.");
        }
    }

    private static bool TryRunProcess(string fileName, string args, bool wait, out int exitCode)
    {
        try
        {
            var process = RunProcess(fileName, args, wait);
            exitCode = wait ? process.ExitCode : 0;
            return true;
        }
        catch
        {
            exitCode = -1;
            return false;
        }
    }

    private static Process RunProcess(string fileName, string args, bool wait)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            WorkingDirectory = Directory.Exists(AppDir) ? AppDir : Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException($"Could not start {fileName}.");

        if (wait)
        {
            if (!process.WaitForExit(120000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort cleanup before surfacing the timeout.
                }

                throw new TimeoutException($"{Path.GetFileName(fileName)} did not finish within 120 seconds.");
            }
        }

        return process;
    }

    private static void StopBridgeProcesses()
    {
        foreach (var process in Process.GetProcessesByName("MagicTrackpad"))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch
            {
                // Best effort; install can continue if the process has already exited.
            }
        }
    }

    private static void DeleteScheduledTask(string taskName)
    {
        TryRunProcess("schtasks.exe", $"/End /TN \"{taskName}\"", wait: true, out _);
        TryRunProcess("schtasks.exe", $"/Delete /TN \"{taskName}\" /F", wait: true, out _);
    }

    private static void CreateShortcut(string path, string target, string arguments, string workingDirectory, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Could not create WScript.Shell.");
        dynamic shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = target;
        shortcut.Arguments = arguments;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.Description = description;
        shortcut.Save();
        Marshal.FinalReleaseComObject(shortcut);
        Marshal.FinalReleaseComObject(shell);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destination);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static int EstimateSizeKb(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        var bytes = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
        return (int)Math.Max(1, bytes / 1024);
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}

internal sealed record InstallResult(string Message, bool RebootRequired);

internal sealed record KeyboardFilterStatusDocument(
    bool Ready,
    bool ReleaseReady,
    bool DriverStoreInstalled,
    KeyboardFilterTargetDocument[] Targets);

internal sealed record KeyboardFilterTargetDocument(
    string InstanceId,
    bool FilterBound);

internal sealed record InstallState(
    string Version,
    bool HasBundledPrecisionTrackpadDriver,
    bool HasBundledKeyboardDriver,
    DateTimeOffset InstalledAt);

internal sealed class SetupOptions
{
    public bool Install { get; private init; }
    public bool InstallDriver { get; private init; }
    public bool Quiet { get; private init; }
    public bool Uninstall { get; private init; }
    public bool UninstallWorker { get; private init; }

    public static SetupOptions Parse(string[] args)
    {
        var normalized = args.Select(arg => arg.TrimStart('-', '/').ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new SetupOptions
        {
            Install = normalized.Contains("install"),
            InstallDriver = normalized.Contains("driver") || normalized.Contains("install-driver"),
            Quiet = normalized.Contains("quiet") || normalized.Contains("silent"),
            Uninstall = normalized.Contains("uninstall"),
            UninstallWorker = normalized.Contains("uninstall-worker"),
        };
    }
}

internal sealed class ConsoleProgress : IProgress<string>
{
    public void Report(string value) => Console.WriteLine(value);
}

internal sealed class UiProgress(Action<string> report) : IProgress<string>
{
    public void Report(string value) => report(value);
}
