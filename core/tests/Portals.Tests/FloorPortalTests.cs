using System;
using Xunit;

namespace Portaballs.Core.Tests
{
    /// <summary>
    /// Portals on the floor and the ceiling: balls (BallTransit) and the player (PlaySpaceWalker, PlayerFall,
    /// PlaySpace's righting). The room is the rig's: floor z = 0, ceiling z = 3, far wall x = 5.
    /// </summary>
    public class FloorPortalTests
    {
        private const float BallRadius = 0.035f;
        private const float Dt = 1f / 90f;
        private const float G = 9.81f;
        private static readonly PortalSettings Settings = new PortalSettings();
        private static readonly Vec3 Gravity = new Vec3(0f, 0f, -G);
        private static readonly WallFrame[] Walls = { Rig.FarWall(), Rig.NearWall(), Rig.LeftWall(), Rig.Floor(), Rig.Ceiling() };

        // A patch of radius 0.5 on each surface. World centres:
        //   floor (2, 0, 0)   second floor patch (4, 1, 0)   ceiling (2, 0, 3), straight above the first floor patch
        //   far wall (5, 0, 1.5), and a higher one to the side (5, -1.2, 2.0)   left wall (2, 3, 1.5)
        private static readonly Vec2 FloorUV = new Vec2(2f, 3f);
        private static readonly Vec2 Floor2UV = new Vec2(4f, 4f);
        private static readonly Vec2 CeilingUV = new Vec2(3f, 3f);
        private static readonly Vec2 FarUV = new Vec2(2f, 1.5f);
        private static readonly Vec2 FarHighUV = new Vec2(3.2f, 2.0f);       // world (5, -1.2, 2.0): its way out misses the floor patch
        private static readonly Vec2 LeftUV = new Vec2(2f, 1.5f);

        private sealed class Layout
        {
            public PortalPair Pair;
            public DiscMask MaskA, MaskB;
        }

        private static Layout Make(WallFrame wallA, Vec2 uvA, WallFrame wallB, Vec2 uvB, float radiusA = 0.5f, float radiusB = 0.5f)
        {
            PortalEnd a = Rig.End(wallA, PaintColor.Orange, uvA, PortalAnchor.PaintCenter, 0f, radiusA);
            PortalEnd b = Rig.End(wallB, PaintColor.Blue, uvB, PortalAnchor.PaintCenter, 0f, radiusB);
            return new Layout
            {
                Pair = new PortalPair(a, b),
                MaskA = new DiscMask(wallA, PaintColor.Orange, uvA, radiusA),
                MaskB = new DiscMask(wallB, PaintColor.Blue, uvB, radiusB),
            };
        }

        private static Layout FloorToWall(bool high = false) { return Make(Rig.Floor(), FloorUV, Rig.FarWall(), high ? FarHighUV : FarUV); }
        private static Layout FloorToFloor() { return Make(Rig.Floor(), FloorUV, Rig.Floor(), Floor2UV); }
        private static Layout FloorToCeiling() { return Make(Rig.Floor(), FloorUV, Rig.Ceiling(), CeilingUV); }

        // ------------------------------------------------------------------ the surfaces themselves

        [Fact]
        public void FloorAndCeilingFramesFaceIntoTheRoom()
        {
            WallFrame floor = Rig.Floor(), ceiling = Rig.Ceiling();
            Rig.Near(new Vec3(0f, 0f, 1f), floor.Normal);
            Rig.Near(new Vec3(0f, 0f, -1f), ceiling.Normal);
            Assert.True(floor.IsFloor && floor.IsFlat && !floor.IsCeiling);
            Assert.True(ceiling.IsCeiling && ceiling.IsFlat && !ceiling.IsFloor);
            Assert.False(Rig.FarWall().IsFlat);
            Assert.False(Rig.LeaningNearWall().IsFlat);                // leaning 30 degrees is still a wall
            Rig.Near(new Vec3(2f, 0f, 0f), floor.ToWorld(FloorUV));
            Rig.Near(new Vec3(2f, 0f, 3f), ceiling.ToWorld(CeilingUV));
            Assert.Equal(1.2f, floor.SignedDistance(new Vec3(1f, 1f, 1.2f)), 5);
            Assert.Equal(0.5f, ceiling.SignedDistance(new Vec3(1f, 1f, 2.5f)), 5);
        }

        [Fact]
        public void ThePlayerCrossingIsTheFullRigidOneWhenAnEndIsFlat()
        {
            // Two walls: upright, as always.
            PortalPair walls = Make(Rig.FarWall(), FarUV, Rig.LeftWall(), LeftUV).Pair;
            Assert.True(walls.PlayerKeepsUpright);
            Assert.Equal(0f, walls.PlayerAToB.Rotation.X, 6);
            Assert.Equal(0f, walls.PlayerAToB.Rotation.Y, 6);

            // Floor to wall: the same transform as for balls, and it tips the player over.
            PortalPair fw = FloorToWall().Pair;
            Rig.SameRotation(fw.AToB.Rotation, fw.PlayerAToB.Rotation, 6);
            Rig.Near(fw.AToB.Translation, fw.PlayerAToB.Translation, 5);
            Assert.False(fw.PlayerKeepsUpright);
            // Falling becomes flying out of the wall; the player's up now points INTO the wall (feet first out).
            Rig.Near(new Vec3(-1f, 0f, 0f), fw.PlayerAToB.TransformDirection(new Vec3(0f, 0f, -1f)));
            Rig.Near(new Vec3(1f, 0f, 0f), fw.PlayerAToB.TransformDirection(new Vec3(0f, 0f, 1f)));

            // Floor to floor: upside down. Floor to the ceiling above: a plain move, nothing turns at all.
            PortalPair ff = FloorToFloor().Pair;
            Assert.False(ff.PlayerKeepsUpright);
            Rig.Near(new Vec3(0f, 0f, -1f), ff.PlayerAToB.TransformDirection(new Vec3(0f, 0f, 1f)));
            PortalPair fc = FloorToCeiling().Pair;
            Assert.True(fc.PlayerKeepsUpright);
            Rig.SameRotation(Quat.Identity, fc.PlayerAToB.Rotation, 6);
            Rig.Near(new Vec3(0f, 0f, 3f), fc.PlayerAToB.Translation, 5);
        }

        // ------------------------------------------------------------------ balls

        [Fact]
        public void BallDroppedIntoAFloorPortalFliesOutOfTheWallSideways()
        {
            Layout l = FloorToWall();
            // Let go 1 m above the middle of the floor patch.
            BallApproach a = BallTransit.Approach(l.Pair.A, l.MaskA, new Vec3(2f, 0f, 1f), Vec3.Zero, Gravity, BallRadius, 1f, Settings);
            Assert.True(a.Touches);
            Assert.True(a.Enters);
            Assert.Equal(MathF.Sqrt(2f * 1f / G), a.CrossTime, 4);
            Rig.Near(FloorUV, a.CrossUV);

            float speed = G * a.CrossTime;
            PortalCarry c = BallTransit.Carry(l.Pair, true, false, l.MaskB, new Vec3(2f, 0f, 0f), BallRadius, Settings);
            Assert.Equal(0f, c.MovedBy);
            Rig.Near(new Vec3(5f, 0f, 1.5f), c.Transform.TransformPoint(new Vec3(2f, 0f, 0f)));
            Vec3 outV = c.Transform.TransformDirection(new Vec3(0f, 0f, -speed));
            Rig.Near(new Vec3(-speed, 0f, 0f), outV);                  // straight out of the far wall, level, same speed
            // A sideways drift on the way in is kept as a sideways drift, same speed overall.
            Vec3 drifting = new Vec3(0.7f, -0.4f, -speed);
            Assert.Equal(drifting.Length(), c.Transform.TransformDirection(drifting).Length(), 4);
            Assert.Equal(speed, -Vec3.Dot(c.Transform.TransformDirection(drifting), new Vec3(1f, 0f, 0f)), 4);
        }

        [Fact]
        public void BallThrownIntoAWallPortalComesUpOutOfTheFloor()
        {
            Layout l = FloorToWall();
            BallApproach a = BallTransit.Approach(l.Pair.B, l.MaskB, new Vec3(4f, 0f, 1.5f), new Vec3(8f, 0f, 0f), Vec3.Zero, BallRadius, 1f, Settings);
            Assert.True(a.Enters);
            PortalCarry c = BallTransit.Carry(l.Pair, false, false, l.MaskA, new Vec3(5f, 0f, 1.5f), BallRadius, Settings);
            Rig.Near(new Vec3(2f, 0f, 0f), c.Transform.TransformPoint(new Vec3(5f, 0f, 1.5f)));
            Rig.Near(new Vec3(0f, 0f, 8f), c.Transform.TransformDirection(new Vec3(8f, 0f, 0f)));
            // Just through the wall plane is just above the floor plane.
            Assert.Equal(0.03f, Rig.Floor().SignedDistance(c.Transform.TransformPoint(new Vec3(5.03f, 0f, 1.5f))), 4);
        }

        [Fact]
        public void BallBetweenTwoFloorPortalsComesBackUpAtTheSameSpeed()
        {
            Layout l = FloorToFloor();
            BallApproach a = BallTransit.Approach(l.Pair.A, l.MaskA, new Vec3(2f, 0f, 0.5f), new Vec3(0f, 0f, -4f), Gravity, BallRadius, 1f, Settings);
            Assert.True(a.Enters);
            PortalCarry c = BallTransit.Carry(l.Pair, true, false, l.MaskB, new Vec3(2f, 0f, 0f), BallRadius, Settings);
            Rig.Near(new Vec3(4f, 1f, 0f), c.Transform.TransformPoint(new Vec3(2f, 0f, 0f)));
            Vec3 outV = c.Transform.TransformDirection(new Vec3(1f, 2f, -5f));
            Rig.Near(new Vec3(-1f, 2f, 5f), outV);
            Assert.Equal(new Vec3(1f, 2f, -5f).Length(), outV.Length(), 4);
        }

        [Fact]
        public void BallBetweenTheCeilingAndTheFloorBelowIsOnlyMoved()
        {
            Layout l = FloorToCeiling();
            // Up into the ceiling against gravity: it gets there, slower.
            BallApproach up = BallTransit.Approach(l.Pair.B, l.MaskB, new Vec3(2f, 0f, 2f), new Vec3(0f, 0f, 6f), Gravity, BallRadius, 1f, Settings);
            Assert.True(up.Enters);
            Assert.True(up.CrossTime > 1f / 6f);
            // Too slow to reach it: never touches.
            Assert.False(BallTransit.Approach(l.Pair.B, l.MaskB, new Vec3(2f, 0f, 2f), new Vec3(0f, 0f, 3f), Gravity, BallRadius, 1f, Settings).Touches);

            // Ceiling to floor and floor to ceiling: no turn at all, so the ball just carries on.
            PortalCarry fromCeiling = BallTransit.Carry(l.Pair, false, false, l.MaskA, new Vec3(2f, 0f, 3f), BallRadius, Settings);
            Rig.Near(new Vec3(2f, 0f, 0f), fromCeiling.Transform.TransformPoint(new Vec3(2f, 0f, 3f)));
            Rig.Near(new Vec3(0.5f, -0.2f, 4f), fromCeiling.Transform.TransformDirection(new Vec3(0.5f, -0.2f, 4f)));
            PortalCarry fromFloor = BallTransit.Carry(l.Pair, true, false, l.MaskB, new Vec3(2f, 0f, 0f), BallRadius, Settings);
            Rig.Near(new Vec3(2.1f, 0.1f, 3f), fromFloor.Transform.TransformPoint(new Vec3(2.1f, 0.1f, 0f)));
            Rig.Near(new Vec3(0f, 0f, -6f), fromFloor.Transform.TransformDirection(new Vec3(0f, 0f, -6f)));
        }

        [Fact]
        public void BallThatClipsTheRimOfAFloorPatchDoesNotEnter()
        {
            Layout l = FloorToWall();
            Assert.False(BallTransit.Approach(l.Pair.A, l.MaskA, new Vec3(2.48f, 0f, 1f), Vec3.Zero, Gravity, BallRadius, 1f, Settings).Enters);
            Assert.True(BallTransit.Approach(l.Pair.A, l.MaskA, new Vec3(2.44f, 0f, 1f), Vec3.Zero, Gravity, BallRadius, 1f, Settings).Enters);
            // Bare floor beside the patch: touches, does not enter.
            BallApproach bare = BallTransit.Approach(l.Pair.A, l.MaskA, new Vec3(3.5f, 0f, 1f), Vec3.Zero, Gravity, BallRadius, 1f, Settings);
            Assert.True(bare.Touches);
            Assert.False(bare.Enters);
        }

        // ------------------------------------------------------------------ the up-and-down part by itself

        [Fact]
        public void FallCanStartGoingUpAndHasNoFloorOverAHole()
        {
            // Thrown up at 3 m/s from the floor: rises 3^2 / (2 g) = 0.459 m and lands again.
            var fall = new PlayerFall();
            fall.Speed = -3f;
            float h = 0f, top = 0f;
            int steps = 0;
            bool landed = false;
            do
            {
                h = fall.Step(h, Dt);
                if (h > top) top = h;
                landed |= fall.JustLanded;
                steps++;
            }
            while (fall.Falling && steps < 1000);
            Assert.Equal(0f, h);
            Assert.True(landed);
            Assert.InRange(top, 0.43f, 0.46f);
            Assert.InRange(steps * Dt, 0.58f, 0.64f);                 // 2 * 3 / g = 0.61 s

            // Over a hole there is nothing to land on: straight through the floor plane, capped at the top speed.
            fall.Reset();
            h = 0f;
            for (int i = 0; i < 200; i++)
            {
                float next = fall.Step(h, Dt, true);
                Assert.True(next < h);
                Assert.True(h - next <= fall.MaxFallSpeed * Dt + 1e-5f);
                h = next;
            }
            Assert.True(h < -5f);
            Assert.Equal(fall.MaxFallSpeed, fall.Speed, 4);
            Assert.True(fall.Falling);

            // The hole closes (or the player is no longer over it): lifted back, as after any crossing.
            float t = 0f;
            h = -0.6f;
            while (h < 0f && t < 2f) { h = fall.Step(h, Dt, false); t += Dt; }
            Assert.Equal(0f, h);
            Assert.InRange(t, 0.19f, 0.22f);                           // 0.6 m at 3 m/s
        }

        // ------------------------------------------------------------------ the player

        private static readonly Vec3 HeadLocal = new Vec3(0.3f, 0.1f, 1.5f);

        private static PlaySpaceWalker WalkerAt(Vec3 headWorld, float yaw = 0f)
        {
            var w = new PlaySpaceWalker();
            Quat q = Quat.CreateFromAxisAngle(Units.Up, yaw);
            w.Anchor = new RigidTransform(q, headWorld - Vec3.Transform(HeadLocal, q));
            return w;
        }

        private static PlaySpaceFrame Tick(PlaySpaceWalker w, Layout l, Vec2 move, bool linked = true)
        {
            return w.Update(HeadLocal, Quat.Identity, move, 0f, Dt, 0f, linked, l.Pair, l.MaskA, l.MaskB, Walls, Walls.Length, Settings);
        }

        private static void AssertFinite(Vec3 v)
        {
            Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z));
        }

        private static float YawDegrees(PlaySpaceWalker w)
        {
            Vec3 fwd = w.Anchor.TransformDirection(new Vec3(1f, 0f, 0f));
            return MathF.Atan2(fwd.Y, fwd.X) * 180f / MathF.PI;
        }

        [Fact]
        public void SolidFloorHoldsThePlayerUnlessTheHoleIsUnderTheBodyAndBigEnough()
        {
            // Beside the patch, on its rim, and over a patch smaller than the body: the floor is there.
            Layout l = FloorToWall();
            foreach (Vec3 head in new[] { new Vec3(3.2f, 0f, 1.5f), new Vec3(2.4f, 0f, 1.5f) })
            {
                PlaySpaceWalker w = WalkerAt(head);
                for (int i = 0; i < 30; i++)
                {
                    PlaySpaceFrame f = Tick(w, l, Vec2.Zero);
                    Assert.False(f.OverHole);
                    Assert.False(f.InAir);
                    Rig.Near(head, f.Head, 5);
                }
            }
            Layout small = Make(Rig.Floor(), FloorUV, Rig.FarWall(), FarUV, 0.15f, 0.5f);
            PlaySpaceWalker onSmall = WalkerAt(new Vec3(2f, 0f, 1.5f));
            for (int i = 0; i < 30; i++) Assert.False(Tick(onSmall, small, Vec2.Zero).OverHole);
            Rig.Near(new Vec3(2f, 0f, 1.5f), onSmall.Anchor.TransformPoint(HeadLocal), 5);

            // Nothing linked: the same paint is just paint.
            PlaySpaceWalker unlinked = WalkerAt(new Vec3(2f, 0f, 1.5f));
            for (int i = 0; i < 30; i++) Assert.False(Tick(unlinked, l, Vec2.Zero, false).InAir);

            // The body radius is the dial: 0.2 m fits a 0.5 m patch up to 0.3 m off centre, not 0.35 m.
            Assert.True(PlayerPassage.FloorIsOpenUnder(true, l.Pair, l.MaskA, l.MaskB, new Vec3(2.29f, 0f, 1f), 0f, 0.2f, Settings));
            Assert.False(PlayerPassage.FloorIsOpenUnder(true, l.Pair, l.MaskA, l.MaskB, new Vec3(2.35f, 0f, 1f), 0f, 0.2f, Settings));
            Assert.False(PlayerPassage.FloorIsOpenUnder(true, l.Pair, l.MaskA, l.MaskB, new Vec3(2f, 0f, 1f), 0f, 0.55f, Settings));
            // A wall patch is never a hole in the floor.
            Assert.False(PlayerPassage.FloorIsOpenUnder(true, l.Pair, l.MaskA, l.MaskB, new Vec3(5f, 0f, 1f), 0f, 0.2f, Settings));
        }

        [Fact]
        public void StandingOverAFloorPortalFallsInAndFliesOutOfTheWall()
        {
            // Blue is 2 m up the far wall, so the player comes out with the feet half a metre above the floor.
            Layout l = FloorToWall(true);
            PlaySpaceWalker w = WalkerAt(new Vec3(2f, 0f, 1.5f));
            Assert.Equal(0.25f, w.RightingSeconds);

            PlaySpaceFrame crossing = default;
            int frames = 0;
            Vec3 lastHead = new Vec3(2f, 0f, 1.5f);
            while (!crossing.Crossed && frames < 200)
            {
                PlaySpaceFrame f = Tick(w, l, Vec2.Zero);
                frames++;
                if (f.Crossed) { crossing = f; break; }
                Assert.True(f.OverHole);
                Assert.True(f.InAir);
                Assert.True(f.Head.Z < lastHead.Z);                    // sinking, straight down
                Assert.Equal(2f, f.Head.X, 5);
                Assert.Equal(0f, f.Head.Y, 5);
                lastHead = f.Head;
            }
            Assert.True(crossing.Crossed);
            Assert.True(crossing.CrossedFlying);
            Assert.True(crossing.Crossing.FromA);
            // The head had 1.5 m to fall: sqrt(2 * 1.5 / g) = 0.553 s, arriving at 5.42 m/s.
            Assert.InRange(frames * Dt, 0.54f, 0.58f);
            float speedIn = crossing.VelocityBeforeCrossing.Length();
            Assert.InRange(speedIn, 5.3f, 5.6f);
            Rig.Near(new Vec3(0f, 0f, -speedIn), crossing.VelocityBeforeCrossing);

            // At the crossing: the rigid mapping, exactly, for the head and for the velocity.
            Rig.Near(l.Pair.PlayerAToB.TransformPoint(crossing.HeadBeforeCrossing), crossing.Head, 4);
            Rig.Near(l.Pair.PlayerAToB.TransformDirection(crossing.VelocityBeforeCrossing), crossing.VelocityAfterCrossing, 4);
            Rig.Near(new Vec3(-speedIn, 0f, 0f), crossing.VelocityAfterCrossing, 4);
            Assert.InRange(Rig.FarWall().SignedDistance(crossing.Head), 0f, 0.07f);
            Assert.Equal(2.0f, crossing.Head.Z, 3);
            // The play space is lying on its side, and no righting has been done in this frame.
            Assert.Equal(MathF.PI / 2f, crossing.Tilt, 4);
            Assert.Equal(0f, crossing.Righted);

            // Then: swung upright over a quarter of a second while flying out along -X and dropping.
            float time = 0f;
            PlaySpaceFrame last = crossing;
            bool wasTilted = true;
            float rightedAt = -1f;
            while ((last.InAir || last.Velocity != Vec3.Zero || last.Tilt > 0f) && time < 3f)
            {
                PlaySpaceFrame f = Tick(w, l, Vec2.Zero);
                time += Dt;
                Assert.False(f.Crossed);
                Assert.True(f.Head.X <= last.Head.X + 1e-5f);
                Assert.Equal(-1.2f, f.Head.Y, 4);
                if (wasTilted && f.Tilt == 0f) { rightedAt = time; wasTilted = false; }
                last = f;
            }
            Assert.InRange(rightedAt, 0.24f, 0.28f);
            Assert.Equal(0f, w.Anchor.Rotation.X);
            Assert.Equal(0f, w.Anchor.Rotation.Y);
            // Standing on the floor again, well out from the wall, not moving.
            Assert.Equal(1.5f, last.Head.Z, 4);
            Assert.Equal(0f, w.Anchor.Translation.Z, 4);
            Assert.False(w.InAir);
            Rig.Near(Vec3.Zero, w.Velocity);
            Assert.InRange(last.Head.X, 1.5f, 3.6f);                   // 0.32 s of flight at 5.4 m/s, then a short slide
            // Having faced +Y while falling in, the player ends up facing out of the wall.
            Assert.Equal(180f, MathF.Abs(YawDegrees(WalkerEndingUpFacing(l, MathF.PI / 2f))), 1);
        }

        // Fall into the floor patch facing the given way, wait until everything has settled, return the walker.
        private static PlaySpaceWalker WalkerEndingUpFacing(Layout l, float yawIn)
        {
            PlaySpaceWalker w = WalkerAt(new Vec3(2f, 0f, 1.5f), yawIn);
            for (int i = 0; i < 360; i++) Tick(w, l, Vec2.Zero);
            Assert.False(w.InAir);
            Assert.Equal(0f, w.Tilt);
            return w;
        }

        [Fact]
        public void RightingAtOnceOrSmoothlyNeverMovesTheHead()
        {
            // The same fall with the dial at 0 (in the crossing frame) and at 0.25 s: the head takes exactly
            // the same path, and both end level, facing the same way.
            Layout l = FloorToWall(true);
            PlaySpaceWalker smooth = WalkerAt(new Vec3(2f, 0f, 1.5f), 0.4f);
            PlaySpaceWalker atOnce = WalkerAt(new Vec3(2f, 0f, 1.5f), 0.4f);
            atOnce.RightingSeconds = 0f;
            bool sawCrossing = false;
            for (int i = 0; i < 300; i++)
            {
                PlaySpaceFrame a = Tick(smooth, l, Vec2.Zero);
                PlaySpaceFrame b = Tick(atOnce, l, Vec2.Zero);
                Rig.Near(a.Head, b.Head, 4);
                Rig.Near(a.Velocity, b.Velocity, 4);
                Assert.Equal(a.Crossed, b.Crossed);
                if (b.Crossed)
                {
                    sawCrossing = true;
                    Assert.Equal(0f, b.Tilt);                          // already level in the crossing frame
                    Assert.Equal(MathF.PI / 2f, b.Righted, 4);
                    Assert.Equal(0f, atOnce.Anchor.Rotation.X);
                    Assert.Equal(0f, atOnce.Anchor.Rotation.Y);
                    Assert.Equal(MathF.PI / 2f, a.Tilt, 4);
                }
            }
            Assert.True(sawCrossing);
            Assert.Equal(0f, smooth.Tilt);
            Assert.Equal(YawDegrees(atOnce), YawDegrees(smooth), 2);
            Rig.Near(atOnce.Anchor.Translation, smooth.Anchor.Translation, 3);
        }

        [Fact]
        public void RightingTurnsAboutTheHeadByTheShortestWayAndEndsExactlyLevel()
        {
            // A play space tipped any old way, standing still: swung level, the head never moving.
            var w = new PlaySpaceWalker();
            Quat tipped = Quat.CreateFromAxisAngle(Vec3.Normalize(new Vec3(1f, 2f, 0.5f)), 2.0f);
            Vec3 headWorld = new Vec3(2.5f, 0.5f, 1.5f);
            w.Anchor = new RigidTransform(tipped, headWorld - Vec3.Transform(HeadLocal, tipped));
            float tilt0 = w.Tilt;
            Assert.InRange(tilt0, 0.5f, MathF.PI);
            Layout l = FloorToWall();
            float lastTilt = tilt0;
            for (int i = 0; i < 60; i++)
            {
                PlaySpaceFrame f = Tick(w, l, Vec2.Zero, false);
                Rig.Near(headWorld, f.Head, 4);
                Assert.True(f.Tilt <= lastTilt + 1e-5f);              // only ever toward level
                lastTilt = f.Tilt;
            }
            Assert.Equal(0f, w.Tilt);
            Assert.Equal(0f, w.Anchor.Rotation.X);
            Assert.Equal(0f, w.Anchor.Rotation.Y);
            Rig.Near(headWorld, w.Anchor.TransformPoint(HeadLocal), 4);

            // The helper: the turn is the angle between the two ups, about the level axis square to both.
            Quat lean = Quat.CreateFromAxisAngle(new Vec3(1f, 0f, 0f), 0.6f);
            Assert.Equal(0.6f, PlaySpace.TiltOf(lean), 5);
            RigidTransform fix = PlaySpace.RightAbout(lean, headWorld, 10f, new Vec3(0f, 1f, 0f));
            Rig.Near(headWorld, fix.TransformPoint(headWorld), 5);
            Rig.SameRotation(Quat.Identity, fix.Rotation * lean, 5);
            // Limited to a slice, it turns just that much.
            RigidTransform slice = PlaySpace.RightAbout(lean, headWorld, 0.1f, new Vec3(0f, 1f, 0f));
            Assert.Equal(0.5f, PlaySpace.TiltOf(slice.Rotation * lean), 5);
            // Exactly upside down: the axis it is told to use, so undoing a half turn gives back the start.
            Vec3 axis = new Vec3(MathF.Cos(0.5f), MathF.Sin(0.5f), 0f);
            Quat over = Quat.CreateFromAxisAngle(axis, MathF.PI);
            Assert.Equal(MathF.PI, PlaySpace.TiltOf(over), 4);
            RigidTransform back = PlaySpace.RightAbout(over, headWorld, 10f, axis);
            Rig.SameRotation(Quat.Identity, back.Rotation * over, 4);
            // Level already: nothing to do.
            Rig.SameRotation(Quat.Identity, PlaySpace.RightAbout(Quat.Identity, headWorld, 10f, axis).Rotation, 6);
        }

        [Fact]
        public void WalkingIntoAWallPortalPopsUpOutOfTheFloorAndTheStickLandsBesideTheHole()
        {
            Layout l = FloorToWall();
            PlaySpaceWalker w = WalkerAt(new Vec3(4.6f, 0f, 1.5f));
            float pop = w.FloorExitSpeed(HeadLocal.Z);
            Assert.Equal(MathF.Sqrt(2f * G * (1.5f + 0.4f)), pop, 4);      // 6.1 m/s for a head 1.5 m up

            PlaySpaceFrame crossing = default;
            for (int i = 0; i < 60 && !crossing.Crossed; i++)
            {
                PlaySpaceFrame f = Tick(w, l, new Vec2(0f, 1f));
                if (f.Crossed) crossing = f;
            }
            Assert.True(crossing.Crossed);
            Assert.False(crossing.CrossedFlying);
            Assert.False(crossing.Crossing.FromA);
            Rig.Near(Vec3.Zero, crossing.VelocityBeforeCrossing);           // walking is not a velocity
            Rig.Near(new Vec3(0f, 0f, pop), crossing.VelocityAfterCrossing, 3);
            Rig.Near(l.Pair.PlayerBToA.TransformPoint(crossing.HeadBeforeCrossing), crossing.Head - new Vec3(0f, 0f, crossing.Dropped), 4);
            Assert.InRange(crossing.Head.Z, 0f, 0.15f);                      // the head is just out of the floor

            // Keep the stick forward. While the feet are still in the hole the rim holds the body; once they
            // are out, the stick steers, and the player lands on solid floor beside the hole.
            float top = 0f;
            int crossings = 1;
            PlaySpaceFrame last = crossing;
            float time = 0f;
            bool heldWhileIn = true;
            while ((last.InAir || last.Tilt > 0f) && time < 3f)
            {
                Vec3 before = last.Head;
                PlaySpaceFrame f = Tick(w, l, new Vec2(0f, 1f));
                time += Dt;
                if (f.Crossed) crossings++;
                float feet = f.Head.Z - HeadLocal.Z;
                if (feet > top) top = feet;
                if (before.Z - HeadLocal.Z < -0.01f && feet < -0.01f)
                {
                    // Still in the hole: never outside the paint with the body.
                    heldWhileIn &= PlayerPassage.FloorIsOpenUnder(true, l.Pair, l.MaskA, l.MaskB, f.Head, 0f, w.BodyRadius, Settings);
                }
                last = f;
            }
            Assert.Equal(1, crossings);
            Assert.True(heldWhileIn);
            Assert.InRange(top, 0.36f, 0.41f);                               // the feet rose about 0.4 m clear
            Assert.False(w.InAir);
            Assert.Equal(1.5f, last.Head.Z, 4);
            Assert.Equal(0f, w.Tilt);
            Assert.False(PlayerPassage.FloorIsOpenUnder(true, l.Pair, l.MaskA, l.MaskB, last.Head, 0f, w.BodyRadius, Settings));
            float fromHole = new Vec2(last.Head.X - 2f, last.Head.Y - 0f).Length();
            Assert.InRange(fromHole, 0.5f, 2.0f);
            // On solid floor the next frames change nothing.
            for (int i = 0; i < 20; i++) Rig.Near(last.Head, Tick(w, l, Vec2.Zero).Head, 4);
        }

        [Fact]
        public void ComingUpOutOfTheFloorOffCentreTheExitIsSlidToWhereTheBodyFits()
        {
            // A shorter player (head 1.15 m up) walks into the wall patch 0.35 m below its centre: the head fits
            // there. Carried rigidly that is 0.35 m from the floor patch's centre, where a 0.2 m body does not
            // fit a 0.5 m hole, so the exit is slid 5 cm toward the middle and the player still pops out.
            Layout l = FloorToWall();
            Vec3 headLocal = new Vec3(0.3f, 0.1f, 1.15f);
            var w = new PlaySpaceWalker();
            w.Anchor = new RigidTransform(Quat.Identity, new Vec3(4.6f, 0f, 1.15f) - headLocal);
            PlaySpaceFrame crossing = default;
            for (int i = 0; i < 60 && !crossing.Crossed; i++)
            {
                PlaySpaceFrame f = w.Update(headLocal, Quat.Identity, new Vec2(0f, 1f), 0f, Dt, 0f, true, l.Pair, l.MaskA, l.MaskB, Walls, Walls.Length, Settings);
                if (f.Crossed) crossing = f;
            }
            Assert.True(crossing.Crossed);
            Assert.Equal(0.05f, crossing.Crossing.ExitMovedBy, 3);
            Assert.Equal(0.3f, new Vec2(crossing.Head.X - 2f, crossing.Head.Y).Length(), 3);
            Assert.True(crossing.OverHole);
            Assert.Equal(w.FloorExitSpeed(1.15f), crossing.VelocityAfterCrossing.Z, 3);
            Assert.True(w.InAir);

            // The head alone (no body radius given) is not slid: it fits where it lands.
            Assert.True(PlayerPassage.TryCross(l.Pair, l.MaskA, l.MaskB, new Vec3(4.97f, 0f, 1.15f), new Vec3(5.03f, 0f, 1.15f), 0.1f, Settings, out PlayerStep plain));
            Assert.Equal(0f, plain.ExitMovedBy);
            // Out of a WALL the body radius changes nothing.
            Assert.True(PlayerPassage.TryCross(l.Pair, l.MaskA, l.MaskB, new Vec3(2.35f, 0f, 0.02f), new Vec3(2.35f, 0f, -0.02f), 0.1f, Settings, out PlayerStep toWall, 0.2f));
            Assert.Equal(0f, toWall.ExitMovedBy);
        }

        [Fact]
        public void WithoutSteeringThePlayerDropsBackInWhichIsAllowed()
        {
            Layout l = FloorToWall();
            PlaySpaceWalker w = WalkerAt(new Vec3(4.6f, 0f, 1.5f));
            int crossings = 0;
            bool backIn = false;
            for (int i = 0; i < 400; i++)
            {
                PlaySpaceFrame f = Tick(w, l, crossings == 0 ? new Vec2(0f, 1f) : Vec2.Zero);
                AssertFinite(f.Head);
                if (f.Crossed)
                {
                    crossings++;
                    if (crossings == 2)
                    {
                        // Back down into the floor: out of the wall again, sideways, at the top speed at most.
                        backIn = f.CrossedFlying && f.Crossing.FromA;
                        Assert.InRange(f.VelocityAfterCrossing.Length(), 5f, w.Fall.MaxFallSpeed + 1e-3f);
                        Assert.True(f.VelocityAfterCrossing.X < -5f);
                    }
                }
            }
            Assert.True(backIn);
            Assert.False(w.InAir);                                           // and in the end standing in the room
            Assert.Equal(0f, w.Tilt);
        }

        [Fact]
        public void FloorToFloorComesOutUpsideDownIsRightedAndStillFacesTheSameWay()
        {
            Layout l = FloorToFloor();
            const float yawIn = 0.7f;
            PlaySpaceWalker w = WalkerAt(new Vec3(2f, 0f, 1.5f), yawIn);
            PlaySpaceFrame crossing = default;
            for (int i = 0; i < 200 && !crossing.Crossed; i++)
            {
                PlaySpaceFrame f = Tick(w, l, Vec2.Zero);
                if (f.Crossed) crossing = f;
            }
            Assert.True(crossing.Crossed && crossing.CrossedFlying);
            Rig.Near(l.Pair.PlayerAToB.TransformPoint(crossing.HeadBeforeCrossing), crossing.Head, 4);
            Assert.InRange(crossing.Head.Z, 0f, 0.07f);
            Rig.Near(new Vec2(4f, 1f), new Vec2(crossing.Head.X, crossing.Head.Y), 3);
            Assert.Equal(MathF.PI, crossing.Tilt, 3);                        // upside down, feet first out of the floor
            // Fell in at 5.4 m/s; comes up at least at the pop-out speed (6.1), straight up.
            float pop = w.FloorExitSpeed(HeadLocal.Z);
            Rig.Near(new Vec3(0f, 0f, pop), crossing.VelocityAfterCrossing, 3);

            for (int i = 0; i < 30; i++)
            {
                PlaySpaceFrame f = Tick(w, l, Vec2.Zero);
                Assert.False(f.Crossed);
            }
            Assert.Equal(0f, w.Tilt);
            Assert.Equal(0f, w.Anchor.Rotation.X);
            Assert.Equal(0f, w.Anchor.Rotation.Y);
            Assert.Equal(yawIn * 180f / MathF.PI, YawDegrees(w), 1);         // facing the way they faced going in
        }

        [Fact]
        public void LoopsNeverGoFasterThanTheTopSpeed()
        {
            // The ceiling patch straight above the floor patch: falling for ever, as in Portal.
            Layout loop = FloorToCeiling();
            PlaySpaceWalker w = WalkerAt(new Vec3(2f, 0f, 1.5f), 0.3f);
            int crossings = 0;
            float fastest = 0f;
            for (int i = 0; i < 90 * 6; i++)
            {
                PlaySpaceFrame f = Tick(w, loop, Vec2.Zero);
                AssertFinite(f.Head);
                AssertFinite(f.Velocity);
                if (f.Crossed)
                {
                    crossings++;
                    Assert.True(f.CrossedFlying);
                    Rig.Near(f.VelocityBeforeCrossing, f.VelocityAfterCrossing, 4);   // same speed, same way: down
                    Assert.Equal(0f, f.Tilt);                                       // nothing tips, nothing to right
                }
                fastest = MathF.Max(fastest, f.Velocity.Length());
                Assert.True(f.Velocity.Length() <= w.Fall.MaxFallSpeed + 1e-3f);
                Assert.Equal(2f, f.Head.X, 3);
                Assert.Equal(0f, f.Head.Y, 3);
                Assert.InRange(f.Head.Z, -0.1f, 3.1f);
            }
            Assert.True(crossings >= 8);                                             // 3 m a loop at up to 6 m/s
            Assert.Equal(w.Fall.MaxFallSpeed, fastest, 3);
            Assert.Equal(0.3f * 180f / MathF.PI, YawDegrees(w), 2);                  // still facing the same way

            // Floor to floor with no steering: up at the pop-out speed, back down, for ever, never faster.
            Layout bounce = FloorToFloor();
            PlaySpaceWalker b = WalkerAt(new Vec3(2f, 0f, 1.5f));
            float pop = b.FloorExitSpeed(HeadLocal.Z);
            int bounces = 0;
            for (int i = 0; i < 90 * 8; i++)
            {
                PlaySpaceFrame f = Tick(b, bounce, Vec2.Zero);
                AssertFinite(f.Head);
                if (f.Crossed) bounces++;
                Assert.True(f.Velocity.Length() <= MathF.Max(pop, b.Fall.MaxFallSpeed) + 1e-3f);
            }
            Assert.True(bounces >= 4);
        }

        [Fact]
        public void VelocityIsMappedForEveryPairOfSurfaces()
        {
            WallFrame[] surfaces = { Rig.Floor(), Rig.Ceiling(), Rig.FarWall(), Rig.LeftWall(), Rig.NearWall() };
            Vec2[] centres = { FloorUV, CeilingUV, FarUV, LeftUV, new Vec2(2f, 1.5f) };
            int tested = 0;
            for (int i = 0; i < surfaces.Length; i++)
            {
                for (int k = 0; k < surfaces.Length; k++)
                {
                    WallFrame entry = surfaces[i], exit = surfaces[k];
                    Vec2 exitUV = i == k ? centres[k] + new Vec2(1.5f, 0.5f) : centres[k];   // same surface: a second patch
                    if (i == k && !entry.IsFloor) continue;
                    Layout l = Make(entry, centres[i], exit, exitUV);
                    PlaySpaceWalker w = new PlaySpaceWalker();

                    // The head 2 cm in front of the middle of the entry patch, flying at it at 3 m/s with a
                    // little drift along the surface.
                    Vec3 headWorld = entry.ToWorld(centres[i], 0.02f);
                    w.Anchor = new RigidTransform(Quat.Identity, headWorld - HeadLocal);
                    Vec3 vIn = entry.Normal * -3f + entry.U * 0.3f;
                    w.SetVelocity(vIn);
                    PlaySpaceFrame f = Tick(w, l, Vec2.Zero);
                    Assert.True(f.Crossed, $"{i} -> {k}: no crossing");
                    Assert.True(f.CrossedFlying);
                    AssertFinite(f.Head);

                    RigidTransform plain = l.Pair.PlayerAToB;
                    Rig.Near(plain.TransformPoint(f.HeadBeforeCrossing), f.Crossing.Head, 4);
                    Vec3 mapped = plain.TransformDirection(f.VelocityBeforeCrossing);
                    if (!exit.IsFloor)
                    {
                        // Same speed, mapped direction: out of the exit as fast as it went into the entry.
                        Rig.Near(mapped, f.VelocityAfterCrossing, 4);
                        Assert.Equal(f.VelocityBeforeCrossing.Length(), f.VelocityAfterCrossing.Length(), 4);
                        Assert.Equal(-Vec3.Dot(f.VelocityBeforeCrossing, entry.Normal), Vec3.Dot(f.VelocityAfterCrossing, exit.Normal), 3);
                    }
                    else
                    {
                        // Up out of a floor: the sideways part is the mapped one, the upward part at least the pop.
                        float pop = w.FloorExitSpeed(HeadLocal.Z);
                        Assert.Equal(mapped.X, f.VelocityAfterCrossing.X, 3);
                        Assert.Equal(mapped.Y, f.VelocityAfterCrossing.Y, 3);
                        Assert.Equal(MathF.Max(pop, mapped.Z), f.VelocityAfterCrossing.Z, 3);
                    }

                    // Two walls: a turn about world up only, and nothing to right. Anything else may tip.
                    bool twoWalls = !entry.IsFlat && !exit.IsFlat;
                    if (twoWalls)
                    {
                        Assert.Equal(0f, f.Crossing.Crossing.Rotation.X, 5);
                        Assert.Equal(0f, f.Crossing.Crossing.Rotation.Y, 5);
                        Assert.Equal(0f, f.Tilt);
                    }
                    Assert.Equal(l.Pair.PlayerKeepsUpright, f.Tilt == 0f);

                    // Whatever happened, a second of frames later nothing is tipped any more.
                    for (int n = 0; n < 90; n++) AssertFinite(Tick(w, l, Vec2.Zero).Head);
                    Assert.Equal(0f, w.Tilt);
                    tested++;
                }
            }
            Assert.Equal(21, tested);       // 5 x 4 different surfaces, and floor to floor
        }

        [Fact]
        public void AirSteeringMovesThePlayerInTheAirBySoMuchOfTheWalkingSpeed()
        {
            Layout l = FloorToWall();
            foreach (float steer in new[] { 1f, 0.5f, 0f })
            {
                PlaySpaceWalker w = WalkerAt(new Vec3(2f, -2f, 3.5f));       // 2 m up in the air, nothing below but floor
                w.AirSteer = steer;
                Tick(w, l, Vec2.Zero, false);                                // one frame to start falling
                Vec3 start = w.Anchor.TransformPoint(HeadLocal);
                PlaySpaceFrame f = default;
                for (int i = 0; i < 27; i++) f = Tick(w, l, new Vec2(0f, 1f), false);
                Assert.True(f.InAir);
                Assert.Equal(2f * 0.3f * steer, f.Head.X - start.X, 3);      // 0.3 s of stick
                Assert.True(f.Head.Z < start.Z - 0.3f);
                Rig.Near(new Vec3(0f, 0f, f.Velocity.Z), f.Velocity);        // steering is not a velocity
            }
        }

        [Fact]
        public void FlungOutOfAWallTheWallsStillStopThePlayerAndTheSlideEnds()
        {
            // Flying at the near wall (x = 0) at 5 m/s, feet a metre up: held 0.2 m from it, then lands and stops.
            Layout l = FloorToWall();
            PlaySpaceWalker w = WalkerAt(new Vec3(1.2f, 0f, 2.5f));
            w.SetVelocity(new Vec3(-5f, 0f, 0f));
            PlaySpaceFrame f = default;
            for (int i = 0; i < 180; i++)
            {
                f = Tick(w, l, Vec2.Zero, false);
                Assert.True(f.Head.X >= 0.2f - 1e-4f);
            }
            Assert.Equal(0.2f, f.Head.X, 3);
            Assert.False(f.InAir);
            Rig.Near(Vec3.Zero, f.Velocity);

            // Landing with sideways speed: a short slide (5 m/s losing 15 m/s a second: about 0.8 m), not a dead stop...
            PlaySpaceWalker slide = WalkerAt(new Vec3(4f, 0f, 1.5f));
            slide.SetVelocity(new Vec3(-5f, 0f, 0f));
            for (int i = 0; i < 90; i++) f = Tick(slide, l, Vec2.Zero, false);
            Assert.InRange(4f - f.Head.X, 0.7f, 0.95f);
            Rig.Near(Vec3.Zero, f.Velocity);
            // ...unless the dial says so.
            PlaySpaceWalker dead = WalkerAt(new Vec3(4f, 0f, 1.5f));
            dead.SlideFriction = 0f;
            dead.SetVelocity(new Vec3(-5f, 0f, 0f));
            for (int i = 0; i < 10; i++) f = Tick(dead, l, Vec2.Zero, false);
            Assert.InRange(4f - f.Head.X, 0f, 0.07f);

            // Thrown up under the ceiling (z = 3): the head stops 0.2 m below it and comes down again.
            PlaySpaceWalker up = WalkerAt(new Vec3(1.5f, -1f, 1.5f));
            up.SetVelocity(new Vec3(0f, 0f, 7f));
            float top = 0f;
            for (int i = 0; i < 200; i++)
            {
                f = Tick(up, l, Vec2.Zero, false);
                top = MathF.Max(top, f.Head.Z);
            }
            Assert.InRange(top, 2.75f, 2.8f + 1e-3f);
            Assert.Equal(1.5f, f.Head.Z, 4);
        }

        [Fact]
        public void ClosingTheHoleUnderASinkingPlayerLiftsThemBackOut()
        {
            Layout l = FloorToWall();
            PlaySpaceWalker w = WalkerAt(new Vec3(2f, 0f, 1.5f));
            PlaySpaceFrame f = default;
            for (int i = 0; i < 30; i++) f = Tick(w, l, Vec2.Zero);
            Assert.InRange(f.Head.Z, 0.8f, 1.1f);                             // a third of a second in: half a metre down
            Assert.False(f.Crossed);
            // The portal goes (the other patch was painted elsewhere): the floor is solid again.
            float time = 0f;
            do { f = Tick(w, l, Vec2.Zero, false); time += Dt; }
            while (f.InAir && time < 2f);
            Assert.Equal(1.5f, f.Head.Z, 4);
            Assert.InRange(time, 0.1f, 0.3f);                                 // about half a metre at 3 m/s
            Rig.Near(Vec3.Zero, w.Velocity);
        }

        [Fact]
        public void AHeadExactlyOnThePlaneIsNeverNotANumber()
        {
            // The head exactly on the floor plane over the patch, the body in the hole, standing still or moving.
            Layout l = FloorToWall();
            foreach (Vec3 velocity in new[] { Vec3.Zero, new Vec3(0f, 0f, -3f), new Vec3(0f, 0f, 3f), new Vec3(1f, 1f, 0f) })
            {
                PlaySpaceWalker w = WalkerAt(new Vec3(2f, 0f, 0f));
                w.SetVelocity(velocity);
                for (int i = 0; i < 120; i++)
                {
                    PlaySpaceFrame f = Tick(w, l, i % 7 == 0 ? new Vec2(0.5f, 0.5f) : Vec2.Zero);
                    AssertFinite(f.Head);
                    AssertFinite(f.Velocity);
                    AssertFinite(w.Anchor.Translation);
                    Assert.True(float.IsFinite(f.Tilt));
                    Assert.Equal(1f, w.Anchor.Rotation.Length(), 4);
                }
            }

            // Exactly on a wall's plane in the middle of its patch, and a frame of no time at all.
            PlaySpaceWalker onWall = WalkerAt(new Vec3(5f, 0f, 1.5f));
            for (int i = 0; i < 10; i++) AssertFinite(Tick(onWall, l, Vec2.Zero).Head);
            PlaySpaceWalker still = WalkerAt(new Vec3(2f, 0f, 0f));
            PlaySpaceFrame frozen = still.Update(HeadLocal, Quat.Identity, new Vec2(0f, 1f), 1f, 0f, 0f, true, l.Pair, l.MaskA, l.MaskB, Walls, Walls.Length, Settings);
            AssertFinite(frozen.Head);
            Rig.Near(new Vec3(2f, 0f, 0f), frozen.Head, 5);

            // The maths under it.
            Assert.Equal(0f, PlaySpace.TiltOf(Quat.Identity));
            Assert.True(float.IsFinite(PlaySpace.TiltOf(new Quat(0f, 0f, 0f, 1f))));
            AssertFinite(PlaySpace.RightAbout(Quat.CreateFromAxisAngle(new Vec3(1f, 0f, 0f), MathF.PI), Vec3.Zero, 1f, Vec3.Zero).Translation);
        }

        [Fact]
        public void BetweenTwoWallsNothingIsDifferentFromBefore()
        {
            // The old layout: orange on the far wall, blue half a metre higher on the left wall.
            Layout l = Make(Rig.FarWall(), FarUV, Rig.LeftWall(), new Vec2(2f, 2.0f), 0.5f, 0.4f);
            PlaySpaceWalker w = WalkerAt(new Vec3(4.5f, 0f, 1.5f));
            PlaySpaceFrame crossing = default;
            for (int i = 0; i < 60 && !crossing.Crossed; i++)
            {
                PlaySpaceFrame f = Tick(w, l, new Vec2(0f, 1f));
                if (f.Crossed) crossing = f;
            }
            Assert.True(crossing.Crossed);
            Assert.False(crossing.CrossedFlying);
            Assert.Equal(0f, crossing.Tilt);
            Assert.Equal(0f, crossing.Righted);
            Rig.Near(Vec3.Zero, crossing.VelocityAfterCrossing);

            // A plain drop: straight down, never faster than the top speed, no drift, lands exactly.
            Vec3 at = crossing.Head;
            PlaySpaceFrame last = crossing;
            for (int i = 0; i < 60; i++)
            {
                PlaySpaceFrame f = Tick(w, l, Vec2.Zero);
                Assert.Equal(at.X, f.Head.X, 5);
                Assert.Equal(at.Y, f.Head.Y, 5);
                Assert.True(f.Head.Z <= last.Head.Z + 1e-6f);
                Assert.Equal(0f, f.Tilt);
                last = f;
            }
            Assert.Equal(1.5f, last.Head.Z, 4);
            Assert.False(w.InAir);
            Rig.Near(Vec3.Zero, w.Velocity);
        }
    }
}
