using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;

namespace HTPCAVRVolume.Ui
{
    /// <summary>
    /// A stand-in for the Windows 11 volume flyout, showing the AVR's level instead of a
    /// percentage Windows no longer controls.
    ///
    /// Windows does not let anyone write into its own flyout, and WinUI cannot draw a window with
    /// per-pixel transparency, so this is a plain Win32 layered window drawn with GDI+. Every
    /// measurement below was taken off the real Windows 11 flyout with a screen capture at 96 DPI,
    /// so the two land in the same place and read the same way. It never takes the focus or a
    /// click, so it can appear over a player without interrupting it.
    /// </summary>
    sealed class VolumeFlyout : IDisposable
    {
        // Measured off the Windows 11 flyout, in pixels at 96 DPI.
        private const float PillHeight = 47f;
        private const float CornerRadius = 8f;
        private const float GlyphLeft = 13f;
        private const float GlyphWidth = 16f;
        private const float IconSize = 16f;
        private const float BarLeft = 42f;
        private const float BarWidth = 110f;
        private const float BarTop = 21f;
        private const float BarHeight = 4f;
        private const float NumberAreaWidth = 39f;
        private const float TextSize = 14f;
        private const float BottomGap = 14f;
        private const float ShadowMargin = 6f;

        private const int FadeDurationMs = 160;
        private const int FadeTickMs = 15;

        private readonly DispatcherQueueTimer _holdTimer;
        private readonly DispatcherQueueTimer _fadeTimer;
        private readonly WndProc _wndProc;
        private readonly string _className;

        private IntPtr _window;
        private Bitmap _surface;
        private Graphics _measurer;
        private Bitmap _measurerOwner;
        private bool _visible;

        private string _text = string.Empty;
        private double? _fraction;
        private bool _muted;
        private int _alpha = 255;
        private int _left;
        private int _top;
        private int _width;
        private int _height;
        private float _scale = 1f;

        private Color _pill;
        private Color _foreground;
        private Color _track;
        private Color _fill;

        public VolumeFlyout(DispatcherQueue dispatcher)
        {
            _className = "HTPCAVRVolumeFlyout";
            _wndProc = OnMessage;

            WNDCLASSEX wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandle(null),
                lpszClassName = _className
            };

            RegisterClassEx(ref wc);

            _window = CreateWindowEx(
                WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
                _className, null, WS_POPUP,
                0, 0, 10, 10,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            _holdTimer = dispatcher.CreateTimer();
            _holdTimer.IsRepeating = false;
            _holdTimer.Tick += (sender, e) => BeginFade();

            _fadeTimer = dispatcher.CreateTimer();
            _fadeTimer.Interval = TimeSpan.FromMilliseconds(FadeTickMs);
            _fadeTimer.IsRepeating = true;
            _fadeTimer.Tick += (sender, e) => StepFade();

            ReadTheme();
        }

        /// <summary>How long the flyout stays up after the last change. Windows uses about 1.8 s.</summary>
        public int DurationMs { get; set; } = 1800;

        /// <summary>
        /// Builds the window and warms up the drawing before anyone is waiting on it, so the first
        /// key press does not pay for it.
        /// </summary>
        public void Prepare()
        {
            Arrange("100", null, false);
            RenderSurface();
        }

        /// <param name="text">What to show as the level, already formatted.</param>
        /// <param name="fraction">Position within the AVR's range, or null when we do not know it.</param>
        public void Display(string text, double? fraction, bool muted)
        {
            if (_window == IntPtr.Zero)
            {
                return;
            }

            _fadeTimer.Stop();
            _holdTimer.Stop();
            _alpha = 255;

            ReadTheme();
            Arrange(text, fraction, muted);
            RenderSurface();

            if (!_visible)
            {
                ShowWindow(_window, SW_SHOWNOACTIVATE);
                _visible = true;
            }

            Push();

            _holdTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(200, DurationMs));
            _holdTimer.Start();
        }

        public void HideNow()
        {
            _holdTimer.Stop();
            _fadeTimer.Stop();
            Hide();
        }

        public void Dispose()
        {
            _holdTimer.Stop();
            _fadeTimer.Stop();

            if (_window != IntPtr.Zero)
            {
                DestroyWindow(_window);
                _window = IntPtr.Zero;
            }

            _surface?.Dispose();
            _measurer?.Dispose();
            _measurerOwner?.Dispose();
        }

        private void Hide()
        {
            if (_visible)
            {
                ShowWindow(_window, SW_HIDE);
                _visible = false;
            }
        }

        private void BeginFade()
        {
            _holdTimer.Stop();
            _fadeTimer.Start();
        }

        private void StepFade()
        {
            _alpha -= Math.Max(1, 255 * FadeTickMs / FadeDurationMs);
            if (_alpha <= 0)
            {
                _alpha = 0;
                _fadeTimer.Stop();
                Hide();
                return;
            }

            // Only the constant alpha changes while fading, so the content is pushed again as is.
            Push();
        }

        /// <summary>
        /// Sizes the window around the text and puts it where Windows puts its own: centred on the
        /// primary screen, a fixed gap above the taskbar.
        /// </summary>
        private void Arrange(string text, double? fraction, bool muted)
        {
            _text = text ?? string.Empty;
            _fraction = fraction;
            _muted = muted;

            _scale = DpiScale();
            float scale = _scale;

            // Windows keeps a fixed area for a number of up to three digits. A level such as
            // "-32,5 dB" does not fit in it, so the area grows and the pill with it, rather than
            // the text being squeezed or clipped.
            float number;
            using (Font font = TextFont(TextSize * scale))
            {
                number = Math.Max(NumberAreaWidth * scale, Measure(_text, font) + 8 * scale);
            }

            int pillWidth = (int)Math.Round((BarLeft + BarWidth) * scale + number);
            int pillHeight = (int)Math.Round(PillHeight * scale);
            int margin = (int)Math.Round(ShadowMargin * scale);

            RECT work = WorkArea();
            _width = pillWidth + margin * 2;
            _height = pillHeight + margin * 2;
            _left = work.Left + (work.Right - work.Left - _width) / 2;
            _top = work.Bottom - (int)Math.Round(BottomGap * scale) - pillHeight - margin;
        }

        private float DpiScale()
        {
            uint dpi = _window != IntPtr.Zero ? GetDpiForWindow(_window) : 96;
            return dpi <= 0 ? 1f : dpi / 96f;
        }

        private static RECT WorkArea()
        {
            RECT area;
            if (SystemParametersInfo(SPI_GETWORKAREA, 0, out area, 0))
            {
                return area;
            }

            return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        }

        private void RenderSurface()
        {
            if (_width <= 0 || _height <= 0)
            {
                return;
            }

            Bitmap previous = _surface;
            Bitmap bitmap = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                Render(g);
            }

            _surface = bitmap;
            previous?.Dispose();
        }

        private void Render(Graphics g)
        {
            float scale = _scale;
            float margin = ShadowMargin * scale;
            float radius = CornerRadius * scale;
            RectangleF pill = new RectangleF(margin, margin, _width - margin * 2, _height - margin * 2);

            // The real flyout sits on a short, soft shadow rather than a hard outline.
            for (int ring = 3; ring >= 1; ring--)
            {
                RectangleF spread = RectangleF.Inflate(pill, ring * scale, ring * scale);
                using (GraphicsPath path = RoundedRectangle(spread, radius + ring * scale))
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(ring == 1 ? 56 : ring == 2 ? 26 : 12, 0, 0, 0)))
                {
                    g.FillPath(brush, path);
                }
            }

            using (GraphicsPath path = RoundedRectangle(pill, radius))
            using (SolidBrush brush = new SolidBrush(_pill))
            {
                g.FillPath(brush, path);
            }

            DrawGlyph(g, pill, scale);
            DrawBar(g, pill, scale);
            DrawNumber(g, pill, scale);
        }

        private void DrawGlyph(Graphics g, RectangleF pill, float scale)
        {
            using (Font font = IconFont(IconSize * scale))
            using (SolidBrush brush = new SolidBrush(_foreground))
            using (StringFormat format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            })
            {
                RectangleF box = new RectangleF(pill.X + GlyphLeft * scale, pill.Y, GlyphWidth * scale, pill.Height);
                g.DrawString(Glyph(), font, brush, box, format);
            }
        }

        private string Glyph()
        {
            // Windows shows the bare speaker cone when muted, and adds waves with the level.
            if (_muted || !_fraction.HasValue || _fraction.Value <= 0.001)
            {
                return "";
            }

            return _fraction.Value < 0.33 ? "" : _fraction.Value < 0.66 ? "" : "";
        }

        private void DrawBar(Graphics g, RectangleF pill, float scale)
        {
            RectangleF track = new RectangleF(
                pill.X + BarLeft * scale,
                pill.Y + BarTop * scale,
                BarWidth * scale,
                BarHeight * scale);

            float radius = track.Height / 2f;
            using (GraphicsPath path = RoundedRectangle(track, radius))
            using (SolidBrush brush = new SolidBrush(_track))
            {
                g.FillPath(brush, path);
            }

            double fraction = _fraction ?? 0;
            if (fraction <= 0)
            {
                return;
            }

            float width = (float)(track.Width * Math.Min(1, fraction));
            if (width < track.Height)
            {
                width = track.Height;
            }

            using (GraphicsPath path = RoundedRectangle(new RectangleF(track.X, track.Y, width, track.Height), radius))
            using (SolidBrush brush = new SolidBrush(_fill))
            {
                g.FillPath(brush, path);
            }
        }

        private void DrawNumber(Graphics g, RectangleF pill, float scale)
        {
            float left = pill.X + (BarLeft + BarWidth) * scale;

            using (Font font = TextFont(TextSize * scale))
            using (SolidBrush brush = new SolidBrush(_foreground))
            using (StringFormat format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            })
            {
                g.DrawString(_text, font, brush, new RectangleF(left, pill.Y, pill.Right - left, pill.Height), format);
            }
        }

        private float Measure(string text, Font font)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            if (_measurer == null)
            {
                _measurerOwner = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
                _measurer = Graphics.FromImage(_measurerOwner);
                _measurer.TextRenderingHint = TextRenderingHint.AntiAlias;
            }

            return _measurer.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;
        }

        private void ReadTheme()
        {
            bool light = false;
            try
            {
                object value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme",
                    0);
                light = value is int state && state != 0;
            }
            catch
            {
                // A missing or unreadable key just means the dark flyout, same as Windows' default.
            }

            if (light)
            {
                _pill = Color.FromArgb(255, 243, 243, 243);
                _foreground = Color.FromArgb(255, 26, 26, 26);
                _track = Color.FromArgb(114, 0, 0, 0);
            }
            else
            {
                // Measured #2C2C2C, and a track of white at 54%.
                _pill = Color.FromArgb(255, 44, 44, 44);
                _foreground = Color.FromArgb(255, 255, 255, 255);
                _track = Color.FromArgb(139, 255, 255, 255);
            }

            _fill = AccentColor(light);
        }

        /// <summary>
        /// The flyout's bar does not use the raw accent colour but the shade Windows keeps for UI
        /// on a dark background (and a darker one on a light background), which is what the accent
        /// palette holds.
        /// </summary>
        private static Color AccentColor(bool light)
        {
            try
            {
                byte[] palette = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent",
                    "AccentPalette",
                    null) as byte[];

                int index = (light ? 4 : 1) * 4;
                if (palette != null && palette.Length >= index + 3)
                {
                    return Color.FromArgb(255, palette[index], palette[index + 1], palette[index + 2]);
                }

                if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr)
                {
                    // Stored as ABGR rather than ARGB.
                    return Color.FromArgb(255, abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
                }
            }
            catch
            {
                // Fall through to the default.
            }

            return light ? Color.FromArgb(255, 0, 95, 184) : Color.FromArgb(255, 76, 194, 255);
        }

        private static Font IconFont(float size)
        {
            return FirstAvailableFont(size, "Segoe Fluent Icons", "Segoe MDL2 Assets");
        }

        private static Font TextFont(float size)
        {
            return FirstAvailableFont(size, "Segoe UI Variable Text", "Segoe UI Variable Display", "Segoe UI");
        }

        private static Font FirstAvailableFont(float size, params string[] families)
        {
            foreach (string family in families)
            {
                try
                {
                    Font font = new Font(family, size, FontStyle.Regular, GraphicsUnit.Pixel);

                    // GDI+ quietly substitutes a default face when the family is missing.
                    if (string.Equals(font.Name, family, StringComparison.OrdinalIgnoreCase))
                    {
                        return font;
                    }

                    font.Dispose();
                }
                catch
                {
                    // Try the next one.
                }
            }

            return new Font(FontFamily.GenericSansSerif, size, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            if (diameter <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }

            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void Push()
        {
            if (_window == IntPtr.Zero || _surface == null)
            {
                return;
            }

            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memoryDc = CreateCompatibleDC(screenDc);
            IntPtr bitmap = IntPtr.Zero;
            IntPtr previous = IntPtr.Zero;

            try
            {
                bitmap = CreatePremultipliedBitmap(_surface);
                if (bitmap == IntPtr.Zero)
                {
                    return;
                }

                previous = SelectObject(memoryDc, bitmap);

                POINT position = new POINT { X = _left, Y = _top };
                SIZE size = new SIZE { Width = _surface.Width, Height = _surface.Height };
                POINT source = new POINT { X = 0, Y = 0 };
                BLENDFUNCTION blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = (byte)_alpha,
                    AlphaFormat = AC_SRC_ALPHA
                };

                UpdateLayeredWindow(_window, screenDc, ref position, ref size, memoryDc, ref source, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
                if (bitmap != IntPtr.Zero)
                {
                    SelectObject(memoryDc, previous);
                    DeleteObject(bitmap);
                }

                DeleteDC(memoryDc);
            }
        }

        /// <summary>
        /// Hands UpdateLayeredWindow what it expects: a top-down 32 bit DIB whose colour channels
        /// are already multiplied by the alpha.
        /// </summary>
        private static IntPtr CreatePremultipliedBitmap(Bitmap source)
        {
            int width = source.Width, height = source.Height;

            BITMAPINFO info = new BITMAPINFO
            {
                biSize = 40,   // sizeof(BITMAPINFOHEADER), not of the struct with its colour slot
                biWidth = width,
                biHeight = -height,   // negative: rows top to bottom, like the GDI+ bitmap
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            };

            IntPtr dib = CreateDIBSection(IntPtr.Zero, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            BitmapData data = source.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int[] row = new int[width];
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, width);

                    for (int x = 0; x < width; x++)
                    {
                        int pixel = row[x];
                        int a = (pixel >> 24) & 0xFF;
                        if (a == 0)
                        {
                            row[x] = 0;
                        }
                        else if (a != 255)
                        {
                            int r = ((pixel >> 16) & 0xFF) * a / 255;
                            int g = ((pixel >> 8) & 0xFF) * a / 255;
                            int b = (pixel & 0xFF) * a / 255;
                            row[x] = (a << 24) | (r << 16) | (g << 8) | b;
                        }
                    }

                    Marshal.Copy(row, 0, bits + y * width * 4, width);
                }
            }
            finally
            {
                source.UnlockBits(data);
            }

            return dib;
        }

        private IntPtr OnMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            return DefWindowProc(window, message, wParam, lParam);
        }

        #region Win32

        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOPMOST = 0x00000008;

        private const int SW_HIDE = 0;
        private const int SW_SHOWNOACTIVATE = 4;

        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const int ULW_ALPHA = 0x00000002;

        private const uint SPI_GETWORKAREA = 0x0030;

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

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int Width, Height; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
            public int colours;
        }

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
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint param, out RECT value, uint update);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc, ref POINT destination,
            ref SIZE size, IntPtr sourceDc, ref POINT source, int key, ref BLENDFUNCTION blend, int flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage,
            out IntPtr bits, IntPtr section, uint offset);

        #endregion
    }
}
