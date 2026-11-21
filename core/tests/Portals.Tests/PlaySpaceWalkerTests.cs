using System;
using Xunit;

namespace Portaballs.Core.Tests
{
    /// <summary>The VR play space driven by sticks: PlaySpaceWalker.</summary>
    public class PlaySpaceWalkerTests
    {
        private static readonly PortalSettings Settings = new PortalSettings();
        private static readonly PortalEnd A = Rig.End(Rig.FarWall(), PaintColor.Orange, new Vec2(2f, 1.5f), PortalAnchor.PaintCenter, 0f, 0.5f);
        private static readonly PortalEnd B = Rig.End(Rig.LeftWall(), PaintColor.Blue, new Vec2(2f, 2.0f), PortalAnchor.PaintCenter, 0f, 0.4f);
        private static readonly PortalPair Pair = new PortalPair(A, B);
        private static readonly DiscMask MaskA = new DiscMask(A.Wall, PaintColor.Orange, new Vec2(2f, 1.5f), 0.5f);
        private static readonly DiscMask MaskB = new DiscMask(B.Wall, PaintColor.Blue, new Vec2(2f, 2.0f), 0.4f);
        private static readonly WallFrame[] Walls = { Rig.FarWall(), Rig.NearWall(), Rig.LeftWall() };
        private const float Dt = 1f / 90f;

        // The real person stands 0.3 m forward and 0.1 m left of the middle of their play space, head 1.5 m up.
        private static readonly Vec3 HeadLocal = new Vec3(0.3f, 0.1f, 1.5f);
        private static readonly Vec3 HandLocal = new Vec3(0.55f, -0.15f, 1.2f);

        private static PlaySpaceWalker WalkerAt(Vec3 headWorld)
        {
            var w = new PlaySpaceWalker();
            w.Anchor = new RigidTransform(Quat.Identity, headWorld - HeadLocal);
            return w;
        }

        private static PlaySpaceFrame Tick(PlaySpaceWalker w, Vec2 move, float turn, bool linked = true)
        {
            return w.Update(HeadLocal, Quat.Identity, move, turn, Dt, 0f, linked, Pair, MaskA, MaskB, Walls, 3, Settings);
        }

        [Fact]
        public void StickWalksAtTwoMetresASecondAndStopsAtAWall()
        {
            // Facing +X, 1.5 m to the side of the patch: half a second of full stick is 1 m, but the wall
            // is 1 m away, so 0.8 m is free and then the head is held 0.2 m from the wall.
            PlaySpaceWalker w = WalkerAt(new Vec3(4f, -1.5f, 1.5f));
            PlaySpaceFrame f = default;
            for (int i = 0; i < 45; i++) f = Tick(w, new Vec2(0f, 1f), 0f);
            Rig.Near(new Vec3(4.8f, -1.5f, 1.5f), f.Head, 3);
            Assert.True(f.Blocked);
            Assert.False(f.Crossed);

            // In the open: exactly 2 m/s.
            w = WalkerAt(new Vec3(1f, 0f, 1.5f));
            for (int i = 0; i < 90; i++) f = Tick(w, new Vec2(0f, 1f), 0f);
            Rig.Near(new Vec3(3f, 0f, 1.5f), f.Head, 3);
        }

        [Fact]
        public void WalkingIntoTheLinkedPatchCarriesTheWholePlaySpaceThrough()
        {
            PlaySpaceWalker w = WalkerAt(new Vec3(4.5f, 0f, 1.5f));
            PlaySpaceFrame crossing = default;
            int crossings = 0;
            RigidTransform anchorBefore = RigidTransform.Identity;
            for (int i = 0; i < 60 && crossings == 0; i++)
            {
                anchorBefore = w.Anchor;
                PlaySpaceFrame f = Tick(w, new Vec2(0f, 1f), 0f);
                if (f.Crossed) { crossing = f; crossings++; }
            }
            Assert.Equal(1, crossings);
            Assert.True(crossing.Crossing.FromA);

            // The head is where the core's player crossing puts the head it had just before
            // (the same frame's first sliver of falling taken off again).
            Rig.Near(Pair.PlayerAToB.TransformPoint(crossing.HeadBeforeCrossing), crossing.Head - new Vec3(0f, 0f, crossing.Dropped), 4);
            // It is just out of the blue patch on the left wall, and the player now faces -Y.
            Assert.InRange(B.Wall.SignedDistance(crossing.Head), 0f, 0.06f);
            Rig.Near(new Vec3(0f, -1f, 0f), w.Anchor.TransformDirection(new Vec3(1f, 0f, 0f)));

            // The hand is still where it was relative to the head, as the player sees it.
            Vec3 before = Vec3.Transform(HandLocal - HeadLocal, anchorBefore.Rotation);
            Vec3 after = w.Anchor.TransformPoint(HandLocal) - w.Anchor.TransformPoint(HeadLocal);
            Rig.Near(Pair.PlayerAToB.TransformDirection(before), after);

            // Blue is 0.5 m higher than orange: the play space arrives 0.5 m up and falls to the floor.
            Assert.InRange(w.Anchor.Translation.Z, 0.45f, 0.5f);
            for (int i = 0; i < 60; i++) Tick(w, Vec2.Zero, 0f);
            Assert.Equal(0f, w.Anchor.Translation.Z, 4);
            Assert.False(w.Fall.Falling);

            // Keep pushing forward: the player walks out into the room along -Y, no second crossing.
            PlaySpaceFrame later = default;
            for (int i = 0; i < 45; i++)
            {
                later = Tick(w, new Vec2(0f, 1f), 0f);
                Assert.False(later.Crossed);
            }
            Assert.True(later.Head.Y < 2.2f);
        }

        [Fact]
        public void LeaningTheRealHeadThroughAlsoCarriesThePlaySpace()
        {
            // No stick at all: the tracked head itself moves 10 cm forward through the patch.
            PlaySpaceWalker w = WalkerAt(new Vec3(4.96f, 0f, 1.5f));
            Tick(w, Vec2.Zero, 0f);
            PlaySpaceFrame f = w.Update(HeadLocal + new Vec3(0.1f, 0f, 0f), Quat.Identity, Vec2.Zero, 0f, Dt, 0f, true, Pair, MaskA, MaskB, Walls, 3, Settings);
            Assert.True(f.Crossed);
            Assert.InRange(B.Wall.SignedDistance(f.Head), 0.05f, 0.07f);
        }

        [Fact]
        public void SnapTurnIsThirtyDegreesAboutTheHead()
        {
            // Snap turning is a setting now (smooth is the default).
            PlaySpaceWalker w = WalkerAt(new Vec3(1f, 0f, 1.5f));
            w.SmoothTurn = false;
            Vec3 head = w.Anchor.TransformPoint(HeadLocal);
            PlaySpaceFrame f = Tick(w, Vec2.Zero, 1f);
            Assert.Equal(1, f.Turned);
            Rig.Near(head, f.Head);
            Vec3 fwd = w.Anchor.TransformDirection(new Vec3(1f, 0f, 0f));
            Assert.Equal(-30f, MathF.Atan2(fwd.Y, fwd.X) * 180f / MathF.PI, 3);
            // Holding the stick over does not turn again.
            Assert.Equal(0, Tick(w, Vec2.Zero, 1f).Turned);
            // Walking forward now goes 30 degrees to the right of +X. (The head's own orientation inside
            // the play space is unchanged; the play space turned under it.)
            for (int i = 0; i < 90; i++) f = Tick(w, new Vec2(0f, 1f), 0.9f);
            Vec3 moved = f.Head - head;
            Assert.Equal(2f, moved.Length(), 3);
            Assert.Equal(-30f, MathF.Atan2(moved.Y, moved.X) * 180f / MathF.PI, 2);
        }

        // ------------------------------------------------------------------ smooth turning (the default)

        private static float YawDegrees(PlaySpaceWalker w)
        {
            Vec3 fwd = w.Anchor.TransformDirection(new Vec3(1f, 0f, 0f));
            return MathF.Atan2(fwd.Y, fwd.X) * 180f / MathF.PI;
        }

        [Fact]
        public void SmoothTurnIsTheDefaultAndTurnsAtTheSetSpeedAboutTheHead()
        {
            PlaySpaceWalker w = WalkerAt(new Vec3(1f, 0f, 1.5f));
            Assert.True(w.SmoothTurn);
            Assert.Equal(120f, w.SmoothTurnDegreesPerSecond);
            Vec3 head = w.Anchor.TransformPoint(HeadLocal);

            // Full right for half a second: 60 degrees to the right, and the head has not moved.
            PlaySpaceFrame f = default;
            for (int i = 0; i < 45; i++)
            {
                f = Tick(w, Vec2.Zero, 1f);
                Assert.Equal(1, f.Turned);
                Assert.Equal(120f * Dt, f.TurnedDegrees, 4);
                Rig.Near(head, f.Head, 4);
            }
            Assert.Equal(-60f, YawDegrees(w), 2);
            Assert.Equal(0f, w.Anchor.Translation.Z, 5);           // the floor stays the floor
            Assert.Equal(0f, w.Tilt, 5);

            // Letting go stops at once; full left for a quarter second comes back 30 degrees.
            Assert.Equal(0, Tick(w, Vec2.Zero, 0f).Turned);
            Assert.Equal(-60f, YawDegrees(w), 2);
            for (int i = 0; i < 45; i++) f = Tick(w, Vec2.Zero, -1f);
            Assert.Equal(-1, f.Turned);
            Assert.Equal(0f, YawDegrees(w), 2);
            Rig.Near(head, f.Head, 3);
        }

        [Fact]
        public void SmoothTurnSpeedFollowsHowFarTheStickIsPushed()
        {
            // Dead zone 0.2: nothing inside it, half speed half way between it and full, never more than full.
            Assert.Equal(0f, PlaySpace.SmoothTurnRate(0.2f, 0.2f));
            Assert.Equal(0f, PlaySpace.SmoothTurnRate(-0.15f, 0.2f));
            Assert.Equal(0.5f, PlaySpace.SmoothTurnRate(0.6f, 0.2f), 5);
            Assert.Equal(-0.5f, PlaySpace.SmoothTurnRate(-0.6f, 0.2f), 5);
            Assert.Equal(1f, PlaySpace.SmoothTurnRate(1f, 0.2f), 5);
            Assert.Equal(1f, PlaySpace.SmoothTurnRate(1.7f, 0.2f), 5);
            Assert.Equal(0f, PlaySpace.SmoothTurnRate(float.NaN, 0.2f));

            PlaySpaceWalker w = WalkerAt(new Vec3(1f, 0f, 1.5f));
            w.SmoothTurnDegreesPerSecond = 90f;
            for (int i = 0; i < 90; i++) Tick(w, Vec2.Zero, 0.6f);         // half rate for one second
            Assert.Equal(-45f, YawDegrees(w), 2);
            for (int i = 0; i < 90; i++) Assert.Equal(0, Tick(w, Vec2.Zero, 0.19f).Turned);
            Assert.Equal(-45f, YawDegrees(w), 2);
        }

        [Fact]
        public void SmoothTurnGoesWithWalkingInTheSameFrame()
        {
            // Stick forward and turning right together for 1.5 s: the path is an arc of the right length
            // (2 m/s for 1.5 s) and ends heading 180 degrees round (120 degrees a second).
            PlaySpaceWalker w = WalkerAt(new Vec3(1.5f, 1f, 1.5f));
            Vec3 last = w.Anchor.TransformPoint(HeadLocal);
            float path = 0f;
            for (int i = 0; i < 135; i++)
            {
                PlaySpaceFrame f = Tick(w, new Vec2(0f, 1f), 1f, false);
                path += (f.Head - last).Length();
                last = f.Head;
                Assert.False(f.Blocked);
                Assert.Equal(1.5f, f.Head.Z, 4);
            }
            Assert.Equal(3f, path, 2);
            Assert.Equal(180f, MathF.Abs(YawDegrees(w)), 1);
            // A half circle of circumference 2 * 3 m: its ends are a diameter (6 / pi) apart, to the right (-Y).
            Rig.Near(new Vec3(1.5f, 1f - 6f / MathF.PI, 1.5f), last, 1);
        }

        [Fact]
        public void SmoothTurnGoesWithCrossingAndFallingInTheSameFrames()
        {
            // Walk into orange while holding the turn stick a little: one crossing, and the fall out of the
            // higher blue patch still ends on the floor.
            PlaySpaceWalker w = WalkerAt(new Vec3(4.6f, 0f, 1.5f));
            int crossings = 0;
            for (int i = 0; i < 40 && crossings == 0; i++)
            {
                PlaySpaceFrame f = Tick(w, new Vec2(0f, 1f), 0.3f);
                if (f.Crossed) crossings++;
            }
            Assert.Equal(1, crossings);
            float yawAfterCrossing = YawDegrees(w);
            PlaySpaceFrame last = default;
            for (int i = 0; i < 90; i++) last = Tick(w, Vec2.Zero, 1f);
            Assert.Equal(0f, w.Anchor.Translation.Z, 4);
            Assert.False(w.InAir);
            Assert.Equal(0f, last.Tilt);
            // One second of full right turn while falling and after: 120 degrees.
            float turned = MathF.IEEERemainder(YawDegrees(w) - yawAfterCrossing, 360f);
            Assert.Equal(-120f, turned, 1);
        }
    }
}
