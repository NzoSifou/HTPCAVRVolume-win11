using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HTPCAVRVolume.Ui
{
    /// <summary>
    /// Grabs the volume keys before Windows sees them.
    ///
    /// Handlers run inside the hook callback, on the thread that installed the hook, and the
    /// whole system's input queue waits on them. They must return immediately: anything that
    /// touches the network belongs on a queue, not here. Windows silently drops a hook that takes
    /// longer than LowLevelHooksTimeout (5 s by default), which is exactly what a blocking connect
    /// to a switched-off amplifier used to do.
    /// </summary>
    public sealed class GlobalKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;

        private const int VK_VOLUME_MUTE = 0xAD;
        private const int VK_VOLUME_DOWN = 0xAE;
        private const int VK_VOLUME_UP = 0xAF;

        private readonly LowLevelKeyboardProc _proc;
        private IntPtr _hookId;

        public GlobalKeyboardHook()
        {
            // Kept in a field: a delegate collected while Windows still holds the pointer takes
            // the process down with it.
            _proc = HookCallback;
            _hookId = SetHook(_proc);
        }

        public event EventHandler VolumeUpPressed;
        public event EventHandler VolumeDownPressed;
        public event EventHandler VolumeMutePressed;

        public bool IsInstalled => _hookId != IntPtr.Zero;

        private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

        private static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using Process process = Process.GetCurrentProcess();
            using ProcessModule module = process.MainModule;
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(module.ModuleName), 0);
        }

        private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                switch (Marshal.ReadInt32(lParam))
                {
                    case VK_VOLUME_UP:
                        Raise(VolumeUpPressed);
                        return (IntPtr)1;   // Swallowed, so Windows neither moves its own volume nor shows its flyout.

                    case VK_VOLUME_DOWN:
                        Raise(VolumeDownPressed);
                        return (IntPtr)1;

                    case VK_VOLUME_MUTE:
                        Raise(VolumeMutePressed);
                        return (IntPtr)1;
                }
            }

            return CallNextHookEx(_hookId, code, wParam, lParam);
        }

        private static void Raise(EventHandler handler)
        {
            try
            {
                handler?.Invoke(null, EventArgs.Empty);
            }
            catch
            {
                // An exception thrown back into the hook chain would take the process with it,
                // and with it the user's volume control.
            }
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string name);
    }
}
