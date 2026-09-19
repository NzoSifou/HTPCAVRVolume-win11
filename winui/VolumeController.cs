using System;
using HTPCAVRVolume.AVRDevices;
using HTPCAVRVolume.Background;

namespace HTPCAVRVolume
{
    /// <summary>
    /// Turns key presses into as few AVR commands as possible.
    ///
    /// The keyboard hook only adds to a counter and returns. Shortly afterwards the ticks that
    /// piled up are turned into one absolute level, so a hard flick of a volume wheel arrives as a
    /// single command that lands where the user stopped, instead of thirty steps the AVR then
    /// grinds through long after they let go.
    /// </summary>
    sealed class VolumeController : IDisposable
    {
        private readonly PumpTimer _flushTimer;
        private IAVRDevice _device;
        private int _pendingSteps;
        private int _lastFlushTicks;

        public VolumeController(MessagePump pump)
        {
            _lastFlushTicks = Environment.TickCount;
            _flushTimer = pump.CreateTimer();
            _flushTimer.Interval = TimeSpan.FromMilliseconds(40);
            _flushTimer.IsRepeating = false;
            _flushTimer.Tick += (sender, e) => Flush();
        }

        /// <summary>How long ticks are allowed to pile up before they are sent as one command.</summary>
        public int FlushIntervalMs
        {
            get { return (int)_flushTimer.Interval.TotalMilliseconds; }
            set { _flushTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, value)); }
        }

        /// <summary>Level change per detent. The AVR's scale and dB differ by an offset, so this
        /// is the same number in either unit.</summary>
        public double Step { get; set; } = 0.5;

        /// <summary>
        /// The app will not raise the volume past this, in the AVR's own units. The setting is the
        /// authority: where it disagrees with what the AVR reported as its maximum, the user asked
        /// for that level on purpose, and the AVR still refuses whatever it cannot do.
        /// </summary>
        public double MaxVolume { get; set; } = 98;

        public IAVRDevice Device
        {
            get { return _device; }
            set
            {
                _flushTimer.Stop();
                _pendingSteps = 0;
                _device = value;
            }
        }

        /// <summary>
        /// Called straight from the keyboard hook: it must stay cheap, so it does no I/O at all.
        /// </summary>
        public void Nudge(int steps)
        {
            if (_device == null)
            {
                return;
            }

            _pendingSteps += steps;

            // A lone press should not wait for the timer; only a burst gets batched.
            if (unchecked(Environment.TickCount - _lastFlushTicks) >= FlushIntervalMs)
            {
                Flush();
            }
            else
            {
                _flushTimer.Start();
            }
        }

        /// <summary>Goes straight to a level, for the slider. Ignored by a step-only AVR.</summary>
        public void SetVolume(double volume)
        {
            IAVRDevice device = _device;
            if (device == null || !device.SupportsAbsoluteVolume)
            {
                return;
            }

            _flushTimer.Stop();
            _pendingSteps = 0;
            _lastFlushTicks = Environment.TickCount;

            device.SetVolume(Clamp(volume));
        }

        public void ToggleMute()
        {
            _device?.SetMute(!_device.Muted);
        }

        public void Dispose()
        {
            _flushTimer.Stop();
        }

        private void Flush()
        {
            _flushTimer.Stop();
            _lastFlushTicks = Environment.TickCount;

            int steps = _pendingSteps;
            _pendingSteps = 0;

            IAVRDevice device = _device;
            if (steps == 0 || device == null)
            {
                return;
            }

            if (!device.SupportsAbsoluteVolume || !device.Volume.HasValue)
            {
                // Either the AVR cannot be told a level, or it has not told us where it is yet.
                device.Step(steps);
                return;
            }

            // The AVR scale and the dB the user reads differ by an offset, so the size of a step
            // is the distance between two converted points rather than the figure itself.
            double stepSize = device.FromDecibels(Step) - device.FromDecibels(0);
            device.SetVolume(Clamp(device.Volume.Value + steps * stepSize));
        }

        private double Clamp(double volume)
        {
            if (volume > MaxVolume)
            {
                return MaxVolume;
            }

            return volume < 0 ? 0 : volume;
        }
    }
}
