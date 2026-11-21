using System;

namespace Portaballs.Core
{
    /// <summary>Tunable numbers for <see cref="ReleaseDelayCalibrator"/>.</summary>
    public sealed class DelayCalibrationSettings
    {
        /// <summary>Only swings at least this fast (m/s) teach anything; drops and gentle tosses have no clear peak. Sensible range 2 to 4.</summary>
        public float MinThrowSpeed = 2.5f;

        /// <summary>How far before the release event to look for the fastest moment, seconds. Sensible range 0.12 to 0.3.</summary>
        public double LookBackSeconds = 0.20;

        /// <summary>How far after the release event to look, seconds (only matters if the event can arrive early). Sensible range 0.02 to 0.08.</summary>
        public double LookAheadSeconds = 0.05;

        /// <summary>Readings within this long either side are averaged when measuring speed at one moment, seconds.</summary>
        public double PeakSmoothingSeconds = 0.015;

        /// <summary>Measured delays outside this span are treated as flukes and ignored, seconds.</summary>
        public double MinDelaySeconds = -0.03;
        public double MaxDelaySeconds = 0.10;

        /// <summary>
        /// How much earlier than the fastest moment of the swing the player really means to let go, seconds.
        /// This cannot be measured from the controller alone; it is a feel setting. 0 means "at the fastest
        /// moment". If calibrated throws come out low, raise it a few milliseconds; if high, lower it.
        /// Sensible range -0.01 to +0.02.
        /// </summary>
        public double PeakToReleaseSeconds = 0.0;

        /// <summary>Throws needed before the answer is trusted at all. Sensible range 3 to 8.</summary>
        public int MinThrows = 5;

        /// <summary>Throws needed for full trust. Sensible range 8 to 15.</summary>
        public int FullConfidenceThrows = 10;

        /// <summary>Scatter between throws (seconds) at or below which trust is full, and at or above which it is zero.</summary>
        public double GoodSpreadSeconds = 0.006;
        public double BadSpreadSeconds = 0.025;
    }

    /// <summary>Why a throw was or was not used for calibration.</summary>
    public enum CalibrationOutcome
    {
        Accepted = 0,

        /// <summary>No measurable swing near the event (too few poses).</summary>
        NoPeak = 1,

        /// <summary>The swing was too slow to have a trustworthy peak.</summary>
        TooSlow = 2,

        /// <summary>The hand was still speeding up at the newest pose. Call again a few frames later.</summary>
        NeedMoreData = 3,

        /// <summary>The fastest moment was at the very start of the search, so the real peak was probably earlier still.</summary>
        PeakTooEarly = 4,

        /// <summary>The measured delay was outside the allowed span, so it was treated as a fluke.</summary>
        OutOfRange = 5,
    }

    /// <summary>What one call to Observe found.</summary>
    public struct CalibrationObservation
    {
        public CalibrationOutcome Outcome;

        /// <summary>Release event time minus the time of peak speed, seconds. Only meaningful when a peak was found.</summary>
        public double DelaySample;

        public double PeakTime;
        public float PeakSpeed;
    }

    /// <summary>
    /// Learns, from real throws, how late the release signal arrives on this particular headset.
    ///
    /// The idea: the hand is moving fastest at about the moment the player means to let go. For each
    /// throw this finds that fastest moment in the pose history and compares it with when the release
    /// was noticed. The gap is the delay. One throw is unreliable, so it keeps the last several and
    /// takes the middle value (the median), which shrugs off the odd fumbled throw.
    ///
    /// Use: after each release call <see cref="Observe"/> with the same event time given to the throw
    /// estimator. When <see cref="IsConfident"/> is true, use <see cref="DelaySeconds"/> as the
    /// estimator's ReleaseTimeOffset (or call <see cref="ApplyTo"/>).
    /// It allocates once, when created.
    /// </summary>
    public sealed class ReleaseDelayCalibrator
    {
        public DelayCalibrationSettings Settings;

        private readonly double[] _samples;   // ring of recent delay measurements
        private readonly double[] _scratch;   // work area for sorting, so nothing is allocated per throw
        private int _next;
        private int _count;

        /// <summary>How many measurements were thrown away for being outside the allowed span. A high number means the span is wrong for this headset.</summary>
        public int RejectedOutOfRange { get; private set; }

        /// <param name="capacity">How many recent throws to remember. More is steadier but slower to notice a
        /// change of headset or link. Sensible range 11 to 41.</param>
        public ReleaseDelayCalibrator(int capacity = 25, DelayCalibrationSettings settings = null)
        {
            if (capacity < 3) capacity = 3;
            Settings = settings ?? new DelayCalibrationSettings();
            _samples = new double[capacity];
            _scratch = new double[capacity];
            Reset();
        }

        /// <summary>How many throws are currently remembered.</summary>
        public int Count { get { return _count; } }

        public int Capacity { get { return _samples.Length; } }

        /// <summary>Forget everything learned, for example when the player changes headset or link settings.</summary>
        public void Reset()
        {
            _next = 0;
            _count = 0;
            RejectedOutOfRange = 0;
        }

        /// <summary>
        /// Look at one throw. Call it at release, or a few frames later (later is better: it lets the
        /// history show the hand slowing down, which proves the peak was real).
        /// </summary>
        /// <param name="eventTime">The release event time, exactly as given to the throw estimator.</param>
        /// <param name="ballLocalOffset">Where the ball sits relative to the controller (same as ThrowSettings.BallLocalOffset).</param>
        public CalibrationObservation Observe(PoseHistory history, double eventTime, Vec3 ballLocalOffset)
        {
            CalibrationObservation obs = default(CalibrationObservation);
            obs.Outcome = CalibrationOutcome.NoPeak;
            if (history == null || history.Count < 3 || double.IsNaN(eventTime) || double.IsInfinity(eventTime)) return obs;

            DelayCalibrationSettings s = Settings;
            double smooth = ThrowMath.Clamp(s.PeakSmoothingSeconds, 0.002, 0.05);
            SpeedPeak peak = ThrowMath.FindSpeedPeak(history, eventTime - s.LookBackSeconds, eventTime + s.LookAheadSeconds,
                ballLocalOffset, smooth);
            if (!peak.Found) return obs;

            obs.PeakTime = peak.Time;
            obs.PeakSpeed = peak.Speed;
            obs.DelaySample = eventTime - peak.Time;

            if (peak.Speed < s.MinThrowSpeed) { obs.Outcome = CalibrationOutcome.TooSlow; return obs; }
            if (peak.AtEnd) { obs.Outcome = CalibrationOutcome.NeedMoreData; return obs; }
            if (!peak.Interior) { obs.Outcome = CalibrationOutcome.PeakTooEarly; return obs; }
            if (double.IsNaN(obs.DelaySample) || obs.DelaySample < s.MinDelaySeconds || obs.DelaySample > s.MaxDelaySeconds)
            {
                RejectedOutOfRange++;
                obs.Outcome = CalibrationOutcome.OutOfRange;
                return obs;
            }

            AddSample(obs.DelaySample);
            obs.Outcome = CalibrationOutcome.Accepted;
            return obs;
        }

        /// <summary>Record a delay measured some other way (seconds). Mostly for tests and for a helper app that knows better.</summary>
        public void AddSample(double delaySeconds)
        {
            if (double.IsNaN(delaySeconds) || double.IsInfinity(delaySeconds)) return;
            _samples[_next] = delaySeconds;
            _next = (_next + 1) % _samples.Length;
            if (_count < _samples.Length) _count++;
        }

        /// <summary>The middle value of the remembered raw measurements (event time minus peak time), seconds. 0 when nothing is known.</summary>
        public double MedianDelaySeconds
        {
            get
            {
                if (_count == 0) return 0.0;
                for (int i = 0; i < _count; i++) _scratch[i] = _samples[i];
                return MedianOfScratch(_count);
            }
        }

        /// <summary>
        /// The learned delay to use as the estimator's ReleaseTimeOffset: the median measurement plus the
        /// "peak to release" feel setting, kept inside the allowed span. 0 when nothing is known.
        /// </summary>
        public double DelaySeconds
        {
            get
            {
                if (_count == 0) return 0.0;
                DelayCalibrationSettings s = Settings;
                return ThrowMath.Clamp(MedianDelaySeconds + s.PeakToReleaseSeconds, s.MinDelaySeconds, s.MaxDelaySeconds);
            }
        }

        /// <summary>
        /// How much the measurements disagree with each other, seconds (a robust figure comparable to a
        /// standard deviation). Small means the delay is steady and well measured.
        /// </summary>
        public double SpreadSeconds
        {
            get
            {
                if (_count < 2) return 0.0;
                double median = MedianDelaySeconds;
                for (int i = 0; i < _count; i++) _scratch[i] = Math.Abs(_samples[i] - median);
                return 1.4826 * MedianOfScratch(_count);
            }
        }

        /// <summary>
        /// 0 (know nothing) to 1 (well measured). Rises with the number of throws seen and falls when
        /// they disagree with each other.
        /// </summary>
        public float Confidence
        {
            get
            {
                DelayCalibrationSettings s = Settings;
                if (_count == 0 || _count < s.MinThrows) return 0f;
                int full = Math.Max(s.FullConfidenceThrows, 1);
                float byCount = MathUtil.Saturate((float)_count / full);
                double spread = SpreadSeconds;
                double good = s.GoodSpreadSeconds;
                double bad = Math.Max(s.BadSpreadSeconds, good + 1e-6);
                float bySpread = 1f - MathUtil.Saturate((float)((spread - good) / (bad - good)));
                return byCount * bySpread;
            }
        }

        /// <summary>True once the learned delay is good enough to use.</summary>
        public bool IsConfident { get { return Confidence >= 0.5f; } }

        /// <summary>The learned delay when it is trusted, otherwise the fallback you pass in.</summary>
        public double DelayOr(double fallbackSeconds)
        {
            return IsConfident ? DelaySeconds : fallbackSeconds;
        }

        /// <summary>
        /// Switch a set of throw settings over to the learned delay, if it is trusted
        /// (see ThrowSettings.UseMeasuredDelay for exactly what changes).
        /// Returns true when the settings were changed.
        /// </summary>
        public bool ApplyTo(ThrowSettings settings)
        {
            if (settings == null || !IsConfident) return false;
            settings.UseMeasuredDelay(DelaySeconds);
            return true;
        }

        private double MedianOfScratch(int n)
        {
            // Insertion sort: n is at most a few dozen.
            for (int i = 1; i < n; i++)
            {
                double key = _scratch[i];
                int j = i - 1;
                while (j >= 0 && _scratch[j] > key)
                {
                    _scratch[j + 1] = _scratch[j];
                    j--;
                }
                _scratch[j + 1] = key;
            }
            if ((n & 1) == 1) return _scratch[n / 2];
            return 0.5 * (_scratch[n / 2 - 1] + _scratch[n / 2]);
        }
    }
}
