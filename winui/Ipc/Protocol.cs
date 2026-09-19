using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HTPCAVRVolume.Ipc
{
    /// <summary>
    /// What the two processes say to each other. One line of JSON per message, over a named pipe
    /// private to the logged-on user.
    ///
    /// The background process is the one that knows anything: it owns the settings file, the link
    /// to the receiver and the volume. The window asks it to change things and is told what
    /// happened, which keeps the two from ever disagreeing about the state of the world.
    /// </summary>
    static class Protocol
    {
        /// <summary>Per user, so two people logged on at once do not talk over each other.</summary>
        public static string PipeName => "HTPCAVRVolume." + Environment.UserName;

        public static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            IncludeFields = false
        };
    }

    /// <summary>Window to background.</summary>
    sealed class Request
    {
        /// <summary>One of: state, volume, step, mute, reconnect, setting, show, quit.</summary>
        public string Verb { get; set; }

        /// <summary>The setting to change, for "setting".</summary>
        public string Name { get; set; }

        /// <summary>The new value, as written in the settings file.</summary>
        public string Value { get; set; }

        /// <summary>An absolute level for "volume", or the number of steps for "step".</summary>
        public double Number { get; set; }
    }

    /// <summary>Background to window: everything the window draws, in one go.</summary>
    sealed class State
    {
        public string Device { get; set; } = string.Empty;

        public string Host { get; set; } = string.Empty;

        public double Step { get; set; }

        public double MaxVolume { get; set; }

        public bool Decibels { get; set; }

        public bool ShowOsd { get; set; }

        public bool OsdOnExternalChange { get; set; }

        public bool AutoReconnect { get; set; }

        public int ReconnectAttempts { get; set; }

        public int ReconnectTimeoutSeconds { get; set; }

        public bool KeepAudioAlive { get; set; }

        /// <summary>Null when the receiver has not told us where it is.</summary>
        public double? Volume { get; set; }

        public bool Muted { get; set; }

        public bool SupportsAbsoluteVolume { get; set; }

        /// <summary>disconnected, connecting or connected.</summary>
        public string Link { get; set; } = "disconnected";

        public string Status { get; set; } = string.Empty;

        public string AudioStatus { get; set; } = "Off";

        /// <summary>Zero units per decibel offset, so the window can convert without a device.</summary>
        public double ZeroDecibelLevel { get; set; } = 80;
    }
}
