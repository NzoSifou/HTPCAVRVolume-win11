using System;
using System.Collections.Generic;

namespace HTPCAVRVolume.AVRDevices
{
    /// <summary>
    /// Which set of speakers is being driven. A receiver keeps a separate volume, mute and
    /// ceiling for each, reached through a different prefix on the wire, so picking a zone
    /// changes what every other member of the device means.
    /// </summary>
    enum Zone
    {
        Main = 1,
        Zone2 = 2,
        Zone3 = 3,
        Zone4 = 4
    }

    class AvrStatusEventArgs : EventArgs
    {
        public AvrStatusEventArgs(bool volumeChanged, bool muteChanged, bool external,
            bool displayUnitChanged = false, bool zonesChanged = false)
        {
            VolumeChanged = volumeChanged;
            MuteChanged = muteChanged;
            External = external;
            DisplayUnitChanged = displayUnitChanged;
            ZonesChanged = zonesChanged;
        }

        public bool VolumeChanged { get; }

        public bool MuteChanged { get; }

        /// <summary>The receiver has told us how it wants the volume written.</summary>
        public bool DisplayUnitChanged { get; }

        /// <summary>A zone has appeared, or one has been switched on or off.</summary>
        public bool ZonesChanged { get; }

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

        /// <summary>
        /// True when the receiver has a volume-display setting of its own. The app then follows
        /// it rather than keeping a preference of its own that could disagree with the front
        /// panel.
        /// </summary>
        bool SupportsDisplayUnit { get; }

        /// <summary>
        /// How the receiver is currently showing the volume: true for decibels, false for its
        /// own scale, null while we have not been told.
        /// </summary>
        bool? DecibelDisplay { get; }

        /// <summary>Asks the receiver to show the volume the other way round.</summary>
        void SetDecibelDisplay(bool decibels);

        /// <summary>
        /// The zone every other member here speaks about. Setting it throws away what we knew
        /// about the old one and asks the receiver about the new one.
        /// </summary>
        Zone Zone { get; set; }

        /// <summary>
        /// The zones this receiver has actually answered for, main first. A zone it does not
        /// have simply never replies, so the list fills in over the first second of a session.
        /// </summary>
        IReadOnlyList<Zone> AvailableZones { get; }

        /// <summary>Whether a zone is switched on, or null while we have not been told.</summary>
        bool? PowerOf(Zone zone);

        /// <summary>Switches a zone on or off, whichever one the app happens to be driving.</summary>
        void SetPower(Zone zone, bool on);

        /// <summary>
        /// Writes the ceiling in a zone's own Limit menu. Only meaningful for a zone: the main
        /// one keeps its maximum in the app alone.
        /// </summary>
        void SetZoneLimit(Zone zone, double limit);

        double ToDecibels(double volume);

        double FromDecibels(double decibels);

        void SetVolume(double volume);

        void Step(int steps);

        void SetMute(bool muted);
    }
}
