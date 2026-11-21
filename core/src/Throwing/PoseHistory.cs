using System;

namespace Portaballs.Core
{
    /// <summary>One reading of a hand controller: when it was taken, where the hand was, which way it was turned.</summary>
    public struct PoseSample
    {
        /// <summary>Seconds on the shared clock (see Units.cs).</summary>
        public double Time;

        /// <summary>World position of the controller's origin, metres.</summary>
        public Vec3 Position;

        /// <summary>World rotation of the controller.</summary>
        public Quat Rotation;

        public PoseSample(double time, Vec3 position, Quat rotation)
        {
            Time = time;
            Position = position;
            Rotation = rotation;
        }
    }

    /// <summary>
    /// The last half second or so of one hand's poses, kept in time order.
    ///
    /// What it is for: when the game notices a release it is already late, so the throw has to be
    /// worked out from where the hand WAS a few hundredths of a second ago. This is the memory that
    /// makes that possible.
    ///
    /// It is a fixed-size ring: once created it never allocates. Samples may arrive at any rate, with
    /// gaps, repeated or slightly out of order; they are always stored sorted by time with one sample
    /// per timestamp, so nothing downstream ever divides by a zero time step.
    /// </summary>
    public sealed class PoseHistory
    {
        /// <summary>Enough for half a second at 1000 Hz, in case a helper app feeds fast samples.</summary>
        public const int DefaultCapacity = 512;

        public const double DefaultMaxAgeSeconds = 0.5;

        private readonly PoseSample[] _items;
        private readonly double _maxAge;
        private int _start;   // slot of the oldest sample
        private int _count;

        /// <param name="capacity">Most samples kept. Oldest are dropped first.</param>
        /// <param name="maxAgeSeconds">Samples older than this (measured from the newest) are forgotten.</param>
        public PoseHistory(int capacity = DefaultCapacity, double maxAgeSeconds = DefaultMaxAgeSeconds)
        {
            if (capacity < 4) capacity = 4;
            if (!(maxAgeSeconds > 0.0)) maxAgeSeconds = DefaultMaxAgeSeconds;
            _items = new PoseSample[capacity];
            _maxAge = maxAgeSeconds;
            _start = 0;
            _count = 0;
        }

        public int Count { get { return _count; } }
        public int Capacity { get { return _items.Length; } }
        public double MaxAgeSeconds { get { return _maxAge; } }

        /// <summary>Time of the oldest sample, or 0 when empty.</summary>
        public double OldestTime { get { return _count > 0 ? _items[_start].Time : 0.0; } }

        /// <summary>Time of the newest sample, or 0 when empty.</summary>
        public double NewestTime { get { return _count > 0 ? _items[Slot(_count - 1)].Time : 0.0; } }

        /// <summary>Sample by age order: 0 is the oldest, Count - 1 the newest. Out-of-range indexes are clamped.</summary>
        public PoseSample this[int index]
        {
            get
            {
                if (_count == 0) return new PoseSample(0.0, default(Vec3), Quat.Identity);
                if (index < 0) index = 0;
                if (index >= _count) index = _count - 1;
                return _items[Slot(index)];
            }
        }

        /// <summary>Forget everything, for example when the hand loses tracking for a long time or the clock restarts.</summary>
        public void Clear()
        {
            _start = 0;
            _count = 0;
        }

        /// <summary>
        /// Store one pose. Returns false (and stores nothing) when the reading is unusable: a time or
        /// position that is not a real number, or a sample older than everything already kept.
        /// A sample with the same timestamp as an existing one replaces it.
        /// </summary>
        public bool Add(double time, Vec3 position, Quat rotation)
        {
            if (double.IsNaN(time) || double.IsInfinity(time)) return false;
            if (!IsFinite(position)) return false;
            rotation = CleanRotation(rotation);

            if (_count > 0)
            {
                double newest = _items[Slot(_count - 1)].Time;
                if (time < newest - _maxAge) return false;   // far too old to matter

                if (time <= newest)
                {
                    // Late or repeated sample: find where it belongs.
                    int at = FirstIndexAtOrAfter(time);
                    if (at < _count && _items[Slot(at)].Time == time)
                    {
                        _items[Slot(at)] = new PoseSample(time, position, rotation);
                        return true;
                    }
                    if (_count == _items.Length)
                    {
                        if (at == 0) return false;            // older than all, and no room
                        _start = (_start + 1) % _items.Length; // drop the oldest to make room
                        _count--;
                        at--;
                    }
                    for (int i = _count; i > at; i--)
                    {
                        _items[Slot(i)] = _items[Slot(i - 1)];
                    }
                    _items[Slot(at)] = new PoseSample(time, position, rotation);
                    _count++;
                    return true;
                }
            }

            // Normal case: newer than everything.
            if (_count == _items.Length)
            {
                _start = (_start + 1) % _items.Length;
                _count--;
            }
            _items[Slot(_count)] = new PoseSample(time, position, rotation);
            _count++;

            // Forget samples that have aged out.
            while (_count > 1 && _items[_start].Time < time - _maxAge)
            {
                _start = (_start + 1) % _items.Length;
                _count--;
            }
            return true;
        }

        public bool Add(PoseSample sample)
        {
            return Add(sample.Time, sample.Position, sample.Rotation);
        }

        /// <summary>Index of the first sample whose time is at or after the given time; Count when there is none.</summary>
        public int FirstIndexAtOrAfter(double time)
        {
            int lo = 0;
            int hi = _count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_items[Slot(mid)].Time < time) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        /// <summary>
        /// The run of samples whose times lie between fromTime and toTime (both included).
        /// first is the index of the earliest one; count is how many (0 when none).
        /// </summary>
        public void FindRange(double fromTime, double toTime, out int first, out int count)
        {
            first = 0;
            count = 0;
            if (_count == 0 || double.IsNaN(fromTime) || double.IsNaN(toTime) || toTime < fromTime) return;
            first = FirstIndexAtOrAfter(fromTime);
            int end = first;
            while (end < _count && _items[Slot(end)].Time <= toTime) end++;
            count = end - first;
        }

        /// <summary>
        /// The pose at any moment, blended between the two samples either side of it.
        /// Outside the stored span it returns the nearest end (it never guesses beyond the data).
        /// Returns false only when the history is empty.
        /// </summary>
        public bool TrySample(double time, out PoseSample pose)
        {
            double gap;
            bool clamped;
            return TrySample(time, out pose, out gap, out clamped);
        }

        /// <param name="gapSeconds">Time between the two samples that were blended (0 when the answer is one stored sample).
        /// A large value means the hand was not being tracked around that moment.</param>
        /// <param name="clamped">True when the time asked for lies outside the stored span.</param>
        public bool TrySample(double time, out PoseSample pose, out double gapSeconds, out bool clamped)
        {
            gapSeconds = 0.0;
            clamped = false;
            if (_count == 0 || double.IsNaN(time))
            {
                pose = new PoseSample(0.0, default(Vec3), Quat.Identity);
                clamped = true;
                return false;
            }

            int after = FirstIndexAtOrAfter(time);
            if (after == 0)
            {
                pose = _items[Slot(0)];
                clamped = time < pose.Time;
                return true;
            }
            if (after == _count)
            {
                pose = _items[Slot(_count - 1)];
                clamped = time > pose.Time;
                return true;
            }

            PoseSample b = _items[Slot(after)];
            if (b.Time == time)
            {
                pose = b;
                return true;
            }
            PoseSample a = _items[Slot(after - 1)];
            double span = b.Time - a.Time;   // always above zero: one sample per timestamp, sorted
            gapSeconds = span;
            float f = (float)((time - a.Time) / span);
            if (f < 0f) f = 0f;
            if (f > 1f) f = 1f;
            Vec3 p = a.Position + (b.Position - a.Position) * f;
            Quat q = CleanRotation(Quat.Slerp(a.Rotation, b.Rotation, f));
            pose = new PoseSample(time, p, q);
            return true;
        }

        private int Slot(int index)
        {
            int s = _start + index;
            int n = _items.Length;
            return s >= n ? s - n : s;
        }

        private static bool IsFinite(Vec3 v)
        {
            return !(float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z)
                  || float.IsInfinity(v.X) || float.IsInfinity(v.Y) || float.IsInfinity(v.Z));
        }

        /// <summary>A unit-length rotation, or "no rotation" when the input is broken (zero or not a number).</summary>
        internal static Quat CleanRotation(Quat q)
        {
            float lenSq = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
            if (float.IsNaN(lenSq) || float.IsInfinity(lenSq) || lenSq < 1e-12f) return Quat.Identity;
            float inv = 1f / MathF.Sqrt(lenSq);
            return new Quat(q.X * inv, q.Y * inv, q.Z * inv, q.W * inv);
        }
    }
}
