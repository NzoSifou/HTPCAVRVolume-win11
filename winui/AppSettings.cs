using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HTPCAVRVolume.AVRDevices;

namespace HTPCAVRVolume
{
    /// <summary>
    /// The three things that are answered differently for each set of speakers: how big a step
    /// is, how loud the app will go, and whether the level is read in decibels. Someone running
    /// a second room quietly at whole steps should not have to set that again every time they
    /// come back to it.
    /// </summary>
    class ZoneSettings
    {
        public ZoneSettings(double step)
        {
            StepDecibels = step;
        }

        public double StepDecibels { get; set; }

        public double MaxVolume { get; set; } = 98;

        /// <summary>Set once the user has chosen a maximum of their own, which the receiver's
        /// answer is then no longer allowed to overwrite.</summary>
        public bool MaxVolumeIsManual { get; set; }

        public bool Decibels { get; set; } = true;
    }

    /// <summary>
    /// The config file next to the executable. The first line keeps the original
    /// "Device=IP" shape so an existing install carries over; everything after it is
    /// one setting per line, and an unknown key is ignored rather than fatal.
    /// </summary>
    class AppSettings
    {
        /// <summary>
        /// Main moves in half steps, the other zones only in whole ones, so they do not start
        /// from the same place.
        /// </summary>
        private readonly Dictionary<Zone, ZoneSettings> _zones = new Dictionary<Zone, ZoneSettings>
        {
            { Zone.Main, new ZoneSettings(0.5) },
            { Zone.Zone2, new ZoneSettings(1) },
            { Zone.Zone3, new ZoneSettings(1) },
            { Zone.Zone4, new ZoneSettings(1) }
        };

        public string Device { get; set; } = string.Empty;

        public string Host { get; set; } = string.Empty;

        /// <summary>The zone the keys, the slider and the display are about.</summary>
        public Zone ActiveZone { get; set; } = Zone.Main;

        public ZoneSettings For(Zone zone)
        {
            return _zones.TryGetValue(zone, out ZoneSettings settings) ? settings : _zones[Zone.Main];
        }

        private ZoneSettings Current
        {
            get { return For(ActiveZone); }
        }

        // The rest of the app asks about "the" step, maximum and unit, and means the ones
        // belonging to whichever zone is selected.

        public double StepDecibels
        {
            get { return Current.StepDecibels; }
            set { Current.StepDecibels = value; }
        }

        /// <summary>
        /// Top of the on-screen display's scale, in the AVR's own units. 98 is a Denon's factory
        /// maximum, but a receiver can be configured lower -- in the main zone's own menu, or in
        /// a zone's Limit. Only used when the AVR does not report one, which most of them do as
        /// soon as we connect.
        /// </summary>
        public double MaxVolume
        {
            get { return Current.MaxVolume; }
            set { Current.MaxVolume = value; }
        }

        public bool MaxVolumeIsManual
        {
            get { return Current.MaxVolumeIsManual; }
            set { Current.MaxVolumeIsManual = value; }
        }

        /// <summary>How long wheel ticks are gathered before being sent as one command.</summary>
        public int FlushIntervalMs { get; set; } = 40;

        /// <summary>Minimum spacing between two commands on the wire.</summary>
        public int MinCommandIntervalMs { get; set; } = 60;

        /// <summary>Keep trying to reach the receiver on its own after a failure.</summary>
        public bool AutoReconnect { get; set; } = true;

        /// <summary>How many attempts in a row may fail before the app stops trying.</summary>
        public int ReconnectAttempts { get; set; } = 3;

        /// <summary>How long one attempt waits for the receiver before calling it a failure.</summary>
        public int ReconnectTimeoutSeconds { get; set; } = 3;

        /// <summary>
        /// Hold a silent stream open on the audio endpoint, so Windows does not power down the
        /// receiver's audio path between tracks.
        /// </summary>
        public bool KeepAudioAlive { get; set; }

        /// <summary>
        /// Send the window to the notification area when it is minimised, rather than to the
        /// taskbar. It is closed outright either way: reopening it costs half a second, and a
        /// window nobody is looking at has no business holding eighty megabytes of XAML.
        /// </summary>
        public bool MinimiseToTray { get; set; }

        /// <summary>
        /// What the close button does. On by default, which is what the app has always done:
        /// the window goes, the volume keys and the overlay stay. Turn it off and the close
        /// button quits everything, the way a window with no tray icon behaves.
        /// </summary>
        public bool CloseToTray { get; set; } = true;

        public bool ShowOsd { get; set; } = true;

        public bool OsdDecibels
        {
            get { return Current.Decibels; }
            set { Current.Decibels = value; }
        }

        /// <summary>Also show the display when the level is changed from the AVR's own remote.</summary>
        public bool OsdOnExternalChange { get; set; } = true;

        public int OsdDurationMs { get; set; } = 1600;

        public bool IsConfigured
        {
            get { return !string.IsNullOrWhiteSpace(Device) && !string.IsNullOrWhiteSpace(Host); }
        }

        public static AppSettings Load(string path)
        {
            AppSettings settings = new AppSettings();
            if (!File.Exists(path))
            {
                return settings;
            }

            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();

                if (i == 0)
                {
                    settings.Device = key;
                    settings.Host = value;
                    continue;
                }

                settings.Apply(key, value);
            }

            return settings;
        }

        public void Save(string path)
        {
            ZoneSettings main = For(Zone.Main);

            List<string> lines = new List<string>
            {
                Device + "=" + Host,
                "Zone=" + ActiveZone,
                "StepDecibels=" + Format(main.StepDecibels),
                "MaxVolume=" + Format(main.MaxVolume),
                "MaxVolumeIsManual=" + main.MaxVolumeIsManual,
                "FlushIntervalMs=" + FlushIntervalMs.ToString(CultureInfo.InvariantCulture),
                "MinCommandIntervalMs=" + MinCommandIntervalMs.ToString(CultureInfo.InvariantCulture),
                "AutoReconnect=" + AutoReconnect,
                "ReconnectAttempts=" + ReconnectAttempts.ToString(CultureInfo.InvariantCulture),
                "ReconnectTimeoutSeconds=" + ReconnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                "KeepAudioAlive=" + KeepAudioAlive,
                "MinimiseToTray=" + MinimiseToTray,
                "CloseToTray=" + CloseToTray,
                "ShowOsd=" + ShowOsd,
                "OsdDecibels=" + main.Decibels,
                "OsdOnExternalChange=" + OsdOnExternalChange,
                "OsdDurationMs=" + OsdDurationMs.ToString(CultureInfo.InvariantCulture)
            };

            // The main zone keeps the key names it has always had, so a settings file written by
            // an older build still reads correctly; the others are written beside it.
            foreach (Zone zone in new[] { Zone.Zone2, Zone.Zone3, Zone.Zone4 })
            {
                ZoneSettings z = For(zone);
                string prefix = zone + ".";
                lines.Add(prefix + "StepDecibels=" + Format(z.StepDecibels));
                lines.Add(prefix + "MaxVolume=" + Format(z.MaxVolume));
                lines.Add(prefix + "MaxVolumeIsManual=" + z.MaxVolumeIsManual);
                lines.Add(prefix + "OsdDecibels=" + z.Decibels);
            }

            File.WriteAllLines(path, lines);
        }

        private void Apply(string key, string value)
        {
            foreach (Zone zone in new[] { Zone.Zone2, Zone.Zone3, Zone.Zone4 })
            {
                string prefix = zone + ".";
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    ApplyToZone(For(zone), key.Substring(prefix.Length), value);
                    return;
                }
            }

            switch (key)
            {
                case "Zone":
                    ActiveZone = Enum.TryParse(value, out Zone parsed) ? parsed : ActiveZone;
                    break;
                case "StepDecibels":
                case "MaxVolume":
                case "MaxVolumeIsManual":
                case "OsdDecibels":
                    // Unprefixed: the main zone, the way every earlier version wrote it.
                    ApplyToZone(For(Zone.Main), key, value);
                    break;
                case "FlushIntervalMs":
                    FlushIntervalMs = ParseInt(value, FlushIntervalMs);
                    break;
                case "MinCommandIntervalMs":
                    MinCommandIntervalMs = ParseInt(value, MinCommandIntervalMs);
                    break;
                case "AutoReconnect":
                    AutoReconnect = ParseBool(value, AutoReconnect);
                    break;
                case "ReconnectAttempts":
                    ReconnectAttempts = ParseInt(value, ReconnectAttempts);
                    break;
                case "ReconnectTimeoutSeconds":
                    ReconnectTimeoutSeconds = ParseInt(value, ReconnectTimeoutSeconds);
                    break;
                case "KeepAudioAlive":
                    KeepAudioAlive = ParseBool(value, KeepAudioAlive);
                    break;
                case "MinimiseToTray":
                    MinimiseToTray = ParseBool(value, MinimiseToTray);
                    break;
                case "CloseToTray":
                    CloseToTray = ParseBool(value, CloseToTray);
                    break;
                case "ShowOsd":
                    ShowOsd = ParseBool(value, ShowOsd);
                    break;
                case "OsdOnExternalChange":
                    OsdOnExternalChange = ParseBool(value, OsdOnExternalChange);
                    break;
                case "OsdDurationMs":
                    OsdDurationMs = ParseInt(value, OsdDurationMs);
                    break;
            }
        }

        private static void ApplyToZone(ZoneSettings zone, string key, string value)
        {
            switch (key)
            {
                case "StepDecibels":
                    zone.StepDecibels = ParseDouble(value, zone.StepDecibels);
                    break;
                case "MaxVolume":
                    zone.MaxVolume = ParseDouble(value, zone.MaxVolume);
                    break;
                case "MaxVolumeIsManual":
                    zone.MaxVolumeIsManual = ParseBool(value, zone.MaxVolumeIsManual);
                    break;
                case "OsdDecibels":
                    zone.Decibels = ParseBool(value, zone.Decibels);
                    break;
            }
        }

        // Written and read culture invariant on purpose: a config file saved on a machine using
        // a comma as the decimal separator has to remain readable everywhere else.
        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static double ParseDouble(string value, double fallback)
        {
            double parsed;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private static int ParseInt(string value, int fallback)
        {
            int parsed;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private static bool ParseBool(string value, bool fallback)
        {
            bool parsed;
            return bool.TryParse(value, out parsed) ? parsed : fallback;
        }
    }
}
