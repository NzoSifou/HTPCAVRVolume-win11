using System;
using System.Collections.Generic;
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

        /// <summary>
        /// The whole main-zone volume menu comes back from this. Asking for the display setting
        /// on its own, with SSVCTZMADIS ?, is answered with silence; asking for the menu is
        /// answered with four lines, one of which is the one we want.
        /// </summary>
        private const string DisplayQuery = "SSVCTZMA ?";

        private const string DisplayPrefix = "SSVCTZMADIS";

        /// <summary>
        /// A zone's own volume menu: SSVCTZ2S ? answers with four lines, of which SSVCTZ2SLIM is
        /// the ceiling set in Setup > General > Zone2 Setup > Limit. The main zone's equivalent,
        /// SSVCTZMALIM, is not used: main learns its ceiling from MVMAX.
        /// </summary>
        private const string ZoneMenuHead = "SSVCTZ";

        private const string ZoneLimitTail = "SLIM";

        /// <summary>
        /// The main zone's own power. PW is the whole unit: it reports ON as soon as any zone is
        /// on, so reading it as the main zone lights a button for a room that is silent, and
        /// writing PWSTANDBY to it switches the entire receiver off. ZM is the main zone alone.
        /// </summary>
        private const string MainPowerPrefix = "ZM";

        /// <summary>How long after changing a zone's Limit its level is still being watched.</summary>
        private const int LimitWatchMs = 5000;

        private const double DefaultMaxLevel = 98.0;

        /// <summary>The highest a zone's Limit menu goes; it has no "unlimited" setting.</summary>
        private const double ZoneMaxLevel = 80.0;

        /// <summary>An echo arriving this soon after we sent something is our own command coming back.</summary>
        private const int SelfEchoWindowMs = 1500;

        private readonly AvrConnection _link;
        private readonly object _gate = new object();

        private double? _volume;
        private double? _maxVolume;
        private bool _muted;
        private bool? _decibelDisplay;
        private Zone _zone = Zone.Main;

        // Set while a zone's Limit has just been written and its level is being watched, because
        // the receiver moves the level to the new ceiling as it takes it.
        private Zone? _limitZone;
        private double _limitLevel;
        private int _limitTicks;

        /// <summary>Main is always there; the rest are added as they answer for themselves.</summary>
        private readonly HashSet<Zone> _zones = new HashSet<Zone> { Zone.Main };

        /// <summary>
        /// On or off, for every zone rather than just the one being driven: the window shows a
        /// button for each and they all have to be right before anyone presses one.
        /// </summary>
        private readonly Dictionary<Zone, bool> _power = new Dictionary<Zone, bool>();
        private int _lastCommandTicks;
        private int _volumeAnswersPending;

        public DenonMarantzDevice(string host, int minCommandIntervalMs, int connectTimeoutMs, int attemptLimit)
        {
            _lastCommandTicks = Environment.TickCount - SelfEchoWindowMs;

            _link = new AvrConnection(host, 23, "\r", minCommandIntervalMs, connectTimeoutMs, attemptLimit)
            {
                HeartbeatCommand = "MV?"
            };
            _link.LineReceived += OnLineReceived;
            _link.LinkChanged += OnLinkChanged;
        }

        public event EventHandler<AvrStatusEventArgs> StatusChanged;

        public event EventHandler<LinkEventArgs> LinkChanged;

        public AvrLinkState Link => _link.State;

        public void Retry() => _link.Retry();

        public bool SupportsAbsoluteVolume => true;

        /// <summary>Main moves in half steps; the other zones only take whole ones.</summary>
        public double VolumeQuantum => _zone == Zone.Main ? 0.5 : 1;

        public Zone Zone
        {
            get { lock (_gate) { return _zone; } }

            set
            {
                lock (_gate)
                {
                    if (_zone == value)
                    {
                        return;
                    }

                    // Everything we hold describes the zone we are leaving, down to the answers
                    // we were still waiting for.
                    _zone = value;
                    _volume = null;
                    _maxVolume = null;
                    _muted = false;
                    _volumeAnswersPending = 0;
                }

                if (_link.State == AvrLinkState.Connected)
                {
                    Ask(value);
                }

                // Nothing has moved, so this must not put the on-screen display up; it only says
                // that what we report is now about somewhere else.
                StatusChanged?.Invoke(this, new AvrStatusEventArgs(false, false, true, false, true));
            }
        }

        public IReadOnlyList<Zone> AvailableZones
        {
            get
            {
                lock (_gate)
                {
                    List<Zone> zones = new List<Zone>(_zones);
                    zones.Sort();
                    return zones;
                }
            }
        }

        public bool? PowerOf(Zone zone)
        {
            lock (_gate)
            {
                return _power.TryGetValue(zone, out bool on) ? on : (bool?)null;
            }
        }

        /// <summary>
        /// Main is powered with PW, a zone with its own prefix, and standby is what "off" is
        /// called for the receiver as a whole.
        /// </summary>
        public void SetPower(Zone zone, bool on)
        {
            lock (_gate)
            {
                _power[zone] = on;
            }

            string command = (zone == Zone.Main ? MainPowerPrefix : VolumePrefix(zone))
                + (on ? "ON" : "OFF");

            _link.Send(command, "PW" + (int)zone);
            StatusChanged?.Invoke(this, new AvrStatusEventArgs(false, false, false, false, true));
        }

        /// <summary>
        /// Writes a zone's Limit, and watches what the level does next.
        ///
        /// The receiver moves the zone's level when it takes a new ceiling, and not always to
        /// the ceiling itself: going from 70 to 80 has been seen to leave a zone playing at 60.
        /// So the level is read here, at the moment of the change, and anywhere it goes within
        /// the next few seconds it is put back to what it was. A receiver that leaves it alone
        /// is left alone in turn, and a level the user moves themselves ends the watch.
        /// </summary>
        public void SetZoneLimit(Zone zone, double limit)
        {
            if (zone == Zone.Main)
            {
                return;
            }

            lock (_gate)
            {
                if (_zone == zone)
                {
                    _maxVolume = limit;

                    if (_volume.HasValue)
                    {
                        _limitZone = zone;
                        _limitLevel = _volume.Value;
                        _limitTicks = Environment.TickCount;
                    }
                }
            }

            _link.Send(ZoneMenuHead + (int)zone + ZoneLimitTail +
                " " + ((int)Math.Round(limit)).ToString("000", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Puts the level back when the receiver has just moved it to a Limit we wrote. Anything
        /// else the level does, including the user moving it themselves, ends the watch.
        /// </summary>
        /// <remarks>Caller holds the lock.</remarks>
        private void RestoreAfterLimit(double level)
        {
            if (!_limitZone.HasValue || _limitZone.Value != _zone)
            {
                return;
            }

            if (unchecked(Environment.TickCount - _limitTicks) > LimitWatchMs)
            {
                _limitZone = null;
                return;
            }

            if (Math.Abs(level - _limitLevel) <= 0.01)
            {
                // Still where it was. Nothing to undo, and worth watching a moment longer.
                return;
            }

            // It moved, and nobody asked it to. Back to the level read when the ceiling was
            // written, except that the new ceiling still has the last word.
            Zone zone = _limitZone.Value;
            double wanted = _maxVolume.HasValue ? Math.Min(_limitLevel, _maxVolume.Value) : _limitLevel;
            _limitZone = null;
            _volume = wanted;

            Expect(_link.Send(VolumePrefix(zone) + Encode(wanted, zone), VolumePrefix(zone)));
        }

        /// <summary>
        /// What a zone's commands are called. Main speaks as MV and MU, the others as Z2, Z3 and
        /// Z4 with their mute hanging off the same prefix.
        /// </summary>
        private static string VolumePrefix(Zone zone)
        {
            return zone == Zone.Main ? "MV" : "Z" + (int)zone;
        }

        private static string MutePrefix(Zone zone)
        {
            return zone == Zone.Main ? "MU" : "Z" + (int)zone + "MU";
        }

        public bool SupportsDisplayUnit => true;

        public bool? DecibelDisplay
        {
            get { lock (_gate) { return _decibelDisplay; } }
        }

        /// <summary>
        /// REL is the receiver's relative scale, which reads in decibels; ABS is its absolute
        /// one, 0 to 98. Everyone in the house reads the front panel, so this is a change to
        /// the receiver itself and not a preference of ours.
        /// </summary>
        public void SetDecibelDisplay(bool decibels)
        {
            lock (_gate)
            {
                _decibelDisplay = decibels;
            }

            _link.Send(DisplayPrefix + " " + (decibels ? "REL" : "ABS"), DisplayPrefix);
        }

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
                    Expect(_link.Send(VolumePrefix(_zone) + Encode(target, _zone), VolumePrefix(_zone)));
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

                // Only used before the AVR has told us where it is; the query is already on its
                // way, so a single step is enough to stay responsive without guessing at a level.
                Expect(_link.Send(VolumePrefix(_zone) + (steps > 0 ? "UP" : "DOWN")));
            }
        }

        public void SetMute(bool muted)
        {
            lock (_gate)
            {
                _muted = muted;
                _lastCommandTicks = Environment.TickCount;
            }

            _link.Send(MutePrefix(_zone) + (muted ? "ON" : "OFF"), MutePrefix(_zone));
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
                _link.Send(MainPowerPrefix + "?");
                _link.Send(DisplayQuery);

                // Which zones this receiver even has: one it does not have never answers, so
                // asking costs a command and no waiting.
                for (int zone = 2; zone <= 4; zone++)
                {
                    if ((int)_zone != zone)
                    {
                        _link.Send("Z" + zone + "?");
                    }
                }

                Ask(_zone);
            }
            else if (e.State == AvrLinkState.Disconnected)
            {
                bool hadVolume;
                lock (_gate)
                {
                    _power.Clear();
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

        /// <summary>Asks one zone about itself: its mute, its ceiling and its level, in that order.</summary>
        private void Ask(Zone zone)
        {
            _link.Send(MutePrefix(zone) + "?");

            if (zone != Zone.Main)
            {
                _link.Send(ZoneMenuHead + (int)zone + "S ?");
            }

            lock (_gate)
            {
                Expect(_link.Send(VolumePrefix(zone) + "?"));
            }
        }

        private void OnLineReceived(object sender, LineEventArgs e)
        {
            string line = e.Line.Trim();
            bool volumeChanged = false;
            bool muteChanged = false;
            bool maximumChanged = false;
            bool displayChanged = false;
            bool zonesChanged = false;
            bool external;

            lock (_gate)
            {
                external = unchecked(Environment.TickCount - _lastCommandTicks) > SelfEchoWindowMs;

                // Anything said under a zone's prefix proves that zone is there, whatever the
                // rest of the line turns out to be about.
                Zone? spoken = ZoneOfLine(line);
                if (spoken.HasValue && _zones.Add(spoken.Value))
                {
                    zonesChanged = true;
                }

                bool understood = Interpret(line, ref volumeChanged, ref muteChanged,
                    ref maximumChanged, ref displayChanged, ref zonesChanged);

                if (!understood && !zonesChanged)
                {
                    return;
                }
            }

            if (volumeChanged || muteChanged || maximumChanged || displayChanged || zonesChanged)
            {
                StatusChanged?.Invoke(this, new AvrStatusEventArgs(
                    volumeChanged, muteChanged, external, displayChanged, zonesChanged));
            }
        }

        /// <summary>The zone a line belongs to, when it is spoken under one.</summary>
        private static Zone? ZoneOfLine(string line)
        {
            if (line.Length < 3 || line[0] != 'Z' || line[1] < '2' || line[1] > '4')
            {
                return null;
            }

            return (Zone)(line[1] - '0');
        }

        /// <summary>
        /// Applies one line to what we hold about the zone we are on. Lines belonging to any
        /// other zone are read for nothing but the fact that the zone exists.
        /// </summary>
        /// <returns>True when the line changed something worth reporting.</returns>
        /// <remarks>Caller holds the lock.</remarks>
        private bool Interpret(string line, ref bool volumeChanged, ref bool muteChanged,
            ref bool maximumChanged, ref bool displayChanged, ref bool zonesChanged)
        {
            if (TakePower(line))
            {
                // Says so out loud: a zone going on or off is not a change to the volume, and
                // without a flag of its own nobody outside would ever hear about it.
                zonesChanged = true;
                return true;
            }

            if (line.StartsWith(DisplayPrefix, StringComparison.Ordinal))
            {
                // One setting for the whole receiver, sent back to every open session, so this
                // arrives whether the change was made here, on a remote or in its own menu.
                string unit = line.Substring(DisplayPrefix.Length).Trim();
                bool? decibels = unit == "REL" ? true : unit == "ABS" ? (bool?)false : null;
                if (!decibels.HasValue || decibels == _decibelDisplay)
                {
                    return false;
                }

                _decibelDisplay = decibels;
                displayChanged = true;
                return true;
            }

            if (_zone == Zone.Main && line.StartsWith("MVMAX", StringComparison.Ordinal))
            {
                // The ceiling the receiver is configured for, which is not always the 98 a
                // Denon leaves the factory with. We ask for it once, when the session opens,
                // and keep that first answer: receivers repeat this line as the volume moves
                // and not all of them repeat the same number, and a scale that rescales
                // itself under the user is worse than one that is slightly stale.
                if (_maxVolume.HasValue)
                {
                    return false;
                }

                double? max = ParseLevel(line.Substring(5).Trim());
                if (!max.HasValue)
                {
                    return false;
                }

                _maxVolume = max;
                maximumChanged = true;
                return true;
            }

            if (line.StartsWith(ZoneMenuHead, StringComparison.Ordinal))
            {
                return TakeZoneLimit(line, ref maximumChanged);
            }

            string mute = MutePrefix(_zone);
            if (line == mute + "ON" || line == mute + "OFF")
            {
                bool muted = line.EndsWith("ON", StringComparison.Ordinal);
                muteChanged = muted != _muted;
                _muted = muted;
                return muteChanged;
            }

            string prefix = VolumePrefix(_zone);
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                double? level = ParseLevel(line.Substring(prefix.Length));
                if (!level.HasValue || !IsLatestAnswer())
                {
                    return false;
                }

                volumeChanged = !_volume.HasValue || Math.Abs(_volume.Value - level.Value) > 0.01;
                _volume = level;

                if (_limitZone.HasValue)
                {
                    RestoreAfterLimit(level.Value);
                }

                return volumeChanged;
            }

            return false;
        }

        /// <summary>
        /// Notes a zone going on or off. Every zone is followed, not just the one being driven,
        /// and the match is exact: Z2MUON and Z2SLPOFF are about other things entirely.
        /// </summary>
        /// <remarks>Caller holds the lock.</remarks>
        private bool TakePower(string line)
        {
            Zone zone;
            bool on;

            if (line == "PWSTANDBY")
            {
                // The whole receiver has gone to sleep, so every zone went with it. PWON is not
                // the mirror of this: it only says the unit is awake, which one zone is enough
                // for, and says nothing about the main one.
                bool any = false;
                foreach (Zone asleep in new List<Zone>(_zones))
                {
                    if (!_power.TryGetValue(asleep, out bool was) || was)
                    {
                        _power[asleep] = false;
                        any = true;
                    }
                }

                return any;
            }

            if (line == MainPowerPrefix + "ON" || line == MainPowerPrefix + "OFF")
            {
                zone = Zone.Main;
                on = line.EndsWith("ON", StringComparison.Ordinal);
            }
            else
            {
                Zone? spoken = ZoneOfLine(line);
                string rest = spoken.HasValue ? line.Substring(2) : null;
                if (!spoken.HasValue || (rest != "ON" && rest != "OFF"))
                {
                    return false;
                }

                zone = spoken.Value;
                on = rest == "ON";
            }

            if (_power.TryGetValue(zone, out bool known) && known == on)
            {
                return false;
            }

            _power[zone] = on;
            return true;
        }

        /// <summary>
        /// Reads SSVCTZ2SLIM 080 and the like: the ceiling set in that zone's own menu, which is
        /// what a zone has instead of MVMAX. A zone's menu offers 60, 70 and 80 and nothing
        /// higher, so OFF, if a receiver ever says it, means the top of that.
        /// </summary>
        /// <remarks>Caller holds the lock.</remarks>
        private bool TakeZoneLimit(string line, ref bool maximumChanged)
        {
            int digitAt = ZoneMenuHead.Length;
            if (line.Length <= digitAt || line[digitAt] < '2' || line[digitAt] > '4')
            {
                return false;
            }

            if ((int)_zone != line[digitAt] - '0')
            {
                // Another zone's menu. Worth nothing to us while we are not on it.
                return false;
            }

            string rest = line.Substring(digitAt + 1);
            if (!rest.StartsWith(ZoneLimitTail, StringComparison.Ordinal) || _maxVolume.HasValue)
            {
                return false;
            }

            string value = rest.Substring(ZoneLimitTail.Length).Trim();
            if (value == "OFF")
            {
                _maxVolume = ZoneMaxLevel;
            }
            else if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int limit)
                     && limit > 0 && limit <= DefaultMaxLevel)
            {
                // Three digits, and whole: 080 is eighty, not eight.
                _maxVolume = limit;
            }
            else
            {
                return false;
            }

            maximumChanged = true;
            return true;
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
            if (_zone != Zone.Main)
            {
                return Math.Round(volume, MidpointRounding.AwayFromZero);
            }

            return Math.Round(volume * 2, MidpointRounding.AwayFromZero) / 2;
        }

        /// <summary>
        /// 45 goes out as "45", 45.5 as "455": the half step is a third digit, not a decimal
        /// point. Only the main zone has half steps; the others take two digits and nothing else.
        /// </summary>
        private static string Encode(double volume, Zone zone)
        {
            int whole = (int)Math.Floor(volume);
            bool half = zone == Zone.Main && volume - whole >= 0.25;
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
