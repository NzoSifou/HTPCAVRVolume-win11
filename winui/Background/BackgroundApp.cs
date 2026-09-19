using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using HTPCAVRVolume.AVRDevices;
using HTPCAVRVolume.Ipc;
using HTPCAVRVolume.Ui;

namespace HTPCAVRVolume.Background
{
    /// <summary>
    /// Everything the app does when nobody is looking at it: the link to the receiver, the volume
    /// keys, the on-screen display, the notification area icon and the silent stream that keeps
    /// the audio endpoint awake.
    ///
    /// Not one line of it touches WinUI, which is the point. The settings window is a separate
    /// process, started when it is asked for and gone the moment it is closed, so what remains
    /// resident between two key presses is a Win32 message loop and a socket.
    /// </summary>
    sealed class BackgroundApp : IDisposable
    {
        private readonly MessagePump _pump = new MessagePump();
        private readonly string _configPath;

        private AppSettings _settings;
        private VolumeController _controller;
        private VolumeFlyout _flyout;
        private GlobalKeyboardHook _hook;
        private TrayIcon _tray;
        private AudioKeepAlive _audio;
        private IpcServer _server;
        private IAVRDevice _device;
        private Process _window;

        private bool _maximumApplied;
        private string _statusDetail;

        public BackgroundApp()
        {
            // Next to the .exe, not next to AppContext.BaseDirectory: a single-file build runs
            // from a temp extraction folder, and the settings belong with the app the user moved.
            _configPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "HTPCAVRVolumeConfig.txt");
        }

        public void Run(bool showWindow)
        {
            _settings = AppSettings.Load(_configPath);

            _controller = new VolumeController(_pump);
            _flyout = new VolumeFlyout(_pump);
            _flyout.Prepare();

            _audio = new AudioKeepAlive(status => _pump.Post(() => PushState()));
            _audio.Enable(_settings.KeepAudioAlive);

            _tray = new TrayIcon("HTPCAVRVolume");
            _tray.Activated += (sender, e) => _pump.Post(OpenWindow);
            _tray.ExitRequested += (sender, e) => _pump.Post(Quit);

            _hook = new GlobalKeyboardHook();
            _hook.VolumeUpPressed += (sender, e) => _controller.Nudge(1);
            _hook.VolumeDownPressed += (sender, e) => _controller.Nudge(-1);
            _hook.VolumeMutePressed += (sender, e) => _controller.ToggleMute();

            _server = new IpcServer(Handle);
            _server.ClientLeft += (sender, e) => _pump.Post(Relax);
            _server.Start();

            ApplyConfiguration();

            // Nothing is configured yet, or someone started the app a second time: either way the
            // user is asking to see it.
            if (showWindow || !_settings.IsConfigured)
            {
                OpenWindow();
            }

            Relax();
            _pump.Run();
        }

        /// <summary>
        /// Gives back what the last burst of work needed. Called once everything is up and again
        /// whenever the settings window goes away, which is exactly when this process goes back to
        /// doing almost nothing and should stop looking like it is doing something.
        /// </summary>
        private void Relax()
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

        public void Dispose()
        {
            _hook?.Dispose();
            _audio?.Dispose();
            _server?.Dispose();
            _controller?.Dispose();
            _device?.Dispose();
            _flyout?.Dispose();
            _tray?.Dispose();
            _pump?.Dispose();
        }

        private void Quit()
        {
            Save();
            _pump.Quit();
        }

        #region The settings window, in its own process

        /// <summary>
        /// Starts the window, or brings back the one already open. It is the same executable with
        /// an argument: one file to carry, two very different jobs.
        /// </summary>
        private void OpenWindow()
        {
            if (_window != null && !_window.HasExited)
            {
                // Already open. The window raises itself when it sees a second instance start.
                try
                {
                    Process.Start(new ProcessStartInfo(Environment.ProcessPath, "--settings --raise") { UseShellExecute = false });
                }
                catch
                {
                    // Nothing to do but leave the one that is open where it is.
                }

                return;
            }

            try
            {
                _window = Process.Start(new ProcessStartInfo(Environment.ProcessPath, "--settings") { UseShellExecute = false });
            }
            catch
            {
                _window = null;
            }
        }

        #endregion

        #region Requests from the window

        /// <summary>
        /// Called on the pipe thread. Everything it touches lives on the pump's thread, so the
        /// work is handed over and waited for.
        /// </summary>
        private State Handle(Request request)
        {
            State state = null;
            using System.Threading.ManualResetEventSlim done = new System.Threading.ManualResetEventSlim(false);

            _pump.Post(() =>
            {
                try
                {
                    Apply(request);
                    state = Snapshot();
                }
                finally
                {
                    done.Set();
                }
            });

            done.Wait(3000);
            return state;
        }

        private void Apply(Request request)
        {
            switch (request?.Verb)
            {
                case "state":
                    break;

                case "show":
                    OpenWindow();
                    break;

                case "volume":
                    _controller.SetVolume(request.Number);
                    break;

                case "step":
                    _controller.Nudge((int)request.Number);
                    break;

                case "mute":
                    _controller.ToggleMute();
                    break;

                case "reconnect":
                    ApplyConfiguration();
                    _device?.Retry();
                    break;

                case "setting":
                    ChangeSetting(request.Name, request.Value);
                    break;

                case "quit":
                    Quit();
                    break;
            }
        }

        /// <summary>
        /// The window never writes the settings file; it says what changed and the background
        /// process decides what that means and saves it.
        /// </summary>
        private void ChangeSetting(string name, string value)
        {
            bool relink = false;

            switch (name)
            {
                case "Device":
                    _settings.Device = value ?? string.Empty;
                    relink = true;
                    break;
                case "Host":
                    _settings.Host = (value ?? string.Empty).Trim();
                    relink = true;
                    break;
                case "Step":
                    _settings.StepDecibels = Number(value, _settings.StepDecibels);
                    _controller.Step = _settings.StepDecibels;
                    break;
                case "MaxVolume":
                    _settings.MaxVolume = Number(value, _settings.MaxVolume);
                    _settings.MaxVolumeIsManual = true;
                    _controller.MaxVolume = _settings.MaxVolume;
                    break;
                case "Decibels":
                    _settings.OsdDecibels = Flag(value, _settings.OsdDecibels);
                    break;
                case "ShowOsd":
                    _settings.ShowOsd = Flag(value, _settings.ShowOsd);
                    break;
                case "OsdOnExternalChange":
                    _settings.OsdOnExternalChange = Flag(value, _settings.OsdOnExternalChange);
                    break;
                case "AutoReconnect":
                    _settings.AutoReconnect = Flag(value, _settings.AutoReconnect);
                    relink = true;
                    break;
                case "ReconnectAttempts":
                    _settings.ReconnectAttempts = (int)Number(value, _settings.ReconnectAttempts);
                    relink = true;
                    break;
                case "ReconnectTimeoutSeconds":
                    _settings.ReconnectTimeoutSeconds = (int)Number(value, _settings.ReconnectTimeoutSeconds);
                    relink = true;
                    break;
                case "KeepAudioAlive":
                    _settings.KeepAudioAlive = Flag(value, _settings.KeepAudioAlive);
                    _audio.Enable(_settings.KeepAudioAlive);
                    break;
                default:
                    return;
            }

            Save();

            if (relink)
            {
                ApplyConfiguration();
            }
        }

        private static double Number(string value, double fallback)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : fallback;
        }

        private static bool Flag(string value, bool fallback)
        {
            return bool.TryParse(value, out bool parsed) ? parsed : fallback;
        }

        private void Save()
        {
            try
            {
                _settings.Save(_configPath);
            }
            catch
            {
                // A read-only folder is the user's to fix; losing a setting is not worth a crash.
            }
        }

        #endregion

        #region The receiver

        private void ApplyConfiguration()
        {
            _controller.Device = null;
            _maximumApplied = false;
            _statusDetail = null;

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

            if (!_settings.IsConfigured)
            {
                PushState();
                return;
            }

            int timeout = Math.Max(1, _settings.ReconnectTimeoutSeconds) * 1000;
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
                    PushState();
                    return;
            }

            _device.StatusChanged += OnDeviceStatus;
            _device.LinkChanged += OnDeviceLink;
            _controller.Device = _device;

            PushState();
        }

        private void OnDeviceLink(object sender, LinkEventArgs e)
        {
            _pump.Post(() =>
            {
                _statusDetail = e.Detail;
                PushState();
            });
        }

        private void OnDeviceStatus(object sender, AvrStatusEventArgs e)
        {
            _pump.Post(() =>
            {
                TakeReportedMaximum();

                if (_settings.ShowOsd && _device != null &&
                    (!e.External || _settings.OsdOnExternalChange) &&
                    (e.MuteChanged || (e.VolumeChanged && _device.Volume.HasValue)))
                {
                    _flyout.Display(FormatLevel(), LevelFraction(), _device.Muted);
                }

                PushState();
            });
        }

        /// <summary>
        /// Takes the maximum the receiver reports, once per run, and leaves it alone afterwards.
        /// A maximum the user typed themselves is never overwritten.
        /// </summary>
        private void TakeReportedMaximum()
        {
            if (_maximumApplied || _settings.MaxVolumeIsManual || _device == null || !_device.MaxVolume.HasValue)
            {
                return;
            }

            _maximumApplied = true;
            _settings.MaxVolume = _device.MaxVolume.Value;
            _controller.MaxVolume = _settings.MaxVolume;
            Save();
        }

        private string FormatLevel()
        {
            if (_device == null || !_device.Volume.HasValue)
            {
                return "--";
            }

            double level = _device.Volume.Value;
            return _settings.OsdDecibels
                ? _device.ToDecibels(level).ToString("0.0", CultureInfo.CurrentCulture) + " dB"
                : level.ToString("0.#", CultureInfo.CurrentCulture);
        }

        private double? LevelFraction()
        {
            if (_device == null || !_device.Volume.HasValue)
            {
                return null;
            }

            double max = _settings.MaxVolume;
            return max > 0 ? _device.Volume.Value / max : (double?)null;
        }

        #endregion

        #region State

        private void PushState()
        {
            _server?.Push(Snapshot());
        }

        private State Snapshot()
        {
            State state = new State
            {
                Device = _settings.Device,
                Host = _settings.Host,
                Step = _settings.StepDecibels,
                MaxVolume = _settings.MaxVolume,
                Decibels = _settings.OsdDecibels,
                ShowOsd = _settings.ShowOsd,
                OsdOnExternalChange = _settings.OsdOnExternalChange,
                AutoReconnect = _settings.AutoReconnect,
                ReconnectAttempts = _settings.ReconnectAttempts,
                ReconnectTimeoutSeconds = _settings.ReconnectTimeoutSeconds,
                KeepAudioAlive = _settings.KeepAudioAlive,
                AudioStatus = _audio?.Status ?? "Off",
                Volume = _device?.Volume,
                Muted = _device?.Muted ?? false,
                SupportsAbsoluteVolume = _device?.SupportsAbsoluteVolume ?? false,
                ZeroDecibelLevel = _device != null ? _device.FromDecibels(0) : 80
            };

            if (_device == null)
            {
                state.Link = "disconnected";
                state.Status = _settings.IsConfigured
                    ? "Unknown receiver \"" + _settings.Device + "\"."
                    : "Pick a receiver and type its IP address.";
                return state;
            }

            switch (_device.Link)
            {
                case AvrLinkState.Connected:
                    state.Link = "connected";
                    state.Status = _device.Muted ? "Connected — muted" : "Connected";
                    break;
                case AvrLinkState.Connecting:
                    state.Link = "connecting";
                    state.Status = _statusDetail ?? "Connecting...";
                    break;
                default:
                    state.Link = "disconnected";
                    state.Status = string.IsNullOrEmpty(_statusDetail)
                        ? "Disconnected — retrying"
                        : "Disconnected — " + _statusDetail;
                    break;
            }

            return state;
        }

        #endregion
    }
}
