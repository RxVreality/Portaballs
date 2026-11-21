using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Portaballs.Core.Tests
{
    public class DelayCalibrationTests
    {
        private readonly ITestOutputHelper _out;

        public DelayCalibrationTests(ITestOutputHelper output)
        {
            _out = output;
        }

        [Theory]
        [InlineData(90.0, 0.000)]
        [InlineData(90.0, 0.035)]
        [InlineData(120.0, 0.020)]
        [InlineData(250.0, 0.040)]
        [InlineData(250.0, 0.005)]
        public void LearnsTheDelayOfAHeadsetSetup(double rate, double buttonDelay)
        {
            Rng rng = new Rng(1000u + (uint)(rate + buttonDelay * 100000));
            Rig rig = Rig.Random(rate, ref rng);
            rig.ButtonDelay = buttonDelay;
            double poseLag = rig.PoseLatency * (1.0 - rig.PredictionFraction);

            ReleaseDelayCalibrator cal = new ReleaseDelayCalibrator();
            Assert.Equal(0f, cal.Confidence);
            Assert.False(cal.IsConfident);
            Assert.Equal(0.011, cal.DelayOr(0.011));

            List<double> whatItShouldMeasure = new List<double>();
            List<double> estimates = new List<double>();
            int firstConfidentAt = -1;
            for (int i = 0; i < 30; i++)
            {
                Swing swing = Swing.Random((ThrowKind)(i % 3), ref rng);
                SimThrow th = ThrowSim.Generate(swing, rig, ref rng);
                Playback pb = Player.Play(th, new ThrowSettings[0], cal);
                Assert.True(pb.Released);
                if (pb.Observation.Outcome == CalibrationOutcome.Accepted)
                {
                    // The calibrator measures "event minus the ball's fastest moment", on the clock the poses are stamped with.
                    whatItShouldMeasure.Add(pb.EventTime - (th.BallPeakTime + poseLag));
                }
                estimates.Add(cal.DelaySeconds);
                if (firstConfidentAt < 0 && cal.IsConfident) firstConfidentAt = i + 1;
                if (cal.Count < cal.Settings.MinThrows) Assert.False(cal.IsConfident);
            }

            double truth = whatItShouldMeasure.Average();
            _out.WriteLine("rate {0} Hz, grip delay {1:F0} ms: learned {2:F1} ms, should be {3:F1} ms, spread {4:F1} ms, confidence {5:F2}, trusted after {6} throws",
                rate, buttonDelay * 1000, cal.DelaySeconds * 1000, truth * 1000, cal.SpreadSeconds * 1000, cal.Confidence, firstConfidentAt);

            Assert.True(cal.IsConfident);
            Assert.InRange(firstConfidentAt, cal.Settings.MinThrows, 12);
            Assert.InRange(cal.Count, 10, cal.Capacity);
            // Single throws scatter by several milliseconds (the player is not a machine), so the middle
            // value of a couple of dozen is good to a few milliseconds, not to a fraction of one.
            Assert.InRange(cal.DelaySeconds, truth - 0.004, truth + 0.004);
            Assert.True(cal.SpreadSeconds < 0.015);
            // Once it has settled it stays put: the last ten estimates all sit within 5 ms of the truth.
            foreach (double e in estimates.Skip(20)) Assert.InRange(e, truth - 0.005, truth + 0.005);

            // And it hands the result to the throw settings.
            ThrowSettings s = new ThrowSettings();
            Assert.True(cal.ApplyTo(s));
            Assert.Equal(cal.DelaySeconds, s.ReleaseTimeOffset);
            Assert.Equal(VelocityFit.Curve, s.Fit);
            Assert.Equal(cal.DelaySeconds, cal.DelayOr(0.011));

            cal.Reset();
            Assert.Equal(0, cal.Count);
            Assert.False(cal.IsConfident);
            Assert.Equal(0.0, cal.DelaySeconds);
            ThrowSettings untouched = new ThrowSettings();
            Assert.False(cal.ApplyTo(untouched));
            Assert.Equal(0.011, untouched.ReleaseTimeOffset);
            Assert.Equal(VelocityFit.Line, untouched.Fit);
        }

        [Fact]
        public void TakesTheMiddleValueSoFlukesDoNotMatter()
        {
            ReleaseDelayCalibrator cal = new ReleaseDelayCalibrator(capacity: 15);
            Assert.Equal(25, new ReleaseDelayCalibrator().Capacity);
            for (int i = 0; i < 10; i++) cal.AddSample(0.030 + 0.001 * (i % 3 - 1));
            cal.AddSample(0.095);
            cal.AddSample(0.090);
            cal.AddSample(-0.020);
            Assert.InRange(cal.DelaySeconds, 0.029, 0.031);
            Assert.True(cal.IsConfident);

            // The feel setting shifts the answer; the result stays inside the allowed span.
            cal.Settings.PeakToReleaseSeconds = 0.006;
            Assert.InRange(cal.DelaySeconds, 0.035, 0.037);
            cal.Settings.PeakToReleaseSeconds = 5.0;
            Assert.Equal(cal.Settings.MaxDelaySeconds, cal.DelaySeconds);
            cal.Settings.PeakToReleaseSeconds = 0.0;

            // Only the most recent throws are remembered: a new steady delay takes over.
            for (int i = 0; i < 15; i++) cal.AddSample(0.050);
            Assert.Equal(15, cal.Count);
            Assert.Equal(0.050, cal.DelaySeconds, 6);
            Assert.Equal(0.0, cal.SpreadSeconds, 6);
            Assert.Equal(1f, cal.Confidence);

            cal.AddSample(double.NaN);   // ignored
            Assert.Equal(0.050, cal.DelaySeconds, 6);
        }

        [Fact]
        public void ConfidenceNeedsEnoughThrowsThatAgree()
        {
            ReleaseDelayCalibrator cal = new ReleaseDelayCalibrator();
            for (int i = 0; i < 4; i++) cal.AddSample(0.030);
            Assert.Equal(0f, cal.Confidence);            // too few
            cal.AddSample(0.030);
            Assert.Equal(0.5f, cal.Confidence, 3);       // 5 of the 10 wanted
            Assert.True(cal.IsConfident);
            for (int i = 0; i < 5; i++) cal.AddSample(0.030);
            Assert.Equal(1f, cal.Confidence, 3);

            // Throws that disagree wildly: plenty of them, but no trust.
            ReleaseDelayCalibrator scattered = new ReleaseDelayCalibrator();
            Rng rng = new Rng(5u);
            for (int i = 0; i < 15; i++) scattered.AddSample(rng.Range(-0.03f, 0.10f));
            Assert.True(scattered.SpreadSeconds > 0.025);
            Assert.Equal(0f, scattered.Confidence);
            Assert.False(scattered.IsConfident);
        }

        // A straight-line swing whose speed peaks at 'peakTime'.
        private static PoseHistory StraightSwing(double peakTime, float topSpeed, double until, double rate = 120.0)
        {
            PoseHistory h = new PoseHistory();
            for (double t = peakTime - 0.3; t <= until + 1e-9; t += 1.0 / rate)
            {
                double x = Math.Max(-0.15, Math.Min(0.15, t - peakTime));
                double k = Math.PI / 0.3;
                double integral = 0.5 * x + Math.Sin(2 * k * x) / (4 * k);
                h.Add(t, new Vec3((float)(topSpeed * integral), 0f, 1f), Quat.Identity);
            }
            return h;
        }

        [Fact]
        public void OnlyClearThrowsAreUsed()
        {
            ReleaseDelayCalibrator cal = new ReleaseDelayCalibrator();

            // A clean throw seen a little after the release: accepted, and the delay is right.
            CalibrationObservation ok = cal.Observe(StraightSwing(10.0, 7f, 10.12), 10.035, default(Vec3));
            Assert.Equal(CalibrationOutcome.Accepted, ok.Outcome);
            Assert.InRange(ok.DelaySample, 0.033, 0.037);
            Assert.InRange(ok.PeakSpeed, 6.7f, 7.05f);
            Assert.Equal(1, cal.Count);

            // Too gentle to have a trustworthy peak.
            Assert.Equal(CalibrationOutcome.TooSlow, cal.Observe(StraightSwing(10.0, 1.5f, 10.12), 10.035, default(Vec3)).Outcome);

            // The hand is still speeding up at the newest pose: ask again later.
            Assert.Equal(CalibrationOutcome.NeedMoreData, cal.Observe(StraightSwing(10.0, 7f, 9.98), 9.98, default(Vec3)).Outcome);

            // The event is so late that the peak falls outside the allowed span.
            CalibrationObservation late = cal.Observe(StraightSwing(10.0, 7f, 10.3), 10.14, default(Vec3));
            Assert.Equal(CalibrationOutcome.OutOfRange, late.Outcome);
            Assert.Equal(1, cal.RejectedOutOfRange);

            // The fastest moment is right at the start of the search: the real peak was earlier.
            Assert.Equal(CalibrationOutcome.PeakTooEarly, cal.Observe(StraightSwing(10.0, 7f, 10.4), 10.25, default(Vec3)).Outcome);

            // Nothing to look at.
            Assert.Equal(CalibrationOutcome.NoPeak, cal.Observe(new PoseHistory(), 10.0, default(Vec3)).Outcome);
            Assert.Equal(CalibrationOutcome.NoPeak, cal.Observe(null, 10.0, default(Vec3)).Outcome);
            Assert.Equal(CalibrationOutcome.NoPeak, cal.Observe(StraightSwing(10.0, 7f, 10.12), double.NaN, default(Vec3)).Outcome);

            Assert.Equal(1, cal.Count);   // none of the rejects were stored
        }
    }

    public class AimAssistTests
    {
        private readonly ITestOutputHelper _out;

        public AimAssistTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static readonly Vec3 G = new Vec3(0f, 0f, -9.81f);
        private static readonly Vec3 Hand = new Vec3(0f, -0.2f, 1.6f);
        private static readonly Vec3 Target = new Vec3(5f, 0.5f, 1.4f);
        private static readonly Vec3 WallNormal = new Vec3(-1f, 0f, 0f);

        // The throw that would hit the target exactly at the given speed, then turned off-target by an angle.
        private static Vec3 ThrowOffBy(float speed, float degreesOff, Vec3 target, out Vec3 perfect)
        {
            float t, min;
            Assert.True(Ballistics.SolveLaunchClosestTo(Hand, target, speed, G, new Vec3(1f, 0f, 0f), out perfect, out t, out min));
            Vec3 dir = Vec3.Normalize(perfect);
            // Turn it sideways (about the "up" that is square to the throw), so the angle off is exact.
            Vec3 up = new Vec3(0f, 0f, 1f);
            Vec3 axis = Vec3.Normalize(up - dir * Vec3.Dot(up, dir));
            Quat turn = Quat.CreateFromAxisAngle(axis, Check.Deg(degreesOff));
            return Vec3.Transform(dir, turn) * speed;
        }

        private static AimAssistSettings Mode(AimAssistMode mode)
        {
            AimAssistSettings s = new AimAssistSettings();
            s.Mode = mode;
            return s;
        }

        // The head cone's strength, which several tests below work with.
        private static readonly float HeadStrength = new AimAssistSettings().Head.Strength;

        [Fact]
        public void DefaultsAreTheAgreedNumbers()
        {
            // As tuned from the tester's headset session of 2026-11-19: the cones as first guessed, the pulls a little stronger.
            AimAssistSettings s = new AimAssistSettings();
            Assert.Equal(Check.Deg(10f), s.Head.InnerAngle, 5);
            Assert.Equal(Check.Deg(25f), s.Head.OuterAngle, 5);
            Assert.Equal(0.45f, s.Head.Strength);
            Assert.Equal(Check.Deg(15f), s.Eye.InnerAngle, 5);
            Assert.Equal(Check.Deg(30f), s.Eye.OuterAngle, 5);
            Assert.Equal(0.65f, s.Eye.Strength);
            // A glance is trusted no more than the head.
            Assert.Equal(s.Head.InnerAngle, s.Glance.InnerAngle, 5);
            Assert.Equal(s.Head.OuterAngle, s.Glance.OuterAngle, 5);
            Assert.Equal(s.Head.Strength, s.Glance.Strength);
        }

        [Theory]
        [InlineData(2f)]
        [InlineData(6f)]
        [InlineData(9.5f)]
        public void InsideTheInnerAngleTheThrowIsPulledByExactlyTheStrength(float degreesOff)
        {
            Vec3 perfect;
            Vec3 thrown = ThrowOffBy(9f, degreesOff, Target, out perfect);
            AimAssistResult r = AimAssist.Apply(Mode(AimAssistMode.Head), Hand, thrown, false, new AimPoint(Target, WallNormal), AimPoint.None);

            Assert.True(r.Applied);
            Assert.Equal(AimAssistOutcome.Applied, r.Outcome);
            Assert.Equal(AimSource.Head, r.Source);
            Assert.Equal(Check.Deg(degreesOff), r.AngleOff, 3);
            Assert.Equal(HeadStrength, r.Weight, 4);
            // The strength's share of the error is removed and the rest of the angle is left.
            Assert.InRange(MathUtil.AngleBetween(r.Velocity, perfect) * 180f / MathF.PI - degreesOff * (1f - HeadStrength), -0.01f, 0.01f);
            // It moved along the shortest arc: the turn taken plus the turn left add up to the original error.
            Assert.InRange(MathUtil.AngleBetween(r.Velocity, thrown) * 180f / MathF.PI - degreesOff * HeadStrength, -0.01f, 0.01f);
            // Speed untouched.
            Assert.Equal(9f, r.Velocity.Length(), 4);
            Assert.Equal(0f, r.SpeedChange, 4);
            // Landing points are reported and the assisted one is closer.
            Assert.True(r.LandingOnSurface);
            Assert.Equal(Target.X, r.LandingWithout.X, 3);
            Assert.Equal(Target.X, r.LandingWith.X, 3);
            Assert.True(r.MissWith < r.MissWithout);
            Assert.InRange(r.MissWith / r.MissWithout, 0.45f, 0.65f);
            Check.Near(perfect, r.PerfectVelocity, 1e-4f, "perfect throw");
        }

        [Fact]
        public void ThePullFadesBetweenTheInnerAndOuterAngles()
        {
            AimAssistSettings s = Mode(AimAssistMode.Head);
            float previous = HeadStrength;
            foreach (float off in new[] { 10f, 13f, 17.5f, 21f, 24.5f })
            {
                Vec3 perfect;
                Vec3 thrown = ThrowOffBy(9f, off, Target, out perfect);
                AimAssistResult r = AimAssist.Apply(s, Hand, thrown, false, new AimPoint(Target, WallNormal), AimPoint.None);
                float expected = HeadStrength * (1f - MathUtil.SmoothStep(Check.Deg(10f), Check.Deg(25f), Check.Deg(off)));
                Assert.Equal(expected, r.Weight, 3);
                Assert.True(r.Weight <= previous + 1e-5f);
                previous = r.Weight;
                Assert.Equal(off * (1f - expected), MathUtil.AngleBetween(r.Velocity, perfect) * 180f / MathF.PI, 1);
            }
            // Half way between the two angles the pull is half the strength.
            Vec3 p;
            AimAssistResult mid = AimAssist.Apply(s, Hand, ThrowOffBy(9f, 17.5f, Target, out p), false, new AimPoint(Target, WallNormal), AimPoint.None);
            Assert.Equal(HeadStrength * 0.5f, mid.Weight, 3);
        }

        [Theory]
        [InlineData(25.5f)]
        [InlineData(40f)]
        [InlineData(90f)]
        [InlineData(170f)]
        public void OutsideTheOuterAngleTheThrowIsUntouched(float degreesOff)
        {
            Vec3 perfect;
            Vec3 thrown = ThrowOffBy(9f, degreesOff, Target, out perfect);
            AimAssistResult r = AimAssist.Apply(Mode(AimAssistMode.Head), Hand, thrown, false, new AimPoint(Target, WallNormal), AimPoint.None);
            Assert.False(r.Applied);
            Assert.Equal(AimAssistOutcome.OutsideCone, r.Outcome);
            Assert.Equal(thrown, r.Velocity);
            Assert.Equal(0f, r.Weight);
            Assert.Equal(r.MissWithout, r.MissWith);
        }

        [Fact]
        public void ATargetBehindThePlayerIsNeverAssisted()
        {
            Vec3 thrown = new Vec3(8f, 0f, 2f);                         // thrown forward
            Vec3 behind = new Vec3(-4f, 0.3f, 1.5f);                    // looking over the shoulder
            foreach (AimAssistMode mode in new[] { AimAssistMode.Head, AimAssistMode.Eye, AimAssistMode.EyeWithHeadFallback })
            {
                AimAssistResult r = AimAssist.Apply(Mode(mode), Hand, thrown, false,
                    new AimPoint(behind, new Vec3(1f, 0f, 0f)), new AimPoint(behind, new Vec3(1f, 0f, 0f)));
                Assert.False(r.Applied);
                Assert.Equal(AimAssistOutcome.OutsideCone, r.Outcome);
                Assert.Equal(thrown, r.Velocity);
                Assert.True(r.AngleOff > Check.Deg(90f));
            }
        }

        [Fact]
        public void ATargetOutOfReachIsReportedAndLeftAlone()
        {
            Vec3 far = new Vec3(30f, 0f, 1.5f);                         // needs about 17 m/s
            Vec3 thrown = Vec3.Normalize(new Vec3(1f, 0f, 0.5f)) * 6f;
            AimAssistResult r = AimAssist.Apply(Mode(AimAssistMode.Head), Hand, thrown, false, new AimPoint(far, WallNormal), AimPoint.None);
            Assert.False(r.Applied);
            Assert.Equal(AimAssistOutcome.OutOfReach, r.Outcome);
            Assert.Equal(thrown, r.Velocity);
            Assert.InRange(r.MinSpeedNeeded, 16f, 18f);
            Check.Finite(r.LandingWithout, "landing without");
        }

        [Fact]
        public void ATargetJustOutOfReachGetsASmallCappedSpeedUp()
        {
            AimAssistSettings s = Mode(AimAssistMode.Head);
            Vec3 target = new Vec3(7f, 0f, 1.6f);
            float minSpeed = Ballistics.SolveLaunch(Hand, target, 1f, G).MinSpeed;

            // 4% too slow: within the 10% allowance.
            float speed = minSpeed / 1.04f;
            Vec3 thrown = Vec3.Normalize(Ballistics.SolveLaunch(Hand, target, 1f, G).MinSpeedVelocity) * speed;
            AimAssistResult r = AimAssist.Apply(s, Hand, thrown, false, new AimPoint(target, WallNormal), AimPoint.None);
            Assert.True(r.Applied);
            Assert.True(r.SpeedChange > 0.005f);
            Assert.True(r.SpeedChange <= s.MaxSpeedChange * s.Head.Strength + 1e-4f, "the speed-up is capped and scaled by the strength");
            Assert.Equal(HeadStrength * (minSpeed * 1.002f / speed - 1f), r.SpeedChange, 3);
            Assert.True(r.MissWith < r.MissWithout);

            // 20% too slow: beyond the allowance, so nothing is done.
            Vec3 weak = Vec3.Normalize(thrown) * (minSpeed / 1.2f);
            AimAssistResult no = AimAssist.Apply(s, Hand, weak, false, new AimPoint(target, WallNormal), AimPoint.None);
            Assert.False(no.Applied);
            Assert.Equal(AimAssistOutcome.OutOfReach, no.Outcome);
            Assert.Equal(weak, no.Velocity);

            // With speed changes switched off, even 4% short is out of reach.
            s.MaxSpeedChange = 0f;
            Assert.Equal(AimAssistOutcome.OutOfReach,
                AimAssist.Apply(s, Hand, thrown, false, new AimPoint(target, WallNormal), AimPoint.None).Outcome);
        }

        [Fact]
        public void DropsAndTinyTossesAreNeverAssisted()
        {
            Vec3 perfect;
            Vec3 thrown = ThrowOffBy(9f, 5f, Target, out perfect);
            AimPoint aim = new AimPoint(Target, WallNormal);

            AimAssistResult flagged = AimAssist.Apply(Mode(AimAssistMode.Head), Hand, thrown, true, aim, AimPoint.None);
            Assert.False(flagged.Applied);
            Assert.Equal(AimAssistOutcome.Drop, flagged.Outcome);
            Assert.Equal(thrown, flagged.Velocity);

            Vec3 slow = new Vec3(1.0f, 0f, 0.5f);
            AimAssistResult tiny = AimAssist.Apply(Mode(AimAssistMode.Head), Hand, slow, false, aim, AimPoint.None);
            Assert.Equal(AimAssistOutcome.Drop, tiny.Outcome);
            Assert.Equal(slow, tiny.Velocity);

            AimAssistResult zero = AimAssist.Apply(Mode(AimAssistMode.Head), Hand, default(Vec3), false, aim, AimPoint.None);
            Assert.Equal(AimAssistOutcome.Drop, zero.Outcome);
            Check.Finite(zero.Velocity, "zero throw");
            Check.Finite(zero.LandingWithout, "zero throw landing");

            // Between the minimum and full speeds the help fades in rather than switching on.
            AimAssistSettings s = Mode(AimAssistMode.Head);
            Vec3 near = new Vec3(0.9f, -0.2f, 0.9f);
            float t, min;
            Vec3 p;
            Assert.True(Ballistics.SolveLaunchClosestTo(Hand, near, 2.25f, G, new Vec3(1f, 0f, 0f), out p, out t, out min));
            Vec3 gentle = Vec3.Transform(p, Quat.CreateFromAxisAngle(new Vec3(0f, 0f, 1f), Check.Deg(5f)));
            AimAssistResult faded = AimAssist.Apply(s, Hand, gentle, false, new AimPoint(near, default(Vec3)), AimPoint.None);
            Assert.True(faded.Applied);
            Assert.InRange(faded.Weight, HeadStrength * 0.5f - 0.005f, HeadStrength * 0.5f + 0.005f);   // 2.25 m/s is half way from 1.5 to 3.0
        }

        [Fact]
        public void ModesPickTheRightAimPointAndNumbers()
        {
            Vec3 perfect;
            Vec3 thrown = ThrowOffBy(9f, 12f, Target, out perfect);
            AimPoint head = new AimPoint(Target, WallNormal);
            AimPoint eye = new AimPoint(Target, WallNormal);

            AimAssistResult off = AimAssist.Apply(Mode(AimAssistMode.Off), Hand, thrown, false, head, eye);
            Assert.Equal(AimAssistOutcome.Off, off.Outcome);
            Assert.Equal(thrown, off.Velocity);
            Assert.Equal(AimSource.None, off.Source);

            // 12 degrees off is inside the eye's inner angle (15) but past the head's (10).
            AimAssistResult byEye = AimAssist.Apply(Mode(AimAssistMode.Eye), Hand, thrown, false, head, eye);
            Assert.Equal(AimSource.Eye, byEye.Source);
            Assert.Equal(0.65f, byEye.Weight, 4);
            Assert.False(byEye.Glance);
            AimAssistResult byHead = AimAssist.Apply(Mode(AimAssistMode.Head), Hand, thrown, false, head, eye);
            Assert.Equal(AimSource.Head, byHead.Source);
            Assert.InRange(byHead.Weight, 0.75f * HeadStrength, 0.999f * HeadStrength);
            Assert.False(byHead.Glance);

            // An eye point that is only a glance still counts as the eyes, but pulls with the gentler glance numbers.
            AimPoint glance = eye;
            glance.Glance = true;
            foreach (AimAssistMode mode in new[] { AimAssistMode.Eye, AimAssistMode.EyeWithHeadFallback })
            {
                AimAssistResult byGlance = AimAssist.Apply(Mode(mode), Hand, thrown, false, head, glance);
                Assert.Equal(AimSource.Eye, byGlance.Source);
                Assert.True(byGlance.Glance);
                Assert.True(byGlance.Applied);
                Assert.Equal(byHead.Weight, byGlance.Weight, 5);      // the glance cone starts out equal to the head's
                Assert.True(byGlance.Weight < byEye.Weight);
                Assert.True(byGlance.MissWith > byEye.MissWith && byGlance.MissWith < byGlance.MissWithout);
            }
            AimAssistSettings ownGlance = Mode(AimAssistMode.Eye);
            ownGlance.Glance = new AimCone(20f, 30f, 0.2f);
            Assert.Equal(0.2f, AimAssist.Apply(ownGlance, Hand, thrown, false, head, glance).Weight, 4);
            // The head is never a glance, whatever the head point says.
            AimPoint oddHead = head;
            oddHead.Glance = true;
            Assert.False(AimAssist.Apply(Mode(AimAssistMode.Head), Hand, thrown, false, oddHead, AimPoint.None).Glance);

            // Eye only, but the gaze is not valid: no help.
            AimAssistResult noGaze = AimAssist.Apply(Mode(AimAssistMode.Eye), Hand, thrown, false, head, AimPoint.None);
            Assert.Equal(AimAssistOutcome.NoTarget, noGaze.Outcome);
            Assert.Equal(thrown, noGaze.Velocity);

            // Eye with fallback: uses the eye when it can, the head (and the head's numbers) when it cannot.
            AimAssistResult both = AimAssist.Apply(Mode(AimAssistMode.EyeWithHeadFallback), Hand, thrown, false, head, eye);
            Assert.Equal(AimSource.Eye, both.Source);
            AimAssistResult fallback = AimAssist.Apply(Mode(AimAssistMode.EyeWithHeadFallback), Hand, thrown, false, head, AimPoint.None);
            Assert.Equal(AimSource.Head, fallback.Source);
            Assert.Equal(byHead.Weight, fallback.Weight, 5);
            AimAssistResult nothing = AimAssist.Apply(Mode(AimAssistMode.EyeWithHeadFallback), Hand, thrown, false, AimPoint.None, AimPoint.None);
            Assert.Equal(AimAssistOutcome.NoTarget, nothing.Outcome);

            // An aim point that is not a real number counts as "no aim point".
            AimPoint broken = new AimPoint(new Vec3(float.NaN, 0f, 0f), WallNormal);
            Assert.Equal(AimAssistOutcome.NoTarget, AimAssist.Apply(Mode(AimAssistMode.Head), Hand, thrown, false, broken, AimPoint.None).Outcome);

            // Strength 1 with a wide inner angle steers the throw exactly onto the point.
            AimAssistSettings full = Mode(AimAssistMode.Head);
            full.Head = new AimCone(20f, 30f, 1f);
            AimAssistResult exact = AimAssist.Apply(full, Hand, thrown, false, head, eye);
            Check.Near(Target, exact.LandingWith, 2e-3f, "full strength lands on the aim point");
            Assert.True(exact.MissWith < 2e-3f);

            // Too close or absurdly far aim points are ignored.
            AimPoint touching = new AimPoint(Hand + new Vec3(0.2f, 0f, 0f), WallNormal);
            Assert.Equal(AimAssistOutcome.BadDistance, AimAssist.Apply(Mode(AimAssistMode.Head), Hand, thrown, false, touching, AimPoint.None).Outcome);
        }

        [Fact]
        public void ALobIsHelpedAsALob()
        {
            // The player lobs high; the assist must bend toward the high throw, not flatten it.
            LaunchSolution sol = Ballistics.SolveLaunch(Hand, Target, 9f, G);
            Vec3 thrown = Vec3.Transform(sol.HighArcVelocity, Quat.CreateFromAxisAngle(new Vec3(0f, 0f, 1f), Check.Deg(4f)));
            AimAssistResult r = AimAssist.Apply(Mode(AimAssistMode.Head), Hand, thrown, false, new AimPoint(Target, WallNormal), AimPoint.None);
            Assert.True(r.Applied);
            Check.Near(sol.HighArcVelocity, r.PerfectVelocity, 1e-4f, "aims for the lob");
            Assert.True(r.MissWith < r.MissWithout);
            Assert.True(r.Velocity.Z > 0.9f * thrown.Z);
        }

        [Fact]
        public void NeverMakesAThrowWorseAndKeepsSpeedWithinLimits()
        {
            Rng rng = new Rng(77u);
            int applied = 0;
            for (int i = 0; i < 3000; i++)
            {
                AimAssistSettings s = new AimAssistSettings();
                s.Mode = (AimAssistMode)rng.RangeInt(1, 4);
                s.Head = new AimCone(rng.Range(2f, 20f), rng.Range(20f, 60f), rng.Range(0f, 1f));
                s.Eye = new AimCone(rng.Range(2f, 20f), rng.Range(20f, 60f), rng.Range(0f, 1f));
                Vec3 hand = new Vec3(rng.Range(-1f, 1f), rng.Range(-1f, 1f), rng.Range(0.8f, 2.2f));
                Vec3 target = hand + new Vec3(rng.Range(-2f, 8f), rng.Range(-4f, 4f), rng.Range(-2f, 2.5f));
                Vec3 normal = rng.NextFloat() < 0.3f ? default(Vec3) : rng.OnUnitSphere();
                if (Vec3.Dot(normal, hand - target) < 0f) normal = -normal;   // surfaces face the thrower
                Vec3 thrown = rng.OnUnitSphere() * rng.Range(0f, 14f);
                if (rng.NextFloat() < 0.6f)
                {
                    // Mostly: throws that are roughly meant for the target.
                    Vec3 v;
                    float t, min;
                    float speed = rng.Range(2f, 14f);
                    Ballistics.SolveLaunchClosestTo(hand, target, speed, G, rng.OnUnitSphere(), out v, out t, out min);
                    Vec3 dir = MathUtil.SafeNormalize(v, new Vec3(1f, 0f, 0f));
                    Quat wobble = Quat.CreateFromAxisAngle(rng.OnUnitSphere(), Check.Deg(rng.Range(0f, 35f)));
                    thrown = Vec3.Transform(dir, wobble) * speed;
                }
                AimPoint aim = new AimPoint(target, normal);
                AimAssistResult r = AimAssist.Apply(s, hand, thrown, rng.NextFloat() < 0.05f, aim, rng.NextFloat() < 0.5f ? aim : AimPoint.None);

                Check.Finite(r.Velocity, "velocity");
                Check.Finite(r.LandingWith, "landing with");
                Check.Finite(r.LandingWithout, "landing without");
                Assert.False(float.IsNaN(r.MissWith) || float.IsNaN(r.MissWithout) || float.IsNaN(r.AngleOff) || float.IsNaN(r.Weight));
                Assert.True(r.MissWith <= r.MissWithout + 1e-4f, "assist made a throw worse: " + r.MissWithout + " -> " + r.MissWith);

                float before = thrown.Length();
                float after = r.Velocity.Length();
                if (!r.Applied)
                {
                    Assert.Equal(thrown, r.Velocity);
                }
                else
                {
                    applied++;
                    Assert.True(after >= before * (1f - 1e-4f), "assist never slows a throw");
                    Assert.True(after <= before * (1f + s.MaxSpeedChange) + 1e-4f, "speed-up is capped");
                    AimCone cone = r.Source == AimSource.Eye ? s.Eye : s.Head;
                    Assert.True(r.Weight <= cone.Strength + 1e-5f);
                    Assert.True(r.AngleOff < cone.OuterAngle);
                    // Never turned by more than the error it was correcting.
                    Assert.True(MathUtil.AngleBetween(thrown, r.Velocity) <= r.AngleOff + 1e-3f);
                }
            }
            Assert.True(applied > 400, "the fuzz should exercise the assisted path, got " + applied);
        }

        [Fact]
        public void FindsWhatTheAimRayPointsAt()
        {
            // A small room: far wall at x = 5, left wall at y = 2, and a pillar face at x = 3 in front of part of the far wall.
            WallFrame far = new WallFrame(new Vec3(5f, 2f, 0f), new Vec3(0f, -1f, 0f), new Vec3(0f, 0f, 1f), 4f, 3f);
            WallFrame left = new WallFrame(new Vec3(0f, 2f, 0f), new Vec3(1f, 0f, 0f), new Vec3(0f, 0f, 1f), 5f, 3f);
            WallFrame pillar = new WallFrame(new Vec3(3f, -1f, 0f), new Vec3(0f, -1f, 0f), new Vec3(0f, 0f, 1f), 0.5f, 3f);
            WallFrame[] walls = { far, left, pillar, default(WallFrame) };
            Vec3 eye = new Vec3(0.5f, 0f, 1.7f);

            AimPoint p;
            int index;
            float distance;
            Assert.True(AimAssist.FindAimPoint(eye, new Vec3(1f, 0f, 0f), walls, 3, 50f, out p, out index, out distance));
            Assert.Equal(0, index);
            Assert.Equal(4.5f, distance, 4);
            Check.Near(new Vec3(5f, 0f, 1.7f), p.Point, 1e-4f, "far wall hit");
            Check.Near(new Vec3(-1f, 0f, 0f), p.Normal, 1e-5f, "far wall normal");
            Assert.True(p.Valid);

            // Looking at the pillar: it is nearer than the far wall behind it.
            Vec3 toPillar = new Vec3(3f, -1.25f, 1.5f) - eye;
            Assert.True(AimAssist.FindAimPoint(eye, toPillar, walls, 3, 50f, out p, out index, out distance));
            Assert.Equal(2, index);
            Assert.Equal(3f, p.Point.X, 4);

            // Looking left.
            Assert.True(AimAssist.FindAimPoint(eye, new Vec3(0.2f, 1f, 0f), walls, 3, 50f, out p, out index, out distance));
            Assert.Equal(1, index);

            // Looking away from every wall, at the ceiling, or from behind a wall: nothing.
            Assert.False(AimAssist.FindAimPoint(eye, new Vec3(-1f, 0f, 0f), walls, 3, 50f, out p, out index, out distance));
            Assert.Equal(-1, index);
            Assert.False(p.Valid);
            Assert.False(AimAssist.FindAimPoint(eye, new Vec3(0f, 0f, 1f), walls, 3, 50f, out p, out index, out distance));
            Assert.False(AimAssist.FindAimPoint(new Vec3(6f, 0f, 1.7f), new Vec3(-1f, 0f, 0f), walls, 1, 50f, out p, out index, out distance));
            // Above the top edge of the far wall.
            Assert.False(AimAssist.FindAimPoint(eye, new Vec3(1f, 0f, 0.5f), walls, 1, 50f, out p, out index, out distance));
            // Beyond the distance limit.
            Assert.False(AimAssist.FindAimPoint(eye, new Vec3(1f, 0f, 0f), walls, 3, 2f, out p, out index, out distance));
            // Rubbish.
            Assert.False(AimAssist.FindAimPoint(eye, default(Vec3), walls, 3, 50f, out p, out index, out distance));
            Assert.False(AimAssist.FindAimPoint(eye, new Vec3(1f, 0f, 0f), null, 3, 50f, out p, out index, out distance));
            Assert.False(AimAssist.FindAimPoint(eye, new Vec3(1f, 0f, 0f), walls, 0, 50f, out p, out index, out distance));
            Assert.True(AimAssist.FindAimPoint(eye, new Vec3(1f, 0f, 0f), walls, 99, 50f, out p, out index, out distance));
        }

        [Fact]
        public void OnSimulatedThrowsTheAssistTightensTheGroup()
        {
            // The whole chain on simulated throws: the calibrated pipeline measures the throw, and the player
            // is looking at the spot their true throw would hit (give or take 10 cm of gaze error).
            // The assist should pull the measured throws closer to that spot.
            List<double> without = new List<double>(), withHead = new List<double>(), withEye = new List<double>();
            foreach (double rate in new[] { 90.0, 120.0, 250.0 })
            {
                for (int session = 0; session < 4; session++)
                {
                    Rng rng = new Rng(31337u + (uint)rate * 13u + (uint)session);
                    Rig rig = Rig.Random(rate, ref rng);
                    ReleaseDelayCalibrator cal = new ReleaseDelayCalibrator();
                    for (int i = 0; i < 52; i++)
                    {
                        ThrowKind kind = (ThrowKind)(i % 3);   // real throws only; a gentle toss gets little help by design
                        SimThrow th = ThrowSim.Generate(Swing.Random(kind, ref rng), rig, ref rng);
                        ThrowSettings s = new ThrowSettings();
                        cal.ApplyTo(s);
                        Playback pb = Player.Play(th, new[] { s }, cal);
                        if (i < 12 || !pb.Released) continue;
                        ThrowResult r = pb.Results[0];

                        // Where the true throw hits the wall (skip throws that land on the floor first).
                        Vec3 wallPoint = new Vec3(th.WallX, 0f, 0f);
                        float t;
                        Vec3 truth;
                        if (!Ballistics.TryHitPlane(th.TrueReleasePosition, th.TrueReleaseVelocity, G, wallPoint, WallNormal, out t, out truth)) continue;
                        if (truth.Z < 0.1f) continue;
                        Vec3 looked = truth + new Vec3(0f, rng.Gaussian(), rng.Gaussian()) * 0.10f;
                        AimPoint aim = new AimPoint(looked, WallNormal);

                        AimAssistResult head = AimAssist.Apply(Mode(AimAssistMode.Head), r.ReleasePosition, r.ReleaseVelocity, r.IsDrop, aim, AimPoint.None);
                        AimAssistResult eye = AimAssist.Apply(Mode(AimAssistMode.Eye), r.ReleasePosition, r.ReleaseVelocity, r.IsDrop, aim, aim);
                        Vec3 hit;
                        if (!Ballistics.TryHitPlane(r.ReleasePosition, r.ReleaseVelocity, G, wallPoint, WallNormal, out t, out hit)) continue;
                        without.Add((hit - truth).Length());
                        Ballistics.TryHitPlane(r.ReleasePosition, head.Velocity, G, wallPoint, WallNormal, out t, out hit);
                        withHead.Add((hit - truth).Length());
                        Ballistics.TryHitPlane(r.ReleasePosition, eye.Velocity, G, wallPoint, WallNormal, out t, out hit);
                        withEye.Add((hit - truth).Length());
                    }
                }
            }
            double m0 = ThrowAccuracyTests.Stats.Percentile(without, 0.5), p0 = ThrowAccuracyTests.Stats.Percentile(without, 0.9);
            double m1 = ThrowAccuracyTests.Stats.Percentile(withHead, 0.5), p1 = ThrowAccuracyTests.Stats.Percentile(withHead, 0.9);
            double m2 = ThrowAccuracyTests.Stats.Percentile(withEye, 0.5), p2 = ThrowAccuracyTests.Stats.Percentile(withEye, 0.9);
            _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "landing error vs the true throw, {0} wall hits: no assist {1:F3} m (p90 {2:F3}); head defaults {3:F3} m (p90 {4:F3}); eye defaults {5:F3} m (p90 {6:F3})",
                without.Count, m0, p0, m1, p1, m2, p2));
            Assert.True(without.Count > 300);
            Assert.True(m1 < 0.85 * m0, "head assist should tighten the middle of the group");
            Assert.True(m2 < 0.75 * m0, "eye assist should tighten it more (it is stronger)");
            Assert.True(p1 < p0 && p2 < p0, "and the bad cases too");
        }
    }

    public class GazeFixationTests
    {
        private static readonly Vec3 Head = new Vec3(0f, 0f, 1.7f);

        private static Vec3 Toward(float yawDegrees, float pitchDegrees)
        {
            float yaw = Check.Deg(yawDegrees), pitch = Check.Deg(pitchDegrees);
            return new Vec3(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch));
        }

        // Feed gaze readings at 120 Hz from 'from' to 'to' using a function of time that returns (direction, valid).
        private static void Feed(GazeFixationFilter f, double from, double to, Func<double, Vec3> direction, Func<double, bool> valid, Rng rng, float jitterDegrees = 0.3f)
        {
            for (double t = from; t <= to + 1e-9; t += 1.0 / 120.0)
            {
                Vec3 d = direction(t);
                Vec3 noise = new Vec3(rng.Gaussian(), rng.Gaussian(), rng.Gaussian()) * Check.Deg(jitterDegrees);
                f.Add(t, Head, Vec3.Normalize(d + noise), valid == null || valid(t));
            }
        }

        [Fact]
        public void ASteadyLookIsFound()
        {
            GazeFixationFilter f = new GazeFixationFilter();
            Feed(f, 9.5, 10.0, t => Toward(10f, -5f), null, new Rng(1u));
            GazeFixation fix;
            Assert.True(f.TryGetFixation(10.0, new GazeFixationSettings(), out fix));
            Assert.True(fix.Found);
            Assert.True(MathUtil.AngleBetween(fix.Direction, Toward(10f, -5f)) < Check.Deg(0.3f));
            Check.Near(Head, fix.Origin, 1e-4f, "origin");
            Assert.InRange(fix.Duration, 0.28f, 0.301f);      // limited by the look-back span
            Assert.InRange(fix.AgeAtRelease, 0f, 0.009f);
            Assert.True(fix.Spread < Check.Deg(2f));
            Assert.True(fix.SampleCount >= 30);
            Assert.Equal(1f, fix.Direction.Length(), 4);
        }

        [Fact]
        public void AGlanceAwayJustBeforeReleaseIsIgnored()
        {
            // Looking at the target, then the eyes dart 25 degrees away for the last 60 ms.
            GazeFixationFilter f = new GazeFixationFilter();
            Feed(f, 9.5, 10.0, t => t < 9.94 ? Toward(10f, -5f) : Toward(-15f, -20f), null, new Rng(2u));
            GazeFixation fix;
            Assert.True(f.TryGetFixation(10.0, new GazeFixationSettings(), out fix));
            Assert.True(MathUtil.AngleBetween(fix.Direction, Toward(10f, -5f)) < Check.Deg(0.5f), "should report the look at the target");
            Assert.InRange(fix.AgeAtRelease, 0.055f, 0.075f);
            Assert.InRange(fix.EndTime, 9.925, 9.941);
            Assert.True(fix.Duration >= 0.1f);

            // If the glance lasts long enough to be a look of its own (150 ms), it is the newest one and wins.
            GazeFixationFilter g = new GazeFixationFilter();
            Feed(g, 9.5, 10.0, t => t < 9.85 ? Toward(10f, -5f) : Toward(-15f, -20f), null, new Rng(3u));
            Assert.True(g.TryGetFixation(10.0, new GazeFixationSettings(), out fix));
            Assert.True(MathUtil.AngleBetween(fix.Direction, Toward(-15f, -20f)) < Check.Deg(0.5f));
        }

        [Fact]
        public void ABlinkAtReleaseIsIgnored()
        {
            // Eyes closed for the last 90 ms (gaze not valid, and pointing somewhere silly meanwhile).
            GazeFixationFilter f = new GazeFixationFilter();
            Feed(f, 9.5, 10.0, t => t < 9.91 ? Toward(0f, 5f) : Toward(40f, -60f), t => t < 9.91, new Rng(4u));
            GazeFixation fix;
            Assert.True(f.TryGetFixation(10.0, new GazeFixationSettings(), out fix));
            Assert.True(MathUtil.AngleBetween(fix.Direction, Toward(0f, 5f)) < Check.Deg(0.5f));
            Assert.InRange(fix.AgeAtRelease, 0.085f, 0.105f);

            // A blink in the middle of a look: the stretch after it (120 ms) is long enough to count on its own.
            GazeFixationFilter g = new GazeFixationFilter();
            Feed(g, 9.5, 10.0, t => Toward(0f, 5f), t => t < 9.75 || t > 9.88, new Rng(5u));
            Assert.True(g.TryGetFixation(10.0, new GazeFixationSettings(), out fix));
            Assert.InRange(fix.StartTime, 9.87, 9.90);
            Assert.InRange(fix.AgeAtRelease, 0f, 0.009f);
        }

        [Fact]
        public void NoGazeMeansNoFixation()
        {
            GazeFixation fix;
            GazeFixationSettings s = new GazeFixationSettings();

            // Never fed anything.
            GazeFixationFilter empty = new GazeFixationFilter();
            Assert.False(empty.TryGetFixation(10.0, s, out fix));
            Assert.False(fix.Found);

            // Eye tracking switched off: every reading is "not valid".
            GazeFixationFilter off = new GazeFixationFilter();
            Feed(off, 9.5, 10.0, t => Toward(0f, 0f), t => false, new Rng(6u));
            Assert.False(off.TryGetFixation(10.0, s, out fix));

            // Eyes jumping to a new spot every 50 ms: nothing is held for the 70 ms needed.
            GazeFixationFilter restless = new GazeFixationFilter();
            Feed(restless, 9.5, 10.0, t => Toward(15f * (float)Math.Floor(t * 20.0 % 5.0), 0f), null, new Rng(7u));
            Assert.False(restless.TryGetFixation(10.0, s, out fix));

            // A sweeping look (120 degrees per second) is not a fixation either.
            GazeFixationFilter sweeping = new GazeFixationFilter();
            Feed(sweeping, 9.5, 10.0, t => Toward((float)((t - 9.5) * 120.0), 0f), null, new Rng(8u));
            Assert.False(sweeping.TryGetFixation(10.0, s, out fix));

            // A good look that ended more than 300 ms before the release is too old to trust.
            GazeFixationFilter old = new GazeFixationFilter();
            Feed(old, 9.3, 9.6, t => Toward(0f, 0f), null, new Rng(9u));
            Feed(old, 9.6, 10.0, t => Toward(0f, 0f), t => false, new Rng(10u));
            Assert.False(old.TryGetFixation(10.0, s, out fix));

            // Rubbish readings count as "not valid" and release times that are not numbers are refused.
            GazeFixationFilter rubbish = new GazeFixationFilter();
            for (int i = 0; i < 60; i++)
            {
                rubbish.Add(9.5 + i / 120.0, Head, i % 2 == 0 ? new Vec3(float.NaN, 0f, 0f) : default(Vec3), true);
            }
            rubbish.Add(double.NaN, Head, Toward(0f, 0f), true);
            Assert.False(rubbish.TryGetFixation(10.0, s, out fix));
            Assert.False(rubbish.TryGetFixation(double.NaN, s, out fix));
            Check.Finite(fix.Direction, "direction");
        }

        [Fact]
        public void ReadingsAfterTheReleaseAndStaleTimestampsAreNotUsed()
        {
            GazeFixationFilter f = new GazeFixationFilter(capacity: 64);
            Rng rng = new Rng(11u);
            Feed(f, 9.6, 10.0, t => Toward(5f, 0f), null, rng);
            int before = f.Count;
            f.Add(9.9, Head, Toward(80f, 0f), true);    // older than the newest reading: ignored
            f.Add(10.0 - 1e-12, Head, Toward(80f, 0f), true);
            Assert.Equal(before, f.Count);
            Feed(f, 10.01, 10.2, t => Toward(-30f, 10f), null, rng);   // where the eyes went after the throw
            Assert.True(f.Count <= 64);

            GazeFixation fix;
            Assert.True(f.TryGetFixation(10.0, new GazeFixationSettings(), out fix));
            Assert.True(MathUtil.AngleBetween(fix.Direction, Toward(5f, 0f)) < Check.Deg(0.5f));
            Assert.True(fix.EndTime <= 10.0);

            f.Clear();
            Assert.Equal(0, f.Count);
            Assert.False(f.TryGetFixation(10.0, null, out fix));
        }
    }
}
