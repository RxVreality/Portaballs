using System;

namespace Portaballs.Core
{
    /// <summary>
    /// Feeds a <see cref="GripReleaseDetector"/> from readings that carry "when this value last
    /// CHANGED" instead of "when it was read" (the helper's GripTime).
    ///
    /// What it is for: the helper polls 500 times a second, but a controller only sends a new squeeze
    /// value every few milliseconds, and SteamVR says when each new value arrived. Giving the detector
    /// (change time, value) pairs places the let-go moment by the controller's own clockwork, finer
    /// than the polling, and far finer than a frame.
    ///
    /// Two things need care, and this class does them:
    ///   1. While the squeeze holds steady the change time does not move, so most readings are
    ///      repeats and are skipped. On real hardware the change time also wobbles by a hair from poll
    ///      to poll; a wobble with the same value is treated as a repeat.
    ///   2. After a long steady hold the detector's "previous reading" is from the START of the hold,
    ///      perhaps seconds ago. It finds the let-go moment by drawing a line between two readings, so
    ///      a sudden drop would be placed far too early. Before the first changed reading this feeds
    ///      one extra reading of the old value a short while earlier (about one controller report),
    ///      which is all that is really known: "it was still the old value just before this".
    /// </summary>
    public sealed class ChangeTimeGripFeed
    {
        /// <summary>
        /// The longest a controller is assumed to go between two reports, seconds. Only matters right
        /// after a steady hold: the let-go moment is then never placed further back than this from the
        /// first changed reading. Sensible range 0.004 to 0.02.
        /// </summary>
        public double MaxReportIntervalSeconds = 0.010;

        /// <summary>A change time that moves by less than this with the same value is the same reading, seconds.</summary>
        public double JitterToleranceSeconds = 0.0005;

        private bool _has;
        private double _lastTime;
        private float _lastValue;

        /// <summary>Readings passed on to the detector so far.</summary>
        public long Fed { get; private set; }

        public void Reset()
        {
            _has = false;
            _lastTime = 0.0;
            _lastValue = 0f;
            Fed = 0;
        }

        /// <summary>
        /// Offer one reading. Returns what the detector made of it (None for a repeat).
        /// </summary>
        /// <param name="changeTime">When the value last changed, seconds on the shared clock.</param>
        /// <param name="value">The squeeze, 0 to 1.</param>
        public GripEvent Feed(GripReleaseDetector detector, double changeTime, float value)
        {
            if (detector == null || double.IsNaN(changeTime) || double.IsInfinity(changeTime) || float.IsNaN(value)) return GripEvent.None;

            if (!_has)
            {
                _has = true;
                _lastTime = changeTime;
                _lastValue = value;
                Fed++;
                return detector.Update(changeTime, value);
            }

            bool sameValue = value == _lastValue;
            if (sameValue && Math.Abs(changeTime - _lastTime) < JitterToleranceSeconds) return GripEvent.None;   // a repeat

            // A new value whose change time did not move forward (clock wobble): it still happened after the last one.
            if (changeTime <= _lastTime) changeTime = _lastTime + 1e-6;

            GripEvent bridge = GripEvent.None;
            double gap = MaxReportIntervalSeconds > 0.0 ? MaxReportIntervalSeconds : 0.0;
            if (!sameValue && gap > 0.0 && changeTime - _lastTime > gap)
            {
                bridge = detector.Update(changeTime - gap, _lastValue);
                Fed++;
            }

            GripEvent result = detector.Update(changeTime, value);
            Fed++;
            _lastTime = changeTime;
            _lastValue = value;
            return result != GripEvent.None ? result : bridge;
        }
    }
}
