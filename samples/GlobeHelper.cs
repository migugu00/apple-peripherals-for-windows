// AppleGlobeHelper.cs
//
// Translates the Apple Magic Keyboard Globe/Fn proxy key into Left Ctrl.
//
// The AppleKeyboardFilter driver injects HID usage 0x65 ("Non-US |") into the
// key array while Globe is held; Windows surfaces that as VK_OEM_102 (0xE2).
// This helper watches a low-level keyboard hook for non-injected 0xE2 events
// and synthesizes real Left Ctrl down/up with SendInput, swallowing the proxy
// key so no backslash is typed. On layouts where VK_OEM_102 carries no real
// key, the original driver-only behavior is harmless if this helper is off.
//
// Build: csc /target:winexe /out:AppleGlobeHelper.exe AppleGlobeHelper.cs

using System;
using System.Runtime.InteropServices;
using System.Threading;

static class GlobeHelper
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100;
    const int WM_KEYUP = 0x0101;
    const int WM_SYSKEYDOWN = 0x0104;
    const int WM_SYSKEYUP = 0x0105;

    const uint VK_OEM_102 = 0xE2;   // proxy key: future builds (usage 0x64 -> VK_OEM_102)
    const uint VK_APPS = 0x5D;      // proxy key: current loaded build (usage 0x65 -> Menu key)
    const uint VK_SUBTRACT = 0x6D;  // proxy key for Lock (usage 0x56 -> Keypad -)
    const uint VK_LCONTROL = 0xA2;  // what Globe synthesizes
    const uint VK_DELETE = 0x2E;    // what Lock synthesizes

    const uint LLKHF_INJECTED = 0x10;
    const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint INPUT_KEYBOARD = 1;

    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    // x64 layout: 40 bytes total, matching the native INPUT struct.
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    static IntPtr hook;
    static LowLevelKeyboardProc hookProc;
    static bool ctrlDown;
    static bool deleteDown;

    static void SendVk(uint vk, bool down, uint scanCode, uint extraFlags)
    {
        var input = new INPUT();
        input.type = INPUT_KEYBOARD;
        input.u.ki.wVk = (ushort)vk;
        input.u.ki.wScan = (ushort)scanCode;
        input.u.ki.dwFlags = (down ? extraFlags : (extraFlags | KEYEVENTF_KEYUP));
        input.u.ki.time = 0;
        input.u.ki.dwExtraInfo = UIntPtr.Zero;
        SendInput(1, new INPUT[] { input }, Marshal.SizeOf(typeof(INPUT)));
    }

    static void SendCtrl(bool down)
    {
        SendVk(VK_LCONTROL, down, 0x1D, 0); // Left Ctrl set-1 make code
        ctrlDown = down;
    }

    static void SendDelete(bool down)
    {
        SendVk(VK_DELETE, down, 0x53, KEYEVENTF_EXTENDEDKEY); // Delete is an extended key
        deleteDown = down;
    }

    static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int message = wParam.ToInt32();
            var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            bool injected = (k.flags & LLKHF_INJECTED) != 0;

            if (!injected && k.vkCode == VK_SUBTRACT)
            {
                bool isDown = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
                bool isUp = message == WM_KEYUP || message == WM_SYSKEYUP;

                if (isDown && !deleteDown)
                {
                    SendDelete(true);
                }
                else if (isUp && deleteDown)
                {
                    SendDelete(false);
                }

                return (IntPtr)1; // swallow the proxy key
            }

            if (!injected && (k.vkCode == VK_OEM_102 || k.vkCode == VK_APPS))
            {
                bool isDown = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
                bool isUp = message == WM_KEYUP || message == WM_SYSKEYUP;

                if (isDown && !ctrlDown)
                {
                    SendCtrl(true);
                }
                else if (isUp && ctrlDown)
                {
                    SendCtrl(false);
                }

                return (IntPtr)1; // swallow the proxy key
            }
        }

        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    [STAThread]
    static int Main()
    {
        bool createdNew;
        using (var mutex = new Mutex(true, "AppleGlobeHelper.SingleInstance", out createdNew))
        {
            if (!createdNew)
            {
                return 0;
            }

            hookProc = HookProc;
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
            {
                return Marshal.GetLastWin32Error();
            }

            // Simple message pump; the hook needs a pumping thread.
            var keepAlive = new ManualResetEvent(false);
            NativeMessage msg;
            while (!IsQuitRequested() && GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                // messages are consumed; no dispatch needed for this hook
            }

            UnhookWindowsHookEx(hook);
            keepAlive.Close();
            return 0;
        }
    }

    static bool IsQuitRequested()
    {
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeMessage
    {
        public IntPtr handle;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pointX;
        public int pointY;
    }

    [DllImport("user32.dll")]
    static extern int GetMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
}
