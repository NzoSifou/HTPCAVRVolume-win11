using System;
using System.Runtime.InteropServices;

namespace HTPCAVRVolume.Ui
{
    /// <summary>
    /// The notification area icon. WinUI has no such control, so this is Shell_NotifyIcon driven
    /// from a message-only window of our own. Double-clicking it brings the window back; the right
    /// button offers the same, plus a way out of the app.
    /// </summary>
    sealed class TrayIcon : IDisposable
    {
        private const int WM_APP = 0x8000;
        private const int WM_TRAYCALLBACK = WM_APP + 1;
        private const int ShowCommand = 1;
        private const int ExitCommand = 2;

        private readonly WndProc _wndProc;
        private readonly string _className;
        private IntPtr _window;
        private IntPtr _icon;
        private bool _added;

        public TrayIcon(string tooltip)
        {
            _className = "HTPCAVRVolumeTray";
            _wndProc = OnMessage;

            WNDCLASSEX wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandle(null),
                lpszClassName = _className
            };

            RegisterClassEx(ref wc);
            _window = CreateWindowEx(0, _className, null, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            _icon = LoadAppIcon();

            NOTIFYICONDATA data = NewData();
            data.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
            data.uCallbackMessage = WM_TRAYCALLBACK;
            data.hIcon = _icon;
            data.szTip = tooltip;
            _added = Shell_NotifyIcon(NIM_ADD, ref data);
        }

        /// <summary>
        /// False when the shell refused the icon. Nothing else can bring the window back, so the
        /// app must not hide itself away when this is not true.
        /// </summary>
        public bool IsVisible => _added;

        /// <summary>The user asked for the window back.</summary>
        public event EventHandler Activated;

        /// <summary>The user asked to quit.</summary>
        public event EventHandler ExitRequested;

        public void Dispose()
        {
            if (_added)
            {
                NOTIFYICONDATA data = NewData();
                Shell_NotifyIcon(NIM_DELETE, ref data);
                _added = false;
            }

            if (_window != IntPtr.Zero)
            {
                DestroyWindow(_window);
                _window = IntPtr.Zero;
            }

            if (_icon != IntPtr.Zero)
            {
                DestroyIcon(_icon);
                _icon = IntPtr.Zero;
            }
        }

        private NOTIFYICONDATA NewData()
        {
            return new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _window,
                uID = 1
            };
        }

        private static IntPtr LoadAppIcon()
        {
            // The icon the exe carries, so the tray matches the window and the taskbar.
            IntPtr module = GetModuleHandle(null);
            IntPtr icon = LoadIcon(module, (IntPtr)32512);   // IDI_APPLICATION as a fallback
            IntPtr own = ExtractIcon(module, Environment.ProcessPath, 0);
            return own != IntPtr.Zero && own != (IntPtr)1 ? own : icon;
        }

        private IntPtr OnMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == WM_TRAYCALLBACK)
            {
                int what = (int)lParam;
                if (what == WM_LBUTTONDBLCLK)
                {
                    Activated?.Invoke(this, EventArgs.Empty);
                }
                else if (what == WM_RBUTTONUP || what == WM_CONTEXTMENU)
                {
                    ShowMenu();
                }

                return IntPtr.Zero;
            }

            if (message == WM_COMMAND)
            {
                switch ((int)wParam & 0xFFFF)
                {
                    case ShowCommand:
                        Activated?.Invoke(this, EventArgs.Empty);
                        return IntPtr.Zero;
                    case ExitCommand:
                        ExitRequested?.Invoke(this, EventArgs.Empty);
                        return IntPtr.Zero;
                }
            }

            return DefWindowProc(window, message, wParam, lParam);
        }

        private void ShowMenu()
        {
            IntPtr menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
            {
                return;
            }

            try
            {
                AppendMenu(menu, MF_STRING, (IntPtr)ShowCommand, "Open HTPCAVRVolume");
                AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
                AppendMenu(menu, MF_STRING, (IntPtr)ExitCommand, "Exit");

                GetCursorPos(out POINT cursor);

                // Without this the menu refuses to close when the user clicks elsewhere.
                SetForegroundWindow(_window);
                TrackPopupMenuEx(menu, TPM_RIGHTALIGN | TPM_BOTTOMALIGN, cursor.X, cursor.Y, _window, IntPtr.Zero);
                PostMessage(_window, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                DestroyMenu(menu);
            }
        }

        #region Win32

        private const int NIM_ADD = 0x00000000;
        private const int NIM_DELETE = 0x00000002;
        private const int NIF_MESSAGE = 0x00000001;
        private const int NIF_ICON = 0x00000002;
        private const int NIF_TIP = 0x00000004;

        private const int WM_COMMAND = 0x0111;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_CONTEXTMENU = 0x007B;

        private const int MF_STRING = 0x00000000;
        private const int MF_SEPARATOR = 0x00000800;
        private const int TPM_RIGHTALIGN = 0x0008;
        private const int TPM_BOTTOMALIGN = 0x0020;

        private static readonly IntPtr HWND_MESSAGE = (IntPtr)(-3);

        private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr menu, int flags, IntPtr id, string item);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        private static extern bool TrackPopupMenuEx(IntPtr menu, int flags, int x, int y, IntPtr window, IntPtr parameters);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr ExtractIcon(IntPtr instance, string file, int index);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        #endregion
    }
}
