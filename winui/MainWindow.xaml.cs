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
        private string _zonesShown = string.Empty;
        private bool _updatingUi;
        private bool _decibels = true;

        /// <summary>Set when the window is closing itself to go to the notification area.</summary>
        private bool _leaving;

        public MainWindow()
        {
            InitializeComponent();
            _dispatcher = DispatcherQueue.GetForCurrentThread();
        }

        /// <summary>Brings the window up and attaches it to the background process.</summary>
        public void Start()
        {
            Title = "HTPC AVR Volume - Configuration";

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
            _appWindow.Changed += OnAppWindowChanged;
            _appWindow.Closing += OnAppWindowClosing;
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
                DeviceBox.SelectedIndex = IndexOfTag(DeviceBox, _state.Device);

                // Leave the address alone while it is being typed into.
                if (HostBox.FocusState == FocusState.Unfocused)
                {
                    HostBox.Text = _state.Host;
                }

                ShowZones();
                ConfigureStepBox();
                StepBox.Value = _state.Step;
                UnitBox.SelectedIndex = _decibels ? 0 : 1;
                UnitCard.Description = _state.FollowsReceiverUnit
                    ? "Shared with the receiver: changing it here changes its display too"
                    : "Scale (0\u201398) or absolute decibels";
                ConfigureMaximumBox();

                OverlaySwitch.IsOn = _state.ShowOsd;
                RemoteSwitch.IsOn = _state.OsdOnExternalChange;
                AutoReconnectSwitch.IsOn = _state.AutoReconnect;
                AttemptsBox.Value = _state.ReconnectAttempts;
                TimeoutBox.Value = _state.ReconnectTimeoutSeconds;
                KeepAliveSwitch.IsOn = _state.KeepAudioAlive;
                MinimiseBox.SelectedIndex = _state.MinimiseToTray ? 1 : 0;
                CloseBox.SelectedIndex = _state.CloseToTray ? 1 : 0;

                AttemptsCard.IsEnabled = _state.AutoReconnect;
                TimeoutCard.IsEnabled = _state.AutoReconnect;
                KeepAliveStatus.Text = _state.AudioStatus;

                ConfigureSlider();
                ShowVolume();
                ShowSummaries();
                ShowStatus();
                ShowAvailability();
            }
            finally
            {
                _updatingUi = false;
            }
        }

        private double StepSize => Math.Max(0.1, _state.Step);

        private bool OnMain => string.IsNullOrEmpty(_state.Zone) || _state.Zone == "Main";

        private bool AnyZoneOn()
        {
            bool[] power = _state.ZonePower ?? new bool[0];
            foreach (bool on in power)
            {
                if (on)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Offers the zones the receiver actually answered for, and rebuilds the list only when
        /// that set changes: it fills in over the first second of a session.
        /// </summary>
        private void ShowZones()
        {
            string[] zones = _state.Zones != null && _state.Zones.Length > 0
                ? _state.Zones
                : new[] { "Main" };

            string signature = string.Join(",", zones);
            if (signature != _zonesShown)
            {
                _zonesShown = signature;
                ZoneBox.Items.Clear();
                PowerButtons.Children.Clear();

                foreach (string zone in zones)
                {
                    string label = zone == "Main" ? "Main" : "Zone " + zone.Substring(4);

                    ZoneBox.Items.Add(new ComboBoxItem { Content = label, Tag = zone });

                    Button button = new Button { Content = label, Tag = zone, MinWidth = 76 };
                    button.Click += OnPowerClicked;
                    PowerButtons.Children.Add(button);
                }
            }

            ShowPower(zones);
            ZoneBox.SelectedIndex = IndexOfTag(ZoneBox, _state.Zone ?? "Main");

            // With every zone switched off there is no volume anywhere to set, so the four
            // things that describe one are closed rather than left to be clicked at. The power
            // buttons above them stay live: they are the way back.
            bool anyOn = AnyZoneOn();
            ZoneBox.IsEnabled = anyOn && ZoneBox.Items.Count > 1;
            UnitBox.IsEnabled = anyOn;
            StepBox.IsEnabled = anyOn;
            MaximumBox.IsEnabled = anyOn;
            MaximumChoice.IsEnabled = anyOn;

            // A zone that is off has no volume to manage, so it cannot be picked. It stays in
            // the list, greyed: a name that vanished would be more puzzling than one that is
            // visibly unavailable, and the button above says how to make it available.
            bool[] power = _state.ZonePower ?? new bool[0];
            for (int i = 0; i < ZoneBox.Items.Count && i < zones.Length; i++)
            {
                if (ZoneBox.Items[i] is ComboBoxItem item)
                {
                    item.IsEnabled = i < power.Length && power[i];
                }
            }
        }

        /// <summary>
        /// A zone that is on wears the accent colour, the way a lit switch does. The state comes
        /// from the receiver, so a zone switched on from anywhere else shows as on here.
        /// </summary>
        private void ShowPower(string[] zones)
        {
            bool[] power = _state.ZonePower ?? new bool[0];

            for (int i = 0; i < PowerButtons.Children.Count && i < zones.Length; i++)
            {
                if (!(PowerButtons.Children[i] is Button button))
                {
                    continue;
                }

                bool on = i < power.Length && power[i];
                button.Style = (Style)Application.Current.Resources[
                    on ? "AccentButtonStyle" : "DefaultButtonStyle"];
                ToolTipService.SetToolTip(button, on ? "Switch this zone off" : "Switch this zone on");
            }
        }

        /// <summary>Half steps exist on main and nowhere else.</summary>
        private void ConfigureStepBox()
        {
            double quantum = _state.Quantum > 0 ? _state.Quantum : 0.5;
            StepBox.SmallChange = quantum;
            StepBox.Minimum = quantum;
        }

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

            ShowMuteIcon();
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

        /// <summary>
        /// The same four speaker glyphs Windows uses, chosen the same way: silence, then thirds
        /// of the way to the top, with the crossed-out one for mute.
        /// </summary>
        private void ShowMuteIcon()
        {
            double max = _state.MaxVolume > 0 ? _state.MaxVolume : 98;
            double level = _state.Volume ?? 0;
            double fraction = max > 0 ? level / max : 0;

            string glyph;
            if (_state.Muted)
            {
                glyph = "\uE74F";
            }
            else if (!_state.Volume.HasValue || level <= 0.001)
            {
                glyph = "\uE992";
            }
            else if (fraction <= 1.0 / 3)
            {
                glyph = "\uE993";
            }
            else if (fraction <= 2.0 / 3)
            {
                glyph = "\uE994";
            }
            else
            {
                glyph = "\uE995";
            }

            MuteIcon.Glyph = glyph;
            ToolTipService.SetToolTip(MuteButton, _state.Muted ? "Unmute" : "Mute");
        }

        private void ShowSummaries()
        {
            string brand = FriendlyDevice(_state.Device);
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
                (OnMain ? "Main" : "Zone " + _state.Zone.Substring(4)) + " \u00b7 " +
                (_decibels ? "dB" : "Scale") +
                " · step " + _state.Step.ToString("0.#", CultureInfo.CurrentCulture) +
                " · max " + ToDisplay(_state.MaxVolume).ToString("0.0", CultureInfo.CurrentCulture);

            WindowExpander.Description =
                "Minimise " + (_state.MinimiseToTray ? "to the notification area" : "to the taskbar") +
                " · close " + (_state.CloseToTray ? "to the notification area" : "quits the app");
        }

        /// <summary>
        /// Nothing below the receiver group means anything until there is a receiver answering,
        /// so it all greys out and the only things left to reach for are the brand and the
        /// address. On a first run that is the whole of the setup, and it shows.
        /// </summary>
        private void ShowAvailability()
        {
            bool live = _state.Link == "connected";

            VolumeCardBody.IsEnabled = live;

            // Text does not dim on its own the way a disabled control does, and a bright readout
            // above a greyed-out slider looks like a fault rather than a state.
            LevelBlock.Opacity = live ? 1 : 0.45;
            AudioExpander.IsEnabled = live;
            BehaviourExpander.IsEnabled = live;
            OverlayExpander.IsEnabled = live;
            WindowExpander.IsEnabled = live;
        }

        private static string FriendlyDevice(string device)
        {
            switch (device)
            {
                case "DenonMarantz":
                    return "Denon / Marantz";
                case "StormAudio":
                    return "StormAudio";
                default:
                    return string.IsNullOrEmpty(device) ? "No receiver" : device;
            }
        }

        /// <summary>
        /// The list shows a name a person would use; the tag carries the one the settings file
        /// and the background process have always used.
        /// </summary>
        private static int IndexOfTag(ComboBox box, string tag)
        {
            for (int i = 0; i < box.Items.Count; i++)
            {
                if (string.Equals((box.Items[i] as ComboBoxItem)?.Tag as string, tag, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
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
            // A zone's ceiling is whatever its Limit menu is set to, so it is picked from that
            // menu's own four values rather than typed.
            MaximumBox.Visibility = OnMain ? Visibility.Visible : Visibility.Collapsed;
            MaximumChoice.Visibility = OnMain ? Visibility.Collapsed : Visibility.Visible;
            MaximumCard.Description = OnMain
                ? "The loudest the app will go, and the top of the slider"
                : "The Limit in the zone's own menu on the receiver, read from it and written to it";

            if (!OnMain)
            {
                MaximumChoice.SelectedIndex = NearestLimit(_state.MaxVolume);
                return;
            }

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

        /// <summary>The entry closest to what the receiver reported, so an odd value still shows.</summary>
        private int NearestLimit(double maximum)
        {
            int best = 0;
            double closest = double.MaxValue;

            for (int i = 0; i < MaximumChoice.Items.Count; i++)
            {
                string tag = (MaximumChoice.Items[i] as ComboBoxItem)?.Tag as string;
                if (!double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    continue;
                }

                double distance = Math.Abs(value - maximum);
                if (distance < closest)
                {
                    closest = distance;
                    best = i;
                }
            }

            return best;
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
                Change("Device", (DeviceBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty);
            }
        }

        private void OnHostChanged(object sender, RoutedEventArgs e)
        {
            if (!_updatingUi)
            {
                Change("Host", HostBox.Text.Trim());
            }
        }

        private void OnPowerClicked(object sender, RoutedEventArgs e)
        {
            string zone = (sender as Button)?.Tag as string;
            if (string.IsNullOrEmpty(zone))
            {
                return;
            }

            int index = Array.IndexOf(_state.Zones ?? new[] { "Main" }, zone);
            bool on = _state.ZonePower != null && index >= 0 && index < _state.ZonePower.Length
                && _state.ZonePower[index];

            _client.Send(new Request { Verb = "power", Name = zone, Value = on ? "False" : "True" });
        }

        private void OnZoneChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_updatingUi)
            {
                Change("Zone", (ZoneBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Main");
            }
        }

        private void OnMaximumChoiceChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_updatingUi)
            {
                Change("MaxVolume", (MaximumChoice.SelectedItem as ComboBoxItem)?.Tag as string ?? "98");
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

        private void OnMinimiseActionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_updatingUi)
            {
                Change("MinimiseToTray", MinimiseBox.SelectedIndex == 1 ? "True" : "False");
            }
        }

        private void OnCloseActionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_updatingUi)
            {
                Change("CloseToTray", CloseBox.SelectedIndex == 1 ? "True" : "False");
            }
        }

        /// <summary>
        /// Minimising to the notification area means closing the window outright rather than
        /// parking it off screen: the tray icon is what brings it back, and until then this
        /// process has no reason to exist.
        /// </summary>
        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (!args.DidPresenterChange || !_state.MinimiseToTray || _leaving)
            {
                return;
            }

            if ((sender.Presenter as OverlappedPresenter)?.State != OverlappedPresenterState.Minimized)
            {
                return;
            }

            _leaving = true;
            _dispatcher.TryEnqueue(Close);
        }

        /// <summary>
        /// The close button, when it is not set to leave the app running, is the only way anyone
        /// has of stopping the half they cannot see.
        /// </summary>
        private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_leaving || _state.CloseToTray)
            {
                return;
            }

            _client.Send(new Request { Verb = "quit" });

            // Long enough for the other process to read the line before this one takes the pipe
            // down with it, short enough that nobody watches the window linger.
            Thread.Sleep(200);
        }

        #endregion
    }
}
