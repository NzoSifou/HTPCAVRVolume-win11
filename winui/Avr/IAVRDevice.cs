using System;

namespace HTPCAVRVolume.AVRDevices
{
    class AvrStatusEventArgs : EventArgs
    {
        public AvrStatusEventArgs(bool volumeChanged, bool muteChanged, bool external)
        {
            VolumeChanged = volumeChanged;
            MuteChanged = muteChanged;
            External = external;
        }

        public bool VolumeChanged { get; }

        public bool MuteChanged { get; }

        /// <summary>True when the change came from the AVR itself: its remote, or its front panel.</summary>
        public bool External { get; }
    }

    /// <summary>
    /// An amplifier we can drive. Volumes are expressed in the AVR's own scale (0 to 98 on a
    /// Denon); <see cref="ToDecibels"/> converts to what the user reads on the front panel.
    /// </summary>
    interface IAVRDevice : IDisposable
    {
        event EventHandler<AvrStatusEventArgs> StatusChanged;

        event EventHandler<LinkEventArgs> LinkChanged;

        /// <summary>State of the control session right now.</summary>
        AvrLinkState Link { get; }

        /// <summary>Starts trying again after the link gave up.</summary>
        void Retry();

        /// <summary>
        /// True when the device can be told to go straight to a level. A wheel flick then costs
        /// one command instead of one per detent.
        /// </summary>
        bool SupportsAbsoluteVolume { get; }

        /// <summary>Last level reported by the AVR, or null while we do not know it yet.</summary>
        double? Volume { get; }

        /// <summary>Ceiling the AVR itself enforces, when it tells us about one.</summary>
        double? MaxVolume { get; }

        bool Muted { get; }

        /// <summary>Smallest level change the AVR accepts.</summary>
        double VolumeQuantum { get; }

        double ToDecibels(double volume);

        double FromDecibels(double decibels);

        void SetVolume(double volume);

        void Step(int steps);

        void SetMute(bool muted);
    }
}
