using System;
using System.Collections.Generic;
using Xunit;

namespace Portaballs.Core.Tests
{
    public class BallisticsTests
    {
        private static readonly Vec3 G = new Vec3(0f, 0f, -9.81f);

        [Fact]
        public void PositionAndVelocityFollowTheSchoolFormula()
        {
            Vec3 p0 = new Vec3(1f, 2f, 3f);
            Vec3 v0 = new Vec3(4f, -1f, 2f);
            Check.Near(new Vec3(1f + 4f * 0.5f, 2f - 0.5f, 3f + 1f - 0.5f * 9.81f * 0.25f), Ballistics.PositionAt(p0, v0, G, 0.5f), 1e-5f, "position");
            Check.Near(new Vec3(4f, -1f, 2f - 9.81f * 0.5f), Ballistics.VelocityAt(v0, G, 0.5f), 1e-5f, "velocity");
        }

        [Fact]
        public void FindsWhereThePathMeetsASurface()
        {
            // Thrown level at 5 m/s from 1.5 m up, at a wall 4 m away: 0.8 s of flight.
            Vec3 p0 = new Vec3(0f, 0f, 1.5f);
            Vec3 v0 = new Vec3(5f, 0f, 3f);
            float t;
            Vec3 hit;
            Assert.True(Ballistics.TryHitPlane(p0, v0, G, new Vec3(4f, 0f, 0f), new Vec3(-1f, 0f, 0f), out t, out hit));
            Assert.Equal(0.8f, t, 4);
            Check.Near(new Vec3(4f, 0f, 1.5f + 3f * 0.8f - 0.5f * 9.81f * 0.64f), hit, 1e-4f, "wall hit");

            // The floor: the falling crossing, not the (negative-time) rising one.
            Assert.True(Ballistics.TryHitPlane(p0, v0, G, default(Vec3), new Vec3(0f, 0f, 1f), out t, out hit));
            Assert.True(t > 0.6f);
            Assert.Equal(0f, hit.Z, 3);

            // Thrown away from the wall: never gets there.
            Assert.False(Ballistics.TryHitPlane(p0, new Vec3(-5f, 0f, 3f), G, new Vec3(4f, 0f, 0f), new Vec3(-1f, 0f, 0f), out t, out hit));
            // A ceiling too high to reach.
            Assert.False(Ballistics.TryHitPlane(p0, v0, G, new Vec3(0f, 0f, 10f), new Vec3(0f, 0f, -1f), out t, out hit));
            // Rubbish in, "no" out.
            Assert.False(Ballistics.TryHitPlane(p0, v0, G, default(Vec3), default(Vec3), out t, out hit));
            Assert.False(Ballistics.TryHitPlane(p0, new Vec3(float.NaN, 0f, 0f), G, default(Vec3), new Vec3(1f, 0f, 0f), out t, out hit));
        }

        [Fact]
        public void WallHitsReportWallCoordinates()
        {
            // The far wall from the Common tests: across the room at x = 5, 4 m wide, 3 m tall.
            WallFrame wall = new WallFrame(new Vec3(5f, 2f, 0f), new Vec3(0f, -1f, 0f), new Vec3(0f, 0f, 1f), 4f, 3f);
            float t;
            Vec3 hit;
            Vec2 uv;
            bool inside;
            Assert.True(Ballistics.TryHitWall(new Vec3(0f, 0f, 1.5f), new Vec3(10f, 0f, 2f), G, wall, out t, out hit, out uv, out inside));
            Assert.Equal(0.5f, t, 4);
            Assert.True(inside);
            Assert.Equal(2f, uv.X, 3);                                    // straight ahead is the middle of the wall
            Assert.Equal(1.5f + 1f - 0.5f * 9.81f * 0.25f, uv.Y, 3);

            // Way off to the side: reaches the wall's plane, but beyond its edge.
            Assert.True(Ballistics.TryHitWall(new Vec3(0f, 0f, 1.5f), new Vec3(10f, 12f, 2f), G, wall, out t, out hit, out uv, out inside));
            Assert.False(inside);
        }

        [Fact]
        public void LaunchSolutionsLandOnTheTarget()
        {
            Rng rng = new Rng(99u);
            int reachable = 0;
            for (int i = 0; i < 500; i++)
            {
                Vec3 from = new Vec3(rng.Range(-1f, 1f), rng.Range(-1f, 1f), rng.Range(0.5f, 2f));
                Vec3 target = from + new Vec3(rng.Range(1f, 8f), rng.Range(-4f, 4f), rng.Range(-2f, 3f));
                float speed = rng.Range(3f, 18f);
                LaunchSolution s = Ballistics.SolveLaunch(from, target, speed, G);
                Assert.True(s.MinSpeed > 0f);
                Check.Finite(s.MinSpeedVelocity, "min-speed velocity");
                Assert.Equal(s.MinSpeed, s.MinSpeedVelocity.Length(), 2);
                Assert.Equal(speed >= s.MinSpeed, s.Reachable);
                if (!s.Reachable) continue;
                reachable++;

                Assert.Equal(speed, s.LowArcVelocity.Length(), 2);
                Assert.Equal(speed, s.HighArcVelocity.Length(), 2);
                Assert.True(s.LowArcTime <= s.HighArcTime + 1e-5f);
                Check.Near(target, Ballistics.PositionAt(from, s.LowArcVelocity, G, s.LowArcTime), 2e-3f, "low arc lands on target");
                Check.Near(target, Ballistics.PositionAt(from, s.HighArcVelocity, G, s.HighArcTime), 5e-3f, "high arc lands on target");
                Assert.True(s.LowArcVelocity.Z <= s.HighArcVelocity.Z + 1e-4f, "the lob goes higher");

                // The slowest possible throw really is the edge of what can reach.
                Assert.True(Ballistics.SolveLaunch(from, target, s.MinSpeed * 1.01f, G).Reachable);
                Assert.False(Ballistics.SolveLaunch(from, target, s.MinSpeed * 0.99f, G).Reachable);

                // Asking for the throw closest to a direction returns one of the two.
                Vec3 v;
                float time, minSpeed;
                Assert.True(Ballistics.SolveLaunchClosestTo(from, target, speed, G, s.HighArcVelocity, out v, out time, out minSpeed));
                Check.Near(s.HighArcVelocity, v, 1e-5f, "picked the lob");
                Assert.True(Ballistics.SolveLaunchClosestTo(from, target, speed, G, s.LowArcVelocity, out v, out time, out minSpeed));
                Check.Near(s.LowArcVelocity, v, 1e-5f, "picked the flat throw");
                Assert.Equal(s.MinSpeed, minSpeed);
            }
            Assert.True(reachable > 200);
        }

        [Fact]
        public void MinimumSpeedMatchesTheTextbookAndOutOfReachIsReported()
        {
            // On level ground the farthest a throw of speed s reaches is s^2 / g, so 10 m needs sqrt(98.1) m/s.
            Vec3 from = new Vec3(0f, 0f, 1f);
            Vec3 target = new Vec3(10f, 0f, 1f);
            LaunchSolution s = Ballistics.SolveLaunch(from, target, 5f, G);
            Assert.False(s.Reachable);
            Assert.Equal(MathF.Sqrt(98.1f), s.MinSpeed, 3);
            // That slowest throw leaves at 45 degrees.
            Assert.Equal(s.MinSpeedVelocity.X, s.MinSpeedVelocity.Z, 3);

            Vec3 v;
            float time, minSpeed;
            Assert.False(Ballistics.SolveLaunchClosestTo(from, target, 5f, G, new Vec3(1f, 0f, 0f), out v, out time, out minSpeed));
            Assert.Equal(s.MinSpeed, minSpeed);
            Check.Near(s.MinSpeedVelocity, v, 1e-5f, "falls back to the slowest throw");
        }

        [Fact]
        public void DegenerateLaunchQuestionsGiveSaneAnswers()
        {
            Vec3 p = new Vec3(1f, 1f, 1f);
            LaunchSolution same = Ballistics.SolveLaunch(p, p, 5f, G);
            Assert.False(same.Reachable);
            Check.Finite(same.LowArcVelocity, "same point");
            Check.Finite(same.MinSpeedVelocity, "same point min");

            LaunchSolution zeroSpeed = Ballistics.SolveLaunch(p, p + new Vec3(3f, 0f, 0f), 0f, G);
            Assert.False(zeroSpeed.Reachable);
            LaunchSolution nanSpeed = Ballistics.SolveLaunch(p, p + new Vec3(3f, 0f, 0f), float.NaN, G);
            Assert.False(nanSpeed.Reachable);

            // No gravity: a straight line at the asked speed.
            LaunchSolution straight = Ballistics.SolveLaunch(p, p + new Vec3(3f, 4f, 0f), 10f, default(Vec3));
            Assert.True(straight.Reachable);
            Check.Near(new Vec3(6f, 8f, 0f), straight.LowArcVelocity, 1e-4f, "straight line");
            Assert.Equal(0.5f, straight.LowArcTime, 4);

            // Straight down: dropping is enough, any speed reaches.
            LaunchSolution down = Ballistics.SolveLaunch(p, p + new Vec3(0f, 0f, -1f), 2f, G);
            Assert.True(down.Reachable);
            Assert.Equal(0f, down.MinSpeed, 3);

            float t, dist;
            Vec3 near = Ballistics.ClosestApproach(p, new Vec3(5f, 0f, 0f), G, p + new Vec3(2.5f, 0f, 0f), 3f, out t, out dist);
            Check.Finite(near, "closest approach");
            Assert.InRange(t, 0.3f, 0.5f);   // a little before it passes underneath, because it is falling
            Assert.True(dist < 1.3f);
            near = Ballistics.ClosestApproach(p, default(Vec3), default(Vec3), p, 0f, out t, out dist);
            Assert.Equal(0f, dist);
        }
    }

    public class ThrowEstimatorTests
    {
        private static readonly Vec3 G = new Vec3(0f, 0f, -9.81f);

        // Fill a history from formulas for position and rotation.
        private static PoseHistory Record(Func<double, Vec3> position, Func<double, Quat> rotation, double from, double to, double rate)
        {
            PoseHistory h = new PoseHistory();
            int n = (int)Math.Floor((to - from) * rate + 1e-9);
            for (int i = 0; i <= n; i++)
            {
                double t = from + i / rate;
                h.Add(t, position(t), rotation(t));
            }
            return h;
        }

        private static void AssertAllFinite(ThrowResult r)
        {
            Check.Finite(r.ReleasePosition, "release position");
            Check.Finite(r.ReleaseVelocity, "release velocity");
            Check.Finite(r.AngularVelocity, "angular velocity");
            Check.Finite(r.NowPosition, "now position");
            Check.Finite(r.NowVelocity, "now velocity");
            Check.Finite(r.Diagnostics.FitVelocity, "fit velocity");
            Check.Finite(r.Diagnostics.PeakVelocity, "peak velocity");
            Check.Finite(r.Diagnostics.HandVelocity, "hand velocity");
            Check.Finite(r.Diagnostics.LeverVelocity, "lever velocity");
            Assert.False(double.IsNaN(r.ReleaseTime));
            Assert.False(double.IsNaN(r.Diagnostics.PeakTime));
            Assert.False(float.IsNaN(r.Diagnostics.FitResidual));
            Assert.False(float.IsNaN(r.Diagnostics.AngleBetweenMethods));
            Assert.False(float.IsNaN(r.Diagnostics.Throwness));
            Assert.False(float.IsNaN(r.Diagnostics.FitSpeed));
            Assert.False(float.IsNaN(r.Diagnostics.PeakSpeed));
        }

        [Theory]
        [InlineData(90.0, VelocityFit.Line)]
        [InlineData(120.0, VelocityFit.Line)]
        [InlineData(250.0, VelocityFit.Curve)]
        [InlineData(90.0, VelocityFit.Curve)]
        public void SteadyMotionGivesTheExactVelocity(double rate, VelocityFit fit)
        {
            Vec3 v = new Vec3(6f, -1.5f, 2.5f);
            PoseHistory h = Record(t => new Vec3(0f, 0f, 1f) + v * (float)(t - 10.0), t => Quat.Identity, 10.0, 10.4, rate);
            ThrowSettings s = new ThrowSettings();
            s.Fit = fit;
            ThrowResult r = ThrowEstimator.Estimate(h, 10.35, 10.36, s);

            Assert.True(r.Valid);
            Assert.False(r.IsDrop);
            Check.Near(v, r.ReleaseVelocity, 5e-3f, "velocity");
            Assert.Equal(10.35 - 0.011, r.ReleaseTime, 9);
            Check.Near(new Vec3(0f, 0f, 1f) + v * (0.35f - 0.011f), r.ReleasePosition, 1e-3f, "release position");
            Check.Near(default(Vec3), r.AngularVelocity, 1e-3f, "no turning");
            Assert.Equal(fit, r.Diagnostics.FitUsed);
            Assert.True(r.Diagnostics.SamplesUsed >= 5);
            Assert.InRange(r.Diagnostics.WindowCovered, 0.044f, 0.0601f);
            Assert.True(r.Diagnostics.FitResidual < 1e-4f);
            Assert.Equal(ThrowMethod.FitDirectionPeakSpeed, r.Diagnostics.Method);
            Assert.True(r.Diagnostics.AngleBetweenMethods < 0.01f);
            Assert.Equal(1f, r.Diagnostics.Throwness);
            AssertAllFinite(r);
        }

        [Fact]
        public void LineFitAnswersForMidWindowAndCurveFitForTheReleaseMoment()
        {
            // Speeding up steadily at 40 m/s^2 along +X: velocity at time t (after 10 s) is 40 * (t - 10).
            const float accel = 40f;
            PoseHistory h = Record(t => new Vec3(0.5f * accel * (float)((t - 10.0) * (t - 10.0)), 0f, 1f), t => Quat.Identity, 10.0, 10.4, 250.0);
            ThrowSettings s = new ThrowSettings();
            s.ReleaseTimeOffset = 0.0;
            s.UsePeakSpeed = false;

            s.Fit = VelocityFit.Line;
            ThrowResult line = ThrowEstimator.Estimate(h, 10.3, 10.3, s);
            Assert.Equal(accel * (0.3f - 0.03f), line.ReleaseVelocity.X, 1);   // half a 60 ms window earlier
            Assert.True(line.Diagnostics.FitResidual > 1e-3f);                  // a line does not sit on a bend

            s.Fit = VelocityFit.Curve;
            ThrowResult curve = ThrowEstimator.Estimate(h, 10.3, 10.3, s);
            Assert.Equal(accel * 0.3f, curve.ReleaseVelocity.X, 1);             // at the release moment itself
            Assert.True(curve.Diagnostics.FitResidual < 1e-4f);

            // Letting the window reach past the release does not change the answer for the Curve fit.
            s.FitWindowAfterSeconds = 0.02;
            ThrowResult both = ThrowEstimator.Estimate(h, 10.3, 10.34, s);
            Assert.Equal(accel * 0.3f, both.ReleaseVelocity.X, 1);
            Assert.InRange(both.Diagnostics.WindowCovered, 0.05f, 0.0601f);
        }

        [Theory]
        [InlineData(VelocityFit.Line)]
        [InlineData(VelocityFit.Curve)]
        public void ABallHeldOutFromTheWristPicksUpSpeedFromRotationAlone(VelocityFit fit)
        {
            // The controller's origin stays put while the hand turns at 12 rad/s about "up".
            // A ball 10 cm out along the controller's X axis then moves at 1.2 m/s, sideways to that axis.
            const float spin = 12f;
            PoseHistory h = Record(t => new Vec3(0f, 0f, 1.2f), t => Check.AboutZ(spin * (float)(t - 10.0)), 10.0, 10.3, 250.0);
            ThrowSettings s = new ThrowSettings();
            s.Fit = fit;
            s.ReleaseTimeOffset = 0.0;
            s.BallLocalOffset = new Vec3(0.10f, 0f, 0f);
            s.DropSpeed = 0.2f;
            s.FullThrowSpeed = 0.5f;
            ThrowResult r = ThrowEstimator.Estimate(h, 10.25, 10.25, s);

            float angle = spin * 0.25f;
            Vec3 arm = new Vec3(MathF.Cos(angle), MathF.Sin(angle), 0f);
            Vec3 expected = new Vec3(-MathF.Sin(angle), MathF.Cos(angle), 0f) * (spin * 0.10f);
            Check.Near(new Vec3(0f, 0f, spin), r.AngularVelocity, 0.05f, "rate of turning");
            Check.Near(new Vec3(0f, 0f, 1.2f) + arm * 0.10f, r.ReleasePosition, 1e-3f, "ball position");
            Check.Near(expected, r.ReleaseVelocity, 0.03f, "ball velocity = turning x offset");
            Check.Near(default(Vec3), r.Diagnostics.HandVelocity, 1e-3f, "the origin is not moving");
            Check.Near(expected, r.Diagnostics.LeverVelocity, 0.03f, "all of it comes from the lever");

            // Without the offset there is nothing to throw.
            s.BallLocalOffset = default(Vec3);
            ThrowResult bare = ThrowEstimator.Estimate(h, 10.25, 10.25, s);
            Assert.True(bare.IsDrop);
            Assert.True(bare.ReleaseVelocity.Length() < 1e-2f);
        }

        [Fact]
        public void TheBallIsMovedAlongItsPathToWhereItWouldBeNow()
        {
            Vec3 v = new Vec3(8f, 0f, 2f);
            PoseHistory h = Record(t => new Vec3(0f, 0f, 1.5f) + v * (float)(t - 10.0), t => Quat.Identity, 10.0, 10.4, 120.0);
            ThrowSettings s = new ThrowSettings();
            s.ReleaseTimeOffset = 0.030;

            ThrowResult r = ThrowEstimator.Estimate(h, 10.35, 10.37, s);
            float flown = 0.05f;   // 30 ms offset + 20 ms between the event and now
            Assert.Equal(10.32, r.ReleaseTime, 9);
            Assert.Equal(flown, r.Diagnostics.RollbackSeconds, 5);
            Assert.False(r.Diagnostics.RollbackClamped);
            Check.Near(Ballistics.PositionAt(r.ReleasePosition, r.ReleaseVelocity, G, flown), r.NowPosition, 1e-5f, "now position");
            Check.Near(r.ReleaseVelocity + G * flown, r.NowVelocity, 1e-5f, "now velocity");
            // It has dropped a little under gravity compared with flying straight.
            Assert.True(r.NowPosition.Z < r.ReleasePosition.Z + v.Z * flown - 0.01f);

            // "Now" before the release never moves the ball backwards.
            ThrowResult early = ThrowEstimator.Estimate(h, 10.35, 10.0, s);
            Assert.Equal(0f, early.Diagnostics.RollbackSeconds);
            Check.Near(early.ReleasePosition, early.NowPosition, 1e-6f, "no rollback");

            // A long stall is capped.
            ThrowResult late = ThrowEstimator.Estimate(h, 10.35, 12.0, s);
            Assert.True(late.Diagnostics.RollbackClamped);
            Assert.Equal((float)s.MaxRollbackSeconds, late.Diagnostics.RollbackSeconds, 5);
        }

        [Fact]
        public void TheReleaseOffsetMovesTheReleaseMomentAndIsBounded()
        {
            Vec3 v = new Vec3(5f, 0f, 0f);
            PoseHistory h = Record(t => v * (float)(t - 10.0), t => Quat.Identity, 10.0, 10.4, 100.0);
            ThrowSettings s = new ThrowSettings();

            s.ReleaseTimeOffset = 0.040;
            Assert.Equal(10.26, ThrowEstimator.Estimate(h, 10.3, 10.3, s).ReleaseTime, 9);
            s.ReleaseTimeOffset = -0.020;
            Assert.Equal(10.32, ThrowEstimator.Estimate(h, 10.3, 10.3, s).ReleaseTime, 9);

            // Silly values are held to the hard limits.
            s.ReleaseTimeOffset = 5.0;
            Assert.Equal(10.3 - ThrowSettings.MaxReleaseTimeOffset, ThrowEstimator.Estimate(h, 10.3, 10.3, s).ReleaseTime, 9);
            s.ReleaseTimeOffset = double.NaN;
            Assert.Equal(10.3, ThrowEstimator.Estimate(h, 10.3, 10.3, s).ReleaseTime, 9);

            // An event later than the newest pose: the release is pinned to the newest pose and flagged.
            s.ReleaseTimeOffset = 0.0;
            ThrowResult beyond = ThrowEstimator.Estimate(h, 10.6, 10.6, s);
            Assert.True(beyond.Diagnostics.ReleaseTimeClamped);
            Assert.Equal(h.NewestTime, beyond.ReleaseTime, 9);
            Check.Near(v, beyond.ReleaseVelocity, 1e-2f, "still the right velocity");
            // And one from before the history began.
            ThrowResult before = ThrowEstimator.Estimate(h, 3.0, 10.6, s);
            Assert.True(before.Diagnostics.ReleaseTimeClamped);
            Assert.Equal(h.OldestTime, before.ReleaseTime, 9);
            AssertAllFinite(before);
        }

        // A swing along +X whose speed rises to 'top' and falls away again, so the release (noticed late) sees a slower hand.
        private static PoseHistory Swingy(float top, double rate = 120.0)
        {
            // speed(t) = top * cos^2(pi * (t - 10.2) / 0.3) for |t - 10.2| < 0.15
            Func<double, Vec3> pos = t =>
            {
                double x = Math.Max(-0.15, Math.Min(0.15, t - 10.2));
                double k = Math.PI / 0.3;
                double integral = 0.5 * x + Math.Sin(2 * k * x) / (4 * k);
                return new Vec3((float)(top * integral), 0f, 1f);
            };
            return Record(pos, t => Quat.Identity, 10.0, 10.4, rate);
        }

        [Fact]
        public void PeakSpeedReplacesTheFittedSpeedForRealThrows()
        {
            PoseHistory h = Swingy(8f);
            ThrowSettings s = new ThrowSettings();
            s.ReleaseTimeOffset = 0.0;
            // Released 80 ms after the fastest moment: the fitted speed has sagged, the peak has not.
            ThrowResult r = ThrowEstimator.Estimate(h, 10.28, 10.28, s);
            Assert.Equal(ThrowMethod.FitDirectionPeakSpeed, r.Diagnostics.Method);
            Assert.InRange(r.Diagnostics.PeakSpeed, 7.7f, 8.05f);
            Assert.InRange(r.Diagnostics.PeakTime, 10.19, 10.21);
            Assert.InRange(r.Diagnostics.FitSpeed, 5f, 7f);
            Assert.Equal(r.Diagnostics.PeakSpeed, r.ReleaseVelocity.Length(), 3);
            Assert.True(r.ReleaseVelocity.X > 0f);

            s.UsePeakSpeed = false;
            ThrowResult plain = ThrowEstimator.Estimate(h, 10.28, 10.28, s);
            Assert.Equal(ThrowMethod.FitOnly, plain.Diagnostics.Method);
            Assert.Equal(plain.Diagnostics.FitSpeed, plain.ReleaseVelocity.Length(), 4);
            Assert.False(plain.Diagnostics.PeakFound);

            // The peak may only lift the fitted speed by the allowed factor.
            s.UsePeakSpeed = true;
            s.MaxPeakBoost = 1.05f;
            ThrowResult capped = ThrowEstimator.Estimate(h, 10.28, 10.28, s);
            Assert.Equal(capped.Diagnostics.FitSpeed * 1.05f, capped.ReleaseVelocity.Length(), 3);
        }

        [Fact]
        public void ADisagreeingPeakIsIgnored()
        {
            // Fast along +X, then a sharp turn to slow motion along +Y just before release.
            Func<double, Vec3> pos = t =>
            {
                double a = Math.Min(t, 10.25) - 10.0;
                double b = Math.Max(0.0, t - 10.25);
                return new Vec3((float)(8.0 * a), (float)(2.5 * b), 1f);
            };
            PoseHistory h = Record(pos, t => Quat.Identity, 10.0, 10.4, 250.0);
            ThrowSettings s = new ThrowSettings();
            s.ReleaseTimeOffset = 0.0;
            s.FitWindowSeconds = 0.04;
            ThrowResult r = ThrowEstimator.Estimate(h, 10.30, 10.30, s);
            Assert.True(r.Diagnostics.PeakFound);
            Assert.True(r.Diagnostics.PeakSpeed > 7f);
            Assert.True(r.Diagnostics.AngleBetweenMethods > Check.Deg(60f));
            Assert.Equal(ThrowMethod.FitOnly, r.Diagnostics.Method);
            Assert.InRange(r.ReleaseVelocity.Length(), 2.3f, 2.7f);
            Assert.True(r.ReleaseVelocity.Y > 2.3f);
        }

        [Fact]
        public void ALetGoDoesNotBecomeAThrow()
        {
            // The hand swings fast and then comes almost to rest (0.3 m/s) before the fingers open.
            Func<double, Vec3> pos = t =>
            {
                double fast = Math.Min(t, 10.2) - 10.0;
                double slow = Math.Max(0.0, t - 10.2);
                return new Vec3((float)(6.0 * fast + 0.3 * slow), 0f, 1f);
            };
            PoseHistory h = Record(pos, t => Quat.Identity, 10.0, 10.4, 120.0);
            ThrowSettings s = new ThrowSettings();
            s.ReleaseTimeOffset = 0.0;
            s.SpeedScale = 1.1f;
            s.PeakSearchSeconds = 0.15;   // far enough back to see the fast part
            ThrowResult r = ThrowEstimator.Estimate(h, 10.30, 10.30, s);
            Assert.True(r.IsDrop);
            Assert.Equal(0f, r.Diagnostics.Throwness);
            Assert.True(r.Diagnostics.PeakSpeed > 5f);                    // the fast part was seen...
            Assert.InRange(r.ReleaseVelocity.Length(), 0.28f, 0.32f);     // ...but the ball just keeps the hand's slow motion

            // Dead still: zero velocity, flagged as a drop, nothing odd.
            PoseHistory still = Record(t => new Vec3(1f, 2f, 3f), t => Quat.Identity, 10.0, 10.4, 90.0);
            ThrowResult z = ThrowEstimator.Estimate(still, 10.3, 10.31, new ThrowSettings());
            Assert.True(z.Valid);
            Assert.True(z.IsDrop);
            Assert.Equal(0f, z.ReleaseVelocity.Length());
            Check.Near(new Vec3(1f, 2f, 3f), z.ReleasePosition, 1e-6f, "position");
            Assert.True(z.NowPosition.Z < 3f);   // it simply falls
            AssertAllFinite(z);
        }

        [Fact]
        public void ThrowHelpFadesInBetweenDropAndFullThrow()
        {
            ThrowSettings s = new ThrowSettings();
            s.ReleaseTimeOffset = 0.0;
            s.SpeedScale = 1.1f;
            s.UsePeakSpeed = false;
            float previousGain = 1f;
            foreach (float speed in new[] { 0.3f, 0.6f, 1.0f, 1.3f, 1.7f, 2.0f, 5.0f })
            {
                Vec3 v = new Vec3(speed, 0f, 0f);
                PoseHistory h = Record(t => v * (float)(t - 10.0), t => Quat.Identity, 10.0, 10.4, 120.0);
                ThrowResult r = ThrowEstimator.Estimate(h, 10.3, 10.3, s);
                float gain = r.ReleaseVelocity.Length() / speed;
                Assert.InRange(gain, 1f - 1e-3f, 1.1f + 1e-3f);
                Assert.True(gain >= previousGain - 1e-4f, "the help must only grow with speed");
                previousGain = gain;
                Assert.Equal(speed < s.DropSpeed, r.IsDrop);
                if (speed <= 0.6f) Assert.Equal(1f, gain, 3);
                if (speed >= 2.0f) Assert.Equal(1.1f, gain, 3);
            }
        }

        [Fact]
        public void SpeedIsCapped()
        {
            Vec3 v = new Vec3(50f, 0f, 0f);
            PoseHistory h = Record(t => v * (float)(t - 10.0), t => Quat.Identity, 10.0, 10.4, 250.0);
            ThrowResult r = ThrowEstimator.Estimate(h, 10.3, 10.3, new ThrowSettings());
            Assert.True(r.Diagnostics.SpeedClamped);
            Assert.Equal(30f, r.ReleaseVelocity.Length(), 3);
        }

        [Fact]
        public void DegenerateInputsGiveSaneOutput()
        {
            ThrowSettings s = new ThrowSettings();

            // No history at all (or no history object).
            ThrowResult none = ThrowEstimator.Estimate(new PoseHistory(), 1.0, 1.0, s);
            Assert.False(none.Valid);
            Assert.True(none.IsDrop);
            Assert.Equal(ThrowMethod.NoData, none.Diagnostics.Method);
            AssertAllFinite(none);
            AssertAllFinite(ThrowEstimator.Estimate(null, 1.0, 1.0, s));
            AssertAllFinite(ThrowEstimator.Estimate(new PoseHistory(), 1.0, 1.0, null));

            // One sample: a position, no speed.
            PoseHistory one = new PoseHistory();
            one.Add(5.0, new Vec3(1f, 2f, 3f), Quat.Identity);
            ThrowResult single = ThrowEstimator.Estimate(one, 5.0, 5.02, s);
            Assert.True(single.Valid);
            Assert.True(single.IsDrop);
            Assert.Equal(ThrowMethod.PositionOnly, single.Diagnostics.Method);
            Assert.Equal(0f, single.ReleaseVelocity.Length());
            Check.Near(new Vec3(1f, 2f, 3f), single.ReleasePosition, 1e-6f, "single sample position");
            AssertAllFinite(single);

            // The same timestamp over and over: still one sample, still no nonsense.
            PoseHistory same = new PoseHistory();
            for (int i = 0; i < 20; i++) same.Add(5.0, new Vec3(i, 0f, 0f), Quat.Identity);
            Assert.Equal(1, same.Count);
            ThrowResult dup = ThrowEstimator.Estimate(same, 5.0, 5.0, s);
            Assert.Equal(ThrowMethod.PositionOnly, dup.Diagnostics.Method);
            AssertAllFinite(dup);

            // Two samples: enough for a straight-line speed, even when a Curve was asked for.
            PoseHistory two = new PoseHistory();
            two.Add(5.00, new Vec3(0f, 0f, 1f), Quat.Identity);
            two.Add(5.01, new Vec3(0.05f, 0f, 1f), Quat.Identity);
            ThrowSettings curve = new ThrowSettings();
            curve.Fit = VelocityFit.Curve;
            curve.ReleaseTimeOffset = 0.0;
            ThrowResult pair = ThrowEstimator.Estimate(two, 5.01, 5.01, curve);
            Assert.Equal(VelocityFit.Line, pair.Diagnostics.FitUsed);
            Assert.Equal(5f, pair.ReleaseVelocity.X, 2);
            AssertAllFinite(pair);

            // An event time that is not a number.
            PoseHistory ok = Record(t => new Vec3((float)t, 0f, 0f), t => Quat.Identity, 10.0, 10.3, 90.0);
            ThrowResult nan = ThrowEstimator.Estimate(ok, double.NaN, 10.3, s);
            Assert.False(nan.Valid);
            AssertAllFinite(nan);
            AssertAllFinite(ThrowEstimator.Estimate(ok, 10.3, double.NaN, s));

            // Rubbish settings.
            ThrowSettings bad = new ThrowSettings();
            bad.FitWindowSeconds = double.NaN;
            bad.FitWindowAfterSeconds = -5;
            bad.PeakSearchSeconds = double.NaN;
            bad.PeakSmoothingSeconds = 0;
            bad.MaxRollbackSeconds = double.NaN;
            bad.BallLocalOffset = new Vec3(float.NaN, 0f, 0f);
            bad.Gravity = new Vec3(float.NaN, 0f, 0f);
            bad.SpeedScale = float.NaN;
            bad.DropSpeed = -3f;
            bad.FullThrowSpeed = -5f;
            AssertAllFinite(ThrowEstimator.Estimate(ok, 10.3, 10.31, bad));
        }

        [Fact]
        public void ATrackingGapIsBridgedAndReported()
        {
            // Steady 6 m/s, but the hand is lost (behind the head) from 10.10 to 10.27; release at 10.30.
            Vec3 v = new Vec3(6f, 0f, 1f);
            PoseHistory h = new PoseHistory();
            for (int i = 0; i <= 36; i++)
            {
                double t = 10.0 + i / 90.0;
                if (t > 10.10 && t < 10.27) continue;
                h.Add(t, v * (float)(t - 10.0), Quat.Identity);
            }
            ThrowSettings s = new ThrowSettings();
            s.ReleaseTimeOffset = 0.0;
            ThrowResult r = ThrowEstimator.Estimate(h, 10.30, 10.30, s);
            Check.Near(v, r.ReleaseVelocity, 0.02f, "velocity across the gap");
            Assert.True(r.Diagnostics.SamplesUsed >= 3);
            AssertAllFinite(r);

            // Release in the middle of the gap: the pose is blended across it, and the size of the hole is reported.
            ThrowResult inGap = ThrowEstimator.Estimate(h, 10.20, 10.20, s);
            Assert.True(inGap.Diagnostics.PoseGapAtRelease > 0.15f);
            Check.Near(v * 0.20f, inGap.ReleasePosition, 1e-3f, "blended position");
            AssertAllFinite(inGap);

            // Only two readings in the window: older ones are pulled in, and that is flagged.
            PoseHistory sparse = new PoseHistory();
            foreach (double t in new[] { 10.00, 10.10, 10.20, 10.27, 10.30 }) sparse.Add(t, v * (float)(t - 10.0), Quat.Identity);
            ThrowResult widened = ThrowEstimator.Estimate(sparse, 10.30, 10.30, s);
            Assert.True(widened.Diagnostics.WindowWidened);
            Assert.Equal(3, widened.Diagnostics.SamplesUsed);
            Check.Near(v, widened.ReleaseVelocity, 0.02f, "velocity from widened window");
        }

        [Fact]
        public void IrregularAndOutOfOrderSamplesGiveTheSameAnswerAsTidyOnes()
        {
            Rng rng = new Rng(31u);
            List<PoseSample> samples = new List<PoseSample>();
            double t = 10.0;
            while (t < 10.4)
            {
                float x = (float)(t - 10.0);
                samples.Add(new PoseSample(t, new Vec3(20f * x * x, 3f * x, 1f + x), Check.AboutZ(4f * x)));
                t += rng.Range(0.002f, 0.014f);   // anything from 70 to 500 Hz, changing every sample
            }
            PoseHistory tidy = new PoseHistory();
            foreach (PoseSample p in samples) tidy.Add(p);

            // Same samples, shuffled within a few places, with some sent twice.
            PoseHistory messy = new PoseHistory();
            List<PoseSample> shuffled = new List<PoseSample>(samples);
            for (int i = 0; i + 3 < shuffled.Count; i += 4)
            {
                PoseSample tmp = shuffled[i];
                shuffled[i] = shuffled[i + 2];
                shuffled[i + 2] = tmp;
            }
            foreach (PoseSample p in shuffled)
            {
                messy.Add(p);
                if (rng.NextFloat() < 0.3f) messy.Add(p);
            }
            Assert.Equal(tidy.Count, messy.Count);

            ThrowSettings s = new ThrowSettings();
            s.Fit = VelocityFit.Curve;
            s.BallLocalOffset = new Vec3(0.08f, 0f, 0.02f);
            ThrowResult a = ThrowEstimator.Estimate(tidy, 10.3312, 10.34, s);
            ThrowResult b = ThrowEstimator.Estimate(messy, 10.3312, 10.34, s);
            Check.Near(a.ReleaseVelocity, b.ReleaseVelocity, 1e-5f, "velocity");
            Check.Near(a.ReleasePosition, b.ReleasePosition, 1e-6f, "position");
            Check.Near(a.NowPosition, b.NowPosition, 1e-5f, "now position");

            // And the answer is right: x = 20 t^2 means 40 t m/s along X at the release time.
            float tr = (float)(a.ReleaseTime - 10.0);
            Assert.Equal(40f * tr, a.Diagnostics.HandVelocity.X, 1);
            Assert.Equal(3f, a.Diagnostics.HandVelocity.Y, 1);
            Assert.Equal(4f, a.AngularVelocity.Z, 1);
        }

        [Fact]
        public void RandomRubbishNeverProducesNotANumber()
        {
            Rng rng = new Rng(2024u);
            for (int trial = 0; trial < 300; trial++)
            {
                PoseHistory h = new PoseHistory(capacity: 32);
                int n = rng.RangeInt(0, 40);
                double t = 100.0;
                for (int i = 0; i < n; i++)
                {
                    int kind = rng.RangeInt(0, 10);
                    if (kind == 0) t -= rng.Range(0f, 0.05f);                 // time goes backwards
                    else if (kind == 1) { }                                    // same timestamp again
                    else if (kind == 2) t += rng.Range(0.1f, 0.7f);           // a long gap
                    else t += rng.Range(0f, 0.012f);
                    Vec3 p = new Vec3(rng.Range(-2f, 2f), rng.Range(-2f, 2f), rng.Range(0f, 2f));
                    if (kind == 3) p *= 1e6f;                                  // a wild tracking glitch
                    if (kind == 4) p = new Vec3(float.NaN, 0f, 0f);
                    Quat q = new Quat(rng.Range(-1f, 1f), rng.Range(-1f, 1f), rng.Range(-1f, 1f), rng.Range(-1f, 1f));
                    if (kind == 5) q = new Quat(0f, 0f, 0f, 0f);
                    h.Add(t, p, q);
                }
                ThrowSettings s = new ThrowSettings();
                s.Fit = (trial & 1) == 0 ? VelocityFit.Line : VelocityFit.Curve;
                s.FitWindowAfterSeconds = (trial & 2) == 0 ? 0.0 : 0.02;
                s.BallLocalOffset = new Vec3(0.08f, 0.01f, 0.02f);
                s.ReleaseTimeOffset = rng.Range(-0.03f, 0.06f);
                double eventTime = t + rng.Range(-0.3f, 0.1f);
                ThrowResult r = ThrowEstimator.Estimate(h, eventTime, eventTime + rng.Range(-0.01f, 0.05f), s);
                AssertAllFinite(r);
                Assert.True(r.ReleaseVelocity.Length() <= s.MaxSpeed + 1e-3f);

                ReleaseDelayCalibrator cal = new ReleaseDelayCalibrator();
                CalibrationObservation obs = cal.Observe(h, eventTime, s.BallLocalOffset);
                Assert.False(double.IsNaN(obs.DelaySample));
                Assert.False(double.IsNaN(cal.DelaySeconds));
                Assert.False(float.IsNaN(cal.Confidence));
            }
        }

        [Fact]
        public void TheSimulatorMakesBelievableThrows()
        {
            // Guards the test generator itself: speeds in a human range, the hand keeps moving after the
            // release, and the follow-through curves away from the launch direction.
            Rng rng = new Rng(8u);
            foreach (ThrowKind kind in new[] { ThrowKind.Overarm, ThrowKind.Underarm, ThrowKind.SidearmFlick, ThrowKind.GentleToss })
            {
                for (int i = 0; i < 40; i++)
                {
                    Swing swing = Swing.Random(kind, ref rng);
                    SimThrow th = ThrowSim.Generate(swing, Rig.Perfect(90), ref rng);
                    float speed = th.TrueReleaseVelocity.Length();
                    if (kind == ThrowKind.GentleToss) Assert.InRange(speed, 1.5f, 4.5f);
                    else Assert.InRange(speed, 4f, 16f);
                    Assert.True(th.TrueReleaseVelocity.X > 0.45f * speed, kind + " should go forward");
                    Assert.InRange(Math.Abs(th.TrueReleaseTime - th.BallPeakTime), 0.0, 0.0161);

                    D3 at40 = swing.BallVelocity(th.TrueReleaseTime + 0.04);
                    D3 at80 = swing.BallVelocity(th.TrueReleaseTime + 0.08);
                    Assert.True(at40.Length > 0.5 * speed, kind + ": the hand must keep moving after the release");
                    float turn40 = MathUtil.AngleBetween(th.TrueReleaseVelocity, at40.ToVec3());
                    float turn80 = MathUtil.AngleBetween(th.TrueReleaseVelocity, at80.ToVec3());
                    Assert.True(turn40 > Check.Deg(6f), kind + ": the follow-through must curve away (" + turn40 * 57.3f + " deg after 40 ms)");
                    Assert.True(turn80 > Check.Deg(8f), kind + ": and stay curved away (" + turn80 * 57.3f + " deg after 80 ms)");

                    // The exact formulas for the ball agree with "hand velocity + turning x offset".
                    D3 p;
                    Qd q;
                    swing.Pose(th.TrueReleaseTime, out p, out q);
                    D3 lever = q.Rotate(swing.BallOffsetLocal);
                    D3 sum = swing.HandVelocity(th.TrueReleaseTime) + D3.Cross(swing.AngularVelocity(th.TrueReleaseTime), lever);
                    Check.Near(th.TrueReleaseVelocity, sum.ToVec3(), 2e-3f, "v + w x r");
                }
            }
        }
    }
}
