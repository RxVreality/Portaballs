using System;
using Xunit;

namespace Portaballs.Core.Tests
{
    /// <summary>
    /// Aim help on a floor and a ceiling. Those become paintable frames in the room's wall list (level U
    /// and V, Normal pointing into the room), so a look point can be underfoot or overhead.
    /// </summary>
    public class AimOnFloorAndCeilingTests
    {
        private static readonly Vec3 G = new Vec3(0f, 0f, -9.81f);
        private static readonly Vec3 Hand = new Vec3(0f, -0.2f, 1.6f);
        private static readonly Vec3 Up = new Vec3(0f, 0f, 1f);
        private static readonly Vec3 Down = new Vec3(0f, 0f, -1f);

        // The test room: 8 x 6 x 3.5 m, centred, floor at z = 0. Four walls as TestRoom builds them, then
        // the floor and the ceiling the way the frames are described: level U and V, Normal into the room.
        private const int FloorIndex = 4, CeilingIndex = 5;

        private static WallFrame[] Room()
        {
            return new[]
            {
                new WallFrame(new Vec3(4f, 3f, 0f), new Vec3(0f, -1f, 0f), Up, 6f, 3.5f),     // +X
                new WallFrame(new Vec3(-4f, -3f, 0f), new Vec3(0f, 1f, 0f), Up, 6f, 3.5f),    // -X
                new WallFrame(new Vec3(-4f, 3f, 0f), new Vec3(1f, 0f, 0f), Up, 8f, 3.5f),     // +Y
                new WallFrame(new Vec3(4f, -3f, 0f), new Vec3(-1f, 0f, 0f), Up, 8f, 3.5f),    // -Y
                new WallFrame(new Vec3(-4f, -3f, 0f), new Vec3(1f, 0f, 0f), new Vec3(0f, 1f, 0f), 8f, 6f),      // floor
                new WallFrame(new Vec3(-4f, 3f, 3.5f), new Vec3(1f, 0f, 0f), new Vec3(0f, -1f, 0f), 8f, 6f),    // ceiling
            };
        }

        // The throw that would hit the target at this speed (the flat one), turned off it by an angle about an axis.
        private static Vec3 ThrowOffBy(float speed, Vec3 target, Vec3 axis, float degreesOff, out Vec3 perfect)
        {
            LaunchSolution sol = Ballistics.SolveLaunch(Hand, target, speed, G);
            Assert.True(sol.Reachable, "the test's own throw must be able to reach");
            perfect = sol.LowArcVelocity;
            Vec3 dir = Vec3.Normalize(perfect);
            Vec3 square = Vec3.Normalize(axis - dir * Vec3.Dot(axis, dir));   // the part of the axis square to the throw
            return Vec3.Transform(dir, Quat.CreateFromAxisAngle(square, Check.Deg(degreesOff))) * speed;
        }

        private static AimAssistSettings EyeOnly()
        {
            AimAssistSettings s = new AimAssistSettings();
            s.Mode = AimAssistMode.Eye;
            return s;
        }

        [Fact]
        public void TheRoomsFramesFaceIntoTheRoom()
        {
            WallFrame[] room = Room();
            Vec3 middle = new Vec3(0f, 0f, 1.75f);
            for (int i = 0; i < room.Length; i++)
            {
                Assert.True(room[i].SignedDistance(middle) > 1f, "frame " + i + " should face the middle of the room");
            }
            Check.Near(Up, room[FloorIndex].Normal, 1e-6f, "floor normal");
            Check.Near(Down, room[CeilingIndex].Normal, 1e-6f, "ceiling normal");
        }

        [Fact]
        public void AnAimRayFindsTheFloorAndTheCeiling()
        {
            WallFrame[] room = Room();
            Vec3 eye = new Vec3(-1f, 0.5f, 1.7f);
            AimPoint p;
            int index;
            float distance;

            // Looking down ahead: 45 degrees below level lands on the floor 1.7 m in front.
            Assert.True(AimAssist.FindAimPoint(eye, new Vec3(1f, 0f, -1f), room, room.Length, 50f, out p, out index, out distance));
            Assert.Equal(FloorIndex, index);
            Check.Near(new Vec3(0.7f, 0.5f, 0f), p.Point, 1e-4f, "floor look point");
            Check.Near(Up, p.Normal, 1e-6f, "floor look normal");
            Assert.Equal(1.7f * MathF.Sqrt(2f), distance, 4);
            Assert.True(p.Valid);
            Vec2 uv = room[FloorIndex].ToUV(p.Point);
            Assert.Equal(4.7f, uv.X, 4);
            Assert.Equal(3.5f, uv.Y, 4);

            // Straight down, at one's own feet.
            Assert.True(AimAssist.FindAimPoint(eye, Down, room, room.Length, 50f, out p, out index, out distance));
            Assert.Equal(FloorIndex, index);
            Check.Near(new Vec3(-1f, 0.5f, 0f), p.Point, 1e-4f, "feet");

            // Looking up and to the left: the ceiling, 1.8 m above the eye.
            Assert.True(AimAssist.FindAimPoint(eye, new Vec3(0.5f, 0.5f, 1f), room, room.Length, 50f, out p, out index, out distance));
            Assert.Equal(CeilingIndex, index);
            Check.Near(new Vec3(-0.1f, 1.4f, 3.5f), p.Point, 1e-4f, "ceiling look point");
            Check.Near(Down, p.Normal, 1e-6f, "ceiling look normal");

            // A shallow look down the room reaches the far wall before it reaches the floor: the nearer surface wins.
            Assert.True(AimAssist.FindAimPoint(eye, new Vec3(1f, 0f, -0.2f), room, room.Length, 50f, out p, out index, out distance));
            Assert.Equal(0, index);
            Assert.Equal(4f, p.Point.X, 4);
            Assert.Equal(0.7f, p.Point.Z, 4);
            // A little steeper and it is the floor just in front of that wall.
            Assert.True(AimAssist.FindAimPoint(eye, new Vec3(1f, 0f, -0.4f), room, room.Length, 50f, out p, out index, out distance));
            Assert.Equal(FloorIndex, index);
            Assert.Equal(3.25f, p.Point.X, 4);

            // With every surface in the list, a ray from inside the room always finds something.
            Rng rng = new Rng(5u);
            int[] hits = new int[room.Length];
            for (int i = 0; i < 2000; i++)
            {
                Vec3 from = new Vec3(rng.Range(-3.9f, 3.9f), rng.Range(-2.9f, 2.9f), rng.Range(0.1f, 3.4f));
                Assert.True(AimAssist.FindAimPoint(from, rng.OnUnitSphere(), room, room.Length, 50f, out p, out index, out distance));
                hits[index]++;
                Assert.True(MathF.Abs(room[index].SignedDistance(p.Point)) < 1e-3f, "the look point lies on the surface found");
                Assert.True(room[index].Contains(room[index].ToUV(p.Point)));
            }
            Assert.True(hits[FloorIndex] > 100 && hits[CeilingIndex] > 100, "the floor and the ceiling take their share");

            // The floor is only seen from above, the ceiling only from below.
            WallFrame[] floorOnly = { room[FloorIndex] };
            Assert.False(AimAssist.FindAimPoint(new Vec3(0f, 0f, -0.5f), Up, floorOnly, 1, 50f, out p, out index, out distance));
            Assert.False(AimAssist.FindAimPoint(eye, Up, floorOnly, 1, 50f, out p, out index, out distance));
            WallFrame[] ceilingOnly = { room[CeilingIndex] };
            Assert.False(AimAssist.FindAimPoint(new Vec3(0f, 0f, 4f), Down, ceilingOnly, 1, 50f, out p, out index, out distance));
            Assert.False(AimAssist.FindAimPoint(eye, Down, ceilingOnly, 1, 50f, out p, out index, out distance));
            // A look along the floor never lands on it.
            Assert.False(AimAssist.FindAimPoint(eye, new Vec3(1f, 0f, 0f), floorOnly, 1, 50f, out p, out index, out distance));
        }

        [Theory]
        [InlineData(0f, 1f, 0f, 8f)]       // off to the side
        [InlineData(0f, 1f, 0f, -12f)]
        [InlineData(0f, 0f, 1f, 6f)]       // about the upright axis: also sideways
        [InlineData(1f, 0f, 0f, 9f)]
        [InlineData(-0.3f, 1f, 0.2f, 14f)]
        public void AThrowAtALookPointOnTheFloorIsPulledTowardIt(float ax, float ay, float az, float degreesOff)
        {
            Vec3 target = new Vec3(3f, 0.6f, 0f);
            Vec3 perfect;
            Vec3 thrown = ThrowOffBy(7f, target, new Vec3(ax, ay, az), degreesOff, out perfect);
            AimAssistSettings s = EyeOnly();

            AimAssistResult r = AimAssist.Apply(s, Hand, thrown, false, AimPoint.None, new AimPoint(target, Up));
            Assert.True(r.Applied, "outcome " + r.Outcome);
            Assert.Equal(AimSource.Eye, r.Source);
            Assert.Equal(Check.Deg(MathF.Abs(degreesOff)), r.AngleOff, 3);
            Assert.Equal(s.Eye.Strength, r.Weight, 4);                 // inside the inner angle: the full pull
            Assert.Equal(7f, r.Velocity.Length(), 3);                  // in reach: the speed is left alone
            Check.Near(perfect, r.PerfectVelocity, 1e-3f, "the perfect throw is the flat one the hand was near");

            // Both landings are measured ON THE FLOOR, and the helped one is nearer the look point.
            Assert.True(r.LandingOnSurface);
            Assert.Equal(0f, r.LandingWithout.Z, 3);
            Assert.Equal(0f, r.LandingWith.Z, 3);
            Assert.True(r.MissWithout > 0.2f, "the test throw should really miss, missed by " + r.MissWithout);
            Assert.True(r.MissWith < r.MissWithout * (1f - s.Eye.Strength) * 1.35f, r.MissWithout + " -> " + r.MissWith);

            // The reported landing is where the ball really comes down.
            float t;
            Vec3 hit;
            Vec2 uv;
            bool inside;
            WallFrame floor = Room()[FloorIndex];
            Assert.True(Ballistics.TryHitWall(Hand, r.Velocity, G, floor, out t, out hit, out uv, out inside));
            Assert.True(inside);
            Check.Near(r.LandingWith, hit, 1e-3f, "landing on the floor frame");

            // At full strength the ball lands on the look point itself.
            AimAssistSettings full = EyeOnly();
            full.Eye = new AimCone(20f, 30f, 1f);
            AimAssistResult exact = AimAssist.Apply(full, Hand, thrown, false, AimPoint.None, new AimPoint(target, Up));
            Check.Near(target, exact.LandingWith, 3e-3f, "full strength lands on the floor point");
        }

        [Fact]
        public void ALobOntoTheFloorIsHelpedAsALobAndAThrowMeantElsewhereIsLeftAlone()
        {
            Vec3 target = new Vec3(3f, 0.6f, 0f);
            AimPoint look = new AimPoint(target, Up);
            AimAssistSettings s = EyeOnly();

            // A high lob that would come down a little to one side.
            LaunchSolution sol = Ballistics.SolveLaunch(Hand, target, 7f, G);
            Vec3 lob = Vec3.Transform(sol.HighArcVelocity, Quat.CreateFromAxisAngle(Up, Check.Deg(5f)));
            AimAssistResult r = AimAssist.Apply(s, Hand, lob, false, AimPoint.None, look);
            Assert.True(r.Applied, "outcome " + r.Outcome);
            Check.Near(sol.HighArcVelocity, r.PerfectVelocity, 1e-3f, "aims for the lob");
            Assert.True(r.Velocity.Z > 0.9f * lob.Z, "still a lob");
            Assert.True(r.MissWith < r.MissWithout);
            Assert.Equal(0f, r.LandingWith.Z, 3);

            // Looking at the floor but throwing up at the far wall: more than 30 degrees away, untouched.
            Vec3 atTheWall = new Vec3(9f, 0f, 3f);
            AimAssistResult no = AimAssist.Apply(s, Hand, atTheWall, false, AimPoint.None, look);
            Assert.False(no.Applied);
            Assert.Equal(AimAssistOutcome.OutsideCone, no.Outcome);
            Assert.Equal(atTheWall, no.Velocity);

            // Letting a ball go while looking at the floor stays a let-go.
            AimAssistResult dropped = AimAssist.Apply(s, Hand, new Vec3(0.3f, 0f, -0.2f), true, AimPoint.None, look);
            Assert.Equal(AimAssistOutcome.Drop, dropped.Outcome);
            Assert.Equal(new Vec3(0.3f, 0f, -0.2f), dropped.Velocity);

            // A soft underhand toss at the floor near one's feet is in reach (downhill needs little speed) and gets the faded-in help.
            Vec3 near = new Vec3(1.2f, -0.2f, 0f);
            Vec3 perfect;
            float time, min;
            Assert.True(Ballistics.SolveLaunchClosestTo(Hand, near, 2.25f, G, new Vec3(1f, 0f, 0f), out perfect, out time, out min));
            Assert.True(min < 2.25f, "needs " + min + " m/s");
            AimAssistResult toss = AimAssist.Apply(s, Hand, Vec3.Transform(perfect, Quat.CreateFromAxisAngle(Up, Check.Deg(6f))), false, AimPoint.None, new AimPoint(near, Up));
            Assert.True(toss.Applied, "outcome " + toss.Outcome);
            Assert.InRange(toss.Weight, s.Eye.Strength * 0.5f - 0.005f, s.Eye.Strength * 0.5f + 0.005f);      // 2.25 m/s is half way from 1.5 to 3.0
            Assert.True(toss.MissWith < toss.MissWithout);
        }

        [Theory]
        [InlineData(0f, 1f, 0f, -7f)]      // tipped up
        [InlineData(0f, 0f, 1f, -10f)]     // off to the side
        [InlineData(0f, 0f, 1f, 12f)]
        [InlineData(1f, 0f, 0f, 5f)]
        public void AThrowAtALookPointOnTheCeilingIsPulledTowardIt(float ax, float ay, float az, float degreesOff)
        {
            Vec3 target = new Vec3(2.5f, 0.4f, 3.5f);
            Vec3 perfect;
            Vec3 thrown = ThrowOffBy(9f, target, new Vec3(ax, ay, az), degreesOff, out perfect);
            AimAssistSettings s = EyeOnly();

            AimAssistResult r = AimAssist.Apply(s, Hand, thrown, false, AimPoint.None, new AimPoint(target, Down));
            Assert.True(r.Applied, "outcome " + r.Outcome);
            Assert.Equal(s.Eye.Strength, r.Weight, 4);
            Assert.True(r.LandingOnSurface);
            Assert.Equal(3.5f, r.LandingWithout.Z, 3);
            Assert.Equal(3.5f, r.LandingWith.Z, 3);
            Assert.True(r.MissWithout > 0.15f, "the test throw should really miss, missed by " + r.MissWithout);
            Assert.True(r.MissWith < r.MissWithout * (1f - s.Eye.Strength) * 1.35f, r.MissWithout + " -> " + r.MissWith);

            // It is the crossing on the way UP that counts, not where the path would come back down through the ceiling.
            float t;
            Vec3 hit;
            Vec2 uv;
            bool inside;
            Assert.True(Ballistics.TryHitWall(Hand, r.Velocity, G, Room()[CeilingIndex], out t, out hit, out uv, out inside));
            Assert.True(inside);
            Check.Near(r.LandingWith, hit, 1e-3f, "landing on the ceiling frame");
            Assert.True(Ballistics.VelocityAt(r.Velocity, G, t).Z > 0f, "still rising when it meets the ceiling");
        }

        [Fact]
        public void AThrowThatFallsShortOfTheCeilingIsStillPulledToward()
        {
            // Tipped down far enough that the ball tops out under the ceiling: there is no landing on the
            // ceiling to compare, so both throws are judged by how near they pass to the look point.
            Vec3 target = new Vec3(2.5f, 0.4f, 3.5f);
            Vec3 perfect;
            Vec3 thrown = ThrowOffBy(8f, target, new Vec3(0f, 1f, 0f), 8f, out perfect);
            float t;
            Vec3 hit;
            Assert.False(Ballistics.TryHitPlane(Hand, thrown, G, target, Down, out t, out hit), "the test throw should fall short of the ceiling");

            AimAssistSettings s = EyeOnly();
            AimAssistResult r = AimAssist.Apply(s, Hand, thrown, false, AimPoint.None, new AimPoint(target, Down));
            Assert.True(r.Applied, "outcome " + r.Outcome);
            Assert.False(r.LandingOnSurface);
            Assert.True(r.LandingWithout.Z < 3.5f && r.LandingWith.Z < 3.5f);
            Assert.True(r.MissWith < 0.8f * r.MissWithout, r.MissWithout + " -> " + r.MissWith);
        }

        [Fact]
        public void ACeilingLookPointOutOfReachIsReportedAndLeftAlone()
        {
            // 1.9 m up needs about 6.1 m/s straight up; a 4 m/s toss cannot get there even with the allowed speed-up.
            Vec3 target = new Vec3(0.5f, -0.2f, 3.5f);
            Vec3 thrown = Vec3.Normalize(new Vec3(0.25f, 0f, 1f)) * 4f;
            AimAssistResult r = AimAssist.Apply(EyeOnly(), Hand, thrown, false, AimPoint.None, new AimPoint(target, Down));
            Assert.False(r.Applied);
            Assert.Equal(AimAssistOutcome.OutOfReach, r.Outcome);
            Assert.Equal(thrown, r.Velocity);
            Assert.InRange(r.MinSpeedNeeded, 6.0f, 6.6f);
            // The path never reaches the ceiling, so the miss is its nearest approach instead, and it is a real number.
            Assert.False(r.LandingOnSurface);
            Check.Finite(r.LandingWithout, "landing without");
            Assert.True(r.LandingWithout.Z < 3.5f);
            Assert.InRange(r.MissWithout, 0.8f, 1.9f);
        }

        [Fact]
        public void OnTheFloorAndTheCeilingHelpNeverMakesAThrowWorse()
        {
            WallFrame[] room = Room();
            Rng rng = new Rng(2026u);
            int applied = 0, onFloor = 0, onCeiling = 0;
            for (int i = 0; i < 4000; i++)
            {
                AimAssistSettings s = new AimAssistSettings();
                Vec3 eye = new Vec3(rng.Range(-3f, 3f), rng.Range(-2f, 2f), rng.Range(1.2f, 1.9f));
                Vec3 hand = eye + new Vec3(rng.Range(-0.4f, 0.4f), rng.Range(-0.4f, 0.4f), rng.Range(-0.6f, 0.3f));

                // Look somewhere on the floor or the ceiling.
                bool floor = rng.NextFloat() < 0.6f;
                Vec3 gaze = Vec3.Normalize(new Vec3(rng.Range(-1f, 1f), rng.Range(-1f, 1f), floor ? rng.Range(-1.5f, -0.3f) : rng.Range(0.5f, 1.5f)));
                AimPoint look;
                int index;
                float distance;
                Assert.True(AimAssist.FindAimPoint(eye, gaze, room, room.Length, 50f, out look, out index, out distance));
                if (index != FloorIndex && index != CeilingIndex) continue;

                // Throw roughly at it: the right throw for some speed, knocked off by up to 35 degrees.
                float speed = rng.Range(2f, 13f);
                Vec3 v;
                float time, min;
                Ballistics.SolveLaunchClosestTo(hand, look.Point, speed, G, rng.OnUnitSphere(), out v, out time, out min);
                Vec3 thrown = Vec3.Transform(MathUtil.SafeNormalize(v, gaze), Quat.CreateFromAxisAngle(rng.OnUnitSphere(), Check.Deg(rng.Range(0f, 35f)))) * speed;

                AimAssistResult r = AimAssist.Apply(s, hand, thrown, false, AimPoint.None, look);
                Check.Finite(r.Velocity, "velocity");
                Check.Finite(r.LandingWith, "landing with");
                Check.Finite(r.LandingWithout, "landing without");
                Assert.False(float.IsNaN(r.MissWith) || float.IsNaN(r.MissWithout));
                Assert.True(r.MissWith <= r.MissWithout + 1e-4f, "assist made a throw worse: " + r.MissWithout + " -> " + r.MissWith);
                if (!r.Applied)
                {
                    Assert.Equal(thrown, r.Velocity);
                    continue;
                }
                applied++;
                if (index == FloorIndex) onFloor++; else onCeiling++;
                Assert.True(r.Weight <= s.Eye.Strength + 1e-5f);
                Assert.True(MathUtil.AngleBetween(thrown, r.Velocity) <= r.AngleOff + 1e-3f, "never turned by more than the error");
                Assert.True(r.Velocity.Length() <= speed * (1f + s.MaxSpeedChange) + 1e-4f);
                if (r.LandingOnSurface)
                {
                    Assert.Equal(index == FloorIndex ? 0f : 3.5f, r.LandingWith.Z, 2);
                }
            }
            Assert.True(onFloor > 500, "floor throws helped: " + onFloor);
            Assert.True(onCeiling > 150, "ceiling throws helped: " + onCeiling);
            Assert.True(applied > 700);
        }
    }

    /// <summary>
    /// The more forgiving search for where the eyes were looking: looser numbers for the steady look,
    /// and a glance when there is no steady look at all.
    /// </summary>
    public class GazeLookTests
    {
        private static readonly Vec3 Head = new Vec3(0f, 0f, 1.7f);

        private static Vec3 Toward(float yawDegrees, float pitchDegrees)
        {
            float yaw = Check.Deg(yawDegrees), pitch = Check.Deg(pitchDegrees);
            return new Vec3(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch));
        }

        // Feed readings at 500 a second (what the helper sends) from 'from' to 'to'.
        private static void Feed(GazeFixationFilter f, double from, double to, Func<double, Vec3> direction, Func<double, bool> valid = null)
        {
            int n = (int)Math.Round((to - from) * 500.0);
            for (int i = 0; i <= n; i++)
            {
                double t = from + i / 500.0;
                f.Add(t, Head, direction(t), valid == null || valid(t));
            }
        }

        // The numbers the search had before the tester's session: 100 ms within 2 degrees.
        private static GazeFixationSettings Strict()
        {
            GazeFixationSettings s = new GazeFixationSettings();
            s.MinDurationSeconds = 0.10;
            s.MaxSpreadAngle = Check.Deg(2f);
            return s;
        }

        [Fact]
        public void TheNumbersAreTheAgreedOnes()
        {
            GazeFixationSettings steady = new GazeFixationSettings();
            Assert.Equal(0.30, steady.LookBackSeconds, 6);
            Assert.Equal(0.07, steady.MinDurationSeconds, 6);
            Assert.Equal(Check.Deg(3f), steady.MaxSpreadAngle, 5);

            GazeFixationSettings glance = GazeFixationSettings.ForGlance();
            Assert.Equal(0.12, glance.LookBackSeconds, 6);
            Assert.Equal(0.04, glance.MinDurationSeconds, 6);
            Assert.Equal(Check.Deg(4.5f), glance.MaxSpreadAngle, 5);
            // A glance is shorter, nearer the release and looser than a steady look.
            Assert.True(glance.LookBackSeconds < steady.LookBackSeconds && glance.MinDurationSeconds < steady.MinDurationSeconds && glance.MaxSpreadAngle > steady.MaxSpreadAngle);
        }

        [Fact]
        public void AShakyLookDuringAHardThrowIsStillASteadyLook()
        {
            // Eyes on the target the whole time, but the reading wobbles 2.2 degrees either side, 9 times a
            // second, as it does when the head jerks in a hard throw.
            Vec3 target = Toward(12f, -4f);
            GazeFixationFilter f = new GazeFixationFilter(512);
            Feed(f, 9.4, 10.0, t => Toward(12f + 2.2f * (float)Math.Sin(t * 2.0 * Math.PI * 9.0), -4f));

            GazeFixation look;
            Assert.False(f.TryGetFixation(10.0, Strict(), out look), "the old numbers lose this look");

            Assert.Equal(GazeLookKind.Steady, f.FindLook(10.0, new GazeFixationSettings(), GazeFixationSettings.ForGlance(), out look));
            Assert.True(look.Found);
            Assert.True(MathUtil.AngleBetween(look.Direction, target) < Check.Deg(1.5f), "points at the target, off by " + MathUtil.AngleBetween(look.Direction, target) * 180f / MathF.PI);
            Assert.InRange(look.AgeAtRelease, 0f, 0.003f);
            Assert.True(look.Duration >= 0.07f);
        }

        [Fact]
        public void AShortLookOfEightyMillisecondsCounts()
        {
            // The eyes land on the target only 80 ms before the release: too short for the old 100 ms, long enough now.
            GazeFixationFilter f = new GazeFixationFilter(512);
            Feed(f, 9.4, 10.0, t => t < 9.92 ? Toward(-30f + (float)((t - 9.4) * 150.0), 10f) : Toward(20f, -8f));

            GazeFixation look;
            Assert.False(f.TryGetFixation(10.0, Strict(), out look));
            Assert.Equal(GazeLookKind.Steady, f.FindLook(10.0, null, null, out look));
            Assert.True(MathUtil.AngleBetween(look.Direction, Toward(20f, -8f)) < Check.Deg(0.2f));
            Assert.InRange(look.Duration, 0.075f, 0.082f);
        }

        [Fact]
        public void WithNoSteadyLookAGlanceIsUsed()
        {
            // Restless eyes: a new spot 15 degrees on every 55 ms. Nothing lasts the 70 ms a steady look needs,
            // but for the last 55 ms before the release they were pointing one way.
            Func<double, Vec3> restless = t => Toward(15f * (float)Math.Floor((t - 9.4 + 1e-9) / 0.055), 0f);
            GazeFixationFilter f = new GazeFixationFilter(512);
            Feed(f, 9.4, 10.0, restless);
            Vec3 last = restless(9.999);

            GazeFixation look;
            GazeFixationSettings steady = new GazeFixationSettings();
            Assert.False(f.TryGetFixation(10.0, steady, out look));
            Assert.Equal(GazeLookKind.Glance, f.FindLook(10.0, steady, GazeFixationSettings.ForGlance(), out look));
            Assert.True(look.Found);
            Assert.True(MathUtil.AngleBetween(look.Direction, last) < Check.Deg(0.1f), "the glance is where the eyes were at the release");
            Assert.InRange(look.AgeAtRelease, 0f, 0.003f);
            Assert.InRange(look.Duration, 0.04f, 0.056f);

            // With the glance switched off (no numbers, or a look-back of zero) there is nothing: head aim.
            Assert.Equal(GazeLookKind.None, f.FindLook(10.0, steady, null, out look));
            Assert.False(look.Found);
            GazeFixationSettings off = GazeFixationSettings.ForGlance();
            off.LookBackSeconds = 0.0;
            Assert.Equal(GazeLookKind.None, f.FindLook(10.0, steady, off, out look));
        }

        [Fact]
        public void ASteadyLookBeatsAGlanceElsewhere()
        {
            // On the target, then the eyes dart away for the last 50 ms (people do, as they let go).
            // The dart is long enough to be a glance, but the steady look is the trusted one and wins.
            GazeFixationFilter f = new GazeFixationFilter(512);
            Feed(f, 9.4, 10.0, t => t < 9.95 ? Toward(10f, -5f) : Toward(-15f, -20f));
            GazeFixation look;
            Assert.Equal(GazeLookKind.Steady, f.FindLook(10.0, new GazeFixationSettings(), GazeFixationSettings.ForGlance(), out look));
            Assert.True(MathUtil.AngleBetween(look.Direction, Toward(10f, -5f)) < Check.Deg(0.2f));
            Assert.InRange(look.AgeAtRelease, 0.048f, 0.054f);
        }

        [Fact]
        public void EyesInMidJumpOrShutGiveNoLookAtAll()
        {
            GazeFixationSettings steady = new GazeFixationSettings();
            GazeFixationSettings glance = GazeFixationSettings.ForGlance();
            GazeFixation look;

            // Sweeping fast (300 degrees a second) right through the release: no steady look, and no glance either.
            GazeFixationFilter jump = new GazeFixationFilter(512);
            Feed(jump, 9.4, 10.0, t => Toward((float)((t - 9.4) * 300.0) - 90f, 0f));
            Assert.Equal(GazeLookKind.None, jump.FindLook(10.0, steady, glance, out look));
            Assert.False(look.Found);

            // Eyes shut (not valid) for the last 350 ms: the look before the blink is too old.
            GazeFixationFilter shut = new GazeFixationFilter(512);
            Feed(shut, 9.2, 10.0, t => Toward(5f, 0f), t => t < 9.65);
            Assert.Equal(GazeLookKind.None, shut.FindLook(10.0, steady, glance, out look));

            // A short blink (100 ms) right at the release: the steady look just before it is found.
            GazeFixationFilter blink = new GazeFixationFilter(512);
            Feed(blink, 9.2, 10.0, t => Toward(5f, 0f), t => t < 9.9);
            Assert.Equal(GazeLookKind.Steady, blink.FindLook(10.0, steady, glance, out look));
            Assert.InRange(look.AgeAtRelease, 0.099f, 0.105f);

            // Never fed anything, or a release time that is not a number.
            Assert.Equal(GazeLookKind.None, new GazeFixationFilter().FindLook(10.0, steady, glance, out look));
            Assert.Equal(GazeLookKind.None, blink.FindLook(double.NaN, steady, glance, out look));
        }

        [Fact]
        public void TheSummarySaysWhatTheEyesWereDoing()
        {
            // 200 ms on one spot, a 60 ms blink, then 40 ms on a spot 20 degrees to the side, up to the release at 10.0.
            GazeFixationFilter f = new GazeFixationFilter(512);
            Feed(f, 9.7, 10.2, t => t < 9.9 ? Toward(0f, 0f) : Toward(20f, 0f), t => t < 9.9 || t >= 9.96);

            GazeSpanSummary s = f.Summarise(9.7, 10.0);
            Assert.Equal(151, s.Readings);                 // 500 a second for 300 ms, both ends included
            Assert.Equal(121, s.ValidReadings);            // 30 lost to the blink
            Assert.True(s.HasLatest);
            Assert.InRange(s.LatestAge, 0f, 0.0021f);      // readings after the release are not counted
            Assert.True(MathUtil.AngleBetween(s.LatestDirection, Toward(20f, 0f)) < 1e-4f);
            Check.Near(Head, s.LatestOrigin, 1e-6f, "origin");
            Assert.Equal(Check.Deg(20f), s.Swing, 3);      // the eyes travelled 20 degrees in that stretch

            // Only the last 30 ms: all on the new spot.
            GazeSpanSummary end = f.Summarise(9.97, 10.0);
            Assert.Equal(end.Readings, end.ValidReadings);
            Assert.InRange(end.Readings, 15, 16);
            Assert.True(end.Swing < 1e-4f);

            // Inside the blink: readings, but none usable.
            GazeSpanSummary dark = f.Summarise(9.91, 9.95);
            Assert.True(dark.Readings > 15);
            Assert.Equal(0, dark.ValidReadings);
            Assert.False(dark.HasLatest);

            // Nothing there, or rubbish times.
            Assert.Equal(0, f.Summarise(5.0, 6.0).Readings);
            Assert.Equal(0, f.Summarise(double.NaN, 10.0).Readings);
            Assert.Equal(0, new GazeFixationFilter().Summarise(0.0, 100.0).Readings);
        }
    }
}
