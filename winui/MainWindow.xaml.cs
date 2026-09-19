using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using HTPCAVRVolume.Ipc;
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
    /// <summary>
    /// The settings window, and nothing else.
    ///
    /// It owns no receiver, no keyboard hook and no audio: all of that lives in the background
    /// process, which this one asks over a pipe. Everything on screen comes from the state that
    /// process sends back, so the two can never disagree, and closing the window ends this process
    /// outright rather than leaving WinUI resident for nobody.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private const int WindowWidth = 560;
        private const int MinimumWindowHeight = 480;

        /// <summary>Title bar and window chrome the content does not know about.</summary>
        private const int TitleBarAllowance = 40;

        private readonly DispatcherQueue _dispatcher;
        private readonly IpcClient _client = new IpcClient();

        private AppWindow _appWindow;
        private State _state = new State();
        private bool _updatingUi;
        private bool _decibels = true;

        public MainWindow()
        {
            InitializeComponent();
            _dispatcher = DispatcherQueue.GetForCurrentThread();
        }

        /// <summary>Brings the window up and attaches it to the background process.</summary>
        public void Start()
        {
            Title = "HTPCAVRVolume";

            IntPtr handle = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(handle));

            try
            {
                _appWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "HTPCAVRVolume.ico"));
            }
            catch
            {
                // The window simply keeps the default icon.
            }

            _appWindow.Resize(new SizeInt32(WindowWidth, MinimumWindowHeight));
            Root.Loaded += (sender, e) => FitToContent();

            _client.StateReceived += (sender, state) => _dispatcher.TryEnqueue(() => Show(state));
            _client.Closed += (sender, e) => _dispatcher.TryEnqueue(Close);

            Activate();

            if (!Attach())
            {
                StatusText.Text = "Cannot reach the background process.";
                return;
            }

            WatchForRaise();
            _client.Send(new Request { Verb = "state" });
        }

        /// <summary>
        /// Connects to the background process, starting it if it is not there: running the window
        /// on its own is a reasonable thing for someone to do.
        /// </summary>
        private bool Attach()
        {
            if (_client.Connect(1500))
            {
                return true;
            }

            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false });
            }
            catch
            {
                return false;
            }

            return _client.Connect(6000);
        }

        /// <summary>A second attempt to open the settings signals this rather than opening twice.</summary>
        private void WatchForRaise()
        {
            Thread watcher = new Thread(() =>
            {
                using EventWaitHandle raise = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\HTPCAVRVolume.Raise");
                while (raise.WaitOne())
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        _appWindow.Show();
                        _appWindow.MoveInZOrderAtTop();
                        Activate();
                    });
                }
            })
            {
                IsBackground = true,
                Name = "Raise watcher"
            };

            watcher.Start();
        }

        /// <summary>
        /// Sizes the window to what it actually contains, so every group is visible at rest rather
        /// than behind a scrollbar, and clamps it to the screen it sits on.
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

        #region Showing what the background process reports

        private void Show(State state)
        {
            _state = state ?? new State();
            _decibels = _state.Decibels;

            _updatingUi = true;
            try
            {
                DeviceBox.SelectedIndex = _state.Device == "StormAudio" ? 1 : _state.Device == "DenonMarantz" ? 0 : -1;

                // Leave the address alone while it is being typed into.
                if (HostBox.FocusState == FocusState.Unfocused)
                {
                    HostBox.Text = _state.Host;
                }

                StepBox.Value = _state.Step;
                UnitBox.SelectedIndex = _decibels ? 0 : 1;
                ConfigureMaximumBox();

                OverlaySwitch.IsOn = _state.ShowOsd;
                RemoteSwitch.IsOn = _state.OsdOnExternalChange;
                AutoReconnectSwitch.IsOn = _state.AutoReconnect;
                AttemptsBox.Value = _state.ReconnectAttempts;
                TimeoutBox.Value = _state.ReconnectTimeoutSeconds;
                KeepAliveSwitch.IsOn = _state.KeepAudioAlive;

                AttemptsCard.IsEnabled = _state.AutoReconnect;
                TimeoutCard.IsEnabled = _state.AutoReconnect;
                KeepAliveStatus.Text = _state.AudioStatus;

                ConfigureSlider();
                ShowVolume();
                ShowSummaries();
                ShowStatus();
            }
            finally
            {
                _updatingUi = false;
            }
        }

        private double StepSize => Math.Max(0.1, _state.Step);

        private void ConfigureSlider()
        {
            double max = _state.MaxVolume > 0 ? _state.MaxVolume : 98;
            VolumeSlider.Minimum = 0;
            VolumeSlider.Maximum = Math.Max(1, Math.Round(max / StepSize));
            VolumeSlider.StepFrequency = 1;
        }

        private void ShowVolume()
        {
            LevelText.Text = FormatLevel();
            MaximumText.Text = "max " + ToDisplay(_state.MaxVolume).ToString("0.0", CultureInfo.CurrentCulture);

            if (_state.Volume.HasValue)
            {
                double level = _state.Volume.Value;
                LevelSubtext.Text = _decibels
                    ? "dB  ·  scale " + level.ToString("0.#", CultureInfo.CurrentCulture)
                    : "scale  ·  " + ToDecibels(level).ToString("0.0", CultureInfo.CurrentCulture) + " dB";
            }
            else
            {
                LevelSubtext.Text = string.Empty;
            }

            MuteButton.Content = _state.Muted ? "Unmute" : "Mute";
            VolumeSlider.IsEnabled = _state.SupportsAbsoluteVolume;

            if (!_state.Volume.HasValue)
            {
                return;
            }

            double position = Math.Round(_state.Volume.Value / StepSize);
            position = Math.Max(VolumeSlider.Minimum, Math.Min(VolumeSlider.Maximum, position));
            if (Math.Abs(VolumeSlider.Value - position) > 0.001)
            {
                VolumeSlider.Value = position;
            }
        }

        private void ShowSummaries()
        {
            string brand = string.IsNullOrEmpty(_state.Device) ? "No receiver" : _state.Device;
            string host = string.IsNullOrWhiteSpace(_state.Host) ? "no address" : _state.Host;
            string retries = _state.AutoReconnect
                ? " · " + _state.ReconnectAttempts + "×" + _state.ReconnectTimeoutSeconds + " s"
                : " · no auto-reconnect";

            ReceiverExpander.Description = brand + " · " + host + " · " + _state.Link + retries;

            AudioExpander.Description = _state.KeepAudioAlive
                ? "Keeping the endpoint awake · " + _state.AudioStatus
                : "Windows decides when the audio device sleeps";

            OverlayExpander.Description = _state.ShowOsd
                ? (_state.OsdOnExternalChange ? "On · follows the receiver's own remote" : "On · your changes only")
                : "Off";

            BehaviourExpander.Description =
                (_decibels ? "dB" : "Scale") +
                " · step " + _state.Step.ToString("0.#", CultureInfo.CurrentCulture) +
                " · max " + ToDisplay(_state.MaxVolume).ToString("0.0", CultureInfo.CurrentCulture);
        }

        private void ShowStatus()
        {
            StatusText.Text = _state.Status;

            bool light = Root.ActualTheme == ElementTheme.Light;
            Windows.UI.Color colour = _state.Link == "connected"
                ? (light ? Windows.UI.Color.FromArgb(255, 15, 123, 15) : Windows.UI.Color.FromArgb(255, 108, 203, 95))
                : (light ? Windows.UI.Color.FromArgb(255, 134, 134, 134) : Windows.UI.Color.FromArgb(255, 154, 154, 154));

            StatusDot.Fill = new SolidColorBrush(colour);
        }

        private string FormatLevel()
        {
            if (!_state.Volume.HasValue)
            {
                return "--";
            }

            double level = _state.Volume.Value;
            return _decibels
                ? ToDecibels(level).ToString("0.0", CultureInfo.CurrentCulture) + " dB"
                : level.ToString("0.#", CultureInfo.CurrentCulture);
        }

        #endregion

        #region Units

        private double ToDisplay(double volume) => _decibels ? ToDecibels(volume) : volume;

        private double FromDisplay(double shown) => _decibels ? FromDecibels(shown) : shown;

        private double ToDecibels(double volume) => volume - _state.ZeroDecibelLevel;

        private double FromDecibels(double decibels) => decibels + _state.ZeroDecibelLevel;

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

            MaximumBox.Value = ToDisplay(_state.MaxVolume);
        }

        #endregion

        #region What the user does

        private void Change(string name, string value)
        {
            _client.Send(new Request { Verb = "setting", Name = name, Value = value });
        }

        private static string Text(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        private void OnStepUp(object sender, RoutedEventArgs e) =>
            _client.Send(new Request { Verb = "step", Number = 1 });

        private void OnStepDown(object sender, RoutedEventArgs e) =>
            _client.Send(new Request { Verb = "step", Number = -1 });

        private void OnToggleMute(object sender, RoutedEventArgs e) =>
            _client.Send(new Request { Verb = "mute" });

        private void OnReconnect(object sender, RoutedEventArgs e) =>
            _client.Send(new Request { Verb = "reconnect" });

        private void OnSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_updatingUi)
            {
                return;
            }

            _client.Send(new Request { Verb = "volume", Number = e.NewValue * StepSize });
        }

        private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_updatingUi)
            {
                Change("Device", (DeviceBox.SelectedItem as ComboBoxItem)?.Content as string ?? string.Empty);
            }
        }

        private void OnHostChanged(object sender, RoutedEventArgs e)
        {
            if (!_updatingUi)
            {
                Change("Host", HostBox.Text.Trim());
            }
        }

        private void OnUnitChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingUi)
            {
                return;
            }

            _decibels = UnitBox.SelectedIndex == 0;
            Change("Decibels", _decibels ? "True" : "False");
        }

        private void OnStepSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (!_updatingUi && !double.IsNaN(args.NewValue))
            {
                Change("Step", Text(args.NewValue));
            }
        }

        private void OnMaximumChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (!_updatingUi && !double.IsNaN(args.NewValue))
            {
                Change("MaxVolume", Text(FromDisplay(args.NewValue)));
            }
        }

        private void OnOverlayToggled(object sender, RoutedEventArgs e)
        {
            if (!_updatingUi)
            {
                Change("ShowOsd", OverlaySwitch.IsOn ? "True" : "False");
            }
        }

        private void OnRemoteToggled(object sender, RoutedEventArgs e)
        {
            if (!_updatingUi)
            {
                Change("OsdOnExternalChange", RemoteSwitch.IsOn ? "True" : "False");
            }
        }

        private void OnAutoReconnectToggled(object sender, RoutedEventArgs e)
        {
            if (!_updatingUi)
            {
                Change("AutoReconnect", AutoReconnectSwitch.IsOn ? "True" : "False");
            }
        }

        private void OnAttemptsChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (!_updatingUi && !double.IsNaN(args.NewValue))
            {
                Change("ReconnectAttempts", Text(args.NewValue));
            }
        }

        private void OnTimeoutChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (!_updatingUi && !double.IsNaN(args.NewValue))
            {
                Change("ReconnectTimeoutSeconds", Text(args.NewValue));
            }
        }

        private void OnKeepAliveToggled(object sender, RoutedEventArgs e)
        {
            if (!_updatingUi)
            {
                Change("KeepAudioAlive", KeepAliveSwitch.IsOn ? "True" : "False");
            }
        }

        #endregion
    }
}
