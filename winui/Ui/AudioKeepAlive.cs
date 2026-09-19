using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace HTPCAVRVolume.Ui
{
    /// <summary>
    /// Holds a silent stream open on the receiver's audio endpoint.
    ///
    /// Windows powers the audio path down between playback sessions, and bringing it back costs a
    /// second or three of missing sound, sometimes with a pop. A shared-mode stream of silence
    /// keeps the endpoint running, at the price of the receiver never being told it can idle.
    ///
    /// It stands aside for anything that wants the device to itself. A player bit-streaming
    /// TrueHD or DTS-HD takes the endpoint in exclusive mode, which disconnects our session; we
    /// let go at once and try again a few seconds after it has finished, rather than fighting it.
    ///
    /// Everything happens on its own MTA thread: WASAPI is COM, and the window's thread is not
    /// the place to wait on it.
    /// </summary>
    sealed class AudioKeepAlive : IDisposable
    {
        /// <summary>How long to stay away once something else has claimed the device.</summary>
        private const int YieldSeconds = 10;

        /// <summary>Latency asked of WASAPI. Longer means fewer wake-ups for the same silence.</summary>
        private const int BufferMilliseconds = 500;

        private readonly object _gate = new object();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Action<string> _report;

        private Thread _thread;
        private volatile bool _stopping;
        private volatile bool _enabled;
        private volatile string _status = "Off";

        private WasapiOut _output;
        private MMDevice _device;
        private AudioSessionControl _session;
        private MMDeviceEnumerator _enumerator;
        private NotificationClient _notifications;
        private int _yieldUntilTicks;

        /// <param name="report">Called with a short line whenever the state changes.</param>
        public AudioKeepAlive(Action<string> report)
        {
            _report = report;
        }

        public string Status => _status;

        public bool IsEnabled => _enabled;

        /// <summary>Turns the silent stream on or off. Returns at once; the work is elsewhere.</summary>
        public void Enable(bool on)
        {
            if (_enabled == on)
            {
                return;
            }

            _enabled = on;

            if (on && _thread == null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "Audio keep-alive", Priority = ThreadPriority.BelowNormal };
                _thread.SetApartmentState(ApartmentState.MTA);
                _thread.Start();
            }

            _wake.Set();
        }

        public void Dispose()
        {
            _stopping = true;
            _enabled = false;
            _wake.Set();

            Thread thread = _thread;
            _thread = null;
            thread?.Join(2000);
        }

        private void Run()
        {
            try
            {
                _enumerator = new MMDeviceEnumerator();
                _notifications = new NotificationClient(this);
                _enumerator.RegisterEndpointNotificationCallback(_notifications);
            }
            catch (Exception ex)
            {
                SetStatus("Unavailable: " + ex.Message);
                return;
            }

            while (!_stopping)
            {
                try
                {
                    if (_enabled)
                    {
                        Tend();
                    }
                    else if (_output != null)
                    {
                        Release("Off");
                    }
                }
                catch (Exception ex)
                {
                    Release("Interrupted: " + ex.Message);
                    _yieldUntilTicks = Environment.TickCount + YieldSeconds * 1000;
                }

                // Woken by a device change or by Enable; the timeout is what makes the yield end.
                _wake.WaitOne(1000);
            }

            Release(null);

            try
            {
                _enumerator?.UnregisterEndpointNotificationCallback(_notifications);
                _enumerator?.Dispose();
            }
            catch
            {
                // Shutting down anyway.
            }
        }

        /// <summary>One pass: open the stream if it should be open, notice if it has gone.</summary>
        private void Tend()
        {
            if (_output != null)
            {
                if (_output.PlaybackState == PlaybackState.Playing)
                {
                    return;
                }

                // Stopped on its own: something took the device, or it went away.
                Release("Yielded, retrying in " + YieldSeconds + " s");
                _yieldUntilTicks = Environment.TickCount + YieldSeconds * 1000;
                return;
            }

            if (unchecked(Environment.TickCount - _yieldUntilTicks) < 0)
            {
                return;
            }

            MMDevice device = DefaultEndpoint();
            if (device == null)
            {
                SetStatus("No active playback device");
                return;
            }

            try
            {
                WasapiOut output = new WasapiOut(device, AudioClientShareMode.Shared, false, BufferMilliseconds);
                output.Init(new SilenceProvider(output.OutputWaveFormat));
                output.PlaybackStopped += OnPlaybackStopped;
                output.Play();

                _device = device;
                _output = output;

                Listen(device);
                SetStatus("Holding " + Short(device.FriendlyName) + " awake");
            }
            catch (Exception ex)
            {
                device.Dispose();

                // Almost always an exclusive-mode application already holding the endpoint.
                SetStatus("Device busy, retrying in " + YieldSeconds + " s");
                _yieldUntilTicks = Environment.TickCount + YieldSeconds * 1000;
                _ = ex;
            }
        }

        private MMDevice DefaultEndpoint()
        {
            try
            {
                MMDevice device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                if (device != null && device.State == DeviceState.Active)
                {
                    return device;
                }

                device?.Dispose();
            }
            catch
            {
                // No endpoint at all, which is a normal state to be in.
            }

            return null;
        }

        /// <summary>Asks to be told when something else claims the session.</summary>
        private void Listen(MMDevice device)
        {
            try
            {
                _session = device.AudioSessionManager.AudioSessionControl;
                _session.RegisterEventClient(new SessionEvents(this));
            }
            catch
            {
                // Without session events the polling in Tend still notices, just a beat later.
                _session = null;
            }
        }

        private void OnPlaybackStopped(object sender, StoppedEventArgs e)
        {
            _wake.Set();
        }

        /// <summary>Something else wants the device: let go now and stay away for a while.</summary>
        internal void Yield(string why)
        {
            lock (_gate)
            {
                _yieldUntilTicks = Environment.TickCount + YieldSeconds * 1000;
            }

            SetStatus(why + ", retrying in " + YieldSeconds + " s");
            _wake.Set();
        }

        internal void DeviceChanged()
        {
            // The endpoint moved, most often because headphones were plugged in or pulled out.
            lock (_gate)
            {
                _yieldUntilTicks = Environment.TickCount;
            }

            _wake.Set();
        }

        private void Release(string status)
        {
            WasapiOut output = _output;
            _output = null;

            if (output != null)
            {
                try
                {
                    output.PlaybackStopped -= OnPlaybackStopped;
                    output.Stop();
                    output.Dispose();
                }
                catch
                {
                    // The device is already gone in most of the cases that land here.
                }
            }

            _session = null;

            MMDevice device = _device;
            _device = null;
            try
            {
                device?.Dispose();
            }
            catch
            {
                // Same.
            }

            if (status != null)
            {
                SetStatus(status);
            }
        }

        private void SetStatus(string status)
        {
            if (_status == status)
            {
                return;
            }

            _status = status;
            _report?.Invoke(status);
        }

        private static string Short(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "the output";
            }

            return name.Length > 28 ? name.Substring(0, 27) + "…" : name;
        }

        /// <summary>The device taken away underneath us, or the default one changing.</summary>
        private sealed class NotificationClient : IMMNotificationClient
        {
            private readonly AudioKeepAlive _owner;

            public NotificationClient(AudioKeepAlive owner)
            {
                _owner = owner;
            }

            public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _owner.DeviceChanged();

            public void OnDeviceAdded(string deviceId) => _owner.DeviceChanged();

            public void OnDeviceRemoved(string deviceId) => _owner.DeviceChanged();

            public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
            {
                if (flow == DataFlow.Render)
                {
                    _owner.DeviceChanged();
                }
            }

            public void OnPropertyValueChanged(string deviceId, PropertyKey key)
            {
            }
        }

        /// <summary>Exclusive-mode players announce themselves by disconnecting our session.</summary>
        private sealed class SessionEvents : IAudioSessionEventsHandler
        {
            private readonly AudioKeepAlive _owner;

            public SessionEvents(AudioKeepAlive owner)
            {
                _owner = owner;
            }

            public void OnSessionDisconnected(AudioSessionDisconnectReason reason)
            {
                _owner.Yield(reason == AudioSessionDisconnectReason.DisconnectReasonExclusiveModeOverride
                    ? "Another app took the device"
                    : "Session ended");
            }

            public void OnVolumeChanged(float volume, bool isMuted)
            {
            }

            public void OnDisplayNameChanged(string displayName)
            {
            }

            public void OnIconPathChanged(string iconPath)
            {
            }

            public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex)
            {
            }

            public void OnGroupingParamChanged(ref Guid groupingId)
            {
            }

            public void OnStateChanged(AudioSessionState state)
            {
            }
        }
    }
}
