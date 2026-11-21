using System;
using Xunit;

namespace Portaballs.Core.Tests
{
    /// <summary>Balls and the player going through a linked pair: BallTransit, PlayerPassage, PlayerFall, PlaySpace.</summary>
    public class PortalTravelTests
    {
        private const float BallRadius = 0.035f;
        private static readonly PortalSettings Settings = new PortalSettings();
        private static readonly Vec3 Gravity = new Vec3(0f, 0f, -9.81f);
        private static readonly Vec3 NoGravity = Vec3.Zero;

        // Orange: disc radius 0.5 on the far wall (plane x = 5) around uv (2, 1.5) = world (5, 0, 1.5).
        // Blue: disc radius 0.4 on the left wall (plane y = 3) around uv (2, 2.0) = world (2, 3, 2.0).
        private static readonly PortalEnd A = Rig.End(Rig.FarWall(), PaintColor.Orange, new Vec2(2f, 1.5f), PortalAnchor.PaintCenter, 0f, 0.5f);
        private static readonly PortalEnd B = Rig.End(Rig.LeftWall(), PaintColor.Blue, new Vec2(2f, 2.0f), PortalAnchor.PaintCenter, 0f, 0.4f);
        private static readonly PortalPair Pair = new PortalPair(A, B);
        private static readonly DiscMask MaskA = new DiscMask(A.Wall, PaintColor.Orange, new Vec2(2f, 1.5f), 0.5f);
        private static readonly DiscMask MaskB = new DiscMask(B.Wall, PaintColor.Blue, new Vec2(2f, 2.0f), 0.4f);
        private static readonly WallFrame[] Walls = { Rig.FarWall(), Rig.NearWall(), Rig.LeftWall() };

        // ------------------------------------------------------------------ balls

        [Fact]
        public void BallHeadingForTheMiddleEnters()
        {
            // 1 m in front of the patch centre, 10 m/s straight in, no gravity.
            BallApproach a = BallTransit.Approach(A, MaskA, new Vec3(4f, 0f, 1.5f), new Vec3(10f, 0f, 0f), NoGravity, BallRadius, 1f, Settings);
            Assert.True(a.Touches);
            Assert.True(a.Enters);
            Assert.Equal((1f - BallRadius) / 10f, a.TouchTime, 5);
            Assert.Equal(0.1f, a.CrossTime, 5);
            Assert.Equal((1f + BallRadius) / 10f, a.ClearTime, 5);
            Rig.Near(new Vec2(2f, 1.5f), a.CrossUV);
            Assert.Equal(17, a.Footprint.TotalSamples);
        }

        [Fact]
        public void BallOutsideTheLookAheadIsNotYetTouching()
        {
            BallApproach a = BallTransit.Approach(A, MaskA, new Vec3(4f, 0f, 1.5f), new Vec3(10f, 0f, 0f), NoGravity, BallRadius, 0.05f, Settings);
            Assert.False(a.Touches);
            Assert.False(a.Enters);
        }

        [Fact]
        public void SlowBallIsSeenComingBeforeItsSkinIsWithinTheNearMargin()
        {
            // 0.3 m/s straight in, skin 25 mm from the wall: the real touch is 83 ms away, two 20 ms physics
            // steps do not reach it, yet a physics engine that reports touches 20 mm early is about to.
            Vec3 start = new Vec3(5f - BallRadius - 0.025f, 0f, 1.5f);
            Vec3 slow = new Vec3(0.3f, 0f, 0f);
            Assert.False(BallTransit.Approach(A, MaskA, start, slow, NoGravity, BallRadius, 0.04f, Settings).Touches);

            BallApproach a = BallTransit.Approach(A, MaskA, start, slow, NoGravity, BallRadius, 0.04f, Settings, float.NegativeInfinity, 0.03f);
            Assert.True(a.Touches);
            Assert.True(a.Enters);
            // What it reports is still the real touch and crossing, not the early one.
            Assert.Equal(0.025f / 0.3f, a.TouchTime, 4);
            Assert.Equal((0.025f + BallRadius) / 0.3f, a.CrossTime, 4);

            // Further out than the margin plus two steps of travel: not yet.
            Vec3 far = new Vec3(5f - BallRadius - 0.03f - 0.3f * 0.04f - 0.005f, 0f, 1.5f);
            Assert.False(BallTransit.Approach(A, MaskA, far, slow, NoGravity, BallRadius, 0.04f, Settings, float.NegativeInfinity, 0.03f).Touches);

            // A fast ball is unchanged apart from being seen a little sooner: same touch, same crossing.
            BallApproach plain = BallTransit.Approach(A, MaskA, new Vec3(4f, 0f, 1.5f), new Vec3(10f, 0f, 0f), NoGravity, BallRadius, 1f, Settings);
            BallApproach near = BallTransit.Approach(A, MaskA, new Vec3(4f, 0f, 1.5f), new Vec3(10f, 0f, 0f), NoGravity, BallRadius, 1f, Settings, float.NegativeInfinity, 0.03f);
            Assert.Equal(plain.TouchTime, near.TouchTime);
            Assert.Equal(plain.CrossTime, near.CrossTime);
            Assert.Equal(plain.Enters, near.Enters);
        }

        [Fact]
        public void BallMovingAwayOrBehindTheWallNeverTouches()
        {
            Assert.False(BallTransit.Approach(A, MaskA, new Vec3(4.99f, 0f, 1.5f), new Vec3(-3f, 0f, 0f), NoGravity, BallRadius, 1f, Settings).Touches);
            Assert.False(BallTransit.Approach(A, MaskA, new Vec3(5.02f, 0f, 1.5f), new Vec3(3f, 0f, 0f), NoGravity, BallRadius, 1f, Settings).Touches);
            // Sliding along the wall, touching it: no crossing.
            Assert.False(BallTransit.Approach(A, MaskA, new Vec3(4.98f, 0f, 1.5f), new Vec3(0f, 2f, 0f), NoGravity, BallRadius, 1f, Settings).Touches);
        }

        [Fact]
        public void BallThatClipsTheRimDoesNotEnter()
        {
            // Centre crosses 0.48 m from the patch centre: on paint, but the 3.5 cm ball sticks out past 0.5 m.
            Vec3 start = new Vec3(4.5f, -0.48f, 1.5f);
            BallApproach a = BallTransit.Approach(A, MaskA, start, new Vec3(10f, 0f, 0f), NoGravity, BallRadius, 1f, Settings);
            Assert.True(a.Touches);
            Assert.True(a.TouchPainted);
            Assert.False(a.Footprint.Fits);
            Assert.True(a.Footprint.PaintedSamples > 0);
            Assert.False(a.Enters);

            // 0.46 m out it fits (0.46 + 0.035 < 0.5).
            Assert.True(BallTransit.Approach(A, MaskA, new Vec3(4.5f, -0.46f, 1.5f), new Vec3(10f, 0f, 0f), NoGravity, BallRadius, 1f, Settings).Enters);
            // Bare wall, and the wrong colour of paint: touches, does not enter.
            Assert.False(BallTransit.Approach(A, MaskA, new Vec3(4.5f, -1.5f, 1.5f), new Vec3(10f, 0f, 0f), NoGravity, BallRadius, 1f, Settings).Enters);
            var blueThere = new DiscMask(A.Wall, PaintColor.Blue, new Vec2(2f, 1.5f), 0.5f);
            Assert.False(BallTransit.Approach(A, blueThere, new Vec3(4f, 0f, 1.5f), new Vec3(10f, 0f, 0f), NoGravity, BallRadius, 1f, Settings).Enters);
        }

        [Fact]
        public void GlancingBallMustStayOnPaintAllTheWayThrough()
        {
            // Coming in at a shallow angle: 1 m/s into the wall, 8 m/s along it. The centre crosses just
            // inside the patch (0.44 m out), but it first touched the wall 0.28 m further back, on bare wall.
            Vec3 velocity = new Vec3(1f, 8f, 0f);
            Vec3 start = new Vec3(4.9f, -0.44f - 0.8f, 1.5f);
            BallApproach a = BallTransit.Approach(A, MaskA, start, velocity, NoGravity, BallRadius, 1f, Settings);
            Assert.True(a.Touches);
            Rig.Near(new Vec2(2f + 0.44f, 1.5f), a.CrossUV);
            Assert.True(a.Footprint.Fits);
            Assert.False(a.TouchPainted);
            Assert.True(a.ClearPainted);
            Assert.False(a.Enters);

            // The same path the other way along the wall: it touches on paint but is still part-way
            // through when it runs off the far side of the patch.
            BallApproach b = BallTransit.Approach(A, MaskA, new Vec3(4.9f, 0.44f - 0.8f, 1.5f), velocity, NoGravity, BallRadius, 1f, Settings);
            Assert.True(b.TouchPainted);
            Assert.True(b.Footprint.Fits);
            Assert.False(b.ClearPainted);
            Assert.False(b.Enters);
        }

        [Fact]
        public void GravityBendsThePredictedPath()
        {
            // Thrown level from 2 m away at 5 m/s: 0.4 s to the wall, dropping 0.785 m on the way.
            BallApproach a = BallTransit.Approach(A, MaskA, new Vec3(3f, 0f, 2.2f), new Vec3(5f, 0f, 0f), Gravity, BallRadius, 1f, Settings);
            Assert.True(a.Touches);
            Assert.Equal(0.4f, a.CrossTime, 4);
            Rig.Near(new Vec2(2f, 2.2f - 0.5f * 9.81f * 0.16f), a.CrossUV, 3);
            Assert.True(a.Enters);
        }

        [Fact]
        public void BallRollingOnTheFloorCanEnterAPatchThatReachesTheFloor()
        {
            // A strip of orange from the floor up, and a ball rolling along the floor into it.
            var strip = new RectMask(A.Wall, PaintColor.Orange, new Vec2(1.5f, 0f), new Vec2(2.5f, 1.5f));
            Vec3 start = new Vec3(4.9f, 0f, BallRadius);
            Vec3 roll = new Vec3(2f, 0f, 0f);
            // Free flight alone would sink it into the floor before it arrives: its footprint leaves the wall.
            Assert.False(BallTransit.Approach(A, strip, start, roll, Gravity, BallRadius, 1f, Settings).Enters);
            // Told where the floor is, it stays on it and goes in.
            BallApproach a = BallTransit.Approach(A, strip, start, roll, Gravity, BallRadius, 1f, Settings, 0f + BallRadius);
            Assert.True(a.Enters);
            Rig.Near(new Vec2(2f, BallRadius), a.CrossUV);
        }

        [Fact]
        public void TimeToDistanceHandlesGravityAlongTheNormal()
        {
            // Falling onto a floor from 1 m at rest: t = sqrt(2 / 9.81).
            Assert.Equal(MathF.Sqrt(2f / 9.81f), BallTransit.TimeToDistance(1f, 0f, -9.81f, 0f), 4);
            // Thrown up at 3 m/s from 1 m: comes back down through 1 m at t = 2 * 3 / 9.81, on to 0 later.
            float t = BallTransit.TimeToDistance(1f, 3f, -9.81f, 0f);
            Assert.Equal(0f, 1f + 3f * t - 0.5f * 9.81f * t * t, 3);
            Assert.True(t > 2f * 3f / 9.81f);
            // Moving toward the wall but slowing and never arriving.
            Assert.True(float.IsInfinity(BallTransit.TimeToDistance(1f, -1f, 9.81f, 0f)));
            // No gravity: plain distance over speed; moving away never arrives.
            Assert.Equal(0.25f, BallTransit.TimeToDistance(1f, -4f, 0f, 0f), 5);
            Assert.True(float.IsInfinity(BallTransit.TimeToDistance(1f, 4f, 0f, 0f)));
        }

        [Fact]
        public void FootprintWhilePartWayThroughUsesTheCutCircle()
        {
            Assert.Equal(0f, BallTransit.CutRadius(BallRadius, 0.05f));
            Assert.Equal(BallRadius, BallTransit.CutRadius(BallRadius, 0f), 6);
            Assert.Equal(MathF.Sqrt(BallRadius * BallRadius - 0.02f * 0.02f), BallTransit.CutRadius(BallRadius, -0.02f), 6);

            // 0.48 m from the centre: the full ball does not fit, but 3 cm before the plane its cut circle
            // (radius 1.8 cm) is still inside the paint; on the plane it is not.
            Assert.True(BallTransit.FootprintOnPaint(A, MaskA, new Vec3(4.97f, -0.48f, 1.5f), BallRadius, Settings));
            Assert.False(BallTransit.FootprintOnPaint(A, MaskA, new Vec3(5.0f, -0.48f, 1.5f), BallRadius, Settings));
            // Not reaching the plane at all counts as fine.
            Assert.True(BallTransit.FootprintOnPaint(A, MaskA, new Vec3(4.5f, -3f, 1.5f), BallRadius, Settings));
        }

        [Fact]
        public void CarryIsThePlainCrossingWhenTheExitHasPaintThere()
        {
            // In through the middle of orange: out of the middle of blue, no slide.
            PortalCarry c = BallTransit.Carry(Pair, true, false, MaskB, new Vec3(5f, 0f, 1.5f), BallRadius, Settings);
            Assert.Equal(0f, c.MovedBy);
            Assert.True(c.LandedOnPaint);
            Rig.Near(new Vec3(2f, 3f, 2.0f), c.Transform.TransformPoint(new Vec3(5f, 0f, 1.5f)));
            // Going +X into the far wall comes out of the left wall heading -Y, same speed.
            Vec3 outV = c.Transform.TransformDirection(new Vec3(7f, 1f, -2f));
            Rig.Near(new Vec3(1f, -7f, -2f), outV);
            Assert.Equal(new Vec3(7f, 1f, -2f).Length(), outV.Length(), 4);
        }

        [Fact]
        public void CarrySlidesIntoTheExitPatchOnlyWhenItWouldLandOnBareWall()
        {
            // In 0.45 m to the right of orange's centre (orange is 0.5 m wide there). Blue is only 0.4 m:
            // the rigid crossing lands 0.45 m from blue's centre, on bare wall, so it is slid in to
            // 0.4 - 0.035 = 0.365 m: moved 0.085 m. Direction is untouched.
            Vec3 entry = new Vec3(5f, -0.45f, 1.5f);
            PortalCarry c = BallTransit.Carry(Pair, true, false, MaskB, entry, BallRadius, Settings);
            Assert.False(c.LandedOnPaint);
            Assert.True(c.Fits);
            Assert.Equal(0.085f, c.MovedBy, 4);
            Vec3 outPoint = c.Transform.TransformPoint(entry);
            Assert.Equal(0f, B.Wall.SignedDistance(outPoint), 4);
            Assert.Equal(0.365f, Vec2.Distance(B.Wall.ToUV(outPoint), new Vec2(2f, 2.0f)), 4);
            Rig.Near(Pair.AToB.TransformDirection(new Vec3(7f, 1f, -2f)), c.Transform.TransformDirection(new Vec3(7f, 1f, -2f)));

            // The other way round blue's 0.3 m maps inside orange's 0.5 m: nothing moves.
            PortalCarry back = BallTransit.Carry(Pair, false, false, MaskA, B.Wall.ToWorld(new Vec2(2.3f, 2.0f)), BallRadius, Settings);
            Assert.Equal(0f, back.MovedBy);
            Assert.True(back.LandedOnPaint);
        }

        [Fact]
        public void ThroughAndStraightBackIsWhereItStarted()
        {
            PortalCarry there = BallTransit.Carry(Pair, true, false, MaskB, new Vec3(5f, 0.1f, 1.6f), BallRadius, Settings);
            Vec3 p = new Vec3(5.02f, 0.1f, 1.6f);
            Vec3 q = there.Transform.TransformPoint(p);
            // A ball that has just come out of B is on B's room side moving away: no entry at B.
            Assert.True(B.Wall.SignedDistance(q) > 0f);
            Vec3 vOut = there.Transform.TransformDirection(new Vec3(6f, 0f, 0f));
            Assert.False(BallTransit.Approach(B, MaskB, q, vOut, NoGravity, BallRadius, 1f, Settings).Touches);
            // Reversed, it goes back in through B and comes out of A where it started.
            PortalCarry back = BallTransit.Carry(Pair, false, false, MaskA, B.Wall.ToWorld(B.Wall.ToUV(q)), BallRadius, Settings);
            Rig.Near(p, back.Transform.TransformPoint(q));
        }

        // ------------------------------------------------------------------ the player

        private const float Head = 0.10f;
        private const float Clear = 0.20f;

        [Fact]
        public void WallsAreSolidAwayFromThePatch()
        {
            // Walking +X at head height 1.5, 1.5 m to the side of the patch: stopped 0.2 m from the wall.
            PlayerStep s = PlayerPassage.Step(true, Pair, MaskA, MaskB, Walls, 3, new Vec3(4.7f, -1.5f, 1.5f), new Vec3(4.95f, -1.5f, 1.5f), Head, Clear, 0.05f, Settings);
            Assert.False(s.Crossed);
            Assert.True(s.Blocked);
            Rig.Near(new Vec3(4.8f, -1.5f, 1.5f), s.Head);

            // Even a step that would end behind the wall is brought back.
            s = PlayerPassage.Step(true, Pair, MaskA, MaskB, Walls, 3, new Vec3(4.8f, -1.5f, 1.5f), new Vec3(5.3f, -1.5f, 1.5f), Head, Clear, 0.05f, Settings);
            Assert.False(s.Crossed);
            Rig.Near(new Vec3(4.8f, -1.5f, 1.5f), s.Head);

            // With nothing linked the painted spot is solid too.
            s = PlayerPassage.Step(false, default, null, null, Walls, 3, new Vec3(4.8f, 0f, 1.5f), new Vec3(5.1f, 0f, 1.5f), Head, Clear, 0.05f, Settings);
            Assert.False(s.Crossed);
            Rig.Near(new Vec3(4.8f, 0f, 1.5f), s.Head);

            // In the open room nothing is touched.
            s = PlayerPassage.Step(true, Pair, MaskA, MaskB, Walls, 3, new Vec3(2f, 0f, 1.5f), new Vec3(2.2f, 0.1f, 1.5f), Head, Clear, 0.05f, Settings);
            Assert.False(s.Blocked);
            Rig.Near(new Vec3(2.2f, 0.1f, 1.5f), s.Head);
        }

        [Fact]
        public void ThePatchLetsTheHeadUpToThePlaneAndThrough()
        {
            // In front of the patch the wall does not push: the head may come right up to it.
            PlayerStep near = PlayerPassage.Step(true, Pair, MaskA, MaskB, Walls, 3, new Vec3(4.85f, 0f, 1.5f), new Vec3(4.97f, 0f, 1.5f), Head, Clear, 0.05f, Settings);
            Assert.False(near.Crossed);
            Assert.False(near.Blocked);
            Rig.Near(new Vec3(4.97f, 0f, 1.5f), near.Head);

            // The next step crosses the plane: carried to the blue patch on the left wall.
            PlayerStep s = PlayerPassage.Step(true, Pair, MaskA, MaskB, Walls, 3, new Vec3(4.97f, 0f, 1.5f), new Vec3(5.03f, 0f, 1.5f), Head, Clear, 0.05f, Settings);
            Assert.True(s.Crossed);
            Assert.True(s.FromA);
            Assert.Equal(0f, s.ExitMovedBy);
            Rig.Near(new Vec2(2f, 1.5f), s.EntryUV);
            // 3 cm past orange's plane = 3 cm out of blue's wall, at blue's centre height (0.5 m higher).
            Rig.Near(new Vec3(2f, 2.97f, 2.0f), s.Head);
            Rig.Near(Pair.PlayerAToB.TransformPoint(new Vec3(5.03f, 0f, 1.5f)), s.Head);
            // Facing +X before, facing -Y after.
            Rig.Near(new Vec3(0f, -1f, 0f), s.Crossing.TransformDirection(new Vec3(1f, 0f, 0f)));

            // Standing just out of blue the player is in blue's mouth, not pushed; walking on is free.
            PlayerStep after = PlayerPassage.Step(true, Pair, MaskA, MaskB, Walls, 3, s.Head, s.Head + new Vec3(0f, -0.05f, 0f), Head, Clear, 0.05f, Settings);
            Assert.False(after.Crossed);
            Assert.False(after.Blocked);

            // Stepping back into blue returns to orange.
            PlayerStep back = PlayerPassage.Step(true, Pair, MaskA, MaskB, Walls, 3, s.Head, s.Head + new Vec3(0f, 0.05f, 0f), Head, Clear, 0.05f, Settings);
            Assert.True(back.Crossed);
            Assert.False(back.FromA);
            Rig.Near(new Vec3(4.98f, 0f, 1.5f), back.Head);
        }

        [Fact]
        public void HeadThatClipsTheRimIsHeldBack()
        {
            // 0.45 m from the centre of a 0.5 m patch: the head's centre is on paint but its 0.1 m circle is not.
            PlayerStep s = PlayerPassage.Step(true, Pair, MaskA, MaskB, Walls, 3, new Vec3(4.8f, -0.45f, 1.5f), new Vec3(5.05f, -0.45f, 1.5f), Head, Clear, 0.05f, Settings);
            Assert.False(s.Crossed);
            Assert.True(s.Blocked);
            Rig.Near(new Vec3(4.8f, -0.45f, 1.5f), s.Head);
        }

        [Fact]
        public void SteppingSidewaysOutOfAPortalMouthEasesOutInsteadOfPopping()
        {
            // 5 cm from the wall inside the mouth, then a sideways step to bare wall: moved out by 2 cm a frame.
            Vec3 inMouth = new Vec3(4.95f, -0.35f, 1.5f);
            PlayerStep s = PlayerPassage.Step(true, Pair, MaskA, MaskB, Walls, 3, inMouth, new Vec3(4.95f, -0.75f, 1.5f), Head, Clear, 0.02f, Settings);
            Assert.True(s.Blocked);
            Rig.Near(new Vec3(4.93f, -0.75f, 1.5f), s.Head);
            // Left alone it reaches the full clearance and stays there.
            Vec3 head = s.Head;
            for (int i = 0; i < 20; i++)
            {
                head = PlayerPassage.Step(true, Pair, MaskA, MaskB, Walls, 3, head, head, Head, Clear, 0.02f, Settings).Head;
            }
            Rig.Near(new Vec3(4.8f, -0.75f, 1.5f), head);
        }

        [Fact]
        public void PlayerExitIsSlidIntoASmallerPatch()
        {
            // In 0.38 m right of orange's centre: fits orange (0.38 + 0.1 < 0.5). Blue is 0.4 m, so the head
            // would land 0.38 m out with no room: slid in to 0.4 - 0.1 = 0.3 m.
            PlayerStep s = PlayerPassage.Step(true, Pair, MaskA, MaskB, Walls, 3, new Vec3(4.97f, -0.38f, 1.5f), new Vec3(5.03f, -0.38f, 1.5f), Head, Clear, 0.05f, Settings);
            Assert.True(s.Crossed);
            Assert.Equal(0.08f, s.ExitMovedBy, 4);
            Assert.Equal(0.3f, Vec2.Distance(B.Wall.ToUV(s.Head), new Vec2(2f, 2.0f)), 4);
        }

        [Fact]
        public void FallIsSteadyAndLandsExactly()
        {
            var fall = new PlayerFall();
            float h = 1.0f;
            float t = 0f;
            float previous = h;
            const float dt = 1f / 90f;
            int steps = 0;
            while (h > 0f && steps < 1000)
            {
                h = fall.Step(h, dt);
                Assert.True(h <= previous);                       // never back up
                Assert.True(previous - h <= fall.MaxFallSpeed * dt + 1e-5f);
                previous = h;
                t += dt;
                steps++;
            }
            Assert.Equal(0f, h);
            Assert.False(fall.Falling);
            Assert.Equal(0f, fall.Speed);
            // A 1 m drop under gravity takes sqrt(2 / 9.81) = 0.45 s (the 6 m/s limit is not reached).
            Assert.InRange(t, 0.43f, 0.48f);
            // Standing still on the floor stays put.
            Assert.Equal(0f, fall.Step(0f, dt));

            // A long drop is capped at the top speed.
            fall.Reset();
            h = 50f;
            for (int i = 0; i < 300; i++) h = fall.Step(h, dt);
            Assert.Equal(fall.MaxFallSpeed, fall.Speed, 4);
        }

        [Fact]
        public void ArrivingBelowTheFloorIsLifted()
        {
            var fall = new PlayerFall { LiftSpeed = 3f };
            float h = -0.5f;
            float t = 0f;
            while (h < 0f && t < 2f)
            {
                h = fall.Step(h, 1f / 90f);
                t += 1f / 90f;
            }
            Assert.Equal(0f, h);
            Assert.InRange(t, 0.16f, 0.19f);       // 0.5 m at 3 m/s
            Assert.False(fall.Lifting);

            // Lift speed 0 = at once.
            var snap = new PlayerFall { LiftSpeed = 0f };
            Assert.Equal(0f, snap.Step(-0.5f, 1f / 90f));

            // Agrees with PlayerArrival: 0.5 m to lift.
            Assert.Equal(0.5f, PlayerArrival.Check(new Vec3(0f, 0f, -0.5f), 0f).LiftNeeded, 5);
        }

        // ------------------------------------------------------------------ the play space

        [Fact]
        public void StickMovesRelativeToTheHead()
        {
            // Facing +X: forward is +X, right is -Y.
            Rig.Near(new Vec3(0.2f, 0f, 0f), PlaySpace.StickMove(new Vec2(0f, 1f), 0f, 2f, 0.1f, 0.15f));
            Rig.Near(new Vec3(0f, -0.2f, 0f), PlaySpace.StickMove(new Vec2(1f, 0f), 0f, 2f, 0.1f, 0.15f));
            // Facing +Y (turned left a quarter): forward is +Y, right is +X.
            Rig.Near(new Vec3(0f, 0.2f, 0f), PlaySpace.StickMove(new Vec2(0f, 1f), MathF.PI / 2f, 2f, 0.1f, 0.15f));
            Rig.Near(new Vec3(0.2f, 0f, 0f), PlaySpace.StickMove(new Vec2(1f, 0f), MathF.PI / 2f, 2f, 0.1f, 0.15f));
            // Inside the dead zone: nothing. Half way between dead zone and full: half speed.
            Rig.Near(Vec3.Zero, PlaySpace.StickMove(new Vec2(0.1f, 0.05f), 0f, 2f, 0.1f, 0.15f));
            Rig.Near(new Vec3(0.1f, 0f, 0f), PlaySpace.StickMove(new Vec2(0f, 0.575f), 0f, 2f, 0.1f, 0.15f));
            // Never faster than full speed, and never up or down.
            Vec3 m = PlaySpace.StickMove(new Vec2(1f, 1f), 0.3f, 2f, 0.1f, 0.15f);
            Assert.Equal(0.2f, m.Length(), 4);
            Assert.Equal(0f, m.Z);
        }

        [Fact]
        public void YawOfReadsTheHeadsFacing()
        {
            Assert.Equal(0f, PlaySpace.YawOf(Quat.Identity), 5);
            Assert.Equal(MathF.PI / 2f, PlaySpace.YawOf(Quat.CreateFromAxisAngle(Units.Up, MathF.PI / 2f)), 5);
            // Looking 40 degrees down while turned 30 degrees left: still 30 degrees.
            Quat q = Quat.CreateFromAxisAngle(Units.Up, MathF.PI / 6f) * Quat.CreateFromAxisAngle(new Vec3(0f, 1f, 0f), 40f * MathF.PI / 180f);
            Assert.Equal(MathF.PI / 6f, PlaySpace.YawOf(q), 4);
        }

        [Fact]
        public void SnapTurnPivotsOnTheHead()
        {
            // Play space centred at (1, 1, 0); the head is 0.5 m forward of it at (1.5, 1, 1.7).
            var anchor = new RigidTransform(Quat.Identity, new Vec3(1f, 1f, 0f));
            Vec3 headLocal = new Vec3(0.5f, 0f, 1.7f);
            Vec3 head = anchor.TransformPoint(headLocal);

            // One 30 degree turn to the right (negative yaw).
            RigidTransform turned = anchor.Then(PlaySpace.TurnAbout(head, -30f * MathF.PI / 180f));
            Rig.Near(head, turned.TransformPoint(headLocal));                   // the head has not moved
            Vec3 forward = turned.TransformDirection(new Vec3(1f, 0f, 0f));
            Assert.Equal(-30f * MathF.PI / 180f, MathF.Atan2(forward.Y, forward.X), 4);
            Assert.Equal(0f, turned.Translation.Z, 5);                          // the floor stays the floor

            // Twelve of them come back to the start.
            RigidTransform t = anchor;
            for (int i = 0; i < 12; i++) t = t.Then(PlaySpace.TurnAbout(t.TransformPoint(headLocal), -30f * MathF.PI / 180f));
            Rig.Near(anchor.Translation, t.Translation, 3);
        }

        [Fact]
        public void SnapTurnLatchGivesOneTurnPerPush()
        {
            var latch = new SnapTurnLatch();
            int turns = 0;
            float[] xs = { 0f, 0.3f, 0.7f, 0.9f, 1f, 0.8f, 0.5f, 0.7f, 0.2f, 0f, -0.8f, -0.9f, -0.1f, 0.65f };
            int[] expected = { 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, -1, 0, 0, 1 };
            for (int i = 0; i < xs.Length; i++)
            {
                int r = latch.Update(xs[i]);
                Assert.Equal(expected[i], r);
                turns += Math.Abs(r);
            }
            Assert.Equal(3, turns);
        }

        [Fact]
        public void CarryingThePlaySpaceThroughKeepsHandsWhereTheyWereOnThePlayer()
        {
            // Anchor in the room, head just through orange. Apply the player crossing to the anchor.
            var anchor = new RigidTransform(Quat.CreateFromAxisAngle(Units.Up, 0.2f), new Vec3(4.2f, 0.1f, 0f));
            Vec3 headLocal = new Vec3(0.8f, -0.25f, 1.5f);
            Vec3 handLocal = new Vec3(0.6f, -0.5f, 1.2f);
            Vec3 headBefore = anchor.TransformPoint(headLocal);

            RigidTransform crossing = Pair.PlayerAToB;
            RigidTransform after = anchor.Then(crossing);
            // The head is where the crossing puts it, and the hand is still the same offset from the head
            // as measured in the player's own play space.
            Rig.Near(crossing.TransformPoint(headBefore), after.TransformPoint(headLocal));
            Vec3 offsetBefore = anchor.TransformPoint(handLocal) - headBefore;
            Vec3 offsetAfter = after.TransformPoint(handLocal) - after.TransformPoint(headLocal);
            Rig.Near(crossing.TransformDirection(offsetBefore), offsetAfter);
            Assert.Equal(offsetBefore.Length(), offsetAfter.Length(), 4);
            // Paint-centre anchoring: blue's centre is 0.5 m higher than orange's, so the play-space floor
            // arrives 0.5 m above the room floor and the player falls that far.
            PlayerArrival arrival = PlayerArrival.Check(after.Translation, 0f);
            Assert.Equal(0.5f, arrival.FallDistance, 4);
        }
    }
}
