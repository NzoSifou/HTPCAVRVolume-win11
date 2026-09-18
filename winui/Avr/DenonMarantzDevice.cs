using System;
using System.Globalization;

namespace HTPCAVRVolume.AVRDevices
{
    /// <summary>
    /// Denon and Marantz telnet control (port 23).
    ///
    /// The session stays open, which buys two things: an absolute "MV45" lands in a single
    /// round trip instead of a connect per volume step, and the AVR pushes every change back at
    /// us, so we always know the real level -- including when someone uses the AVR's own remote.
    /// </summary>
    class DenonMarantzDevice : IAVRDevice
    {
        /// <summary>The wire scale runs 0 to 98 and reports 0.0 dB as 80.</summary>
        private const double ZeroDecibelLevel = 80.0;

        private const double DefaultMaxLevel = 98.0;

        /// <summary>An echo arriving this soon after we sent something is our own command coming back.</summary>
        private const int SelfEchoWindowMs = 1500;

        private readonly AvrConnection _link;
        private readonly object _gate = new object();

        private double? _volume;
        private double? _maxVolume;
        private bool _muted;
        private int _lastCommandTicks;
        private int _volumeAnswersPending;

        public DenonMarantzDevice(string host, int minCommandIntervalMs)
        {
            _lastCommandTicks = Environment.TickCount - SelfEchoWindowMs;

            _link = new AvrConnection(host, 23, "\r", minCommandIntervalMs) { HeartbeatCommand = "MV?" };
            _link.LineReceived += OnLineReceived;
            _link.LinkChanged += OnLinkChanged;
        }

        public event EventHandler<AvrStatusEventArgs> StatusChanged;

        public event EventHandler<LinkEventArgs> LinkChanged;

        public AvrLinkState Link => _link.State;

        public bool SupportsAbsoluteVolume => true;

        public double VolumeQuantum => 0.5;

        public double? Volume
        {
            get { lock (_gate) { return _volume; } }
        }

        public double? MaxVolume
        {
            get { lock (_gate) { return _maxVolume; } }
        }

        public bool Muted
        {
            get { lock (_gate) { return _muted; } }
        }

        public double ToDecibels(double volume) => volume - ZeroDecibelLevel;

        public double FromDecibels(double decibels) => decibels + ZeroDecibelLevel;

        public void SetVolume(double volume)
        {
            double target = Clamp(Quantise(volume));

            lock (_gate)
            {
                // Once the ceiling is reached, more turns of the wheel change nothing: there is
                // no point telling the AVR again.
                bool send = !_volume.HasValue || Math.Abs(_volume.Value - target) > 0.01;
                _volume = target;
                _lastCommandTicks = Environment.TickCount;

                if (send)
                {
                    // Queued under a coalesce key: while the wheel is spinning, only the newest
                    // target survives in the queue, so the AVR never works through a backlog of
                    // stale steps. Counting happens here, under the same lock as the level we
                    // just assumed, so an answer to an earlier command cannot slip in between
                    // and be mistaken for the answer to this one.
                    Expect(_link.Send("MV" + Encode(target), "MV"));
                }
            }

            // Worth showing even when the level did not move, so the display stays up while the
            // user keeps turning against the ceiling.
            StatusChanged?.Invoke(this, new AvrStatusEventArgs(true, false, false));
        }

        public void Step(int steps)
        {
            if (steps == 0)
            {
                return;
            }

            lock (_gate)
            {
                _lastCommandTicks = Environment.TickCount;

                // Only used before the AVR has told us where it is; MV? is already on its way, so
                // a single step is enough to stay responsive without guessing at a level.
                Expect(_link.Send(steps > 0 ? "MVUP" : "MVDOWN"));
            }
        }

        public void SetMute(bool muted)
        {
            lock (_gate)
            {
                _muted = muted;
                _lastCommandTicks = Environment.TickCount;
            }

            _link.Send(muted ? "MUON" : "MUOFF", "MU");
            StatusChanged?.Invoke(this, new AvrStatusEventArgs(false, true, false));
        }

        public void Dispose()
        {
            _link.LineReceived -= OnLineReceived;
            _link.LinkChanged -= OnLinkChanged;
            _link.Dispose();
        }

        /// <summary>
        /// Notes that one more MV line is owed to us. Every MV command, query or not, is answered
        /// by exactly one, and a command that merely replaced a queued one is not owed a second.
        /// </summary>
        /// <remarks>Caller holds the lock.</remarks>
        private void Expect(bool queued)
        {
            if (queued)
            {
                _volumeAnswersPending++;
            }
        }

        private void OnLinkChanged(object sender, LinkEventArgs e)
        {
            if (e.State == AvrLinkState.Connected)
            {
                // Find out where the AVR actually is before anyone asks us to move it.
                _link.Send("PW?");
                _link.Send("MU?");

                lock (_gate)
                {
                    Expect(_link.Send("MV?"));
                }
            }
            else if (e.State == AvrLinkState.Disconnected)
            {
                bool hadVolume;
                lock (_gate)
                {
                    hadVolume = _volume.HasValue;
                    _volume = null;

                    // Answers we were waiting for died with the session.
                    _volumeAnswersPending = 0;
                }

                if (hadVolume)
                {
                    // Without the session we no longer track the remote, so the level we last saw
                    // is only a guess from here on. Jumping to a guessed absolute level would be
                    // the one way this app could get loud by surprise.
                    StatusChanged?.Invoke(this, new AvrStatusEventArgs(true, false, true));
                }
            }

            LinkChanged?.Invoke(this, e);
        }

        private void OnLineReceived(object sender, LineEventArgs e)
        {
            string line = e.Line.Trim();
            bool volumeChanged = false;
            bool muteChanged = false;
            bool maximumChanged = false;
            bool external;

            lock (_gate)
            {
                external = unchecked(Environment.TickCount - _lastCommandTicks) > SelfEchoWindowMs;

                if (line.StartsWith("MVMAX", StringComparison.Ordinal))
                {
                    // The ceiling the receiver is configured for, which is not always the 98 a
                    // Denon leaves the factory with. We ask for it once, when the session opens,
                    // and keep that first answer: receivers repeat this line as the volume moves
                    // and not all of them repeat the same number, and a scale that rescales
                    // itself under the user is worse than one that is slightly stale.
                    if (_maxVolume.HasValue)
                    {
                        return;
                    }

                    double? max = ParseLevel(line.Substring(5).Trim());
                    if (!max.HasValue)
                    {
                        return;
                    }

                    _maxVolume = max;
                    maximumChanged = true;
                }
                else if (line.StartsWith("MV", StringComparison.Ordinal))
                {
                    double? level = ParseLevel(line.Substring(2));
                    if (!level.HasValue || !IsLatestAnswer())
                    {
                        return;
                    }

                    volumeChanged = !_volume.HasValue || Math.Abs(_volume.Value - level.Value) > 0.01;
                    _volume = level;
                }
                else if (line == "MUON" || line == "MUOFF")
                {
                    bool muted = line == "MUON";
                    muteChanged = muted != _muted;
                    _muted = muted;
                }
                else
                {
                    return;
                }
            }

            if (volumeChanged || muteChanged || maximumChanged)
            {
                StatusChanged?.Invoke(this, new AvrStatusEventArgs(volumeChanged, muteChanged, external));
            }
        }

        /// <summary>
        /// Decides whether a level coming off the wire may replace what we hold. During a fast
        /// turn the AVR is still answering commands the user has moved past, and applying one
        /// would drag the volume backwards; only the answer to the last command we sent counts.
        /// Whatever that answer says is then taken as true even when it is not what we asked for,
        /// which is how a receiver refusing to go any louder gets reported honestly.
        /// </summary>
        /// <remarks>Caller holds the lock.</remarks>
        private bool IsLatestAnswer()
        {
            if (_volumeAnswersPending <= 0)
            {
                // Nothing outstanding: the AVR is telling us about a change of its own, from its
                // remote or its front panel.
                _volumeAnswersPending = 0;
                return true;
            }

            _volumeAnswersPending--;
            return _volumeAnswersPending == 0;
        }

        /// <summary>
        /// Only keeps the value inside what the protocol can express. The AVR's own reported
        /// maximum is not applied here: it is what the app offers as a default for the setting,
        /// and a receiver that cannot go that high simply answers with where it stopped.
        /// </summary>
        private double Clamp(double volume)
        {
            if (volume > DefaultMaxLevel)
            {
                return DefaultMaxLevel;
            }

            return volume < 0 ? 0 : volume;
        }

        private double Quantise(double volume)
        {
            return Math.Round(volume * 2, MidpointRounding.AwayFromZero) / 2;
        }

        /// <summary>
        /// 45 goes out as "45", 45.5 as "455": the half step is a third digit, not a decimal point.
        /// </summary>
        private static string Encode(double volume)
        {
            int whole = (int)Math.Floor(volume);
            bool half = volume - whole >= 0.25;
            string digits = whole.ToString("00", CultureInfo.InvariantCulture);
            return half ? digits + "5" : digits;
        }

        private static double? ParseLevel(string digits)
        {
            digits = digits.Trim();
            if (digits.Length == 0 || digits.Length > 3)
            {
                return null;
            }

            foreach (char c in digits)
            {
                if (c < '0' || c > '9')
                {
                    return null;
                }
            }

            if (digits.Length == 3)
            {
                // "455" is 45.5; the AVR only ever uses 5 as the third digit.
                double whole = double.Parse(digits.Substring(0, 2), CultureInfo.InvariantCulture);
                return digits[2] == '5' ? whole + 0.5 : whole;
            }

            return double.Parse(digits, CultureInfo.InvariantCulture);
        }
    }
}
