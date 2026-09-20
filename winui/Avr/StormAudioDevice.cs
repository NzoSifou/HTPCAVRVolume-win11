using System;
using System.Collections.Generic;

namespace HTPCAVRVolume.AVRDevices
{
    /// <summary>
    /// StormAudio telnet control (port 23).
    ///
    /// Same stepping commands as before, but sent over a session that stays open and a queue that
    /// spaces them out, so a fast wheel can no longer stall the keyboard hook. No absolute level:
    /// the app therefore keeps stepping, and the on-screen display only reports mute for these.
    /// </summary>
    class StormAudioDevice : IAVRDevice
    {
        /// <summary>A wheel flick is worth this many steps at most, so the AVR keeps up.</summary>
        private const int MaxStepsPerFlush = 8;

        private readonly AvrConnection _link;
        private bool _muted;

        public StormAudioDevice(string host, int minCommandIntervalMs, int connectTimeoutMs, int attemptLimit)
        {
            _link = new AvrConnection(host, 23, "\r", minCommandIntervalMs, connectTimeoutMs, attemptLimit);
            _link.LinkChanged += OnLinkChanged;
        }

        public event EventHandler<AvrStatusEventArgs> StatusChanged;

        public event EventHandler<LinkEventArgs> LinkChanged;

        public AvrLinkState Link => _link.State;

        public void Retry() => _link.Retry();

        public bool SupportsAbsoluteVolume => false;

        public double VolumeQuantum => 1;

        /// <summary>No setting of its own to follow, so the app keeps its own preference.</summary>
        public bool SupportsDisplayUnit => false;

        public bool? DecibelDisplay => null;

        public void SetDecibelDisplay(bool decibels)
        {
        }

        /// <summary>One zone, and no way to ask for another.</summary>
        public Zone Zone
        {
            get { return Zone.Main; }
            set { }
        }

        public IReadOnlyList<Zone> AvailableZones { get; } = new[] { Zone.Main };

        public bool? PowerOf(Zone zone)
        {
            return null;
        }

        public void SetPower(Zone zone, bool on)
        {
        }

        public void SetZoneLimit(Zone zone, double limit)
        {
        }

        public double? Volume => null;

        public double? MaxVolume => null;

        public bool Muted => _muted;

        public double ToDecibels(double volume) => volume;

        public double FromDecibels(double decibels) => decibels;

        public void SetVolume(double volume)
        {
            throw new NotSupportedException("StormAudio is driven by steps, not by an absolute level.");
        }

        public void Step(int steps)
        {
            if (steps == 0)
            {
                return;
            }

            int count = Math.Min(Math.Abs(steps), MaxStepsPerFlush);
            string command = steps > 0 ? "ssp.vol.up" : "ssp.vol.down";
            for (int i = 0; i < count; i++)
            {
                _link.Send(command);
            }
        }

        public void SetMute(bool muted)
        {
            _muted = muted;
            _link.Send("ssp.mute.toggle", "mute");
            StatusChanged?.Invoke(this, new AvrStatusEventArgs(false, true, false));
        }

        public void Dispose()
        {
            _link.LinkChanged -= OnLinkChanged;
            _link.Dispose();
        }

        private void OnLinkChanged(object sender, LinkEventArgs e)
        {
            LinkChanged?.Invoke(this, e);
        }
    }
}
