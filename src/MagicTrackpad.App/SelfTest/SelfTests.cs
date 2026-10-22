using MagicTrackpad.Configuration;
using MagicTrackpad.Gestures;
using MagicTrackpad.Hid;
using MagicTrackpad.Input;
using MagicTrackpad.Keyboard;
using MagicTrackpad.Runtime;

namespace MagicTrackpad.SelfTest;

internal static class SelfTests
{
    public static int Run()
    {
        try
        {
            TestHotkeys();
            TestKeyboardDetection();
            TestKeyboardFilterStatus();
            TestKeyboardFilterDriverStoreParsing();
            TestReports();
            TestBatteryReports();
            TestGestures();
            TestConfigRoundTrip();
            TestConfigReloadSignal();
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static void TestHotkeys()
    {
        var parsed = Hotkeys.Parse("Win+Ctrl+Left");
        Require(parsed.SequenceEqual(new ushort[] { 0x5B, 0x11, 0x25 }));
        Require(Hotkeys.Normalize("windows-control-left") == "Win+Ctrl+Left");
        Require(Hotkeys.Normalize("ctrl-plus") == "Ctrl+Plus");
        Require(Hotkeys.Parse("MediaPlayPause").SequenceEqual(new ushort[] { 0xB3 }));
        Require(Hotkeys.Parse("BrightnessDown").Count == 0);
        Require(Hotkeys.Parse("unchanged").Count == 0);
    }

    private static void TestReports()
    {
        var report = new byte[] { 0x31, 1, 0, 0 }.Concat(EncodeTrackpad2Touch(-100, 200, 7)).ToArray();
        var frames = HidReportParser.ParseReports(report);
        Require(frames.Count == 1);
        Require(frames[0].ActiveTouches[0].TrackingId == 7);
        Require(frames[0].ActiveTouches[0].X == -100);
        Require(frames[0].ActiveTouches[0].Y == 200);
    }

    private static void TestBatteryReports()
    {
        Require(DeviceBattery.TryParseBatteryReport([0x90, 0x02, 66], out var percent, out var charging));
        Require(percent == 66);
        Require(charging == true);
        Require(DeviceBattery.TryParseBatteryReport([0x47, 44], out percent, out charging));
        Require(percent == 44);
    }

    private static void TestKeyboardDetection()
    {
        var name = @"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&0320&COL01\9&346F8316&0&0000";
        var parsed = DeviceCatalog.ParseVidPid(name);
        var device = new HidDeviceInfo(IntPtr.Zero, name, parsed.VendorId, parsed.ProductId, null, null, null);
        Require(parsed.VendorId == DeviceCatalog.AppleBluetoothVendorId);
        Require(parsed.ProductId == DeviceCatalog.MagicKeyboardBluetooth);
        Require(device.IsAppleKeyboard);
        Require(!device.IsAppleMagicTrackpad);
        Require(RawInputWindow.ShouldDispatchRawReport(device));

        var trackpadName = @"HID\VID_05AC&PID_0324&COL02\9&102B385&0&0000";
        var trackpad = new HidDeviceInfo(IntPtr.Zero, trackpadName, DeviceCatalog.AppleUsbVendorId, DeviceCatalog.MagicTrackpad2UsbC, null, 0x0D, 0x05);
        Require(trackpad.IsAppleMagicTrackpad);
        Require(RawInputWindow.ShouldDispatchRawReport(trackpad));

        var other = new HidDeviceInfo(IntPtr.Zero, @"HID\VID_045E&PID_0000&COL01", 0x045E, 0x0000, null, 0x01, 0x06);
        Require(!other.IsAppleKeyboard);
        Require(!other.IsAppleMagicTrackpad);
        Require(!RawInputWindow.ShouldDispatchRawReport(other));
    }

    private static void TestKeyboardFilterStatus()
    {
        var active = KeyboardFilterDriverStatus.Evaluate(
            true,
            [new KeyboardFilterTargetState(@"BTHENUM\Apple", true, "AppleKeyboardFilter.inf", "HidBth", ["AppleKeyboardFilter"])],
            true,
            true);
        Require(active.Ready);
        Require(active.ReleaseReady);
        Require(active.Diagnosis == "Globe/Fn driver is active.");

        var missing = KeyboardFilterDriverStatus.Evaluate(
            true,
            [new KeyboardFilterTargetState(@"BTHENUM\Apple", false, "hidbth.inf", "HidBth", [])],
            false);
        Require(!missing.Ready);
        Require(missing.Diagnosis == "Globe/Fn driver is not installed.");

        var waiting = KeyboardFilterDriverStatus.Evaluate(
            true,
            [new KeyboardFilterTargetState(@"BTHENUM\Apple", false, "hidbth.inf", "HidBth", [])],
            true);
        Require(!waiting.Ready);
        Require(waiting.Diagnosis == "Globe/Fn driver is waiting for reconnect or restart.");
    }

    private static void TestKeyboardFilterDriverStoreParsing()
    {
        var output = """
Published Name:     oem42.inf
Original Name:      AppleKeyboardFilter.inf
Provider Name:      Apple Peripherals for Windows
Driver Version:     06/04/2026 0.4.0.0
Signer Name:        Microsoft Windows Hardware Compatibility Publisher
Catalog File:       applekeyboardfilter.cat

Published Name:     oem99.inf
Original Name:      keyboard.inf
Provider Name:      Microsoft
Signer Name:        Microsoft Windows
Catalog File:       keyboard.cat
""";
        var packages = KeyboardFilterDriverStatus.ParseDriverStorePackages(output);
        Require(packages.Count == 1);
        Require(packages[0].PublishedName == "oem42.inf");
        Require(packages[0].SignerName == "Microsoft Windows Hardware Compatibility Publisher");
    }

    private static void TestGestures()
    {
        var injector = new DryRunInputInjector();
        var config = new GestureConfig { SwipeThreshold = 100 };
        config.Hotkeys.ThreeFingerSwipeLeft = "Alt+Left";
        var engine = new GestureEngine(injector, config);
        engine.ProcessFrame(Frame([Touch(1, 500, 0), Touch(2, 600, 0), Touch(3, 700, 0)]), DateTimeOffset.UnixEpoch);
        engine.ProcessFrame(Frame([Touch(1, 300, 0), Touch(2, 400, 0), Touch(3, 500, 0)]), DateTimeOffset.UnixEpoch.AddMilliseconds(100));
        Require(injector.Events.Contains("hotkey:18,37"));

        injector = new DryRunInputInjector();
        config = new GestureConfig { TwoFingerSwipeThreshold = 100 };
        config.Hotkeys.TwoFingerSwipeLeft = "Alt+Left";
        engine = new GestureEngine(injector, config);
        engine.ProcessFrame(Frame([Touch(1, 500, 0), Touch(2, 650, 0)]), DateTimeOffset.UnixEpoch);
        engine.ProcessFrame(Frame([Touch(1, 250, 0), Touch(2, 400, 0)]), DateTimeOffset.UnixEpoch.AddMilliseconds(100));
        Require(injector.Events.Contains("hotkey:18,37"));

        injector = new DryRunInputInjector();
        config = new GestureConfig { FourFingerPinchThreshold = 40 };
        config.Hotkeys.FourFingerSpread = "Win+D";
        engine = new GestureEngine(injector, config);
        engine.ProcessFrame(Frame([Touch(1, -50, -50), Touch(2, 50, -50), Touch(3, -50, 50), Touch(4, 50, 50)]), DateTimeOffset.UnixEpoch);
        engine.ProcessFrame(Frame([Touch(1, -120, -120), Touch(2, 120, -120), Touch(3, -120, 120), Touch(4, 120, 120)]), DateTimeOffset.UnixEpoch.AddMilliseconds(100));
        Require(injector.Events.Contains("hotkey:91,68"));
    }

    private static void TestConfigRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "magic-trackpad-native-self-test.json");
        var config = new AppConfig();
        Require(config.Keyboard.FnGlobe == "Ctrl");
        Require(config.Keyboard.LeftControl == "Win");
        Require(config.Keyboard.RightControl == "Win");
        Require(config.Keyboard.LeftOption == "Alt");
        Require(config.Keyboard.RightOption == "Alt");
        Require(config.Keyboard.LeftCommand == "Ctrl");
        Require(config.Keyboard.RightCommand == "Ctrl");
        Require(KeyboardRemapper.IsHeldModifierAction(config.Keyboard.FnGlobe));
        Require(KeyboardRemapper.ModifierTarget(config.Keyboard.FnGlobe, 0x86).SequenceEqual(new ushort[] { 0xA2 }));
        Require(KeyboardRemapper.ModifierTarget(config.Keyboard.FnGlobe, 0x87).SequenceEqual(new ushort[] { 0xA3 }));
        Require(KeyboardRemapper.GlobeVirtualKey(KeyboardFilterDriverStatus.DefaultGlobeHidCode) == 0x86);
        Require(KeyboardRemapper.GlobeVirtualKey(0x65) == 0x5D);
        Require(KeyboardRemapper.GlobeVirtualKey(0x64) == 0xE2);
        Require(KeyboardRemapper.GlobeVirtualKey(0x4C) == null);
        Require(KeyboardRemapper.ModifierTarget(config.Keyboard.FnGlobe, 0x5D).SequenceEqual(new ushort[] { 0xA2 }));
        Require(KeyboardRemapper.ModifierTarget(config.Keyboard.LeftControl, 0xA2).SequenceEqual(new ushort[] { 0x5B }));
        Require(KeyboardRemapper.ModifierTarget(config.Keyboard.RightControl, 0xA3).SequenceEqual(new ushort[] { 0x5C }));
        Require(KeyboardRemapper.ModifierTarget(config.Keyboard.LeftOption, 0xA4).SequenceEqual(new ushort[] { 0xA4 }));
        Require(KeyboardRemapper.ModifierTarget(config.Keyboard.RightOption, 0xA5).SequenceEqual(new ushort[] { 0xA5 }));
        Require(KeyboardRemapper.ModifierTarget(config.Keyboard.LeftCommand, 0x5B).SequenceEqual(new ushort[] { 0xA2 }));
        Require(KeyboardRemapper.ModifierTarget(config.Keyboard.RightCommand, 0x5C).SequenceEqual(new ushort[] { 0xA3 }));
        Require(KeyboardRemapper.TryGetAppleFnState([0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02], out var bodyFnDown) && bodyFnDown);
        Require(KeyboardRemapper.TryGetAppleFnState([0x01, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02], out var prefixedFnDown) && prefixedFnDown);
        Require(KeyboardRemapper.TryGetAppleFnState([0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], out var fnUp) && !fnUp);
        Require(!KeyboardRemapper.TryGetAppleFnState([0x00, 0x01, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00], out _));
        Require(!KeyboardRemapper.TryGetAppleFnState([0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80], out _));
        config.Gestures.PointerSensitivity = 0.73;
        config.Gestures.SwapLeftRightButtons = true;
        config.Keyboard.FKeyMode = "custom";
        config.Keyboard.F1 = "Win+H";
        config.Keyboard.F13 = "Ctrl+Alt+Delete";
        ConfigStore.Save(path, config);
        var loaded = ConfigStore.Load(path);
        File.Delete(path);
        Require(Math.Abs(loaded.Gestures.PointerSensitivity - 0.73) < 0.001);
        Require(loaded.Gestures.SwapLeftRightButtons);
        Require(loaded.Keyboard.FKeyMode == "custom");
        Require(loaded.Keyboard.F1 == "Win+H");
        Require(loaded.Keyboard.F13 == "Ctrl+Alt+Delete");

        File.WriteAllText(path, """{"keyboard":{"fn_globe":"Win+Period"}}""");
        loaded = ConfigStore.Load(path);
        File.Delete(path);
        Require(loaded.Keyboard.FnGlobe == "Ctrl");
    }

    private static void TestConfigReloadSignal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"magic-trackpad-native-signal-{Guid.NewGuid():N}.json");
        using var signal = ConfigReloadSignal.Create(path);
        ConfigReloadSignal.Notify(path);
        Require(signal.WaitOne(1000));
    }

    private static TrackpadFrame Frame(IReadOnlyList<Touch> touches) => new(0x31, 0, touches, []);

    private static Touch Touch(int id, int x, int y) => new(id, x, y, 10, 0, 20, 18, 50, true);

    private static byte[] EncodeTrackpad2Touch(int x, int y, int trackingId)
    {
        var rawX = x & 0x1FFF;
        var rawY = -y & 0x1FFF;
        return
        [
            (byte)(rawX & 0xFF),
            (byte)(((rawX >> 8) & 0x1F) | ((rawY & 0x07) << 5)),
            (byte)((rawY >> 3) & 0xFF),
            (byte)(((rawY >> 11) & 0x03) | 0x80),
            20,
            18,
            11,
            64,
            (byte)(trackingId & 0x0F),
        ];
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Self-test failed.");
        }
    }
}
