using System;

namespace Portaballs.Core
{
    /// <summary>Three numbers at double precision. Only used inside the throwing maths, where float is too coarse.</summary>
    internal struct ThrowVec3D
    {
        public double X;
        public double Y;
        public double Z;

        public ThrowVec3D(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public Vec3 ToVec3()
        {
            return new Vec3((float)X, (float)Y, (float)Z);
        }
    }

    /// <summary>
    /// Fits a smooth curve through a handful of timed 3D readings and reads off its value, speed and
    /// acceleration at time zero. This is how noisy hand positions become a clean velocity: every
    /// reading in the window pulls on the answer a little, so no single jittery frame decides it.
    ///
    /// Order 1 fits a straight line (steady speed). Order 2 also lets the speed change (a bend).
    /// It only keeps running totals, so it never allocates.
    /// </summary>
    internal struct ThrowFit3
    {
        private double _scale;
        private double _s0, _s1, _s2, _s3, _s4;
        private double _x0, _x1, _x2;
        private double _y0, _y1, _y2;
        private double _z0, _z1, _z2;
        private double _sumSq;
        private double _tMin, _tMax;
        private int _n;

        /// <param name="windowSeconds">Rough length of the time window; only used to keep the numbers well scaled.</param>
        public void Begin(double windowSeconds)
        {
            this = default(ThrowFit3);
            _scale = windowSeconds > 1e-6 ? 1.0 / windowSeconds : 1.0;
            _tMin = double.MaxValue;
            _tMax = double.MinValue;
        }

        public int Count { get { return _n; } }

        /// <summary>Time between the earliest and latest reading added.</summary>
        public double Span { get { return _n > 0 ? _tMax - _tMin : 0.0; } }

        /// <summary>Average of the times added (seconds relative to time zero).</summary>
        public double MeanTime { get { return _n > 0 ? _s1 / (_s0 * _scale) : 0.0; } }

        /// <param name="t">Seconds relative to the moment the answer is wanted for (negative = before it).</param>
        public void Add(double t, double x, double y, double z)
        {
            if (t < _tMin) _tMin = t;
            if (t > _tMax) _tMax = t;
            double u = t * _scale;
            double u2 = u * u;
            _s0 += 1.0;
            _s1 += u;
            _s2 += u2;
            _s3 += u2 * u;
            _s4 += u2 * u2;
            _x0 += x; _x1 += x * u; _x2 += x * u2;
            _y0 += y; _y1 += y * u; _y2 += y * u2;
            _z0 += z; _z1 += z * u; _z2 += z * u2;
            _sumSq += x * x + y * y + z * z;
            _n++;
        }

        /// <summary>
        /// Solve the fit. Returns the order actually used: 2 (bend), 1 (straight line), 0 (only an
        /// average: too few readings or all at one instant), or -1 (nothing was added).
        /// </summary>
        /// <param name="value">The fitted value at time zero.</param>
        /// <param name="slope">Its rate of change per second at time zero.</param>
        /// <param name="accel">How fast that rate is itself changing, per second squared (0 for a line).</param>
        /// <param name="rms">Typical distance between the readings and the fitted curve.</param>
        public int Solve(int wantedOrder, out ThrowVec3D value, out ThrowVec3D slope, out ThrowVec3D accel, out double rms)
        {
            value = default(ThrowVec3D);
            slope = default(ThrowVec3D);
            accel = default(ThrowVec3D);
            rms = 0.0;
            if (_n == 0) return -1;

            double meanU = _s1 / _s0;
            double varU = _s2 / _s0 - meanU * meanU;   // spread of the times, in scaled units

            if (wantedOrder >= 2 && _n >= 4 && varU > 1e-10)
            {
                // Three unknowns per axis; the same 3x3 system for all three axes.
                double a = _s0, b = _s1, c = _s2, d = _s3, e = _s4;
                double m00 = c * e - d * d;
                double m01 = c * d - b * e;
                double m02 = b * d - c * c;
                double m11 = a * e - c * c;
                double m12 = b * c - a * d;
                double m22 = a * c - b * b;
                double det = a * m00 + b * m01 + c * m02;
                if (det > 1e-9 * a * c * e && det > 0.0)
                {
                    double inv = 1.0 / det;
                    double c0x = (m00 * _x0 + m01 * _x1 + m02 * _x2) * inv;
                    double c1x = (m01 * _x0 + m11 * _x1 + m12 * _x2) * inv;
                    double c2x = (m02 * _x0 + m12 * _x1 + m22 * _x2) * inv;
                    double c0y = (m00 * _y0 + m01 * _y1 + m02 * _y2) * inv;
                    double c1y = (m01 * _y0 + m11 * _y1 + m12 * _y2) * inv;
                    double c2y = (m02 * _y0 + m12 * _y1 + m22 * _y2) * inv;
                    double c0z = (m00 * _z0 + m01 * _z1 + m02 * _z2) * inv;
                    double c1z = (m01 * _z0 + m11 * _z1 + m12 * _z2) * inv;
                    double c2z = (m02 * _z0 + m12 * _z1 + m22 * _z2) * inv;

                    double explained = c0x * _x0 + c1x * _x1 + c2x * _x2
                                     + c0y * _y0 + c1y * _y1 + c2y * _y2
                                     + c0z * _z0 + c1z * _z1 + c2z * _z2;
                    rms = Math.Sqrt(Math.Max(0.0, _sumSq - explained) / _n);
                    value = new ThrowVec3D(c0x, c0y, c0z);
                    slope = new ThrowVec3D(c1x * _scale, c1y * _scale, c1z * _scale);
                    double s2 = 2.0 * _scale * _scale;
                    accel = new ThrowVec3D(c2x * s2, c2y * s2, c2z * s2);
                    return 2;
                }
            }

            if (wantedOrder >= 1 && _n >= 2 && varU > 1e-10)
            {
                double det = _s0 * _s2 - _s1 * _s1;
                if (det > 0.0)
                {
                    double inv = 1.0 / det;
                    double c0x = (_s2 * _x0 - _s1 * _x1) * inv;
                    double c1x = (_s0 * _x1 - _s1 * _x0) * inv;
                    double c0y = (_s2 * _y0 - _s1 * _y1) * inv;
                    double c1y = (_s0 * _y1 - _s1 * _y0) * inv;
                    double c0z = (_s2 * _z0 - _s1 * _z1) * inv;
                    double c1z = (_s0 * _z1 - _s1 * _z0) * inv;
                    double explained = c0x * _x0 + c1x * _x1 + c0y * _y0 + c1y * _y1 + c0z * _z0 + c1z * _z1;
                    rms = Math.Sqrt(Math.Max(0.0, _sumSq - explained) / _n);
                    value = new ThrowVec3D(c0x, c0y, c0z);
                    slope = new ThrowVec3D(c1x * _scale, c1y * _scale, c1z * _scale);
                    return 1;
                }
            }

            // Not enough to tell a speed: just the average.
            value = new ThrowVec3D(_x0 / _s0, _y0 / _s0, _z0 / _s0);
            double meanSq = value.X * value.X + value.Y * value.Y + value.Z * value.Z;
            rms = Math.Sqrt(Math.Max(0.0, _sumSq / _n - meanSq));
            return 0;
        }
    }

    /// <summary>The fastest moment of the ball's travel found inside a stretch of pose history.</summary>
    internal struct SpeedPeak
    {
        /// <summary>False when there were not enough readings to measure any speed.</summary>
        public bool Found;

        /// <summary>When the ball was fastest (refined between readings).</summary>
        public double Time;

        /// <summary>The ball's velocity around that moment (an average over a few readings).</summary>
        public Vec3 Velocity;

        public float Speed;

        /// <summary>True when slower readings were seen both before and after the peak, so it is a real peak
        /// and not just the edge of the stretch that was searched.</summary>
        public bool Interior;

        /// <summary>True when the fastest reading was the last one searched (the hand may still be speeding up).</summary>
        public bool AtEnd;

        /// <summary>How many readings had a measurable speed.</summary>
        public int Candidates;
    }

    /// <summary>Shared helpers for the throw estimator and the delay calibration.</summary>
    internal static class ThrowMath
    {
        /// <summary>Readings further apart than this are not treated as neighbours when measuring speed.</summary>
        public const double MaxNeighbourGapSeconds = 0.05;

        /// <summary>How far either side of the fastest reading is used when pinning down the exact moment of peak speed.</summary>
        public const double PeakRefineHalfWidthSeconds = 0.04;

        /// <summary>
        /// The turn that takes rotation 'from' to rotation 'to', as an axis in world space whose length is
        /// the angle in radians. Dividing by the time between them gives the rate of turning.
        /// </summary>
        public static Vec3 RotationVector(Quat from, Quat to)
        {
            Quat d = to * Quat.Conjugate(from);
            float w = d.W;
            Vec3 v = new Vec3(d.X, d.Y, d.Z);
            if (w < 0f)
            {
                // The same turn written the long way round: flip to the short way.
                w = -w;
                v = -v;
            }
            float s = v.Length();
            if (float.IsNaN(s) || float.IsNaN(w)) return default(Vec3);
            if (s < 1e-7f) return v * 2f;
            float angle = 2f * MathF.Atan2(s, w);
            return v * (angle / s);
        }

        /// <summary>World position of the ball for one pose: the controller's origin plus the ball's offset turned with the hand.</summary>
        public static Vec3 BallPoint(PoseSample pose, Vec3 ballLocalOffset)
        {
            return pose.Position + Vec3.Transform(ballLocalOffset, pose.Rotation);
        }

        /// <summary>
        /// The ball's velocity around reading number 'index', from a straight-line fit through that reading
        /// and its neighbours within halfWidth seconds either side. Averaging over neighbours keeps noise
        /// down; using both sides keeps the answer centred on the reading instead of lagging behind it.
        /// </summary>
        /// <param name="centreTime">The moment the answer really belongs to (the average time of the readings used).</param>
        public static bool LocalBallVelocity(PoseHistory history, int index, Vec3 ballLocalOffset, double halfWidth,
            out Vec3 velocity, out double centreTime)
        {
            velocity = default(Vec3);
            centreTime = 0.0;
            int n = history.Count;
            if (index < 0 || index >= n) return false;

            PoseSample centre = history[index];
            double t0 = centre.Time;
            centreTime = t0;

            int lo = index;
            while (lo > 0 && t0 - history[lo - 1].Time <= halfWidth) lo--;
            int hi = index;
            while (hi < n - 1 && history[hi + 1].Time - t0 <= halfWidth) hi++;
            // Always reach for at least one neighbour each side when one is reasonably close.
            if (lo == index && index > 0 && t0 - history[index - 1].Time <= MaxNeighbourGapSeconds) lo = index - 1;
            if (hi == index && index < n - 1 && history[index + 1].Time - t0 <= MaxNeighbourGapSeconds) hi = index + 1;
            if (hi == lo) return false;

            Vec3 reference = BallPoint(centre, ballLocalOffset);
            ThrowFit3 fit = default(ThrowFit3);
            fit.Begin(halfWidth * 2.0);
            for (int i = lo; i <= hi; i++)
            {
                PoseSample s = history[i];
                Vec3 b = BallPoint(s, ballLocalOffset) - reference;
                fit.Add(s.Time - t0, b.X, b.Y, b.Z);
            }
            ThrowVec3D value, slope, accel;
            double rms;
            int order = fit.Solve(1, out value, out slope, out accel, out rms);
            if (order < 1) return false;
            velocity = slope.ToVec3();
            centreTime = t0 + fit.MeanTime;
            return IsFinite(velocity);
        }

        /// <summary>
        /// Search the readings between two times for the moment the ball was moving fastest.
        /// </summary>
        public static SpeedPeak FindSpeedPeak(PoseHistory history, double fromTime, double toTime, Vec3 ballLocalOffset,
            double halfWidth)
        {
            SpeedPeak peak = default(SpeedPeak);
            int first, count;
            history.FindRange(fromTime, toTime, out first, out count);
            if (count <= 0) return peak;

            bool havePrev = false;
            double prevT = 0.0;
            float prevS = 0f;

            bool bestHasPrev = false, bestHasNext = false;
            double bestPrevT = 0.0, bestNextT = 0.0;
            float bestPrevS = 0f, bestNextS = 0f;
            bool lastWasBest = false;

            for (int i = first; i < first + count; i++)
            {
                Vec3 v;
                double tc;
                if (!LocalBallVelocity(history, i, ballLocalOffset, halfWidth, out v, out tc)) continue;
                float s = v.Length();
                peak.Candidates++;

                if (lastWasBest)
                {
                    bestHasNext = true;
                    bestNextT = tc;
                    bestNextS = s;
                    lastWasBest = false;
                }
                if (!peak.Found || s > peak.Speed)
                {
                    peak.Found = true;
                    peak.Speed = s;
                    peak.Velocity = v;
                    peak.Time = tc;
                    bestHasPrev = havePrev;
                    bestPrevT = prevT;
                    bestPrevS = prevS;
                    bestHasNext = false;
                    lastWasBest = true;
                }
                havePrev = true;
                prevT = tc;
                prevS = s;
            }

            if (!peak.Found) return peak;
            peak.AtEnd = !bestHasNext;
            peak.Interior = bestHasPrev && bestHasNext;
            if (!peak.Interior) return peak;

            // The top of a swing is broad and flat, so "the single fastest reading" jumps about with noise.
            // Pin the moment down better by laying a smooth hill over all the speeds near the top and
            // taking the hill's summit.
            double rawTime = peak.Time;
            ThrowFit3 hill = default(ThrowFit3);
            hill.Begin(PeakRefineHalfWidthSeconds * 2.0);
            for (int i = first; i < first + count; i++)
            {
                double ti = history[i].Time;
                if (ti < rawTime - PeakRefineHalfWidthSeconds - halfWidth) continue;
                if (ti > rawTime + PeakRefineHalfWidthSeconds + halfWidth) break;
                Vec3 v;
                double tc;
                if (!LocalBallVelocity(history, i, ballLocalOffset, halfWidth, out v, out tc)) continue;
                if (Math.Abs(tc - rawTime) > PeakRefineHalfWidthSeconds) continue;
                hill.Add(tc - rawTime, v.Length(), 0.0, 0.0);
            }
            ThrowVec3D top, slope, bend;
            double rms;
            bool refined = false;
            if (hill.Count >= 5 && hill.Solve(2, out top, out slope, out bend, out rms) == 2 && bend.X < -1e-6)
            {
                double shift = -slope.X / bend.X;
                if (Math.Abs(shift) <= PeakRefineHalfWidthSeconds * 0.75)
                {
                    peak.Time = rawTime + shift;
                    refined = true;
                }
            }

            if (!refined)
            {
                // Too few readings for that: use the top of the curve through the peak and its two neighbours.
                double d1 = rawTime - bestPrevT;
                double d2 = bestNextT - rawTime;
                if (d1 > 1e-6 && d2 > 1e-6)
                {
                    double slopeBefore = (peak.Speed - bestPrevS) / d1;
                    double slopeAfter = (bestNextS - peak.Speed) / d2;
                    double curve = (slopeAfter - slopeBefore) / (d1 + d2);
                    if (curve < -1e-9)
                    {
                        double slopeHere = (slopeAfter * d1 + slopeBefore * d2) / (d1 + d2);
                        double shift = -slopeHere / (2.0 * curve);
                        if (shift < -0.5 * d1) shift = -0.5 * d1;
                        if (shift > 0.5 * d2) shift = 0.5 * d2;
                        peak.Time = rawTime + shift;
                    }
                }
            }
            return peak;
        }

        public static bool IsFinite(Vec3 v)
        {
            return !(float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z)
                  || float.IsInfinity(v.X) || float.IsInfinity(v.Y) || float.IsInfinity(v.Z));
        }

        public static Vec3 FiniteOrZero(Vec3 v)
        {
            return IsFinite(v) ? v : default(Vec3);
        }

        public static double Clamp(double x, double min, double max)
        {
            return x < min ? min : (x > max ? max : x);
        }

        public static float Clamp(float x, float min, float max)
        {
            return x < min ? min : (x > max ? max : x);
        }
    }
}
