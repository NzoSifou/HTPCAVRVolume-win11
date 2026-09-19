using System;
using System.Globalization;
using System.IO;
using HTPCAVRVolume.AVRDevices;
using HTPCAVRVolume.Ui;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace HTPCAVRVolume
{
    public sealed partial class MainWindow : Window
    {
        /// <summary>A Denon reports 0.0 dB as 80, used before we have a device to ask.</summary>
        private const double AssumedZeroDecibelLevel = 80;

        private const int WindowWidth = 560;
        private const int MinimumWindowHeight = 480;

        /// <summary>Title bar and window chrome the content does not know about.</summary>
        private const int TitleBarAllowance = 40;

        private readonly DispatcherQueue _dispatcher;
        private readonly DispatcherQueueTimer _saveTimer;
        private readonly string _configPath;

        private AppSettings _settings = new AppSettings();
        private VolumeController _controller;
        private VolumeFlyout _flyout;
        private GlobalKeyboardHook _hook;
        private TrayIcon _tray;
        private AudioKeepAlive _audio;
        private IAVRDevice _device;
        private AppWindow _appWindow;

        private bool _updatingUi;
        private bool _maximumApplied;
        private bool _decibels = true;
        private bool _exiting;

        public MainWindow()
        {
            InitializeComponent();

            _dispatcher = DispatcherQueue.GetForCurrentThread();

            // Next to the .exe, not next to AppContext.BaseDirectory: a single-file build runs
            // from a temp extraction folder, and the settings belong with the app the user moved.
            _configPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "HTPCAVRVolumeConfig.txt");

            _saveTimer = _dispatcher.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (sender, e) => SaveNow();
        }

        /// <summary>Brings everything up. Called once, from the application's launch.</summary>
        public void Start()
        {
            Title = "HTPCAVRVolume";

            IntPtr handle = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(handle));
            _appWindow.Closing += OnClosing;

            try
            {
                _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "HTPCAVRVolume.ico"));
            }
            catch
            {
                // The window simply keeps the default icon.
            }

            _appWindow.Resize(new SizeInt32(WindowWidth, MinimumWindowHeight));
            Root.Loaded += (sender, e) => FitToContent();

            _controller = new VolumeController(_dispatcher);
            _flyout = new VolumeFlyout(_dispatcher);
            _flyout.Prepare();

            _audio = new AudioKeepAlive(status => _dispatcher.TryEnqueue(() => ShowAudioStatus(status)));

            _tray = new TrayIcon("HTPCAVRVolume");
            _tray.Activated += (sender, e) => _dispatcher.TryEnqueue(ShowWindow);
            _tray.ExitRequested += (sender, e) => _dispatcher.TryEnqueue(Quit);

            _settings = AppSettings.Load(_configPath);
            ShowSettings();
            ApplyConfiguration();

            _audio.Enable(_settings.KeepAudioAlive);

            _hook = new GlobalKeyboardHook();
            _hook.VolumeUpPressed += (sender, e) => _controller.Nudge(1);
            _hook.VolumeDownPressed += (sender, e) => _controller.Nudge(-1);
            _hook.VolumeMutePressed += (sender, e) => _controller.ToggleMute();

            // Configured means the app already knows what to talk to: it belongs in the
            // notification area, not in the user's face.
            if (_settings.IsConfigured && _tray.IsVisible)
            {
                _appWindow.Hide();
                ReleaseWhatWeCan();
            }
            else
            {
                Activate();
            }
        }

        /// <summary>
        /// Sizes the window to what it actually contains, so both groups are visible at rest
        /// rather than behind a scrollbar, and clamps it to the screen it sits on.
        /// </summary>
        private void FitToContent()
        {
            double scale = Root.XamlRoot?.RasterizationScale ?? 1;

            ContentPanel.Measure(new Windows.Foundation.Size(WindowWidth / scale, double.PositiveInfinity));
            Footer.Measure(new Windows.Foundation.Size(WindowWidth / scale, double.PositiveInfinity));

            double needed = ContentPanel.DesiredSize.Height + Footer.DesiredSize.Height;
            int height = (int)Math.Round(needed * scale) + TitleBarAllowance;

            DisplayArea display = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
            int ceiling = display.WorkArea.Height - 80;
            if (height > ceiling)
            {
                height = ceiling;
            }

            if (height < MinimumWindowHeight)
            {
                height = MinimumWindowHeight;
            }

            _appWindow.Resize(new SizeInt32(WindowWidth, height));
        }

        private void ShowWindow()
        {
            _appWindow.Show();
            _appWindow.MoveInZOrderAtTop();
            Activate();
        }

        private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_exiting)
            {
                return;
            }

            if (_tray == null || !_tray.IsVisible)
            {
                // Without an icon in the notification area there would be no way back in.
                Quit();
                return;
            }

            // Closing the window only puts the app away; the volume keys keep working.
            args.Cancel = true;
            _appWindow.Hide();
            ReleaseWhatWeCan();
        }

        /// <summary>
        /// Hands back what the window was using while nobody is looking at it. WinUI itself never
        /// gives its memory back, but a collection and a working-set trim move the pages the app
        /// is no longer touching out of physical memory, which is what a background utility
        /// should be costing between two key presses.
        /// </summary>
        private void ReleaseWhatWeCan()
        {
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);

            try
            {
                SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
            }
            catch
            {
                // A refused trim costs nothing but the memory we hoped to give back.
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

        private void Quit()
        {
            _exiting = true;

            SaveNow();
            _hook?.Dispose();
            _audio?.Dispose();
            _controller?.Dispose();
            _device?.Dispose();
            _flyout?.Dispose();
            _tray?.Dispose();

            Close();
            Application.Current.Exit();
        }

        #region Settings and the link

        /// <summary>Fills the window from the settings we loaded.</summary>
        private void ShowSettings()
        {
            _updatingUi = true;
            try
            {
                DeviceBox.SelectedIndex = _settings.Device == "StormAudio" ? 1 : _settings.Device == "DenonMarantz" ? 0 : -1;
                HostBox.Text = _settings.Host;
                StepBox.Value = _settings.StepDecibels;
                OverlaySwitch.IsOn = _settings.ShowOsd;
                RemoteSwitch.IsOn = _settings.OsdOnExternalChange;

                AutoReconnectSwitch.IsOn = _settings.AutoReconnect;
                AttemptsBox.Value = _settings.ReconnectAttempts;
                TimeoutBox.Value = _settings.ReconnectTimeoutSeconds;
                ShowReconnectAvailability();

                KeepAliveSwitch.IsOn = _settings.KeepAudioAlive;

                _decibels = _settings.OsdDecibels;
                UnitBox.SelectedIndex = _decibels ? 0 : 1;
                ConfigureMaximumBox();
            }
            finally
            {
                _updatingUi = false;
            }
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

            _controller.Step = _settings.StepDecibels;
            _controller.MaxVolume = _settings.MaxVolume;
            _controller.FlushIntervalMs = _settings.FlushIntervalMs;
            _flyout.DurationMs = _settings.OsdDurationMs;
            ConfigureSlider();

            if (!_settings.IsConfigured)
            {
                SetStatus("Pick a receiver and type its IP address.", false);
                ShowSummaries();
                return;
            }

            int timeout = Math.Max(1, _settings.ReconnectTimeoutSeconds) * 1000;

            // Zero means never stop trying, which is what the switch turning retries off would
            // mean if it were not for the one attempt we always make.
            int attempts = _settings.AutoReconnect ? Math.Max(1, _settings.ReconnectAttempts) : 1;

            switch (_settings.Device)
            {
                case "DenonMarantz":
                    _device = new DenonMarantzDevice(_settings.Host, _settings.MinCommandIntervalMs, timeout, attempts);
                    break;
                case "StormAudio":
                    _device = new StormAudioDevice(_settings.Host, _settings.MinCommandIntervalMs, timeout, attempts);
                    break;
                default:
                    SetStatus("Unknown receiver \"" + _settings.Device + "\".", false);
                    return;
            }

            _device.StatusChanged += OnDeviceStatus;
            _device.LinkChanged += OnDeviceLink;
            _controller.Device = _device;

            UpdateStatus(null);
            ShowVolume();
            ShowSummaries();
        }

        private void QueueSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void SaveNow()
        {
            _saveTimer.Stop();

            try
            {
                _settings.Save(_configPath);
            }
            catch (Exception ex)
            {
                SetStatus("Could not save settings: " + ex.Message, false);
            }
        }

        #endregion

        #region Device events

        private void OnDeviceLink(object sender, LinkEventArgs e)
        {
            _dispatcher.TryEnqueue(() =>
            {
                UpdateStatus(e.Detail);
                ShowVolume();
                ShowSummaries();
            });
        }

        private void OnDeviceStatus(object sender, AvrStatusEventArgs e)
        {
            _dispatcher.TryEnqueue(() =>
            {
                UpdateStatus(null);
                ShowReportedMaximum();
                ShowVolume();
                ShowSummaries();

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
                if (e.MuteChanged || (e.VolumeChanged && _device.Volume.HasValue))
                {
                    _flyout.Display(FormatLevel(), LevelFraction(), _device.Muted);
                }
            });
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
                MaximumBox.Value = ToDisplay(_settings.MaxVolume);
            }
            finally
            {
                _updatingUi = false;
            }

            ConfigureSlider();
            QueueSave();
        }

        #endregion

        #region Display

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

            // Deliberately the setting rather than whatever the AVR last said: the scale has to
            // stay put for the whole session, otherwise the bar redraws itself to a different
            // length while the user is still turning the wheel.
            double max = _settings.MaxVolume;
            return max > 0 ? _device.Volume.Value / max : (double?)null;
        }

        private double StepSize => Math.Max(0.1, _settings.StepDecibels);

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
                VolumeSlider.Minimum = 0;
                VolumeSlider.Maximum = positions;
                VolumeSlider.StepFrequency = 1;
            }
            finally
            {
                _updatingUi = false;
            }

            ShowVolume();
        }

        /// <summary>Puts the level under the slider's thumb, and in words above it.</summary>
        private void ShowVolume()
        {
            LevelText.Text = FormatLevel();
            MaximumText.Text = "max " + ToDisplay(_settings.MaxVolume).ToString("0.0", CultureInfo.CurrentCulture);

            if (_device != null && _device.Volume.HasValue)
            {
                double level = _device.Volume.Value;
                LevelSubtext.Text = _decibels
                    ? "dB  ·  scale " + level.ToString("0.#", CultureInfo.CurrentCulture)
                    : "scale  ·  " + ToDecibels(level).ToString("0.0", CultureInfo.CurrentCulture) + " dB";
            }
            else
            {
                LevelSubtext.Text = string.Empty;
            }

            MuteButton.Content = _device != null && _device.Muted ? "Unmute" : "Mute";

            bool absolute = _device != null && _device.SupportsAbsoluteVolume;
            VolumeSlider.IsEnabled = absolute;

            if (!absolute || _device == null || !_device.Volume.HasValue)
            {
                return;
            }

            double position = Math.Round(_device.Volume.Value / StepSize);
            position = Math.Max(VolumeSlider.Minimum, Math.Min(VolumeSlider.Maximum, position));

            if (Math.Abs(VolumeSlider.Value - position) < 0.001)
            {
                return;
            }

            _updatingUi = true;
            try
            {
                VolumeSlider.Value = position;
            }
            finally
            {
                _updatingUi = false;
            }
        }

        /// <summary>The one-line summaries the collapsed expanders show.</summary>
        private void ShowSummaries()
        {
            string brand = string.IsNullOrEmpty(_settings.Device) ? "No receiver" : _settings.Device;
            string host = string.IsNullOrWhiteSpace(_settings.Host) ? "no address" : _settings.Host;
            string link = _device == null
                ? "not configured"
                : _device.Link == AvrLinkState.Connected ? "connected"
                : _device.Link == AvrLinkState.Connecting ? "connecting" : "disconnected";

            string retries = _settings.AutoReconnect
                ? " · " + _settings.ReconnectAttempts + "×" + _settings.ReconnectTimeoutSeconds + " s"
                : " · no auto-reconnect";

            ReceiverExpander.Description = brand + " · " + host + " · " + link + retries;

            AudioExpander.Description = _settings.KeepAudioAlive
                ? "Keeping the endpoint awake · " + (_audio?.Status ?? "starting")
                : "Windows decides when the audio device sleeps";

            OverlayExpander.Description = _settings.ShowOsd
                ? (_settings.OsdOnExternalChange ? "On · follows the receiver's own remote" : "On · your changes only")
                : "Off";

            BehaviourExpander.Description =
                (_decibels ? "dB" : "Scale") +
                " · step " + _settings.StepDecibels.ToString("0.#", CultureInfo.CurrentCulture) +
                " · max " + ToDisplay(_settings.MaxVolume).ToString("0.0", CultureInfo.CurrentCulture);
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
                    SetStatus(_device.Muted ? "Connected — muted" : "Connected", true);
                    break;
                case AvrLinkState.Connecting:
                    SetStatus(detail ?? "Connecting...", false);
                    break;
                default:
                    SetStatus(string.IsNullOrEmpty(detail) ? "Disconnected — retrying" : "Disconnected — " + detail, false);
                    break;
            }
        }

        private void SetStatus(string text, bool connected)
        {
            StatusText.Text = text;

            bool light = Root.ActualTheme == ElementTheme.Light;
            Windows.UI.Color colour = connected
                ? (light ? Windows.UI.Color.FromArgb(255, 15, 123, 15) : Windows.UI.Color.FromArgb(255, 108, 203, 95))
                : (light ? Windows.UI.Color.FromArgb(255, 134, 134, 134) : Windows.UI.Color.FromArgb(255, 154, 154, 154));

            StatusDot.Fill = new SolidColorBrush(colour);
        }

        #endregion

        #region Units

        private double ToDisplay(double volume) => _decibels ? ToDecibels(volume) : volume;

        private double FromDisplay(double shown) => _decibels ? FromDecibels(shown) : shown;

        private double ToDecibels(double volume) =>
            _device != null ? _device.ToDecibels(volume) : volume - AssumedZeroDecibelLevel;

        private double FromDecibels(double decibels) =>
            _device != null ? _device.FromDecibels(decibels) : decibels + AssumedZeroDecibelLevel;

        /// <summary>
        /// Volumes read either in dB, as the AVR's own display shows them, or in the 0 to 98 the
        /// protocol speaks. The two differ by an offset, which is why a step is the same number in
        /// both and only levels need converting.
        /// </summary>
        private void ConfigureMaximumBox()
        {
            if (_decibels)
            {
                MaximumBox.Minimum = -79;
                MaximumBox.Maximum = 18;
            }
            else
            {
                MaximumBox.Maximum = 98;
                MaximumBox.Minimum = 1;
            }

            MaximumBox.Value = ToDisplay(_settings.MaxVolume);
        }

        #endregion

        #region User actions

        private void OnStepUp(object sender, RoutedEventArgs e) => _controller.Nudge(1);

        private void OnStepDown(object sender, RoutedEventArgs e) => _controller.Nudge(-1);

        private void OnToggleMute(object sender, RoutedEventArgs e) => _controller.ToggleMute();

        private void OnReconnect(object sender, RoutedEventArgs e)
        {
            SaveNow();
            ApplyConfiguration();
            _device?.Retry();
        }

        private void OnSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_updatingUi || _controller == null)
            {
                return;
            }

            _controller.SetVolume(e.NewValue * StepSize);
        }

        private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingUi)
            {
                return;
            }

            _settings.Device = (DeviceBox.SelectedItem as ComboBoxItem)?.Content as string ?? string.Empty;
            QueueSave();
            ApplyConfiguration();
        }

        private void OnHostChanged(object sender, RoutedEventArgs e)
        {
            if (_updatingUi)
            {
                return;
            }

            string host = HostBox.Text.Trim();
            if (host == _settings.Host)
            {
                return;
            }

            _settings.Host = host;
            QueueSave();
            ApplyConfiguration();
        }

        private void OnUnitChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingUi)
            {
                return;
            }

            double maximum = FromDisplay(MaximumBox.Value);
            _decibels = UnitBox.SelectedIndex == 0;
            _settings.OsdDecibels = _decibels;

            _updatingUi = true;
            try
            {
                _settings.MaxVolume = maximum;
                ConfigureMaximumBox();
            }
            finally
            {
                _updatingUi = false;
            }

            ShowVolume();
            ShowSummaries();
            QueueSave();
        }

        private void OnStepSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_updatingUi || double.IsNaN(args.NewValue) || _controller == null)
            {
                return;
            }

            _settings.StepDecibels = args.NewValue;
            _controller.Step = _settings.StepDecibels;
            ConfigureSlider();
            ShowSummaries();
            QueueSave();
        }

        private void OnMaximumChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_updatingUi || double.IsNaN(args.NewValue) || _controller == null)
            {
                return;
            }

            // Typed by hand: from here on the AVR's answer no longer has a say.
            _settings.MaxVolumeIsManual = true;
            _settings.MaxVolume = FromDisplay(args.NewValue);
            _controller.MaxVolume = _settings.MaxVolume;

            ConfigureSlider();
            ShowSummaries();
            QueueSave();
        }

        private void OnAutoReconnectToggled(object sender, RoutedEventArgs e)
        {
            if (_updatingUi)
            {
                return;
            }

            _settings.AutoReconnect = AutoReconnectSwitch.IsOn;
            ShowReconnectAvailability();
            ShowSummaries();
            QueueSave();
            ApplyConfiguration();
        }

        /// <summary>The two numbers only mean anything while the app is allowed to retry.</summary>
        private void ShowReconnectAvailability()
        {
            bool on = AutoReconnectSwitch.IsOn;
            AttemptsCard.IsEnabled = on;
            TimeoutCard.IsEnabled = on;
        }

        private void OnAttemptsChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_updatingUi || double.IsNaN(args.NewValue))
            {
                return;
            }

            _settings.ReconnectAttempts = (int)args.NewValue;
            ShowSummaries();
            QueueSave();
            ApplyConfiguration();
        }

        private void OnTimeoutChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_updatingUi || double.IsNaN(args.NewValue))
            {
                return;
            }

            _settings.ReconnectTimeoutSeconds = (int)args.NewValue;
            ShowSummaries();
            QueueSave();
            ApplyConfiguration();
        }

        private void OnKeepAliveToggled(object sender, RoutedEventArgs e)
        {
            if (_updatingUi)
            {
                return;
            }

            _settings.KeepAudioAlive = KeepAliveSwitch.IsOn;
            _audio.Enable(_settings.KeepAudioAlive);
            ShowSummaries();
            QueueSave();
        }

        private void ShowAudioStatus(string status)
        {
            KeepAliveStatus.Text = status;
            ShowSummaries();
        }

        private void OnOverlayToggled(object sender, RoutedEventArgs e)
        {
            if (_updatingUi)
            {
                return;
            }

            _settings.ShowOsd = OverlaySwitch.IsOn;
            ShowSummaries();
            QueueSave();
        }

        private void OnRemoteToggled(object sender, RoutedEventArgs e)
        {
            if (_updatingUi)
            {
                return;
            }

            _settings.OsdOnExternalChange = RemoteSwitch.IsOn;
            ShowSummaries();
            QueueSave();
        }

        #endregion
    }
}
