using System.Runtime.InteropServices;
using MagicTrackpad.Configuration;
using MagicTrackpad.Hid;
using MagicTrackpad.Input;
using MagicTrackpad.Interop;

namespace MagicTrackpad.Keyboard;

internal sealed class KeyboardRemapper : IDisposable
{
    private const ushort VkLeftControl = 0xA2;
    private const ushort VkRightControl = 0xA3;
    private const ushort VkLeftMenu = 0xA4;
    private const ushort VkRightMenu = 0xA5;
    private const ushort VkLeftWin = 0x5B;
    private const ushort VkRightWin = 0x5C;
    private const ushort VkCapsLock = 0x14;
    private const ushort VkEscape = 0x1B;
    private const ushort VkF1 = 0x70;
    private const ushort VkF12 = 0x7B;
    private const ushort VkF13 = 0x7C;
    private const ushort VkF19 = 0x82;
    private const ushort VkF23 = 0x86;
    private const ushort VkF24 = 0x87;

    private readonly IInputInjector injector;
    private readonly NativeMethods.LowLevelKeyboardProc callback;
    private readonly Dictionary<ushort, IReadOnlyList<ushort>> heldRemaps = [];
    private readonly HashSet<ushort> suppressedKeyUps = [];
    private KeyboardConfig config;
    private bool appleKeyboardPresent;
    private bool rawGlobeDown;
    private IntPtr hook;

    public KeyboardRemapper(IInputInjector injector, KeyboardConfig config)
    {
        this.injector = injector;
        this.config = config;
        callback = HookProc;
        EnsureHook();
    }

    public void UpdateConfig(KeyboardConfig nextConfig)
    {
        ReleaseHeldRemaps();
        config = nextConfig;
        EnsureHook();
    }

    public void SetAppleKeyboardPresent(bool present)
    {
        if (appleKeyboardPresent == present)
        {
            return;
        }

        appleKeyboardPresent = present;
        if (!present)
        {
            ReleaseHeldRemaps();
        }
    }

    private void EnsureHook()
    {
        if (!config.Enabled)
        {
            RemoveHook();
            return;
        }

        if (hook != IntPtr.Zero)
        {
            return;
        }

        hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, callback, IntPtr.Zero, 0);
        if (hook == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0)
        {
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
        }

        try
        {
            var data = Marshal.PtrToStructure<NativeMethods.KeyboardHookStruct>(lParam);
            if ((data.Flags & NativeMethods.LLKHF_INJECTED) != 0 || !ShouldApply())
            {
                return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
            }

            var vk = (ushort)data.VkCode;
            var isDown = wParam == NativeMethods.WM_KEYDOWN || wParam == NativeMethods.WM_SYSKEYDOWN;
            var isUp = wParam == NativeMethods.WM_KEYUP || wParam == NativeMethods.WM_SYSKEYUP;

            if (isDown && HandleKeyDown(vk))
            {
                return 1;
            }

            if (isUp && HandleKeyUp(vk))
            {
                return 1;
            }
        }
        catch (Exception)
        {
            // A hook callback must never throw: an invalid hotkey in the config or a rejected
            // SendInput (for example while an elevated window has focus) would otherwise crash
            // the whole bridge. Fall through and let the original key press pass unchanged.
        }

        return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
    }

    private bool ShouldApply() =>
        config.Enabled && (!config.OnlyWhenAppleKeyboardPresent || appleKeyboardPresent);

    public void ProcessKeyboardReport(HidDeviceInfo device, byte[] report)
    {
        if (!device.IsAppleKeyboard || !ShouldApply() || !TryGetAppleFnState(report, out var globeDown))
        {
            return;
        }

        if (globeDown == rawGlobeDown)
        {
            return;
        }

        rawGlobeDown = globeDown;
        if (globeDown)
        {
            HandleKeyDown(VkF23);
        }
        else
        {
            HandleKeyUp(VkF23);
        }
    }

    private bool HandleKeyDown(ushort vk)
    {
        var action = ActionFor(vk);
        if (action == null)
        {
            return false;
        }

        if (IsUnchangedAction(action))
        {
            return false;
        }

        if (IsGlobeKey(vk) && IsHeldModifierAction(action))
        {
            return HandleMappedKeyDown(vk, action);
        }

        if (IsFunctionHotkey(vk))
        {
            if (SystemActions.TryRun(action))
            {
                suppressedKeyUps.Add(vk);
                return true;
            }

            SendHotkey(action);
            suppressedKeyUps.Add(vk);
            return true;
        }

        return HandleMappedKeyDown(vk, action);
    }

    private bool HandleMappedKeyDown(ushort vk, string action)
    {
        var mapped = ModifierTarget(action, vk);
        if (mapped.Count == 0)
        {
            suppressedKeyUps.Add(vk);
            return true;
        }

        if (IsClickAction(action))
        {
            injector.Hotkey(mapped);
            suppressedKeyUps.Add(vk);
            return true;
        }

        if (!heldRemaps.ContainsKey(vk))
        {
            injector.HotkeyDown(mapped);
            heldRemaps[vk] = mapped;
        }

        return true;
    }

    private bool HandleKeyUp(ushort vk)
    {
        var action = ActionFor(vk);
        if (action != null && IsUnchangedAction(action))
        {
            return false;
        }

        if (heldRemaps.Remove(vk, out var mapped))
        {
            injector.HotkeyUp(mapped.Reverse());
            return true;
        }

        if (suppressedKeyUps.Remove(vk))
        {
            return true;
        }

        return action != null && ModifierTarget(action, vk).Count == 0;
    }

    private string? ActionFor(ushort vk)
    {
        return vk switch
        {
            VkLeftWin when config.SwapExchangedKeys => config.LeftCommand,
            VkRightWin when config.SwapExchangedKeys => config.RightCommand,
            VkLeftControl when config.SwapExchangedKeys => config.LeftControl,
            VkRightControl when config.SwapExchangedKeys => config.RightControl,
            VkLeftMenu when config.SwapExchangedKeys => config.LeftOption,
            VkRightMenu when config.SwapExchangedKeys => config.RightOption,
            VkCapsLock when config.SwapExchangedKeys => config.CapsLock,
            0x70 when UseCustomFKeys => config.F1,
            0x71 when UseCustomFKeys => config.F2,
            0x72 when UseCustomFKeys => config.F3,
            0x73 when UseCustomFKeys => config.F4,
            0x74 when UseCustomFKeys => config.F5,
            0x75 when UseCustomFKeys => config.F6,
            0x76 when UseCustomFKeys => config.F7,
            0x77 when UseCustomFKeys => config.F8,
            0x78 when UseCustomFKeys => config.F9,
            0x79 when UseCustomFKeys => config.F10,
            0x7A when UseCustomFKeys => config.F11,
            0x7B when UseCustomFKeys => config.F12,
            0x7C => config.F13,
            0x7D => config.F14,
            0x7E => config.F15,
            0x7F => config.F16,
            0x80 => config.F17,
            0x81 => config.F18,
            0x82 => config.F19,
            VkF23 or VkF24 => config.FnGlobe,
            _ => null,
        };
    }

    private bool UseCustomFKeys =>
        NormalizeAction(config.FKeyMode) == "custom";

    private static bool IsFunctionHotkey(ushort vk) => vk is >= VkF1 and <= VkF24;

    private static bool IsGlobeKey(ushort vk) => vk is VkF23 or VkF24;

    internal static bool TryGetAppleFnState(byte[] report, out bool down)
    {
        down = false;

        if (TryGetAppleFnStateFromKeyboardBody(report, 1, out down))
        {
            return true;
        }

        if (TryGetAppleFnStateFromKeyboardBody(report, 0, out down))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetAppleFnStateFromKeyboardBody(byte[] report, int offset, out bool down)
    {
        down = false;
        const byte fnMask = 0x02;
        const int appleKeyboardBodyLength = 9;

        if (report.Length < offset + appleKeyboardBodyLength)
        {
            return false;
        }

        var specialKeyByte = report[offset + 8];
        if ((specialKeyByte & ~0x03) != 0)
        {
            return false;
        }

        down = (specialKeyByte & fnMask) != 0;
        return true;
    }

    private static bool IsUnchangedAction(string action) =>
        NormalizeAction(action) is "" or "unchanged";

    internal static bool IsHeldModifierAction(string action) =>
        NormalizeAction(action) is "ctrl" or "control" or "alt" or "option" or "win" or "windows" or "command" or "shift";

    private static bool IsClickAction(string action) =>
        NormalizeAction(action) is "esc" or "escape" or "capslock";

    internal static IReadOnlyList<ushort> ModifierTarget(string action, ushort originalVk)
    {
        return NormalizeAction(action) switch
        {
            "" or "unchanged" => [originalVk],
            "none" => [],
            "ctrl" or "control" => [originalVk == VkRightWin || originalVk == VkRightControl || originalVk == VkF24 ? VkRightControl : VkLeftControl],
            "alt" or "option" => [originalVk == VkRightWin || originalVk == VkRightControl || originalVk == VkRightMenu ? VkRightMenu : VkLeftMenu],
            "win" or "windows" or "command" => [originalVk == VkRightWin || originalVk == VkRightControl ? VkRightWin : VkLeftWin],
            "shift" => [0x10],
            "esc" or "escape" => [VkEscape],
            "capslock" => [VkCapsLock],
            _ => Hotkeys.Parse(action),
        };
    }

    private void SendHotkey(string value)
    {
        var keys = Hotkeys.Parse(value);
        if (keys.Count > 0)
        {
            injector.Hotkey(keys);
        }
    }

    private void ReleaseHeldRemaps()
    {
        foreach (var mapped in heldRemaps.Values.Reverse())
        {
            injector.HotkeyUp(mapped.Reverse());
        }

        heldRemaps.Clear();
        suppressedKeyUps.Clear();
        rawGlobeDown = false;
    }

    private void RemoveHook()
    {
        ReleaseHeldRemaps();
        if (hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
    }

    private static string NormalizeAction(string value) =>
        value.Trim().Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();

    public void Dispose()
    {
        RemoveHook();
    }
}
