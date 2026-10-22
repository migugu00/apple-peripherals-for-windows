using MagicTrackpad.Hid;
using MagicTrackpad.Interop;
using MagicTrackpad.Runtime;
using System.Runtime.InteropServices;

namespace MagicTrackpad.Keyboard;

internal static class GlobeKeyProbe
{
    private const ushort VkF23 = 0x86;
    private const ushort VkF24 = 0x87;

    public static GlobeKeyProbeResult Run(TimeSpan timeout)
    {
        timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeout.TotalMilliseconds, 1000, 60000));
        GlobeKeyProbeResult? result = null;
        Exception? exception = null;
        using var complete = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            try
            {
                using var context = new ProbeContext(timeout);
                Application.Run(context);
                result = context.Result;
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                complete.Set();
            }
        })
        {
            IsBackground = true,
            Name = "Globe/Fn live key probe",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!complete.Wait(timeout + TimeSpan.FromSeconds(3)))
        {
            return GlobeKeyProbeResult.NotObserved(
                false,
                false,
                "The Globe/Fn live test timed out.",
                [],
                []);
        }

        if (exception != null)
        {
            throw exception;
        }

        return result ?? GlobeKeyProbeResult.NotObserved(
            false,
            false,
            "The Globe/Fn live test did not produce a result.",
            [],
            []);
    }

    private sealed class ProbeContext : ApplicationContext
    {
        private readonly object sync = new();
        private readonly DateTimeOffset deadline;
        private readonly System.Windows.Forms.Timer timer;
        private readonly NativeMethods.LowLevelKeyboardProc callback;
        private readonly List<string> sources = [];
        private readonly List<DirectHidReaderStatus> readerStatuses = [];
        private readonly DirectHidReportReader reader;
        private readonly bool appleKeyboardPresent;
        private readonly bool filterReady;
        private readonly ushort? driverGlobeVk;
        private IntPtr hook;
        private bool observed;
        private bool disposed;

        public ProbeContext(TimeSpan timeout)
        {
            deadline = DateTimeOffset.UtcNow + timeout;
            callback = HookProc;
            var devices = DeviceActions.EnumerateRawInputDevices();
            appleKeyboardPresent = DeviceCatalog.FindAppleKeyboards(devices).Count > 0;
            filterReady = KeyboardFilterDriverStatus.Query().Ready;
            driverGlobeVk = KeyboardRemapper.GlobeVirtualKey(KeyboardFilterDriverStatus.ConfiguredGlobeHidCode());

            hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, callback, IntPtr.Zero, 0);

            reader = new DirectHidReportReader(OnReport, OnStatus);
            reader.UpdateDevices(devices);

            timer = new System.Windows.Forms.Timer { Interval = 50 };
            timer.Tick += (_, _) =>
            {
                if (observed || DateTimeOffset.UtcNow >= deadline)
                {
                    ExitThread();
                }
            };
            timer.Start();
        }

        public GlobeKeyProbeResult Result
        {
            get
            {
                lock (sync)
                {
                    var distinctSources = sources.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    return observed
                        ? GlobeKeyProbeResult.Detected(
                            appleKeyboardPresent,
                            filterReady,
                            "Globe/Fn was detected by the app.",
                            distinctSources,
                            readerStatuses.ToList())
                        : GlobeKeyProbeResult.NotObserved(
                            appleKeyboardPresent,
                            filterReady,
                            Diagnosis(appleKeyboardPresent, filterReady, readerStatuses),
                            distinctSources,
                            readerStatuses.ToList());
                }
            }
        }

        private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var data = Marshal.PtrToStructure<NativeMethods.KeyboardHookStruct>(lParam);
                var vk = (ushort)data.VkCode;
                var isDown = wParam == NativeMethods.WM_KEYDOWN || wParam == NativeMethods.WM_SYSKEYDOWN;
                var injected = (data.Flags & NativeMethods.LLKHF_INJECTED) != 0;
                if (isDown && !injected && vk is VkF23 or VkF24)
                {
                    MarkObserved(vk == VkF23 ? "keyboard-hook:F23" : "keyboard-hook:F24");
                }
                else if (isDown && !injected && vk == driverGlobeVk)
                {
                    MarkObserved($"keyboard-hook:0x{vk:X2}");
                }
            }

            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
        }

        private void OnStatus(DirectHidReaderStatus status)
        {
            lock (sync)
            {
                readerStatuses.Add(status);
            }
        }

        private void OnReport(HidDeviceInfo device, byte[] report)
        {
            if (device.IsAppleKeyboard &&
                KeyboardRemapper.TryGetAppleFnState(report, out var down) &&
                down)
            {
                MarkObserved("raw-hid:apple-fn-bit");
            }
        }

        private void MarkObserved(string source)
        {
            lock (sync)
            {
                observed = true;
                sources.Add(source);
            }
        }

        private static string Diagnosis(
            bool appleKeyboardPresent,
            bool filterReady,
            IReadOnlyList<DirectHidReaderStatus> statuses)
        {
            if (!appleKeyboardPresent)
            {
                return "Magic Keyboard was not detected.";
            }

            if (!filterReady)
            {
                return "Globe/Fn was not detected. Install and bind the Apple Keyboard Filter driver, then test again.";
            }

            if (statuses.Any(status => status.ProductName.Contains("Keyboard", StringComparison.OrdinalIgnoreCase) &&
                status.State == "open_failed" &&
                status.Error == 5))
            {
                return "Globe/Fn was not detected. Windows still denied access to the keyboard collection.";
            }

            return "Globe/Fn was not detected during the test window.";
        }

        protected override void ExitThreadCore()
        {
            DisposeProbe();
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeProbe();
            }

            base.Dispose(disposing);
        }

        private void DisposeProbe()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            timer.Stop();
            timer.Dispose();
            reader.Dispose();
            if (hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }
        }
    }
}

internal sealed record GlobeKeyProbeResult(
    bool Observed,
    bool AppleKeyboardPresent,
    bool FilterReady,
    string Diagnosis,
    IReadOnlyList<string> Sources,
    IReadOnlyList<DirectHidReaderStatus> ReaderStatuses)
{
    public static GlobeKeyProbeResult Detected(
        bool appleKeyboardPresent,
        bool filterReady,
        string diagnosis,
        IReadOnlyList<string> sources,
        IReadOnlyList<DirectHidReaderStatus> readerStatuses) =>
        new(true, appleKeyboardPresent, filterReady, diagnosis, sources, readerStatuses);

    public static GlobeKeyProbeResult NotObserved(
        bool appleKeyboardPresent,
        bool filterReady,
        string diagnosis,
        IReadOnlyList<string> sources,
        IReadOnlyList<DirectHidReaderStatus> readerStatuses) =>
        new(false, appleKeyboardPresent, filterReady, diagnosis, sources, readerStatuses);
}
