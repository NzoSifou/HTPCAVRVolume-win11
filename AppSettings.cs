using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace HTPCAVRVolume
{
    /// <summary>
    /// The config file next to the executable. The first line keeps the original
    /// "Device=IP" shape so an existing install carries over; everything after it is
    /// one setting per line, and an unknown key is ignored rather than fatal.
    /// </summary>
    class AppSettings
    {
        public string Device { get; set; } = string.Empty;

        public string Host { get; set; } = string.Empty;

        public double StepDecibels { get; set; } = 0.5;

        /// <summary>
        /// Top of the on-screen display's scale, in the AVR's own units. 98 is a Denon's factory
        /// maximum, but a receiver can be configured lower. Only used when the AVR does not
        /// report its own maximum, which most of them do as soon as we connect.
        /// </summary>
        public double MaxVolume { get; set; } = 98;

        /// <summary>
        /// Set once the user has typed a maximum of their own, after which the AVR's answer is
        /// no longer allowed to overwrite it.
        /// </summary>
        public bool MaxVolumeIsManual { get; set; }

        /// <summary>How long wheel ticks are gathered before being sent as one command.</summary>
        public int FlushIntervalMs { get; set; } = 40;

        /// <summary>Minimum spacing between two commands on the wire.</summary>
        public int MinCommandIntervalMs { get; set; } = 60;

        public bool ShowOsd { get; set; } = true;

        public bool OsdDecibels { get; set; } = true;

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
            List<string> lines = new List<string>
            {
                Device + "=" + Host,
                "StepDecibels=" + Format(StepDecibels),
                "MaxVolume=" + Format(MaxVolume),
                "MaxVolumeIsManual=" + MaxVolumeIsManual,
                "FlushIntervalMs=" + FlushIntervalMs.ToString(CultureInfo.InvariantCulture),
                "MinCommandIntervalMs=" + MinCommandIntervalMs.ToString(CultureInfo.InvariantCulture),
                "ShowOsd=" + ShowOsd,
                "OsdDecibels=" + OsdDecibels,
                "OsdOnExternalChange=" + OsdOnExternalChange,
                "OsdDurationMs=" + OsdDurationMs.ToString(CultureInfo.InvariantCulture)
            };

            File.WriteAllLines(path, lines);
        }

        private void Apply(string key, string value)
        {
            switch (key)
            {
                case "StepDecibels":
                    StepDecibels = ParseDouble(value, StepDecibels);
                    break;
                case "MaxVolume":
                    MaxVolume = ParseDouble(value, MaxVolume);
                    break;
                case "MaxVolumeIsManual":
                    MaxVolumeIsManual = ParseBool(value, MaxVolumeIsManual);
                    break;
                case "FlushIntervalMs":
                    FlushIntervalMs = ParseInt(value, FlushIntervalMs);
                    break;
                case "MinCommandIntervalMs":
                    MinCommandIntervalMs = ParseInt(value, MinCommandIntervalMs);
                    break;
                case "ShowOsd":
                    ShowOsd = ParseBool(value, ShowOsd);
                    break;
                case "OsdDecibels":
                    OsdDecibels = ParseBool(value, OsdDecibels);
                    break;
                case "OsdOnExternalChange":
                    OsdOnExternalChange = ParseBool(value, OsdOnExternalChange);
                    break;
                case "OsdDurationMs":
                    OsdDurationMs = ParseInt(value, OsdDurationMs);
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
