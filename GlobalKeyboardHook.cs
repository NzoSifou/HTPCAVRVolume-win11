using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HTPCAVRVolume
{
    /// <summary>
    /// Grabs the volume keys before Windows sees them.
    ///
    /// Handlers run inside the hook callback, on the thread that installed the hook, and the
    /// whole system's input queue waits on them. They must return immediately: anything that
    /// touches the network belongs on a queue, not here. Windows silently drops a hook that
    /// takes longer than LowLevelHooksTimeout (5 s by default), which is exactly what a blocking
    /// connect to a switched-off amplifier used to do.
    /// </summary>
    public class GlobalKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;

        private IntPtr _hookID = IntPtr.Zero;
        private LowLevelKeyboardProc _proc;

        public event EventHandler VolumeUpPressed;
        public event EventHandler VolumeDownPressed;
        public event EventHandler VolumeMutePressed;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        public GlobalKeyboardHook()
        {
            _proc = HookCallback;
            _hookID = SetHook(_proc);
        }

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using Process curProcess = Process.GetCurrentProcess();
            using ProcessModule curModule = curProcess.MainModule;
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                Keys key = (Keys)vkCode;

                switch (key)
                {
                    case Keys.VolumeUp:
                        Raise(VolumeUpPressed);
                        return (IntPtr)1;  // Prevents the key from being passed to Windows

                    case Keys.VolumeDown:
                        Raise(VolumeDownPressed);
                        return (IntPtr)1;  // Block the key

                    case Keys.VolumeMute:
                        Raise(VolumeMutePressed);
                        return (IntPtr)1;  // Block the key
                }
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
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
            if (_hookID != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookID);
                _hookID = IntPtr.Zero;
            }
        }

        #region PInvoke

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
            IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode,
            IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        #endregion
    }
}
