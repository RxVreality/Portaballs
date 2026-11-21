using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Portaballs.Core.Tests
{
    internal static class Check
    {
        public static void Finite(Vec3 v, string what)
        {
            Assert.False(float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z)
                      || float.IsInfinity(v.X) || float.IsInfinity(v.Y) || float.IsInfinity(v.Z), what + " is not a real number");
        }

        public static void Finite(Quat q, string what)
        {
            Assert.False(float.IsNaN(q.X) || float.IsNaN(q.Y) || float.IsNaN(q.Z) || float.IsNaN(q.W), what + " is not a real number");
        }

        public static void Near(Vec3 expected, Vec3 actual, float tolerance, string what)
        {
            Assert.True((expected - actual).Length() <= tolerance,
                what + ": expected " + expected + " but got " + actual + " (allowed error " + tolerance + ")");
        }

        public static Quat AboutZ(float radians)
        {
            return Quat.CreateFromAxisAngle(new Vec3(0f, 0f, 1f), radians);
        }

        public static float Deg(float degrees)
        {
            return degrees * MathF.PI / 180f;
        }
    }

    public class PoseHistoryTests
    {
        [Fact]
        public void QuaternionConventionsAreWhatTheMathsAssumes()
        {
            // Turning +X a quarter turn about +Z (up) must give +Y (left), per Units.cs.
            Vec3 turned = Vec3.Transform(new Vec3(1f, 0f, 0f), Check.AboutZ(MathF.PI / 2f));
            Check.Near(new Vec3(0f, 1f, 0f), turned, 1e-5f, "quarter turn about up");

            // The simulator's own double-precision rotation must agree with System.Numerics.
            Qd q = Qd.AxisAngle(new D3(0.3, -0.5, 0.8), 1.1) * Qd.AxisAngle(new D3(1, 0, 0), -0.4);
            D3 v = new D3(0.2, 0.7, -0.4);
            Check.Near(q.Rotate(v).ToVec3(), Vec3.Transform(v.ToVec3(), q.ToQuat()), 1e-5f, "Qd.Rotate vs Vec3.Transform");
            Quat product = Quat.CreateFromAxisAngle(Vec3.Normalize(new Vec3(0.3f, -0.5f, 0.8f)), 1.1f)
                         * Quat.CreateFromAxisAngle(new Vec3(1f, 0f, 0f), -0.4f);
            Check.Near(Vec3.Transform(v.ToVec3(), product), q.Rotate(v).ToVec3(), 1e-5f, "product order");

            // The turn between two rotations comes back as a world-space axis times the angle.
            Quat from = Quat.CreateFromAxisAngle(new Vec3(1f, 0f, 0f), 0.7f);
            Quat to = Check.AboutZ(0.25f) * from;
            Check.Near(new Vec3(0f, 0f, 0.25f), ThrowMath.RotationVector(from, to), 1e-5f, "rotation vector");
            // The same rotation written with the opposite sign must give the same answer.
            Quat toNegated = new Quat(-to.X, -to.Y, -to.Z, -to.W);
            Check.Near(new Vec3(0f, 0f, 0.25f), ThrowMath.RotationVector(from, toNegated), 1e-5f, "rotation vector, negated");
        }

        [Fact]
        public void InterpolatesBetweenSamples()
        {
            PoseHistory h = new PoseHistory();
            Assert.True(h.Add(1.00, new Vec3(0f, 0f, 1f), Check.AboutZ(0f)));
            Assert.True(h.Add(1.02, new Vec3(2f, 0f, 1f), Check.AboutZ(1f)));

            PoseSample p;
            double gap;
            bool clamped;
            Assert.True(h.TrySample(1.005, out p, out gap, out clamped));
            Check.Near(new Vec3(0.5f, 0f, 1f), p.Position, 1e-5f, "quarter way");
            Check.Near(new Vec3(0f, 0f, 0.25f), ThrowMath.RotationVector(Check.AboutZ(0f), p.Rotation), 1e-4f, "quarter turn");
            Assert.Equal(0.02, gap, 6);
            Assert.False(clamped);

            // Outside the stored span it gives the nearest end and says so.
            Assert.True(h.TrySample(5.0, out p, out gap, out clamped));
            Check.Near(new Vec3(2f, 0f, 1f), p.Position, 1e-6f, "after the end");
            Assert.True(clamped);
            Assert.True(h.TrySample(0.0, out p, out gap, out clamped));
            Check.Near(new Vec3(0f, 0f, 1f), p.Position, 1e-6f, "before the start");
            Assert.True(clamped);
        }

        [Fact]
        public void KeepsSamplesSortedWithOnePerTimestamp()
        {
            PoseHistory h = new PoseHistory();
            h.Add(1.00, new Vec3(0f, 0f, 0f), Quat.Identity);
            h.Add(1.02, new Vec3(2f, 0f, 0f), Quat.Identity);
            h.Add(1.01, new Vec3(1f, 0f, 0f), Quat.Identity);     // late arrival
            h.Add(1.02, new Vec3(2.5f, 0f, 0f), Quat.Identity);   // repeat of a timestamp: replaces
            h.Add(1.02, new Vec3(3f, 0f, 0f), Quat.Identity);     // and again
            h.Add(1.00, new Vec3(0.5f, 0f, 0f), Quat.Identity);   // repeat of the oldest

            Assert.Equal(3, h.Count);
            Assert.Equal(1.00, h[0].Time);
            Assert.Equal(1.01, h[1].Time);
            Assert.Equal(1.02, h[2].Time);
            Assert.Equal(0.5f, h[0].Position.X);
            Assert.Equal(3f, h[2].Position.X);

            for (double t = 0.99; t <= 1.03; t += 0.001)
            {
                PoseSample p;
                Assert.True(h.TrySample(t, out p));
                Check.Finite(p.Position, "position at " + t);
                Check.Finite(p.Rotation, "rotation at " + t);
            }
        }

        [Fact]
        public void IsARingThatForgetsTheOldest()
        {
            PoseHistory h = new PoseHistory(capacity: 8, maxAgeSeconds: 10.0);
            for (int i = 0; i < 20; i++) h.Add(i * 0.01, new Vec3(i, 0f, 0f), Quat.Identity);
            Assert.Equal(8, h.Count);
            Assert.Equal(12f, h[0].Position.X);
            Assert.Equal(19f, h[7].Position.X);

            // A late sample goes into its place even when the ring is full (the oldest makes room).
            Assert.True(h.Add(0.155, new Vec3(15.5f, 0f, 0f), Quat.Identity));
            Assert.Equal(8, h.Count);
            Assert.Equal(13f, h[0].Position.X);
            for (int i = 1; i < h.Count; i++) Assert.True(h[i].Time > h[i - 1].Time);
            // One that is older than everything is refused.
            Assert.False(h.Add(0.001, new Vec3(0f, 0f, 0f), Quat.Identity));
        }

        [Fact]
        public void ForgetsSamplesOlderThanHalfASecond()
        {
            PoseHistory h = new PoseHistory();
            for (int i = 0; i <= 200; i++) h.Add(i * 0.01, new Vec3(i, 0f, 0f), Quat.Identity);
            Assert.True(h.NewestTime - h.OldestTime <= 0.5 + 1e-9);
            Assert.True(h.NewestTime - h.OldestTime >= 0.48);
            Assert.Equal(2.0, h.NewestTime, 9);
        }

        [Fact]
        public void RangeQueriesAreInclusiveAndSafe()
        {
            PoseHistory h = new PoseHistory();
            for (int i = 0; i < 10; i++) h.Add(1.0 + i * 0.01, new Vec3(i, 0f, 0f), Quat.Identity);
            int first, count;
            h.FindRange(1.0199, 1.0501, out first, out count);
            Assert.Equal(2, first);
            Assert.Equal(4, count);
            // Both ends are included when they land exactly on a sample.
            h.FindRange(h[2].Time, h[5].Time, out first, out count);
            Assert.Equal(2, first);
            Assert.Equal(4, count);
            h.FindRange(5.0, 6.0, out first, out count);
            Assert.Equal(0, count);
            h.FindRange(1.05, 1.02, out first, out count);
            Assert.Equal(0, count);
            h.FindRange(double.NaN, 1.0, out first, out count);
            Assert.Equal(0, count);
        }

        [Fact]
        public void ToleratesGapsAndRubbish()
        {
            PoseHistory h = new PoseHistory();
            PoseSample p;
            Assert.False(h.TrySample(1.0, out p));                  // empty
            Check.Finite(p.Position, "empty position");
            Assert.Equal(1f, p.Rotation.W);

            Assert.False(h.Add(double.NaN, new Vec3(1f, 2f, 3f), Quat.Identity));
            Assert.False(h.Add(1.0, new Vec3(float.NaN, 0f, 0f), Quat.Identity));
            Assert.False(h.Add(1.0, new Vec3(float.PositiveInfinity, 0f, 0f), Quat.Identity));
            Assert.Equal(0, h.Count);

            // A broken rotation (all zeros, or not a number) is stored as "no rotation".
            Assert.True(h.Add(1.0, new Vec3(0f, 0f, 0f), new Quat(0f, 0f, 0f, 0f)));
            Assert.True(h.Add(1.3, new Vec3(3f, 0f, 0f), new Quat(float.NaN, 0f, 0f, 1f)));   // after a long gap
            Assert.Equal(1f, h[0].Rotation.W);
            Assert.Equal(1f, h[1].Rotation.W);

            double gap;
            bool clamped;
            Assert.True(h.TrySample(1.1, out p, out gap, out clamped));
            Assert.Equal(0.3, gap, 6);
            Check.Near(new Vec3(1f, 0f, 0f), p.Position, 1e-4f, "inside the gap");
            Assert.False(h.TrySample(double.NaN, out p));

            h.Clear();
            Assert.Equal(0, h.Count);
        }
    }

    public class ReleaseDetectorTests
    {
        private readonly ITestOutputHelper _out;

        public ReleaseDetectorTests(ITestOutputHelper output)
        {
            _out = output;
        }

        // A squeeze that is held at 'hold' until 'onset', then falls in a straight line to zero over 'ramp' seconds.
        private static float Ramp(double t, double onset, double ramp, double hold)
        {
            double x = Math.Max(0.0, Math.Min(1.0, (t - onset) / ramp));
            return (float)(hold * (1.0 - x));
        }

        [Fact]
        public void GrabsWhenTheSqueezeRisesThroughTheThreshold()
        {
            GripReleaseDetector d = new GripReleaseDetector();
            Assert.Equal(GripEvent.None, d.Update(0.00, 0.0f));
            Assert.Equal(GripEvent.None, d.Update(0.01, 0.2f));
            Assert.Equal(GripEvent.Grabbed, d.Update(0.02, 0.8f));
            Assert.True(d.IsHeld);
            // 0.2 -> 0.8 crosses 0.5 exactly half way between the two readings.
            Assert.Equal(0.015, d.GrabTime, 6);
            Assert.Equal(GripEvent.None, d.Update(0.03, 1.0f));
            Assert.Equal(1.0f, d.PeakValue);
        }

        [Theory]
        [InlineData(90.0)]
        [InlineData(120.0)]
        [InlineData(250.0)]
        public void ReleaseTimeAndOnsetAreFoundBetweenFrames(double rate)
        {
            const double onset = 1.0037, ramp = 0.045, hold = 0.9;
            GripReleaseDetector d = new GripReleaseDetector();
            GripEvent last = GripEvent.None;
            double noticed = 0;
            for (double t = 0.5; t < 1.2 && last != GripEvent.Released; t += 1.0 / rate)
            {
                float v = t < 0.52 ? 0f : Ramp(t, onset, ramp, hold);
                last = d.Update(t, v);
                noticed = t;
            }
            Assert.Equal(GripEvent.Released, last);
            Assert.False(d.IsHeld);

            // The squeeze crosses 75% of its hardest value a quarter of the way down the ramp.
            double expected = onset + 0.25 * ramp;
            Assert.InRange(d.ReleaseTime, expected - 0.0005, expected + 0.0005);
            Assert.True(d.ReleaseNoticedTime >= d.ReleaseTime);
            Assert.Equal(noticed, d.ReleaseNoticedTime);
            // The start of the fall is recovered to within a couple of milliseconds.
            Assert.InRange(d.ReleaseOnsetTime, onset - 0.002, onset + 0.002);
            Assert.True(d.ReleaseOnsetTime <= d.ReleaseTime);
        }

        [Fact]
        public void SmallDipsDoNotRelease()
        {
            GripReleaseDetector d = new GripReleaseDetector();
            d.Update(0.00, 0f);
            Assert.Equal(GripEvent.Grabbed, d.Update(0.01, 1.0f));
            // Wobble between 0.8 and 1.0: always above 75% of the peak.
            for (int i = 0; i < 100; i++)
            {
                float v = 0.9f + 0.1f * MathF.Sin(i * 0.7f);
                Assert.Equal(GripEvent.None, d.Update(0.02 + i * 0.01, v));
            }
            Assert.True(d.IsHeld);
            Assert.Equal(0.75f, d.ReleaseThreshold, 3);
            Assert.Equal(GripEvent.Released, d.Update(1.03, 0.7f));
        }

        [Fact]
        public void TheLetGoLevelNeverGoesBelowTheFloor()
        {
            GripSettings s = new GripSettings();
            s.ReleaseFraction = 0.2f;   // would put the let-go level at 0.12 for a peak of 0.6
            s.ReleaseFloor = 0.15f;
            GripReleaseDetector d = new GripReleaseDetector(s);
            d.Update(0.00, 0f);
            d.Update(0.01, 0.6f);
            Assert.Equal(0.15f, d.ReleaseThreshold, 4);
            Assert.Equal(GripEvent.None, d.Update(0.02, 0.16f));
            Assert.Equal(GripEvent.Released, d.Update(0.03, 0.14f));
        }

        [Fact]
        public void SqueezingBackUpAfterAPartialOpenGrabsAgain()
        {
            GripReleaseDetector d = new GripReleaseDetector();
            d.Update(0.00, 0f);
            d.Update(0.01, 1.0f);
            Assert.Equal(GripEvent.Released, d.Update(0.02, 0.7f));   // let go, but the hand is still half closed
            Assert.Equal(GripEvent.None, d.Update(0.03, 0.62f));      // no flicker back to "held"
            Assert.Equal(GripEvent.None, d.Update(0.04, 0.60f));
            Assert.Equal(GripEvent.None, d.Update(0.05, 0.70f));      // rose by 0.10: not enough
            Assert.Equal(GripEvent.Grabbed, d.Update(0.06, 0.80f));   // rose by 0.20: grabbed again
            Assert.True(d.IsHeld);
        }

        [Fact]
        public void OptionalPeakDecayForgivesAHardSqueezeAtPickUp()
        {
            GripSettings s = new GripSettings();
            s.PeakDecayPerSecond = 1.0f;
            GripReleaseDetector d = new GripReleaseDetector(s);
            d.Update(0.00, 0f);
            d.Update(0.01, 1.0f);
            // Relax slowly to 0.7 over 0.6 s: slower than the decay, so it is never a release.
            GripEvent seen = GripEvent.None;
            for (int i = 1; i <= 60; i++)
            {
                GripEvent e = d.Update(0.01 + i * 0.01, 1.0f - 0.005f * i);
                if (e != GripEvent.None) seen = e;
            }
            Assert.Equal(GripEvent.None, seen);
            Assert.True(d.IsHeld);
            Assert.InRange(d.PeakValue, 0.69f, 0.75f);
        }

        [Fact]
        public void PlainButtonWorksWithFrameTimesAndExactTimes()
        {
            GripReleaseDetector d = new GripReleaseDetector();
            Assert.Equal(GripEvent.None, d.UpdateDigital(1.00, false));
            Assert.Equal(GripEvent.Grabbed, d.UpdateDigital(1.01, true));
            Assert.Equal(1.005, d.GrabTime, 6);                 // half way between the two readings
            Assert.Equal(GripEvent.None, d.UpdateDigital(1.02, true));
            Assert.Equal(GripEvent.Released, d.UpdateDigital(1.03, false));
            Assert.Equal(1.025, d.ReleaseTime, 6);
            Assert.Equal(1.025, d.ReleaseOnsetTime, 6);
            Assert.Equal(1.03, d.ReleaseNoticedTime, 6);

            // With exact event times (for example from SteamVR) the time is used as given,
            // even when it is older than the newest frame.
            d.Reset();
            d.UpdateDigital(2.00, false);
            Assert.Equal(GripEvent.Grabbed, d.UpdateDigital(2.0034, true, timeIsExact: true));
            Assert.Equal(2.0034, d.GrabTime, 6);
            d.UpdateDigital(2.02, true);
            Assert.Equal(GripEvent.Released, d.UpdateDigital(2.0171, false, timeIsExact: true));
            Assert.Equal(2.0171, d.ReleaseTime, 6);

            // With the "half way" option off, the frame time itself is reported.
            GripSettings s = new GripSettings();
            s.DigitalEdgeMidway = false;
            GripReleaseDetector plain = new GripReleaseDetector(s);
            plain.UpdateDigital(3.00, false);
            plain.UpdateDigital(3.01, true);
            Assert.Equal(3.01, plain.GrabTime, 6);
        }

        [Fact]
        public void IgnoresRubbishAndOutOfOrderReadings()
        {
            GripReleaseDetector d = new GripReleaseDetector();
            Assert.Equal(GripEvent.None, d.Update(double.NaN, 1f));
            Assert.Equal(GripEvent.None, d.Update(0.0, float.NaN));
            d.Update(0.00, 0f);
            Assert.Equal(GripEvent.Grabbed, d.Update(0.02, 5f));       // over-range values are clamped to 1
            Assert.Equal(1f, d.PeakValue);
            Assert.Equal(GripEvent.None, d.Update(0.01, 0f));           // older than the last reading: ignored
            Assert.True(d.IsHeld);
            Assert.Equal(GripEvent.None, d.Update(0.02, 1f));           // same timestamp again: harmless
            Assert.Equal(GripEvent.Released, d.Update(0.02, 0f));       // a drop to zero at the same timestamp
            Assert.False(double.IsNaN(d.ReleaseTime));
            Assert.False(double.IsNaN(d.ReleaseOnsetTime));
            Assert.Equal(0.02, d.ReleaseTime, 9);

            d.ForceGrab(1.0, 0.9f);
            Assert.True(d.IsHeld);
            Assert.Equal(1.0, d.GrabTime, 9);
            Assert.Equal(GripEvent.Released, d.Update(1.01, 0.1f));
        }

        [Fact]
        public void OnSimulatedThrowsTheTimingIsCloseToTheTruth()
        {
            // Over many noisy simulated throws: how well does the detector recover (a) the moment the
            // delayed squeeze signal began to fall and (b) the moment it crossed the let-go level?
            List<double> onsetError = new List<double>();
            List<double> eventAfterOnset = new List<double>();
            foreach (double rate in new[] { 90.0, 120.0, 250.0 })
            {
                Rng rng = new Rng(4242u + (uint)rate);
                for (int i = 0; i < 150; i++)
                {
                    Rig rig = Rig.Random(rate, ref rng);
                    SimThrow th = ThrowSim.Generate(Swing.Random((ThrowKind)(i % 4), ref rng), rig, ref rng);
                    Playback pb = Player.Play(th, new ThrowSettings[0]);
                    Assert.True(pb.Released);
                    double signalOnset = th.SqueezeOnsetTime + th.ButtonDelay;   // when the game could first see the fall
                    onsetError.Add(pb.OnsetTime - signalOnset);
                    eventAfterOnset.Add(pb.EventTime - signalOnset);
                    Assert.True(pb.OnsetTime <= pb.EventTime);
                    Assert.True(pb.EventTime <= pb.NoticedTime + 1e-9);
                }
            }
            double meanAbs = onsetError.Select(Math.Abs).Average();
            double bias = onsetError.Average();
            _out.WriteLine("onset estimate: mean error {0:F2} ms, mean absolute error {1:F2} ms", bias * 1000, meanAbs * 1000);
            _out.WriteLine("release event comes {0:F1} ms after the fall begins on average (min {1:F1}, max {2:F1})",
                eventAfterOnset.Average() * 1000, eventAfterOnset.Min() * 1000, eventAfterOnset.Max() * 1000);
            Assert.True(meanAbs < 0.004, "onset should be found within 4 ms on average, was " + meanAbs * 1000 + " ms");
            Assert.True(Math.Abs(bias) < 0.003, "onset should not lean early or late by more than 3 ms, was " + bias * 1000 + " ms");
            // The crossing happens roughly a quarter to a third of the way down a 30-60 ms ramp.
            Assert.InRange(eventAfterOnset.Average(), 0.006, 0.020);
        }
    }
}
