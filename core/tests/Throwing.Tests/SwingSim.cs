using System;
using System.Collections.Generic;

namespace Portaballs.Core.Tests
{
    // ---------------------------------------------------------------------------------------------
    // A pretend arm and a pretend headset, so the throwing maths can be scored against a known truth.
    //
    // Part 1 (Swing) is the "real world": an arm swinging smoothly, with the exact position and
    // velocity of the ball known at every instant, including the moment the player truly lets go.
    //
    // Part 2 (Rig + ThrowSim) is "what the game sees": that motion sampled at a frame rate, late,
    // noisy, with frames missing, and a grip signal that arrives after its own delay and falls
    // gradually as the fingers open.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Three numbers at double precision, for the simulator's exact arithmetic.</summary>
    internal struct D3
    {
        public double X, Y, Z;
        public D3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static D3 operator +(D3 a, D3 b) { return new D3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static D3 operator -(D3 a, D3 b) { return new D3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static D3 operator *(D3 a, double s) { return new D3(a.X * s, a.Y * s, a.Z * s); }
        public static double Dot(D3 a, D3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        public static D3 Cross(D3 a, D3 b)
        {
            return new D3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }
        public double Length { get { return Math.Sqrt(Dot(this, this)); } }
        public D3 Normalized { get { double l = Length; return l > 1e-300 ? this * (1.0 / l) : new D3(1, 0, 0); } }
        public Vec3 ToVec3() { return new Vec3((float)X, (float)Y, (float)Z); }
        public static D3 From(Vec3 v) { return new D3(v.X, v.Y, v.Z); }
    }

    /// <summary>A rotation at double precision. a * b means "turn by b first, then by a".</summary>
    internal struct Qd
    {
        public double W, X, Y, Z;
        public Qd(double w, double x, double y, double z) { W = w; X = x; Y = y; Z = z; }
        public static Qd Identity { get { return new Qd(1, 0, 0, 0); } }

        public static Qd AxisAngle(D3 axis, double angle)
        {
            D3 a = axis.Normalized;
            double s = Math.Sin(angle * 0.5);
            return new Qd(Math.Cos(angle * 0.5), a.X * s, a.Y * s, a.Z * s);
        }

        public static Qd operator *(Qd a, Qd b)
        {
            return new Qd(
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W);
        }

        public Qd Conjugate { get { return new Qd(W, -X, -Y, -Z); } }

        public D3 Rotate(D3 v)
        {
            D3 q = new D3(X, Y, Z);
            D3 t = D3.Cross(q, v) * 2.0;
            return v + t * W + D3.Cross(q, t);
        }

        /// <summary>The turn from 'from' to 'to' as a world-space axis whose length is the angle.</summary>
        public static D3 RotationVector(Qd from, Qd to)
        {
            Qd d = to * from.Conjugate;
            double w = d.W;
            D3 v = new D3(d.X, d.Y, d.Z);
            if (w < 0) { w = -w; v = v * -1.0; }
            double s = v.Length;
            if (s < 1e-14) return v * 2.0;
            return v * (2.0 * Math.Atan2(s, w) / s);
        }

        public Quat ToQuat() { return new Quat((float)X, (float)Y, (float)Z, (float)W); }
    }

    public enum ThrowKind
    {
        Overarm = 0,
        Underarm = 1,
        SidearmFlick = 2,
        GentleToss = 3,
    }

    /// <summary>
    /// One arm swing, described by smooth formulas so the truth is known exactly.
    ///
    /// The arm is a rod swinging about a pivot (shoulder or elbow) in a tilted plane. Its turning
    /// speed rises smoothly to a peak and falls smoothly away again, so the hand keeps moving after
    /// the release. The rod gets longer through the swing (the elbow straightening), the wrist adds its
    /// own snap, the body drifts forward, and the whole swing plane turns across the body, which is
    /// what makes the follow-through curve away to one side.
    /// </summary>
    internal sealed class Swing
    {
        public ThrowKind Kind;
        public D3 Pivot0;
        public D3 BodyVelocity;
        public D3 A0;              // direction of the rod at swing angle 0
        public D3 B0;              // direction of the rod at swing angle 90 degrees
        public Qd Base;            // turns the controller's own X axis onto A0
        public double PeakTime;    // when the arm is turning fastest
        public double PhiPeak;     // swing angle at that moment
        public double Omega;       // arm turning speed at that moment, rad/s (signed)
        public double W1, W2;      // how long the arm takes to speed up / slow down
        public double R0, DR;      // rod length at the start, and how much it grows
        public double WristOmega;  // wrist snap peak turning speed, rad/s (signed)
        public double WristW1, WristW2, WristLag, WristPsiPeak;
        public double CrossYaw;    // how far the swing plane turns across the body in total, radians
        public D3 BallOffsetLocal; // where the ball sits in the controller's own axes

        // Running total of a smooth bump of height 1 that rises over w1 and falls over w2, measured from its peak.
        private static double BumpIntegral(double tau, double w1, double w2)
        {
            if (tau < 0)
            {
                if (tau < -w1) tau = -w1;
                return 0.5 * (tau + (w1 / Math.PI) * Math.Sin(Math.PI * tau / w1));
            }
            if (tau > w2) tau = w2;
            return 0.5 * (tau + (w2 / Math.PI) * Math.Sin(Math.PI * tau / w2));
        }

        // Smooth 0-to-1 ramp with no jolt at either end.
        private static double Ease(double x)
        {
            if (x <= 0) return 0;
            if (x >= 1) return 1;
            return x * x * x * (x * (x * 6 - 15) + 10);
        }

        /// <summary>Exact pose of the controller at a time.</summary>
        public void Pose(double t, out D3 position, out Qd rotation)
        {
            double phi = PhiPeak + Omega * BumpIntegral(t - PeakTime, W1, W2);
            double psi = WristPsiPeak + WristOmega * BumpIntegral(t - PeakTime - WristLag, WristW1, WristW2);
            double yaw = CrossYaw * Ease((t - (PeakTime - 0.06)) / 0.30);
            double radius = R0 + DR * Ease((t - (PeakTime - 0.16)) / 0.22);

            D3 n0 = D3.Cross(A0, B0);
            Qd yawTurn = Qd.AxisAngle(new D3(0, 0, 1), yaw);
            D3 rod = A0 * Math.Cos(phi) + B0 * Math.Sin(phi);
            D3 pivot = Pivot0 + BodyVelocity * (t - PeakTime);

            position = pivot + yawTurn.Rotate(rod * radius);
            rotation = yawTurn * Qd.AxisAngle(n0, phi + psi) * Base;
        }

        /// <summary>Exact world position of the ball while it is still in the hand.</summary>
        public D3 BallPoint(double t)
        {
            D3 p;
            Qd q;
            Pose(t, out p, out q);
            return p + q.Rotate(BallOffsetLocal);
        }

        /// <summary>Exact velocity of the ball while it is still in the hand.</summary>
        public D3 BallVelocity(double t)
        {
            const double h = 1e-4;
            return (BallPoint(t + h) - BallPoint(t - h)) * (1.0 / (2 * h));
        }

        public D3 HandVelocity(double t)
        {
            const double h = 1e-4;
            D3 a, b;
            Qd qa, qb;
            Pose(t - h, out a, out qa);
            Pose(t + h, out b, out qb);
            return (b - a) * (1.0 / (2 * h));
        }

        /// <summary>Exact rate of turning of the controller (world axis, radians per second).</summary>
        public D3 AngularVelocity(double t)
        {
            const double h = 1e-4;
            D3 a, b;
            Qd qa, qb;
            Pose(t - h, out a, out qa);
            Pose(t + h, out b, out qb);
            return Qd.RotationVector(qa, qb) * (1.0 / (2 * h));
        }

        /// <summary>The moment the ball itself is moving fastest.</summary>
        public double BallPeakTime()
        {
            double best = PeakTime;
            double bestSpeed = -1;
            for (double t = PeakTime - 0.12; t <= PeakTime + 0.12; t += 0.0005)
            {
                double s = BallVelocity(t).Length;
                if (s > bestSpeed) { bestSpeed = s; best = t; }
            }
            double lo = best - 0.0005, hi = best + 0.0005;
            for (int i = 0; i < 30; i++)
            {
                double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
                if (BallVelocity(m1).Length < BallVelocity(m2).Length) lo = m1; else hi = m2;
            }
            return 0.5 * (lo + hi);
        }

        private static double Deg(double d) { return d * Math.PI / 180.0; }

        /// <summary>A believable right-handed throw of the given kind, with its details picked by the seeded generator.</summary>
        public static Swing Random(ThrowKind kind, ref Rng rng)
        {
            Swing s = new Swing();
            s.Kind = kind;
            s.PeakTime = 1.0 + rng.Range(0f, 0.011f);
            s.Base = Qd.Identity;
            D3 forward = new D3(1, 0, 0), left = new D3(0, 1, 0), up = new D3(0, 0, 1);

            switch (kind)
            {
                case ThrowKind.Overarm:
                    s.Pivot0 = new D3(rng.Range(-0.05f, 0.05f), -0.20, rng.Range(1.35f, 1.45f));
                    s.BodyVelocity = new D3(rng.Range(0.3f, 1.2f), 0, 0);
                    s.A0 = forward; s.B0 = up;
                    s.PhiPeak = Deg(rng.Range(78f, 98f));
                    s.Omega = -rng.Range(9f, 14f);
                    s.W1 = rng.Range(0.20f, 0.30f); s.W2 = rng.Range(0.16f, 0.24f);
                    s.R0 = rng.Range(0.42f, 0.50f); s.DR = rng.Range(0.12f, 0.22f);
                    s.WristOmega = -rng.Range(8f, 18f);
                    s.WristW1 = rng.Range(0.07f, 0.11f); s.WristW2 = rng.Range(0.07f, 0.12f);
                    s.WristLag = rng.Range(-0.010f, 0.015f);
                    s.WristPsiPeak = rng.Range(-0.2f, 0.2f);
                    s.CrossYaw = Deg(rng.Range(20f, 40f));
                    s.BallOffsetLocal = new D3(rng.Range(0.06f, 0.09f), 0, rng.Range(0f, 0.03f));
                    break;

                case ThrowKind.Underarm:
                case ThrowKind.GentleToss:
                    bool gentle = kind == ThrowKind.GentleToss;
                    s.Pivot0 = new D3(rng.Range(-0.05f, 0.05f), -0.22, rng.Range(1.35f, 1.45f));
                    s.BodyVelocity = new D3(gentle ? rng.Range(0f, 0.2f) : rng.Range(0f, 0.6f), 0, 0);
                    s.A0 = forward; s.B0 = up;
                    s.PhiPeak = -Deg(gentle ? rng.Range(35f, 55f) : rng.Range(40f, 62f));
                    s.Omega = gentle ? rng.Range(2.8f, 4.5f) : rng.Range(6f, 10f);
                    s.W1 = gentle ? rng.Range(0.25f, 0.35f) : rng.Range(0.22f, 0.32f);
                    s.W2 = gentle ? rng.Range(0.22f, 0.30f) : rng.Range(0.18f, 0.28f);
                    s.R0 = rng.Range(0.55f, 0.62f); s.DR = rng.Range(0.03f, 0.08f);
                    s.WristOmega = gentle ? rng.Range(2f, 5f) : rng.Range(4f, 10f);
                    s.WristW1 = rng.Range(0.08f, 0.12f); s.WristW2 = rng.Range(0.08f, 0.12f);
                    s.WristLag = rng.Range(-0.010f, 0.015f);
                    s.WristPsiPeak = rng.Range(-0.2f, 0.2f);
                    s.CrossYaw = Deg(rng.Range(5f, 20f));
                    s.BallOffsetLocal = new D3(rng.Range(0.06f, 0.09f), 0, rng.Range(0f, 0.03f));
                    break;

                default: // SidearmFlick: mostly forearm and a strong wrist snap, in a nearly level plane
                    {
                        double tilt = Deg(rng.Range(5f, 20f));   // the plane climbs a little toward the front
                        s.Pivot0 = new D3(rng.Range(0.05f, 0.15f), -0.28, rng.Range(1.15f, 1.25f));
                        s.BodyVelocity = new D3(rng.Range(0f, 0.3f), 0, 0);
                        s.A0 = new D3(Math.Cos(tilt), 0, Math.Sin(tilt));
                        s.B0 = left;
                        s.Base = Qd.AxisAngle(new D3(0, 1, 0), -tilt);
                        s.PhiPeak = -Deg(rng.Range(70f, 95f));
                        s.Omega = rng.Range(7f, 11f);
                        s.W1 = rng.Range(0.14f, 0.20f); s.W2 = rng.Range(0.12f, 0.18f);
                        s.R0 = rng.Range(0.33f, 0.42f); s.DR = rng.Range(0.03f, 0.08f);
                        s.WristOmega = rng.Range(20f, 32f);
                        s.WristW1 = rng.Range(0.05f, 0.08f); s.WristW2 = rng.Range(0.05f, 0.08f);
                        s.WristLag = rng.Range(-0.005f, 0.015f);
                        s.WristPsiPeak = rng.Range(-0.2f, 0.2f);
                        s.CrossYaw = Deg(rng.Range(10f, 30f));
                        s.BallOffsetLocal = new D3(rng.Range(0.08f, 0.11f), 0, 0);
                        break;
                    }
            }
            return s;
        }
    }

    /// <summary>
    /// One headset-and-link setup: everything about how the game gets its data that stays the same
    /// from throw to throw. The delay calibration is meant to learn these, so a "session" of throws
    /// shares one Rig.
    /// </summary>
    internal sealed class Rig
    {
        public double RateHz = 90;
        public double ButtonDelay = 0.020;        // how late the grip signal arrives, seconds
        public double ButtonDelayJitter = 0.002;  // throw-to-throw wobble of that delay
        public double PoseLatency = 0.010;        // how old the pose data is when the runtime gets it
        public double PredictionFraction = 0.5;   // how much of that the runtime makes up by guessing ahead (0 none, 1 all)
        public double PositionNoise = 0.0008;     // metres, per axis
        public double RotationNoise = 0.2 * Math.PI / 180.0;   // radians, per axis
        public double TimestampNoise = 0.0002;    // error in the reported time of each pose, seconds
        public double FrameJitter = 0.08;         // how uneven the frame spacing is, as a share of a frame
        public double DropProbability = 0.02;     // chance any one frame is missing
        public double BurstProbability = 0.10;    // chance a throw loses 2-3 frames in a row shortly before release
        public double RampMin = 0.030, RampMax = 0.060;   // how long the fingers take to open, seconds
        public double FingerTimingJitter = 0.003; // throw-to-throw scatter of when the fingers start to open, relative to the true release
        public double SqueezeNoise = 0.01;

        /// <summary>A setup with no imperfections at all, for checking the maths in isolation.</summary>
        public static Rig Perfect(double rateHz)
        {
            Rig r = new Rig();
            r.RateHz = rateHz;
            r.ButtonDelay = 0; r.ButtonDelayJitter = 0; r.PoseLatency = 0; r.PredictionFraction = 0;
            r.PositionNoise = 0; r.RotationNoise = 0; r.TimestampNoise = 0; r.FrameJitter = 0;
            r.DropProbability = 0; r.BurstProbability = 0; r.SqueezeNoise = 0; r.FingerTimingJitter = 0;
            return r;
        }

        public static Rig Random(double rateHz, ref Rng rng)
        {
            Rig r = new Rig();
            r.RateHz = rateHz;
            r.ButtonDelay = rng.Range(0f, 0.040f);
            r.PoseLatency = rng.Range(0f, 0.015f);
            r.PredictionFraction = rng.Range(0f, 1f);
            r.PositionNoise = rng.Range(0.0005f, 0.0010f);
            r.RotationNoise = rng.Range(0.1f, 0.3f) * Math.PI / 180.0;
            return r;
        }
    }

    /// <summary>One frame of what the game receives.</summary>
    internal struct SimFrame
    {
        public double Stamp;
        public Vec3 Position;
        public Quat Rotation;
        public float Squeeze;
    }

    /// <summary>One simulated throw: the frames the game sees, plus the truth to score against.</summary>
    internal sealed class SimThrow
    {
        public Swing Swing;
        public Rig Rig;
        public List<SimFrame> Frames = new List<SimFrame>();

        public double TrueReleaseTime;
        public Vec3 TrueReleasePosition;
        public Vec3 TrueReleaseVelocity;
        public double BallPeakTime;
        public double SqueezeOnsetTime;   // when the fingers really began to open (before the signal's delay)
        public double ButtonDelay;        // the delay this particular throw's grip signal had
        public double RampSeconds;
        public float WallX;               // the wall this throw is aimed at is the plane x = WallX
    }

    internal static class ThrowSim
    {
        /// <param name="intentMean">How long after the ball's fastest moment the player truly lets go, on average (negative = before).</param>
        /// <param name="intentSd">Throw-to-throw scatter of that.</param>
        public static SimThrow Generate(Swing swing, Rig rig, ref Rng rng, double intentMean = -0.004, double intentSd = 0.005)
        {
            SimThrow th = new SimThrow();
            th.Swing = swing;
            th.Rig = rig;
            th.BallPeakTime = swing.BallPeakTime();
            double intent = intentMean + intentSd * rng.Gaussian();
            intent = Math.Max(intentMean - 0.012, Math.Min(intentMean + 0.012, intent));
            th.TrueReleaseTime = th.BallPeakTime + intent;
            th.TrueReleasePosition = swing.BallPoint(th.TrueReleaseTime).ToVec3();
            th.TrueReleaseVelocity = swing.BallVelocity(th.TrueReleaseTime).ToVec3();
            th.WallX = rng.Range(3f, 6f);

            // The grip: held firmly, then the fingers start opening at about the true release and take a while.
            double hold = rng.Range(0.85f, 1.0f);
            th.SqueezeOnsetTime = th.TrueReleaseTime + rig.FingerTimingJitter * rng.Gaussian();
            th.RampSeconds = rig.RampMin + (rig.RampMax - rig.RampMin) * rng.NextFloat();
            th.ButtonDelay = Math.Max(0.0, rig.ButtonDelay + rig.ButtonDelayJitter * rng.Gaussian());

            double period = 1.0 / rig.RateHz;
            double start = swing.PeakTime - 0.55 + period * rng.NextFloat();
            double end = swing.PeakTime + 0.35;

            // Maybe lose a few frames in a row somewhere in the last 150 ms before release.
            double burstStart = double.MaxValue, burstEnd = double.MinValue;
            if (rng.NextFloat() < rig.BurstProbability)
            {
                burstStart = th.TrueReleaseTime - 0.15 * rng.NextFloat();
                burstEnd = burstStart + period * rng.RangeInt(2, 4) - period * 0.5;
            }

            int k = 0;
            for (double frameTime = start; frameTime <= end; frameTime += period, k++)
            {
                double t = frameTime + period * rig.FrameJitter * (2.0 * rng.NextFloat() - 1.0);
                bool dropped = rng.NextFloat() < rig.DropProbability || (t >= burstStart && t <= burstEnd);

                // The pose the runtime hands over: measured a little in the past, then pushed forward by a guess.
                double measuredAt = t - rig.PoseLatency;
                double guessAhead = rig.PoseLatency * rig.PredictionFraction;
                D3 p;
                Qd q;
                swing.Pose(measuredAt, out p, out q);
                if (guessAhead > 0)
                {
                    p = p + swing.HandVelocity(measuredAt) * guessAhead;
                    D3 turn = swing.AngularVelocity(measuredAt) * guessAhead;
                    double angle = turn.Length;
                    if (angle > 1e-12) q = Qd.AxisAngle(turn, angle) * q;
                }
                // Tracking noise.
                p = p + new D3(rng.Gaussian(), rng.Gaussian(), rng.Gaussian()) * rig.PositionNoise;
                D3 wobble = new D3(rng.Gaussian(), rng.Gaussian(), rng.Gaussian()) * rig.RotationNoise;
                double wobbleAngle = wobble.Length;
                if (wobbleAngle > 1e-12) q = Qd.AxisAngle(wobble, wobbleAngle) * q;

                // The grip value the game reads this frame left the controller a little while ago.
                double gripAt = t - th.ButtonDelay;
                double squeeze;
                if (k < 3)
                {
                    squeeze = 0.0;   // hand open at the very start, so the detector sees a proper grab
                }
                else
                {
                    double x = (gripAt - th.SqueezeOnsetTime) / th.RampSeconds;
                    x = Math.Max(0.0, Math.Min(1.0, x));
                    double eased = x * x * (3 - 2 * x);
                    double open = 0.5 * x + 0.5 * eased;   // mostly a steady fall with slightly rounded ends
                    squeeze = hold * (1.0 - open) + rig.SqueezeNoise * rng.Gaussian();
                }

                double stamp = t + rig.TimestampNoise * rng.Gaussian();
                if (dropped) continue;

                SimFrame f;
                f.Stamp = stamp;
                f.Position = p.ToVec3();
                f.Rotation = q.ToQuat();
                f.Squeeze = (float)Math.Max(0.0, Math.Min(1.0, squeeze));
                th.Frames.Add(f);
            }
            return th;
        }
    }

    /// <summary>How far one launch was from the truth.</summary>
    internal struct ThrowError
    {
        public double DirectionDeg;   // angle between the launch direction and the true one
        public double SpeedFraction;  // (speed - true speed) / true speed; negative means too weak
        public double LandingMeters;  // distance between where it lands and where the true throw lands
        public bool Missed;           // never reached the surface the true throw landed on
    }

    internal static class Scoring
    {
        public static readonly Vec3 Gravity = new Vec3(0f, 0f, -9.81f);
        public const double MissPenaltyMeters = 10.0;

        /// <summary>
        /// Score a launch against the truth. "Landing" is where the true throw first meets either the wall
        /// (the plane x = WallX) or the floor (z = 0); the launch under test is followed to that same surface.
        /// </summary>
        public static ThrowError Score(SimThrow th, Vec3 position, Vec3 velocity)
        {
            ThrowError e = default(ThrowError);
            Vec3 trueV = th.TrueReleaseVelocity;
            e.DirectionDeg = MathUtil.AngleBetween(velocity, trueV) * 180.0 / Math.PI;
            if (velocity.Length() < 1e-4f) e.DirectionDeg = 180.0;
            e.SpeedFraction = (velocity.Length() - trueV.Length()) / trueV.Length();

            Vec3 wallPoint = new Vec3(th.WallX, 0f, 0f);
            Vec3 wallNormal = new Vec3(-1f, 0f, 0f);
            Vec3 floorNormal = new Vec3(0f, 0f, 1f);

            float tWall, tFloor;
            Vec3 hitWall, hitFloor;
            bool wall = Ballistics.TryHitPlane(th.TrueReleasePosition, trueV, Gravity, wallPoint, wallNormal, out tWall, out hitWall);
            bool floor = Ballistics.TryHitPlane(th.TrueReleasePosition, trueV, Gravity, default(Vec3), floorNormal, out tFloor, out hitFloor);
            bool useWall = wall && (!floor || tWall <= tFloor);

            Vec3 truth = useWall ? hitWall : hitFloor;
            float t;
            Vec3 hit;
            bool ok = useWall
                ? Ballistics.TryHitPlane(position, velocity, Gravity, wallPoint, wallNormal, out t, out hit)
                : Ballistics.TryHitPlane(position, velocity, Gravity, default(Vec3), floorNormal, out t, out hit);
            if (!ok)
            {
                e.Missed = true;
                e.LandingMeters = MissPenaltyMeters;
            }
            else
            {
                e.LandingMeters = Math.Min(MissPenaltyMeters, (hit - truth).Length());
            }
            return e;
        }
    }

    /// <summary>What happened when one simulated throw was played through the real code.</summary>
    internal sealed class Playback
    {
        public bool Released;
        public double EventTime;     // the detector's release time (worked out between frames)
        public double OnsetTime;     // the detector's guess at when the fingers began to open
        public double NoticedTime;   // timestamp of the frame the release was noticed on
        public Vec3 NaivePosition;   // the ball at that frame
        public Vec3 NaiveVelocity;   // its velocity from that frame and the one before: the "naive method"
        public ThrowResult[] Results;
        public CalibrationObservation Observation;
        public PoseHistory History;
    }

    internal static class Player
    {
        /// <summary>
        /// Feed a simulated throw to the pose history and grip detector frame by frame, exactly as the
        /// game would. On the frame the release is noticed, run the estimator once per settings object.
        /// A little later, let the calibrator (if any) look at the throw.
        /// </summary>
        public static Playback Play(SimThrow th, ThrowSettings[] variants, ReleaseDelayCalibrator calibrator = null,
            bool useOnsetTime = false, double observeAfterSeconds = 0.08, Action<Playback> atRelease = null)
        {
            Playback pb = new Playback();
            pb.History = new PoseHistory();
            pb.Results = new ThrowResult[variants.Length];
            GripReleaseDetector detector = new GripReleaseDetector();
            Vec3 offset = th.Swing.BallOffsetLocal.ToVec3();
            bool observed = false;

            for (int i = 0; i < th.Frames.Count; i++)
            {
                SimFrame f = th.Frames[i];
                pb.History.Add(f.Stamp, f.Position, f.Rotation);
                GripEvent ev = detector.Update(f.Stamp, f.Squeeze);

                if (ev == GripEvent.Released && !pb.Released)
                {
                    pb.Released = true;
                    pb.EventTime = useOnsetTime ? detector.ReleaseOnsetTime : detector.ReleaseTime;
                    pb.OnsetTime = detector.ReleaseOnsetTime;
                    pb.NoticedTime = f.Stamp;

                    // The naive method: the ball's velocity from this frame and the previous one.
                    pb.NaivePosition = f.Position + Vec3.Transform(offset, f.Rotation);
                    if (i > 0)
                    {
                        SimFrame prev = th.Frames[i - 1];
                        Vec3 prevBall = prev.Position + Vec3.Transform(offset, prev.Rotation);
                        double dt = f.Stamp - prev.Stamp;
                        if (dt > 1e-6) pb.NaiveVelocity = (pb.NaivePosition - prevBall) / (float)dt;
                    }

                    for (int v = 0; v < variants.Length; v++)
                    {
                        variants[v].BallLocalOffset = offset;
                        pb.Results[v] = ThrowEstimator.Estimate(pb.History, pb.EventTime, f.Stamp, variants[v]);
                    }
                    // Lets a test run extra estimates with only the data the game has at this moment.
                    if (atRelease != null) atRelease(pb);
                }

                if (pb.Released && !observed && calibrator != null && f.Stamp >= pb.NoticedTime + observeAfterSeconds)
                {
                    observed = true;
                    pb.Observation = calibrator.Observe(pb.History, pb.EventTime, offset);
                }
            }
            return pb;
        }
    }
}
