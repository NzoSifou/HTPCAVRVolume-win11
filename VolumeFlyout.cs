using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HTPCAVRVolume
{
    /// <summary>
    /// A stand-in for the Windows 11 volume flyout, showing the AVR's level instead of a
    /// percentage Windows no longer controls.
    ///
    /// Windows does not let anyone write into its own flyout, so this is our own window. Every
    /// measurement below was taken off the real one with a screen capture at 96 DPI, so the two
    /// land in the same place and read the same way. It is a layered window drawn per pixel,
    /// which is what gives it antialiased rounded corners, a soft edge and a clean fade, and it
    /// never takes the focus or a click, so it can appear over a player without interrupting it.
    /// </summary>
    class VolumeFlyout : Form
    {
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const int ULW_ALPHA = 0x00000002;

        private const int FadeDurationMs = 160;
        private const int FadeTickMs = 15;

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

        private readonly Timer _holdTimer = new Timer();
        private readonly Timer _fadeTimer = new Timer { Interval = FadeTickMs };

        private string _text = string.Empty;
        private double? _fraction;
        private bool _muted;
        private int _alpha = 255;
        private Bitmap _surface;
        private Graphics _measurer;
        private Bitmap _measurerOwner;

        private Color _pill;
        private Color _foreground;
        private Color _track;
        private Color _fill;

        public VolumeFlyout()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;

            _holdTimer.Tick += (sender, e) => BeginFade();
            _fadeTimer.Tick += (sender, e) => StepFade();

            ReadTheme();
        }

        /// <summary>How long the flyout stays up after the last change. Windows uses about 1.8 s.</summary>
        public int DurationMs { get; set; } = 1800;

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        /// <summary>
        /// Builds the window and warms up the drawing before anyone is waiting on it. Without
        /// this the first key press pays for creating a window from inside the keyboard hook,
        /// and the ticks arriving meanwhile are lost.
        /// </summary>
        public void Prepare()
        {
            GC.KeepAlive(Handle);
            Arrange("100", null, false);
            RenderSurface();
        }

        /// <param name="text">What to show as the level, already formatted.</param>
        /// <param name="fraction">Position within the AVR's range, or null when we do not know it.</param>
        public void Display(string text, double? fraction, bool muted)
        {
            _fadeTimer.Stop();
            _holdTimer.Stop();
            _alpha = 255;

            ReadTheme();
            Arrange(text, fraction, muted);
            RenderSurface();

            if (!Visible)
            {
                Show();
            }

            Push();

            _holdTimer.Interval = Math.Max(200, DurationMs);
            _holdTimer.Start();
        }

        public void HideNow()
        {
            _holdTimer.Stop();
            _fadeTimer.Stop();
            Hide();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // The content comes from UpdateLayeredWindow, not from the normal paint path.
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _holdTimer.Dispose();
                _fadeTimer.Dispose();
                _surface?.Dispose();
                _measurer?.Dispose();
                _measurerOwner?.Dispose();
            }

            base.Dispose(disposing);
        }

        private float DpiScale => DeviceDpi / 96f;

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
        /// Sizes the window around the text and puts it where Windows puts its own: centred on
        /// the primary screen, a fixed gap above the taskbar.
        /// </summary>
        private void Arrange(string text, double? fraction, bool muted)
        {
            _text = text ?? string.Empty;
            _fraction = fraction;
            _muted = muted;

            float scale = DpiScale;

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

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(pillWidth + margin * 2, pillHeight + margin * 2);
            Location = new Point(
                area.Left + (area.Width - Width) / 2,
                area.Bottom - (int)Math.Round(BottomGap * scale) - pillHeight - margin);
        }

        private void RenderSurface()
        {
            if (Width <= 0 || Height <= 0)
            {
                return;
            }

            Bitmap previous = _surface;
            Bitmap bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                Render(g);
            }

            _surface = bitmap;
            previous?.Dispose();
        }

        private void Push()
        {
            if (IsHandleCreated && _surface != null)
            {
                PushBitmap(_surface, (byte)_alpha);
            }
        }

        private void Render(Graphics g)
        {
            float scale = DpiScale;
            float margin = ShadowMargin * scale;
            float radius = CornerRadius * scale;
            RectangleF pill = new RectangleF(margin, margin, Width - margin * 2, Height - margin * 2);

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

            RectangleF filled = new RectangleF(track.X, track.Y, width, track.Height);
            using (GraphicsPath path = RoundedRectangle(filled, radius))
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
                RectangleF box = new RectangleF(left, pill.Y, pill.Right - left, pill.Height);
                g.DrawString(_text, font, brush, box, format);
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
                light = value is int && (int)value != 0;
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
        /// on a dark background (and a darker one on a light background), which is what the
        /// accent palette holds.
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

                object accent = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null);
                if (accent is int)
                {
                    // Stored as ABGR rather than ARGB.
                    int abgr = (int)accent;
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
                    if (string.Equals(font.Name, family, StringComparison.OrdinalIgnoreCase))
                    {
                        return font;
                    }

                    // GDI+ quietly substitutes a default face when the family is missing.
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

        private void PushBitmap(Bitmap bitmap, byte alpha)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memoryDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;

            try
            {
                hBitmap = CreatePremultipliedBitmap(bitmap);
                if (hBitmap == IntPtr.Zero)
                {
                    return;
                }

                oldBitmap = SelectObject(memoryDc, hBitmap);

                Size size = bitmap.Size;
                Point source = new Point(0, 0);
                Point position = Location;
                BLENDFUNCTION blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = alpha,
                    AlphaFormat = AC_SRC_ALPHA
                };

                UpdateLayeredWindow(Handle, screenDc, ref position, ref size, memoryDc, ref source, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
                if (hBitmap != IntPtr.Zero)
                {
                    SelectObject(memoryDc, oldBitmap);
                    DeleteObject(hBitmap);
                }

                DeleteDC(memoryDc);
            }
        }

        /// <summary>
        /// Hands UpdateLayeredWindow what it actually expects: a top-down 32 bit DIB whose colour
        /// channels are already multiplied by the alpha. Going through Bitmap.GetHbitmap instead
        /// produces a device dependent bitmap, and Windows then reads an alpha channel that is
        /// not the one we drew.
        /// </summary>
        private static IntPtr CreatePremultipliedBitmap(Bitmap source)
        {
            int width = source.Width, height = source.Height;

            BITMAPINFO info = new BITMAPINFO();
            info.biSize = 40;   // sizeof(BITMAPINFOHEADER), not of the struct with its colour slot
            info.biWidth = width;
            info.biHeight = -height;   // negative: rows top to bottom, like the GDI+ bitmap
            info.biPlanes = 1;
            info.biBitCount = 32;
            info.biCompression = 0;    // BI_RGB

            IntPtr bits;
            IntPtr dib = CreateDIBSection(IntPtr.Zero, ref info, 0, out bits, IntPtr.Zero, 0);
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

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO info, uint usage,
            out IntPtr bits, IntPtr section, uint offset);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize,
            IntPtr hdcSrc, ref Point pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }
}
