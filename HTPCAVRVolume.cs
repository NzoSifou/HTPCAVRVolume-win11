using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using HTPCAVRVolume.AVRDevices;

namespace HTPCAVRVolume
{
    public partial class HTPCAVRVolume : Form
    {
        /// <summary>A Denon reports 0.0 dB as 80, used before we have a device to ask.</summary>
        private const double AssumedZeroDecibelLevel = 80;

        /// <summary>Fallback for the slider's thumb width, when the control will not say.</summary>
        private const int SliderInset = 10;

        private const int TBM_GETTHUMBLENGTH = 0x0400 + 28;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        private readonly string _configPath = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location) + "\\HTPCAVRVolumeConfig.txt";

        private AppSettings _settings = new AppSettings();
        private readonly VolumeController _controller = new VolumeController();
        private VolumeFlyout _flyout;
        private GlobalKeyboardHook _globalHook;
        private IAVRDevice _device;

        private bool _updatingUi;
        private bool _maximumApplied;
        private bool _decibels = true;
        private bool _draggingSlider;

        public HTPCAVRVolume()
        {
            InitializeComponent();
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            notifyIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            string version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3).Replace(".0", "");
            Text = $"HTPCAVRVolume v{version}";
        }

        private void HTPCAVRVolume_Load(object sender, EventArgs e)
        {
            _flyout = new VolumeFlyout();

            // Creating a window costs a few dozen milliseconds. Paying that now rather than on
            // the first key press keeps the first turn of the wheel as responsive as the rest.
            _flyout.Prepare();

            ApplyButtonIcons();

            _settings = AppSettings.Load(_configPath);
            ShowSettings();
            ApplyConfiguration();

            _globalHook = new GlobalKeyboardHook();
            _globalHook.VolumeUpPressed += (s, args) => _controller.Nudge(1);
            _globalHook.VolumeDownPressed += (s, args) => _controller.Nudge(-1);
            _globalHook.VolumeMutePressed += (s, args) => _controller.ToggleMute();
        }

        /// <summary>Uses the system icon font for the two step buttons, and plain text without it.</summary>
        private void ApplyButtonIcons()
        {
            Font icons = IconFont(10F);
            if (icons == null)
            {
                return;
            }

            btnDown.Font = icons;
            btnDown.Text = "";
            btnUp.Font = icons;
            btnUp.Text = "";
        }

        private static Font IconFont(float size)
        {
            foreach (string family in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            {
                try
                {
                    Font font = new Font(family, size);

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

            return null;
        }

        /// <summary>Fills the window from the settings we loaded.</summary>
        private void ShowSettings()
        {
            _updatingUi = true;
            try
            {
                if (!string.IsNullOrEmpty(_settings.Device) && cmbDevice.Items.Contains(_settings.Device))
                {
                    cmbDevice.SelectedItem = _settings.Device;
                }

                tbIP.Text = _settings.Host;
                numStep.Value = Bound(numStep, (decimal)_settings.StepDecibels);
                chkShowOsd.Checked = _settings.ShowOsd;
                chkOsdRemote.Checked = _settings.OsdOnExternalChange;

                _decibels = _settings.OsdDecibels;
                cmbUnit.SelectedIndex = _decibels ? 0 : 1;
                ConfigureUnitBoxes();
                numMaxVolume.Value = Bound(numMaxVolume, (decimal)ToDisplay(_settings.MaxVolume));
            }
            finally
            {
                _updatingUi = false;
            }
        }

        /// <summary>
        /// Volumes are shown either in dB, as the AVR's own display does, or in the 0 to 98 the
        /// protocol speaks. The two differ by an offset, which is why a step is the same number
        /// in both and only the levels need converting.
        /// </summary>
        private void CmbUnit_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_updatingUi)
            {
                return;
            }

            double maximum = FromDisplay((double)numMaxVolume.Value);
            _decibels = cmbUnit.SelectedIndex == 0;
            _settings.OsdDecibels = _decibels;

            _updatingUi = true;
            try
            {
                ConfigureUnitBoxes();
                numMaxVolume.Value = Bound(numMaxVolume, (decimal)ToDisplay(maximum));
            }
            finally
            {
                _updatingUi = false;
            }

            ShowVolume();
        }

        private void ConfigureUnitBoxes()
        {
            if (_decibels)
            {
                lblStep.Text = "Step (dB):";
                lblMaxVolume.Text = "Max volume (dB):";
                numMaxVolume.Minimum = -79;
                numMaxVolume.Maximum = 18;
            }
            else
            {
                lblStep.Text = "Step:";
                lblMaxVolume.Text = "Max volume:";
                numMaxVolume.Maximum = 98;
                numMaxVolume.Minimum = 1;
            }
        }

        private double ToDisplay(double volume)
        {
            return _decibels ? ToDecibels(volume) : volume;
        }

        private double FromDisplay(double shown)
        {
            return _decibels ? FromDecibels(shown) : shown;
        }

        private double ToDecibels(double volume)
        {
            return _device != null ? _device.ToDecibels(volume) : volume - AssumedZeroDecibelLevel;
        }

        private double FromDecibels(double decibels)
        {
            return _device != null ? _device.FromDecibels(decibels) : decibels + AssumedZeroDecibelLevel;
        }

        private static decimal Bound(NumericUpDown control, decimal value)
        {
            return value < control.Minimum ? control.Minimum : value > control.Maximum ? control.Maximum : value;
        }

        private static int Bound(TrackBar control, int value)
        {
            return value < control.Minimum ? control.Minimum : value > control.Maximum ? control.Maximum : value;
        }

        /// <summary>Rebuilds the link to the AVR from the current settings.</summary>
        private void ApplyConfiguration()
        {
            _controller.Device = null;
            _maximumApplied = false;

            if (_device != null)
            {
                _device.StatusChanged -= OnDeviceStatus;
                _device.LinkChanged -= OnDeviceLink;
                _device.Dispose();
                _device = null;
            }

            _controller.StepDecibels = _settings.StepDecibels;
            _controller.MaxVolume = _settings.MaxVolume;
            _controller.FlushIntervalMs = _settings.FlushIntervalMs;
            _flyout.DurationMs = _settings.OsdDurationMs;
            ConfigureSlider();

            if (!_settings.IsConfigured)
            {
                lblStatus.Text = "Pick a device, type its IP address, then Save.";
                return;
            }

            switch (_settings.Device)
            {
                case "DenonMarantz":
                    _device = new DenonMarantzDevice(_settings.Host, _settings.MinCommandIntervalMs);
                    break;
                case "StormAudio":
                    _device = new StormAudioDevice(_settings.Host, _settings.MinCommandIntervalMs);
                    break;
                default:
                    lblStatus.Text = "Unknown device \"" + _settings.Device + "\".";
                    return;
            }

            _device.StatusChanged += OnDeviceStatus;
            _device.LinkChanged += OnDeviceLink;
            _controller.Device = _device;

            // The link starts connecting inside the constructor, so its first transition may
            // already be behind us by now.
            UpdateStatus(null);
            ShowVolume();
        }

        private void OnDeviceLink(object sender, LinkEventArgs e)
        {
            RunOnUiThread(() =>
            {
                UpdateStatus(e.Detail);
                ShowVolume();
            });
        }

        private void OnDeviceStatus(object sender, AvrStatusEventArgs e)
        {
            RunOnUiThread(() =>
            {
                UpdateStatus(null);
                ShowReportedMaximum();
                ShowVolume();

                if (!_settings.ShowOsd || _device == null)
                {
                    return;
                }

                if (e.External && !_settings.OsdOnExternalChange)
                {
                    return;
                }

                // A volume change we cannot put a number on is not worth a flyout: that is what a
                // dropped link looks like.
                bool haveSomethingToShow = e.MuteChanged || (e.VolumeChanged && _device.Volume.HasValue);
                if (haveSomethingToShow)
                {
                    ShowFlyout();
                }
            });
        }

        /// <summary>
        /// Device events arrive either on the link's worker thread, where the window is not ours
        /// to touch, or from inside the keyboard hook, where every millisecond spent is a
        /// millisecond the whole system's input queue is held up. Both cases want the same thing:
        /// post the work and let the message loop pick it up once the hook has returned.
        /// </summary>
        private void RunOnUiThread(Action action)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            try
            {
                BeginInvoke(action);
            }
            catch (ObjectDisposedException)
            {
                // The window went away while the link was still talking to us.
            }
        }

        private void ShowFlyout()
        {
            _flyout.Display(FormatLevel(), LevelFraction(), _device.Muted);
        }

        private string FormatLevel()
        {
            if (_device == null || !_device.Volume.HasValue)
            {
                return "--";
            }

            double level = _device.Volume.Value;
            return _decibels
                ? ToDecibels(level).ToString("0.0", CultureInfo.CurrentCulture) + " dB"
                : level.ToString("0.#", CultureInfo.CurrentCulture);
        }

        private double? LevelFraction()
        {
            if (_device == null || !_device.Volume.HasValue)
            {
                return null;
            }

            // Deliberately the setting rather than whatever the AVR last said: the display's
            // scale has to stay put for the whole session, otherwise the bar redraws itself to a
            // different length while the user is still turning the wheel.
            double max = _settings.MaxVolume;
            return max > 0 ? _device.Volume.Value / max : (double?)null;
        }

        /// <summary>
        /// Takes the maximum the AVR reports, once per run, and leaves it alone afterwards. A
        /// maximum the user typed themselves is never overwritten.
        /// </summary>
        private void ShowReportedMaximum()
        {
            if (_maximumApplied || _settings.MaxVolumeIsManual || _device == null || !_device.MaxVolume.HasValue)
            {
                return;
            }

            _maximumApplied = true;
            _settings.MaxVolume = _device.MaxVolume.Value;
            _controller.MaxVolume = _settings.MaxVolume;

            _updatingUi = true;
            try
            {
                numMaxVolume.Value = Bound(numMaxVolume, (decimal)ToDisplay(_settings.MaxVolume));
            }
            finally
            {
                _updatingUi = false;
            }

            ConfigureSlider();
        }

        private void NumMaxVolume_ValueChanged(object sender, EventArgs e)
        {
            if (_updatingUi)
            {
                return;
            }

            // Typed by hand: from here on the AVR's answer no longer has a say.
            _settings.MaxVolumeIsManual = true;

            // Applied at once rather than on Save: a slider that still runs to the old maximum
            // while the box shows a new one is a lie about what the app will do.
            _settings.MaxVolume = FromDisplay((double)numMaxVolume.Value);
            _controller.MaxVolume = _settings.MaxVolume;
            ConfigureSlider();
        }

        private void NumStep_ValueChanged(object sender, EventArgs e)
        {
            if (_updatingUi)
            {
                return;
            }

            _settings.StepDecibels = (double)numStep.Value;
            _controller.StepDecibels = _settings.StepDecibels;
            ConfigureSlider();
        }

        private double StepSize
        {
            get { return Math.Max(0.1, _settings.StepDecibels); }
        }

        /// <summary>
        /// The slider moves in whole steps rather than in AVR units, so dragging it lands on the
        /// same levels the buttons and the volume keys produce.
        /// </summary>
        private void ConfigureSlider()
        {
            double max = _settings.MaxVolume > 0 ? _settings.MaxVolume : 98;
            int positions = Math.Max(1, (int)Math.Round(max / StepSize));

            _updatingUi = true;
            try
            {
                trkVolume.Minimum = 0;
                trkVolume.Maximum = positions;
                trkVolume.SmallChange = 1;
                trkVolume.LargeChange = 1;
                trkVolume.TickFrequency = positions;
            }
            finally
            {
                _updatingUi = false;
            }

            ShowVolume();
        }

        /// <summary>Puts the level under the slider's thumb and above it in words.</summary>
        private void ShowVolume()
        {
            lblVolume.Text = FormatLevel();
            trkVolume.Enabled = _device != null && _device.SupportsAbsoluteVolume;

            if (_draggingSlider || _device == null || !_device.Volume.HasValue)
            {
                return;
            }

            int position = Bound(trkVolume, (int)Math.Round(_device.Volume.Value / StepSize));
            if (trkVolume.Value == position)
            {
                return;
            }

            _updatingUi = true;
            try
            {
                trkVolume.Value = position;
            }
            finally
            {
                _updatingUi = false;
            }
        }

        private void TrkVolume_ValueChanged(object sender, EventArgs e)
        {
            if (_updatingUi)
            {
                return;
            }

            _controller.SetVolume(trkVolume.Value * StepSize);
        }

        private void TrkVolume_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            _draggingSlider = true;
            SlideTo(e.X);
        }

        private void TrkVolume_MouseMove(object sender, MouseEventArgs e)
        {
            if (_draggingSlider && e.Button == MouseButtons.Left)
            {
                SlideTo(e.X);
            }
        }

        private void TrkVolume_MouseUp(object sender, MouseEventArgs e)
        {
            _draggingSlider = false;
            ShowVolume();
        }

        /// <summary>A click anywhere on the bar goes there, rather than creeping one step at a time.</summary>
        private void SlideTo(int x)
        {
            // The thumb travels between its own half widths, so the level under the cursor is not
            // simply the fraction of the control's width. The control knows how wide its thumb is.
            int thumb = (int)SendMessage(trkVolume.Handle, TBM_GETTHUMBLENGTH, IntPtr.Zero, IntPtr.Zero);
            if (thumb <= 0)
            {
                thumb = SliderInset * 2;
            }

            double usable = trkVolume.Width - thumb;
            if (usable <= 0)
            {
                return;
            }

            double fraction = (x - thumb / 2.0) / usable;
            fraction = fraction < 0 ? 0 : fraction > 1 ? 1 : fraction;

            int position = trkVolume.Minimum + (int)Math.Round(fraction * (trkVolume.Maximum - trkVolume.Minimum));
            if (trkVolume.Value != position)
            {
                trkVolume.Value = position;
            }
        }

        private void UpdateStatus(string detail)
        {
            if (_device == null)
            {
                return;
            }

            switch (_device.Link)
            {
                case AvrLinkState.Connected:
                    lblStatus.Text = _device.Muted ? "Connected - muted" : "Connected";
                    break;
                case AvrLinkState.Connecting:
                    lblStatus.Text = detail ?? "Connecting...";
                    break;
                default:
                    lblStatus.Text = string.IsNullOrEmpty(detail)
                        ? "Disconnected - retrying"
                        : "Disconnected - " + detail;
                    break;
            }
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            _settings.Device = cmbDevice.SelectedItem?.ToString() ?? string.Empty;
            _settings.Host = tbIP.Text.Trim();
            _settings.StepDecibels = (double)numStep.Value;
            _settings.MaxVolume = FromDisplay((double)numMaxVolume.Value);
            _settings.ShowOsd = chkShowOsd.Checked;
            _settings.OsdDecibels = _decibels;
            _settings.OsdOnExternalChange = chkOsdRemote.Checked;

            try
            {
                _settings.Save(_configPath);
            }
            catch
            {
                MessageBox.Show("Try running as Administrator and saving your config again.", "Error saving config");
            }

            ApplyConfiguration();
        }

        private void BtnVolUp_Click(object sender, EventArgs e)
        {
            _controller.Nudge(1);
        }

        private void BtnVolDown_Click(object sender, EventArgs e)
        {
            _controller.Nudge(-1);
        }

        private void BtnToggleMute_Click(object sender, EventArgs e)
        {
            _controller.ToggleMute();
        }

        private void HTPCAVRVolume_FormClosed(object sender, FormClosedEventArgs e)
        {
            _globalHook?.Dispose();
            _controller.Dispose();
            _device?.Dispose();
            _flyout?.Dispose();
            notifyIcon.Visible = false;
        }

        private void NotifyIcon_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            Show();
            WindowState = FormWindowState.Normal;
        }

        private void HTPCAVRVolume_Resize(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized)
            {
                notifyIcon.Visible = true;
                ShowInTaskbar = false;  // window hidden in the taskbar, but still active.
            }
            else if (WindowState == FormWindowState.Normal)
            {
                notifyIcon.Visible = false;
                ShowInTaskbar = true;
            }
        }

        private void HTPCAVRVolume_Shown(object sender, EventArgs e)
        {
            if (_settings.IsConfigured)
            {
                WindowState = FormWindowState.Minimized;
            }
        }
    }
}
