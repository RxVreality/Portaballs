using System;

namespace Portaballs.Core
{
    /// <summary>One reading of where the eyes were looking.</summary>
    public struct GazeSample
    {
        public double Time;

        /// <summary>World position the gaze ray starts from (between the eyes).</summary>
        public Vec3 Origin;

        /// <summary>World direction of the gaze (unit length).</summary>
        public Vec3 Direction;

        /// <summary>False during blinks or when the eye tracker has lost the eyes.</summary>
        public bool Valid;
    }

    /// <summary>
    /// Tunable numbers for <see cref="GazeFixationFilter"/>. The defaults are the STEADY look.
    ///
    /// Loosened after the tester's headset session of 2026-11-19 (205 throws). At 100 ms / 2 degrees the search
    /// found no look on 14 percent of real throws, and the looks it did find sat right at its own limits:
    /// half were under 150 ms long and their spread crowded the 2 degree line. The eyes had not left the
    /// target; a hard throw shakes the head, and the reading shakes with it. The look-back was left at
    /// 300 ms on purpose: looks that had ended more than 100 ms before the release pointed worse (16.5
    /// degrees from the throw in the middle case, against 11 for fresh ones), so reaching further back
    /// would only find worse targets.
    /// </summary>
    public sealed class GazeFixationSettings
    {
        /// <summary>How far back from the release to look for a steady gaze, seconds. Sensible range 0.2 to 0.5.</summary>
        public double LookBackSeconds = 0.30;

        /// <summary>The gaze must hold steady at least this long to count, seconds. Sensible range 0.04 to 0.15.</summary>
        public double MinDurationSeconds = 0.07;

        /// <summary>
        /// "Steady" means every reading stays within this angle of the group's average direction, radians.
        /// Should sit above the eye tracker's jitter WHILE THROWING. Sensible range 1 to 5 degrees.
        /// </summary>
        public float MaxSpreadAngle = 3.0f * (MathF.PI / 180f);

        /// <summary>Two readings further apart in time than this are not treated as one continuous look, seconds.</summary>
        public double MaxSampleGapSeconds = 0.05;

        /// <summary>
        /// The numbers for a GLANCE: where the eyes were pointing in the last moments before the release
        /// when they never held steady long enough for a proper look. Short (40 ms), close to the release
        /// (inside the last 120 ms) and loose (4.5 degrees), but still tight enough to refuse eyes that
        /// are in the middle of a jump to somewhere else.
        /// </summary>
        public static GazeFixationSettings ForGlance()
        {
            GazeFixationSettings s = new GazeFixationSettings();
            s.LookBackSeconds = 0.12;
            s.MinDurationSeconds = 0.04;
            s.MaxSpreadAngle = 4.5f * (MathF.PI / 180f);
            return s;
        }
    }

    /// <summary>What kind of look <see cref="GazeFixationFilter.FindLook"/> found.</summary>
    public enum GazeLookKind
    {
        /// <summary>Nothing usable: fall back to the head.</summary>
        None = 0,

        /// <summary>The eyes held still on one spot. The trusted kind.</summary>
        Steady = 1,

        /// <summary>No steady look, but the eyes were pointing one way just before the release. Less sure.</summary>
        Glance = 2,
    }

    /// <summary>What the gaze readings in a stretch of time looked like, for the console.</summary>
    public struct GazeSpanSummary
    {
        /// <summary>Readings in the stretch, and how many of them were usable (not a blink, eyes tracked).</summary>
        public int Readings;
        public int ValidReadings;

        /// <summary>True when at least one reading was usable; the three fields below are then filled in.</summary>
        public bool HasLatest;

        /// <summary>The newest usable reading in the stretch.</summary>
        public Vec3 LatestOrigin;
        public Vec3 LatestDirection;

        /// <summary>How long before the end of the stretch that reading was taken, seconds.</summary>
        public float LatestAge;

        /// <summary>The largest angle between that reading and any other usable reading in the stretch, radians: how far the eyes travelled.</summary>
        public float Swing;
    }

    /// <summary>A steady look found by the filter.</summary>
    public struct GazeFixation
    {
        /// <summary>False when no steady look was found; fall back to the head ray.</summary>
        public bool Found;

        /// <summary>Average starting point and direction of the gaze during the steady look.</summary>
        public Vec3 Origin;
        public Vec3 Direction;

        public double StartTime;
        public double EndTime;

        /// <summary>How long the look was held, seconds.</summary>
        public float Duration;

        /// <summary>How long before the release the look ended, seconds. Large means the player looked away early.</summary>
        public float AgeAtRelease;

        /// <summary>The largest angle between any reading in the look and the average direction, radians.</summary>
        public float Spread;

        public int SampleCount;
    }

    /// <summary>
    /// Finds what the player was really looking at when they threw.
    ///
    /// People often glance away or blink right as they let go, so the gaze at the exact release moment
    /// is unreliable. This keeps a short record of gaze readings and, when asked, looks back from the
    /// release for the most recent stretch where the eyes held still on one spot. When there is no such
    /// stretch it says so, and the caller should use the head direction instead.
    ///
    /// It is a fixed-size ring: once created it never allocates.
    /// </summary>
    public sealed class GazeFixationFilter
    {
        private readonly GazeSample[] _items;
        private int _start;
        private int _count;

        /// <param name="capacity">Readings kept. 256 covers a second at 250 Hz.</param>
        public GazeFixationFilter(int capacity = 256)
        {
            if (capacity < 8) capacity = 8;
            _items = new GazeSample[capacity];
        }

        public int Count { get { return _count; } }

        public void Clear()
        {
            _start = 0;
            _count = 0;
        }

        /// <summary>
        /// Store one gaze reading. Readings that are not newer than the last one are ignored. A reading
        /// with a broken direction is stored as "not valid" so it still breaks up a steady look.
        /// </summary>
        public void Add(double time, Vec3 origin, Vec3 direction, bool valid)
        {
            if (double.IsNaN(time) || double.IsInfinity(time)) return;
            if (_count > 0 && time <= _items[Slot(_count - 1)].Time) return;

            GazeSample s;
            s.Time = time;
            s.Origin = origin;
            s.Direction = direction;
            s.Valid = valid;
            if (!ThrowMath.IsFinite(origin) || !ThrowMath.IsFinite(direction) || direction.LengthSquared() < 1e-8f)
            {
                s.Valid = false;
                s.Origin = default(Vec3);
                s.Direction = new Vec3(1f, 0f, 0f);
            }
            else
            {
                s.Direction = Vec3.Normalize(direction);
            }

            if (_count == _items.Length)
            {
                _start = (_start + 1) % _items.Length;
                _count--;
            }
            _items[Slot(_count)] = s;
            _count++;
        }

        /// <summary>
        /// The most recent steady look that ended at or before the release time.
        /// Returns false (and Found = false) when there was none in the look-back span.
        /// </summary>
        public bool TryGetFixation(double releaseTime, GazeFixationSettings settings, out GazeFixation fixation)
        {
            fixation = default(GazeFixation);
            if (_count == 0 || double.IsNaN(releaseTime)) return false;
            if (settings == null) settings = new GazeFixationSettings();

            double earliest = releaseTime - settings.LookBackSeconds;
            float maxSpread = MathF.Max(1e-4f, settings.MaxSpreadAngle);

            // Newest reading at or before the release.
            int end = _count - 1;
            while (end >= 0 && _items[Slot(end)].Time > releaseTime) end--;

            // Try each possible end point, newest first, and grow a steady group backwards from it.
            for (; end >= 0; end--)
            {
                GazeSample last = _items[Slot(end)];
                if (last.Time < earliest) break;
                if (!last.Valid) continue;

                Vec3 sumDir = last.Direction;
                Vec3 sumOrigin = last.Origin;
                int n = 1;
                int begin = end;
                double prevTime = last.Time;

                for (int i = end - 1; i >= 0; i--)
                {
                    GazeSample s = _items[Slot(i)];
                    if (!s.Valid) break;                                        // a blink ends the look
                    if (s.Time < earliest) break;
                    if (prevTime - s.Time > settings.MaxSampleGapSeconds) break; // a hole in the data ends it
                    Vec3 mean = MathUtil.SafeNormalize(sumDir, last.Direction);
                    if (MathUtil.AngleBetween(s.Direction, mean) > maxSpread) break;          // the eyes moved
                    if (MathUtil.AngleBetween(s.Direction, last.Direction) > 2f * maxSpread) break; // slow drift
                    sumDir += s.Direction;
                    sumOrigin += s.Origin;
                    n++;
                    begin = i;
                    prevTime = s.Time;
                }

                double duration = last.Time - _items[Slot(begin)].Time;
                if (n < 2 || duration < settings.MinDurationSeconds) continue;

                Vec3 direction = MathUtil.SafeNormalize(sumDir, last.Direction);
                float spread = 0f;
                for (int i = begin; i <= end; i++)
                {
                    float a = MathUtil.AngleBetween(_items[Slot(i)].Direction, direction);
                    if (a > spread) spread = a;
                }

                fixation.Found = true;
                fixation.Direction = direction;
                fixation.Origin = sumOrigin / n;
                fixation.StartTime = _items[Slot(begin)].Time;
                fixation.EndTime = last.Time;
                fixation.Duration = (float)duration;
                fixation.AgeAtRelease = (float)(releaseTime - last.Time);
                fixation.Spread = spread;
                fixation.SampleCount = n;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Where the eyes were looking for a throw, best kind first: a steady look if there was one, and
        /// if not, a glance (the same search with looser, shorter, closer-to-the-release numbers).
        /// </summary>
        /// <param name="steady">Numbers for the steady look. Null uses the defaults.</param>
        /// <param name="glance">Numbers for the glance. Null, or a look-back of zero, switches the glance off.</param>
        public GazeLookKind FindLook(double releaseTime, GazeFixationSettings steady, GazeFixationSettings glance, out GazeFixation look)
        {
            if (TryGetFixation(releaseTime, steady, out look)) return GazeLookKind.Steady;
            if (glance != null && glance.LookBackSeconds > 0.0 && TryGetFixation(releaseTime, glance, out look)) return GazeLookKind.Glance;
            look = default(GazeFixation);
            return GazeLookKind.None;
        }

        /// <summary>
        /// Count the readings between two times (both included) and say where the eyes last pointed and
        /// how far they travelled. It decides nothing; it is there so the console can say what the eyes
        /// were doing when a look was or was not found.
        /// </summary>
        public GazeSpanSummary Summarise(double fromTime, double toTime)
        {
            GazeSpanSummary summary = default(GazeSpanSummary);
            if (double.IsNaN(fromTime) || double.IsNaN(toTime)) return summary;

            // Newest first, so the first usable reading met is the latest one.
            for (int i = _count - 1; i >= 0; i--)
            {
                GazeSample s = _items[Slot(i)];
                if (s.Time > toTime) continue;
                if (s.Time < fromTime) break;
                summary.Readings++;
                if (!s.Valid) continue;
                summary.ValidReadings++;
                if (!summary.HasLatest)
                {
                    summary.HasLatest = true;
                    summary.LatestOrigin = s.Origin;
                    summary.LatestDirection = s.Direction;
                    summary.LatestAge = (float)(toTime - s.Time);
                    continue;
                }
                float angle = MathUtil.AngleBetween(s.Direction, summary.LatestDirection);
                if (angle > summary.Swing) summary.Swing = angle;
            }
            return summary;
        }

        private int Slot(int index)
        {
            int s = _start + index;
            int n = _items.Length;
            return s >= n ? s - n : s;
        }
    }
}
