using System;
using Xunit;

namespace Portaballs.Core.Tests
{
    /// <summary>A hand at a portal: is it in the opening, is it through, and where does a ball start that is let go there (PortalReach).</summary>
    public class PortalReachTests
    {
        private const float BallRadius = 0.035f;
        private const float Clearance = 0.03f;
        private const float Rim = PortalReach.OpeningRim;
        private const float HandReach = 0.12f;
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

        private static ReachState Find(Vec3 p)
        {
            return PortalReach.Find(Pair, MaskA, MaskB, p, Rim, HandReach, PortalReach.ArmReach, false, Settings);
        }

        private static ReachState Update(ref ReachLatch latch, Vec3 p)
        {
            return PortalReach.Update(ref latch, Pair, MaskA, MaskB, p, Rim, HandReach, PortalReach.ArmReach, false, Settings);
        }

        // ------------------------------------------------------------------ is the hand in the opening

        [Fact]
        public void HandInTheMiddleOfTheRoomIsNotAtAPortal()
        {
            ReachState s = Find(new Vec3(2.5f, 0f, 1.5f));
            Assert.False(s.AtPortal);
            Assert.False(s.Through);
        }

        [Fact]
        public void HandJustInFrontOfTheOpeningIsAtItButNotThrough()
        {
            ReachState s = Find(new Vec3(4.95f, 0.1f, 1.6f));
            Assert.True(s.AtPortal);
            Assert.True(s.FromA);
            Assert.False(s.Through);
            Assert.Equal(0.05f, s.Depth, 4);
        }

        [Fact]
        public void HandPushedThroughTheOpeningIsThrough()
        {
            ReachState s = Find(new Vec3(5.2f, 0.1f, 1.6f));
            Assert.True(s.Through);
            Assert.True(s.FromA);
            Assert.Equal(-0.2f, s.Depth, 4);
        }

        [Fact]
        public void HandPushedIntoBareWallGetsNothing()
        {
            // Same depths, a metre to the side of the orange patch: bare wall.
            Assert.False(Find(new Vec3(4.95f, 1.2f, 1.5f)).AtPortal);
            Assert.False(Find(new Vec3(5.2f, 1.2f, 1.5f)).AtPortal);
        }

        [Fact]
        public void HandOnThePaintRimIsNotInTheOpening()
        {
            // Blue is a 0.4 m disc. 0.38 m from its centre: on paint, but inside the 4 cm rim that is drawn as paint.
            Assert.False(Find(new Vec3(2.38f, 3.1f, 2.0f)).AtPortal);
            // 0.34 m out: inside the opening.
            Assert.True(Find(new Vec3(2.34f, 3.1f, 2.0f)).AtPortal);
        }

        [Fact]
        public void PlainPaintWithNoPaintWhereItComesOutIsNotTheOpening()
        {
            // The blue disc is 0.4 m, the orange one 0.5 m: 0.44 m from the orange centre comes out on bare
            // wall beside the blue patch. Paint here, no paint there: not open, as on screen.
            ReachState s = Find(new Vec3(5.1f, 0f, 1.5f + 0.44f));
            Assert.False(s.AtPortal);
            // From the blue side everything inside its own rim comes out on orange paint.
            Assert.True(Find(new Vec3(2f, 3.1f, 2.0f + 0.34f)).AtPortal);
        }

        [Fact]
        public void TooFarInFrontOrTooFarBehindDoesNotCount()
        {
            Assert.False(Find(new Vec3(5f - HandReach - 0.01f, 0f, 1.5f)).AtPortal);
            Assert.True(Find(new Vec3(5f + PortalReach.ArmReach - 0.01f, 0f, 1.5f)).AtPortal);
            Assert.False(Find(new Vec3(5f + PortalReach.ArmReach + 0.01f, 0f, 1.5f)).AtPortal);
        }

        [Fact]
        public void BlueEndIsFoundTheSameWay()
        {
            ReachState s = Find(new Vec3(2f, 3.15f, 2.0f));
            Assert.True(s.Through);
            Assert.False(s.FromA);
            Assert.Equal(-0.15f, s.Depth, 4);
        }

        // ------------------------------------------------------------------ where the twin goes

        [Fact]
        public void TwinOfAHandThroughOrangeSticksOutOfBlueByTheSameDepth()
        {
            Vec3 hand = new Vec3(5.2f, 0.1f, 1.6f);   // 0.2 m through orange
            RigidTransform crossing = PortalReach.Crossing(Pair, true, false);
            Vec3 twin = crossing.TransformPoint(hand);
            Assert.Equal(0.2f, B.Wall.SignedDistance(twin), 4);   // 0.2 m out of the blue wall
            // Up the wall is kept; sideways swaps sign (the hand's right stays its right).
            Vec2 inUV = A.Wall.ToUV(hand) - A.Opening.CenterUV;
            Vec2 outUV = B.Wall.ToUV(twin) - B.Opening.CenterUV;
            Assert.Equal(-inUV.X, outUV.X, 4);
            Assert.Equal(inUV.Y, outUV.Y, 4);
        }

        [Fact]
        public void BothCrossingsAreTheSameForStandingWalls()
        {
            Vec3 hand = new Vec3(5.2f, 0.1f, 1.6f);
            Rig.Near(PortalReach.Crossing(Pair, true, false).TransformPoint(hand), PortalReach.Crossing(Pair, true, true).TransformPoint(hand));
            Rig.Near(PortalReach.Crossing(Pair, false, false).TransformPoint(hand), PortalReach.Crossing(Pair, false, true).TransformPoint(hand));
        }

        // ------------------------------------------------------------------ the latch

        [Fact]
        public void HandThroughMayReachSidewaysPastTheOutlineAndStaysThrough()
        {
            var latch = new ReachLatch();
            Assert.True(Update(ref latch, new Vec3(5.2f, 0f, 1.5f)).Through);
            Assert.True(latch.Through);

            // 0.3 m deep, 0.9 m to the side: far outside the opening's outline, but the hand is in the far room.
            ReachState s = Update(ref latch, new Vec3(5.3f, 0.9f, 1.5f));
            Assert.True(s.Through);
            Assert.True(s.FromA);
            Assert.Equal(-0.3f, s.Depth, 4);

            // With nothing remembered the same place is just "behind bare wall".
            Assert.False(Find(new Vec3(5.3f, 0.9f, 1.5f)).AtPortal);
        }

        [Fact]
        public void LatchLetsGoWhenTheHandComesBackToTheRoomSide()
        {
            var latch = new ReachLatch();
            Update(ref latch, new Vec3(5.2f, 0f, 1.5f));
            Update(ref latch, new Vec3(5.3f, 0.9f, 1.5f));

            // Pulled back through the bare wall beside the patch: no longer through, and not at the opening.
            ReachState s = Update(ref latch, new Vec3(4.9f, 0.9f, 1.5f));
            Assert.False(s.AtPortal);
            Assert.False(latch.Through);

            // Pulled back out through the opening itself: still at the portal (it straddles), not through.
            latch = new ReachLatch();
            Update(ref latch, new Vec3(5.2f, 0f, 1.5f));
            s = Update(ref latch, new Vec3(4.97f, 0f, 1.5f));
            Assert.True(s.AtPortal);
            Assert.False(s.Through);
            Assert.False(latch.Through);
        }

        [Fact]
        public void TrailingHandIsThroughTheExitAfterThePlayerCrosses()
        {
            // The head went in through orange. A hand that was 0.25 m in front of orange is carried with the
            // play space to 0.25 m BEHIND blue: it is still in the orange room, reaching back.
            Vec3 before = new Vec3(4.75f, 0.1f, 1.4f);
            Vec3 after = Pair.PlayerCrossing(true).TransformPoint(before);
            Assert.Equal(-0.25f, B.Wall.SignedDistance(after), 4);

            var latch = new ReachLatch();
            PortalReach.PlayerCrossed(ref latch, Pair, true, after, PortalReach.ArmReach);
            Assert.True(latch.Through);
            Assert.False(latch.FromA);
            ReachState s = Update(ref latch, after);
            Assert.True(s.Through);
            // Its twin is back where the hand was.
            Rig.Near(before, PortalReach.Crossing(Pair, false, false).TransformPoint(after));
        }

        [Fact]
        public void LeadingHandIsSimplyInTheNewRoomAfterThePlayerCrosses()
        {
            var latch = new ReachLatch();
            Vec3 before = new Vec3(5.3f, 0.1f, 1.4f);   // 0.3 m through orange, ahead of the head
            Assert.True(Update(ref latch, before).Through);

            Vec3 after = Pair.PlayerCrossing(true).TransformPoint(before);
            PortalReach.PlayerCrossed(ref latch, Pair, true, after, PortalReach.ArmReach);
            Assert.False(latch.Through);
            Assert.False(Update(ref latch, after).AtPortal);   // 0.3 m out in front of blue: an ordinary hand
        }

        // ------------------------------------------------------------------ letting go

        [Fact]
        public void LetGoFarFromAnyPortalIsAnOrdinaryThrow()
        {
            Vec3 p = new Vec3(2.5f, 0f, 1.5f), v = new Vec3(6f, 1f, 2f), spin = new Vec3(0f, 3f, 0f);
            HeldLaunch l = PortalReach.Launch(new ReachLatch(), Pair, MaskA, MaskB, p, v, spin, BallRadius, Clearance, PortalReach.ArmReach, Settings);
            Assert.False(l.AtPortal);
            Assert.False(l.Carried);
            Assert.Equal(p, l.Position);
            Assert.Equal(v, l.Velocity);
            Assert.Equal(spin, l.Spin);
        }

        [Fact]
        public void LetGoThroughOrangeStartsOutOfBlueWithTheSpeedKeptAndTheDirectionMapped()
        {
            Vec3 p = new Vec3(5.25f, 0.1f, 1.6f);          // 0.25 m through orange
            Vec3 v = new Vec3(4f, -1f, 2f);                // going on into the wall, to the right, and up
            HeldLaunch l = PortalReach.Launch(new ReachLatch(), Pair, MaskA, MaskB, p, v, new Vec3(0f, 3f, 0f), BallRadius, Clearance, PortalReach.ArmReach, Settings);
            Assert.True(l.AtPortal);
            Assert.True(l.Carried);
            Assert.True(l.FromA);
            Assert.Equal(-0.25f, l.Depth, 4);

            // It starts 0.25 m out in front of the blue wall, exactly where a ball in flight would be carried.
            Assert.Equal(0.25f, B.Wall.SignedDistance(l.Position), 4);
            Rig.Near(Pair.AToB.TransformPoint(p), l.Position);
            Rig.Near(Pair.AToB.TransformDirection(v), l.Velocity);
            Assert.Equal(v.Length(), l.Velocity.Length(), 4);

            // Said with the walls' own axes: into the entry wall = out of the exit wall, sideways swaps, up stays.
            Assert.Equal(-Vec3.Dot(v, A.Wall.Normal), Vec3.Dot(l.Velocity, B.Wall.Normal), 4);
            Assert.Equal(-Vec3.Dot(v, A.Wall.U), Vec3.Dot(l.Velocity, B.Wall.U), 4);
            Assert.Equal(Vec3.Dot(v, A.Wall.V), Vec3.Dot(l.Velocity, B.Wall.V), 4);
            Assert.Equal(3f, l.Spin.Length(), 4);
        }

        [Fact]
        public void LetGoWithALatchedHandFarToTheSideIsStillCarried()
        {
            var latch = new ReachLatch { Through = true, FromA = true };
            Vec3 p = new Vec3(5.3f, 0.9f, 1.5f);
            HeldLaunch l = PortalReach.Launch(latch, Pair, MaskA, MaskB, p, new Vec3(0f, 0f, 1f), Vec3.Zero, BallRadius, Clearance, PortalReach.ArmReach, Settings);
            Assert.True(l.Carried);
            Rig.Near(Pair.AToB.TransformPoint(p), l.Position);
            // Without the latch the same let-go is a ball behind bare wall: left alone.
            l = PortalReach.Launch(new ReachLatch(), Pair, MaskA, MaskB, p, new Vec3(0f, 0f, 1f), Vec3.Zero, BallRadius, Clearance, PortalReach.ArmReach, Settings);
            Assert.False(l.AtPortal);
        }

        [Fact]
        public void BallLetGoWellThroughIsClearOfTheExitWallAndLandsWhereTheMappedPathSays()
        {
            Vec3 p = new Vec3(5.25f, 0.1f, 1.6f);
            Vec3 v = new Vec3(3f, -0.5f, 1f);
            HeldLaunch l = PortalReach.Launch(new ReachLatch(), Pair, MaskA, MaskB, p, v, Vec3.Zero, BallRadius, Clearance, PortalReach.ArmReach, Settings);
            var ball = new MouthBall { Position = l.Position, Velocity = l.Velocity, Spin = l.Spin };
            Assert.Equal(MouthOutcome.Clear, PortalReach.Where(ball, Pair, MaskA, MaskB, BallRadius, Clearance, Settings));

            // Half a second of flight on the far side is the entry-side free flight carried across (both walls stand, so gravity maps onto itself).
            const float t = 0.5f;
            Assert.Equal(MouthOutcome.Clear, PortalReach.Fly(ref ball, Pair, MaskA, MaskB, Gravity, t, 0.005f, BallRadius, Clearance, Settings));
            Vec3 expected = Pair.AToB.TransformPoint(BallTransit.At(p, v, Gravity, t));
            Rig.Near(expected, ball.Position, 3);
            Rig.Near(Pair.AToB.TransformDirection(v + Gravity * t), ball.Velocity, 3);
            Assert.Equal(0, ball.Crossings);
        }

        [Fact]
        public void BallLetGoInTheMouthMovingInIsCarriedAcrossAndComesClear()
        {
            // Centre 1 cm in front of orange (the ball straddles the wall plane), moving in at 2 m/s.
            Vec3 p = new Vec3(4.99f, 0f, 1.5f);
            Vec3 v = new Vec3(2f, 0f, 0f);
            HeldLaunch l = PortalReach.Launch(new ReachLatch(), Pair, MaskA, MaskB, p, v, Vec3.Zero, BallRadius, Clearance, PortalReach.ArmReach, Settings);
            Assert.True(l.AtPortal);
            Assert.False(l.Carried);

            var ball = new MouthBall { Position = l.Position, Velocity = l.Velocity };
            Assert.Equal(MouthOutcome.InMouth, PortalReach.Where(ball, Pair, MaskA, MaskB, BallRadius, Clearance, Settings));

            MouthOutcome outcome = MouthOutcome.InMouth;
            int steps = 0;
            while (outcome == MouthOutcome.InMouth && steps++ < 200) outcome = PortalReach.Step(ref ball, Pair, MaskA, MaskB, NoGravity, 0.005f, BallRadius, Clearance, Settings);
            Assert.Equal(MouthOutcome.Clear, outcome);
            Assert.Equal(1, ball.Crossings);
            Assert.True(ball.LastFromA);
            Assert.Equal(0f, ball.ExitMovedBy, 5);
            Assert.True(B.Wall.SignedDistance(ball.Position) >= BallRadius + Clearance);
            Rig.Near(Pair.AToB.TransformDirection(v), ball.Velocity);
            // Exactly on the straight path carried across.
            Rig.Near(Pair.AToB.TransformPoint(p + v * (0.005f * steps)), ball.Position, 3);
        }

        [Fact]
        public void BallLetGoInTheMouthAndPulledBackOutComesClearOnTheSameSide()
        {
            Vec3 p = new Vec3(4.98f, 0f, 1.5f);
            var ball = new MouthBall { Position = p, Velocity = new Vec3(-1.5f, 0f, 0f) };
            MouthOutcome outcome = MouthOutcome.InMouth;
            int steps = 0;
            while (outcome == MouthOutcome.InMouth && steps++ < 200) outcome = PortalReach.Step(ref ball, Pair, MaskA, MaskB, NoGravity, 0.005f, BallRadius, Clearance, Settings);
            Assert.Equal(MouthOutcome.Clear, outcome);
            Assert.Equal(0, ball.Crossings);
            Assert.True(A.Wall.SignedDistance(ball.Position) >= BallRadius + Clearance);
        }

        [Fact]
        public void BallDroppedInTheMouthFallsUntilItMeetsTheBottomOfTheOpening()
        {
            // Straddling the plane with no speed: it falls straight down inside the mouth, and where the paint
            // ends it has met the wall. Until then nothing may call the opening a wall.
            var ball = new MouthBall { Position = new Vec3(4.99f, 0f, 1.5f), Velocity = Vec3.Zero };
            MouthOutcome outcome = MouthOutcome.InMouth;
            int steps = 0;
            while (outcome == MouthOutcome.InMouth && steps++ < 400) outcome = PortalReach.Step(ref ball, Pair, MaskA, MaskB, Gravity, 0.005f, BallRadius, Clearance, Settings);
            Assert.Equal(MouthOutcome.OffPaint, outcome);
            Assert.True(steps > 10);
            // The disc's lowest point is at z = 1.0: the ball's cut circle reaches it a little above that.
            Assert.InRange(ball.Position.Z, 1.0f, 1.1f);
            Assert.Equal(0, ball.Crossings);
        }

        [Fact]
        public void BallLetGoBarelyThroughStaysOffThePhysicsUntilItIsClearOfTheExitWall()
        {
            // Centre 1 cm through orange: carried, it is 1 cm in front of blue and still cutting the blue wall.
            Vec3 p = new Vec3(5.01f, 0f, 1.5f);
            Vec3 v = new Vec3(1f, 0f, 0f);
            HeldLaunch l = PortalReach.Launch(new ReachLatch(), Pair, MaskA, MaskB, p, v, Vec3.Zero, BallRadius, Clearance, PortalReach.ArmReach, Settings);
            Assert.True(l.Carried);
            var ball = new MouthBall { Position = l.Position, Velocity = l.Velocity };
            Assert.Equal(MouthOutcome.InMouth, PortalReach.Where(ball, Pair, MaskA, MaskB, BallRadius, Clearance, Settings));
            MouthOutcome outcome = PortalReach.Fly(ref ball, Pair, MaskA, MaskB, NoGravity, 0.1f, 0.005f, BallRadius, Clearance, Settings);
            Assert.Equal(MouthOutcome.Clear, outcome);
            Assert.Equal(0.11f, B.Wall.SignedDistance(ball.Position), 3);
        }

        [Fact]
        public void BallLetGoThroughAndThrownBackComesOutOfTheEntryAgain()
        {
            // 5 cm through orange, thrown back toward the room it came from at 3 m/s. On the far side that is
            // a ball 5 cm in front of blue heading into blue: it goes back in and comes out of orange.
            Vec3 p = new Vec3(5.05f, 0f, 1.5f);
            Vec3 v = new Vec3(-3f, 0f, 0f);
            HeldLaunch l = PortalReach.Launch(new ReachLatch(), Pair, MaskA, MaskB, p, v, Vec3.Zero, BallRadius, Clearance, PortalReach.ArmReach, Settings);
            Assert.True(l.Carried);
            var ball = new MouthBall { Position = l.Position, Velocity = l.Velocity };
            MouthOutcome outcome = PortalReach.Fly(ref ball, Pair, MaskA, MaskB, NoGravity, 0.1f, 0.005f, BallRadius, Clearance, Settings);
            Assert.Equal(MouthOutcome.Clear, outcome);
            Assert.Equal(1, ball.Crossings);
            Assert.False(ball.LastFromA);   // it went in through blue
            Rig.Near(p + v * 0.1f, ball.Position, 3);
            Rig.Near(v, ball.Velocity);
        }

        [Fact]
        public void BallCuttingBareWallBesideTheOpeningHasHitTheWall()
        {
            var ball = new MouthBall { Position = new Vec3(4.99f, 1.2f, 1.5f), Velocity = new Vec3(1f, 0f, 0f) };
            Assert.Equal(MouthOutcome.OffPaint, PortalReach.Where(ball, Pair, MaskA, MaskB, BallRadius, Clearance, Settings));
            // Near bare wall but not touching it: an ordinary ball.
            ball.Position = new Vec3(4.95f, 1.2f, 1.5f);
            Assert.Equal(MouthOutcome.Clear, PortalReach.Where(ball, Pair, MaskA, MaskB, BallRadius, Clearance, Settings));
        }

        // ------------------------------------------------------------------ any orientation: a portal in the floor

        // Orange: disc radius 0.5 in the FLOOR (plane z = 0, facing up) around world (2, 0, 0).
        // The floor's frame: origin (0, -2, 0), U = +Y, V = -X... chosen so U x V = +Z (up, into the room).
        private static readonly WallFrame Floor = new WallFrame(new Vec3(0f, -2f, 0f), new Vec3(1f, 0f, 0f), new Vec3(0f, 1f, 0f), 5f, 4f);
        private static readonly PortalEnd FloorA = Rig.End(Floor, PaintColor.Orange, new Vec2(2f, 2f), PortalAnchor.PaintCenter, 0f, 0.5f);
        private static readonly PortalPair FloorPair = new PortalPair(FloorA, B);
        private static readonly DiscMask FloorMaskA = new DiscMask(Floor, PaintColor.Orange, new Vec2(2f, 2f), 0.5f);

        [Fact]
        public void FloorFrameFacesUp()
        {
            Rig.Near(new Vec3(0f, 0f, 1f), Floor.Normal);
            Rig.Near(new Vec3(2f, 0f, 0f), FloorA.PaintCenter);
        }

        [Fact]
        public void ReachingDownIntoAFloorPortalIsThrough()
        {
            ReachState s = PortalReach.Find(FloorPair, FloorMaskA, MaskB, new Vec3(2.1f, 0.1f, -0.2f), Rim, HandReach, PortalReach.ArmReach, false, Settings);
            Assert.True(s.Through);
            Assert.True(s.FromA);
            Assert.Equal(-0.2f, s.Depth, 4);

            // The twin (full crossing) sticks 0.2 m out of the blue wall.
            Vec3 twin = PortalReach.Crossing(FloorPair, true, false).TransformPoint(new Vec3(2.1f, 0.1f, -0.2f));
            Assert.Equal(0.2f, B.Wall.SignedDistance(twin), 4);

            // Bare floor beside the patch: nothing.
            Assert.False(PortalReach.Find(FloorPair, FloorMaskA, MaskB, new Vec3(3.5f, 0.1f, -0.2f), Rim, HandReach, PortalReach.ArmReach, false, Settings).AtPortal);
        }

        [Fact]
        public void BallLetGoBelowAFloorPortalComesOutOfTheWallMovingOutOfIt()
        {
            // 0.2 m down through the floor patch, let go moving down at 2 m/s.
            Vec3 p = new Vec3(2f, 0f, -0.2f);
            Vec3 v = new Vec3(0f, 0f, -2f);
            HeldLaunch l = PortalReach.Launch(new ReachLatch(), FloorPair, FloorMaskA, MaskB, p, v, Vec3.Zero, BallRadius, Clearance, PortalReach.ArmReach, Settings);
            Assert.True(l.Carried);
            Assert.Equal(0.2f, B.Wall.SignedDistance(l.Position), 4);
            Assert.Equal(2f, Vec3.Dot(l.Velocity, B.Wall.Normal), 4);   // straight out of the blue wall
            Assert.Equal(2f, l.Velocity.Length(), 4);
            Rig.Near(B.PaintCenter + B.Wall.Normal * 0.2f, l.Position);

            // From there it is an ordinary ball in the blue room: real gravity pulls it DOWN the wall.
            var ball = new MouthBall { Position = l.Position, Velocity = l.Velocity };
            Assert.Equal(MouthOutcome.Clear, PortalReach.Fly(ref ball, FloorPair, FloorMaskA, MaskB, Gravity, 0.2f, 0.005f, BallRadius, Clearance, Settings));
            Assert.Equal(l.Position.Z - 0.5f * 9.81f * 0.04f, ball.Position.Z, 3);
            Assert.Equal(0.2f + 2f * 0.2f, B.Wall.SignedDistance(ball.Position), 3);
        }

        [Fact]
        public void BallDroppedIntoAFloorPortalFromTheMouthFallsThroughAndOutOfTheWall()
        {
            // Resting in the floor opening's mouth, centre 2 cm above the floor, let go with no speed: it
            // falls through the plane and comes out of the blue wall.
            var ball = new MouthBall { Position = new Vec3(2f, 0f, 0.02f), Velocity = Vec3.Zero };
            Assert.Equal(MouthOutcome.InMouth, PortalReach.Where(ball, FloorPair, FloorMaskA, MaskB, BallRadius, Clearance, Settings));
            MouthOutcome outcome = MouthOutcome.InMouth;
            int steps = 0;
            while (outcome == MouthOutcome.InMouth && steps++ < 400) outcome = PortalReach.Step(ref ball, FloorPair, FloorMaskA, MaskB, Gravity, 0.005f, BallRadius, Clearance, Settings);
            Assert.Equal(MouthOutcome.Clear, outcome);
            Assert.Equal(1, ball.Crossings);
            Assert.True(ball.LastFromA);
            Assert.True(B.Wall.SignedDistance(ball.Position) >= BallRadius + Clearance);
            Assert.True(Vec3.Dot(ball.Velocity, B.Wall.Normal) > 0f);
        }

        [Fact]
        public void TheTwinOfAHandInAFloorPortalPointsOutOfTheWall()
        {
            // A hand reaching straight down into the floor: its twin reaches straight out of the blue wall, and
            // the hand's "up" (the floor's V) is the wall's "up the wall" (the wall's V).
            RigidTransform crossing = PortalReach.Crossing(FloorPair, true, false);
            Rig.Near(B.Wall.Normal, crossing.TransformDirection(-Floor.Normal));
            Rig.Near(B.Wall.V, crossing.TransformDirection(Floor.V));
            // Deliberately no test of the player crossing here: what it does for a floor end is PortalPair's
            // business (the floor-portal work makes it the full crossing, which the twins use by default).
        }
    }
}
