using System;

namespace Portaballs.Core
{
    /// <summary>What the grip did on the latest reading.</summary>
    public enum GripEvent
    {
        None = 0,
        Grabbed = 1,
        Released = 2,
    }

    /// <summary>Tunable numbers for <see cref="GripReleaseDetector"/>. Squeeze values run from 0 (open hand) to 1 (full squeeze).</summary>
    public sealed class GripSettings
    {
        /// <summary>The hand counts as holding once the squeeze rises through this. Sensible range 0.3 to 0.7.</summary>
        public float GrabThreshold = 0.5f;

        /// <summary>
        /// Let go when the squeeze falls below this share of the hardest squeeze since grabbing.
        /// Higher notices the release sooner but risks letting go by accident. Sensible range 0.6 to 0.9.
        /// </summary>
        public float ReleaseFraction = 0.75f;

        /// <summary>The let-go level never goes below this, however light the grab was. Sensible range 0.05 to 0.3.</summary>
        public float ReleaseFloor = 0.15f;

        /// <summary>
        /// After letting go without fully opening the hand, squeezing back up by this much grabs again.
        /// Sensible range 0.1 to 0.3.
        /// </summary>
        public float RegrabRise = 0.15f;

        /// <summary>
        /// How far below the hardest squeeze still counts as "holding steady" when working out the moment
        /// the fingers began to open. Should sit just above the sensor's noise. Sensible range 0.02 to 0.08.
        /// </summary>
        public float OnsetDeadband = 0.04f;

        /// <summary>
        /// How fast the remembered hardest squeeze relaxes toward the current squeeze, per second.
        /// 0 means it never relaxes. Raise it (0.5 to 2) if a hard squeeze at pick-up makes later
        /// releases trigger too early.
        /// </summary>
        public float PeakDecayPerSecond = 0f;

        /// <summary>
        /// For a plain on/off button read once per frame: the real change happened somewhere between the
        /// last two readings, so report the halfway point (true) rather than the later reading (false).
        /// </summary>
        public bool DigitalEdgeMidway = true;
    }

    /// <summary>
    /// Watches one hand's grip and says when it grabbed and when it let go.
    ///
    /// What it is for: the squeeze value falls gradually as the fingers open, and the game only sees it
    /// once per frame. This works out the let-go moment more finely than a frame (by drawing a straight
    /// line between the two readings either side of the let-go level), and also estimates when the
    /// fingers first STARTED to open, which is closer to when the player meant to throw.
    ///
    /// Feed it every reading with its timestamp. Use <see cref="Update"/> for the analog squeeze, or
    /// <see cref="UpdateDigital"/> for an on/off button. It never allocates.
    /// </summary>
    public sealed class GripReleaseDetector
    {
        public GripSettings Settings;

        private bool _hasSample;
        private double _lastTime;
        private float _lastValue;
        private bool _lastPressed;

        private bool _held;
        private float _peak;
        private float _lowSinceOpen;

        // The newest reading that was still at (or within the deadband of) the hardest squeeze.
        private double _plateauTime;
        // The first reading after that which had clearly started to fall.
        private bool _fallSeen;
        private double _fallTime;
        private float _fallValue;

        public GripReleaseDetector(GripSettings settings = null)
        {
            Settings = settings ?? new GripSettings();
            Reset();
        }

        /// <summary>True between a grab and the release that follows it.</summary>
        public bool IsHeld { get { return _held; } }

        /// <summary>When the last grab happened (worked out between readings).</summary>
        public double GrabTime { get; private set; }

        /// <summary>When the squeeze crossed the let-go level (worked out between readings). This is the "event time".</summary>
        public double ReleaseTime { get; private set; }

        /// <summary>Best guess at when the fingers started to open before that release. Never later than ReleaseTime.</summary>
        public double ReleaseOnsetTime { get; private set; }

        /// <summary>Timestamp of the reading on which the release was noticed (always at or after ReleaseTime).</summary>
        public double ReleaseNoticedTime { get; private set; }

        /// <summary>Hardest squeeze since the last grab.</summary>
        public float PeakValue { get { return _peak; } }

        /// <summary>The squeeze level that would count as letting go right now.</summary>
        public float ReleaseThreshold
        {
            get { return ThresholdFor(_peak); }
        }

        /// <summary>Forget everything, hand open.</summary>
        public void Reset()
        {
            _hasSample = false;
            _lastTime = 0.0;
            _lastValue = 0f;
            _lastPressed = false;
            _held = false;
            _peak = 0f;
            _lowSinceOpen = float.MaxValue;
            _plateauTime = 0.0;
            _fallSeen = false;
            _fallTime = 0.0;
            _fallValue = 0f;
            GrabTime = 0.0;
            ReleaseTime = 0.0;
            ReleaseOnsetTime = 0.0;
            ReleaseNoticedTime = 0.0;
        }

        /// <summary>
        /// Start holding right now without waiting for the squeeze to rise, for example when the game
        /// puts a ball straight into an already-closed hand.
        /// </summary>
        public void ForceGrab(double time, float value)
        {
            if (float.IsNaN(value)) value = 1f;
            value = MathUtil.Saturate(value);
            BeginHold(time, time, value);
            _hasSample = true;
            _lastTime = time;
            _lastValue = value;
            _lastPressed = true;
        }

        /// <summary>
        /// Feed one analog squeeze reading (0..1). Returns what happened on this reading.
        /// Readings that arrive older than the previous one are ignored.
        /// </summary>
        public GripEvent Update(double time, float value)
        {
            if (double.IsNaN(time) || double.IsInfinity(time) || float.IsNaN(value)) return GripEvent.None;
            value = MathUtil.Saturate(value);
            GripSettings s = Settings;

            if (!_hasSample)
            {
                _hasSample = true;
                _lastTime = time;
                _lastValue = value;
                _lowSinceOpen = value;
                return GripEvent.None;
            }
            if (time < _lastTime) return GripEvent.None;

            double dt = time - _lastTime;
            GripEvent result = GripEvent.None;

            if (!_held)
            {
                if (value < _lowSinceOpen) _lowSinceOpen = value;
                bool roseThrough = _lastValue < s.GrabThreshold && value >= s.GrabThreshold;
                bool squeezedBack = value >= s.GrabThreshold && value >= _lowSinceOpen + s.RegrabRise;
                if (roseThrough || squeezedBack)
                {
                    double grabTime = time;
                    if (roseThrough && value > _lastValue)
                    {
                        float f = (s.GrabThreshold - _lastValue) / (value - _lastValue);
                        grabTime = _lastTime + dt * MathUtil.Saturate(f);
                    }
                    BeginHold(grabTime, time, value);
                    result = GripEvent.Grabbed;
                }
            }
            else
            {
                // Remember the hardest squeeze (optionally letting that memory relax over time).
                if (s.PeakDecayPerSecond > 0f && dt > 0.0)
                {
                    _peak -= s.PeakDecayPerSecond * (float)dt;
                }
                if (value > _peak) _peak = value;

                float threshold = ThresholdFor(_peak);
                if (value < threshold)
                {
                    // Let go. Find the crossing moment on the straight line between the two readings.
                    double releaseTime = time;
                    if (_lastValue > threshold && _lastValue > value && dt > 0.0)
                    {
                        float f = (_lastValue - threshold) / (_lastValue - value);
                        releaseTime = _lastTime + dt * MathUtil.Saturate(f);
                    }

                    // When did the fall begin? Follow the falling line back up to the hardest squeeze.
                    double fallRate = 0.0;   // squeeze lost per second, positive
                    if (_fallSeen && releaseTime > _fallTime && _fallValue > threshold)
                    {
                        fallRate = (_fallValue - threshold) / (releaseTime - _fallTime);
                    }
                    else if (dt > 0.0 && _lastValue > value)
                    {
                        fallRate = (_lastValue - value) / dt;
                    }
                    double onset = releaseTime;
                    if (fallRate > 1e-6)
                    {
                        onset = releaseTime - (_peak - threshold) / fallRate;
                    }
                    double latestOnset = _fallSeen ? _fallTime : releaseTime;
                    if (latestOnset > releaseTime) latestOnset = releaseTime;
                    if (onset > latestOnset) onset = latestOnset;
                    if (onset < _plateauTime) onset = _plateauTime;
                    if (onset > releaseTime) onset = releaseTime;

                    _held = false;
                    _lowSinceOpen = value;
                    ReleaseTime = releaseTime;
                    ReleaseOnsetTime = onset;
                    ReleaseNoticedTime = time;
                    result = GripEvent.Released;
                }
                else if (value >= _peak - s.OnsetDeadband)
                {
                    _plateauTime = time;
                    _fallSeen = false;
                }
                else if (!_fallSeen)
                {
                    _fallSeen = true;
                    _fallTime = time;
                    _fallValue = value;
                }
            }

            _lastTime = time;
            _lastValue = value;
            _lastPressed = _held;
            return result;
        }

        /// <summary>
        /// Feed one on/off button reading. Pressing grabs, letting go releases.
        /// </summary>
        /// <param name="timeIsExact">True when 'time' is the real moment the button changed (for example an
        /// event time from SteamVR). False when it is just the time of this frame's reading.</param>
        public GripEvent UpdateDigital(double time, bool pressed, bool timeIsExact = false)
        {
            if (double.IsNaN(time) || double.IsInfinity(time)) return GripEvent.None;
            if (_hasSample && time < _lastTime && !timeIsExact) return GripEvent.None;

            double edgeTime = time;
            if (!timeIsExact && _hasSample && Settings.DigitalEdgeMidway && time > _lastTime)
            {
                edgeTime = _lastTime + (time - _lastTime) * 0.5;
            }

            GripEvent result = GripEvent.None;
            bool wasPressed = _hasSample ? _lastPressed : false;
            if (pressed && !wasPressed && !_held)
            {
                BeginHold(edgeTime, time, 1f);
                result = GripEvent.Grabbed;
            }
            else if (!pressed && _held)
            {
                _held = false;
                _lowSinceOpen = 0f;
                ReleaseTime = edgeTime;
                ReleaseOnsetTime = edgeTime;
                ReleaseNoticedTime = time;
                result = GripEvent.Released;
            }

            _hasSample = true;
            if (time > _lastTime || !timeIsExact) _lastTime = time;
            _lastValue = pressed ? 1f : 0f;
            _lastPressed = pressed;
            return result;
        }

        private void BeginHold(double grabTime, double sampleTime, float value)
        {
            _held = true;
            _peak = value;
            _plateauTime = sampleTime;
            _fallSeen = false;
            GrabTime = grabTime;
        }

        private float ThresholdFor(float peak)
        {
            GripSettings s = Settings;
            float t = peak * s.ReleaseFraction;
            if (t < s.ReleaseFloor) t = s.ReleaseFloor;
            return t;
        }
    }
}
