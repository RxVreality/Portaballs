using System;
using Xunit;

namespace Portaballs.Core.Tests
{
    /// <summary>Crossing detection, the passage test and exit placement.</summary>
    public class PortalCrossingTests
    {
        // Orange portal on the far wall (plane x = 5), patch a disc of radius 0.5 around uv (2, 1.5) = world (5, 0, 1.5).
        private static readonly WallFrame Wall = Rig.FarWall();
        private static readonly Vec2 Centre = new Vec2(2f, 1.5f);
        private static readonly PortalEnd End = Rig.End(Wall, PaintColor.Orange, Centre, PortalAnchor.PaintCenter);
        private static readonly DiscMask Mask = new DiscMask(Wall, PaintColor.Orange, Centre, 0.5f);

        [Fact]
        public void FastBallIsCaughtMidFrame()
        {
            // 0.3 m toward the wall and 0.3 m sideways in one frame; positions 0.2 m in front and 0.1 m behind.
            CrossingResult r = PortalCrossing.Detect(End, Mask, new Vec3(4.8f, 0f, 1.5f), new Vec3(5.1f, -0.3f, 1.5f));
            Assert.Equal(CrossingKind.Entered, r.Kind);
            Assert.Equal(2f / 3f, r.T, 4);
            Rig.Near(new Vec3(5f, -0.2f, 1.5f), r.Point);
            Rig.Near(new Vec2(2.2f, 1.5f), r.UV);
            Assert.True(r.OnWall);
            Assert.True(r.Painted);
            Assert.True(r.Teleports);
        }

        [Fact]
        public void BallThatSkipsOverTheWallInOneFrameIsStillCaught()
        {
            // 30 m/s at 90 frames per second is 0.333 m per frame: never within 1 cm of the wall at a frame.
            Vec3 prev = new Vec3(5f - 0.3f, 0f, 1.5f);
            Vec3 curr = new Vec3(5f + 0.0333f, 0f, 1.5f);
            CrossingResult r = PortalCrossing.Detect(End, Mask, prev, curr);
            Assert.Equal(CrossingKind.Entered, r.Kind);
            Assert.Equal(0.3f / 0.3333f, r.T, 3);
            Assert.True(r.Teleports);
        }

        [Fact]
        public void SlowCreepIsCaught()
        {
            CrossingResult r = PortalCrossing.Detect(End, Mask, new Vec3(4.999f, 0f, 1.5f), new Vec3(5.001f, 0f, 1.5f));
            Assert.Equal(CrossingKind.Entered, r.Kind);
            Assert.Equal(0.5f, r.T, 2);
            Rig.Near(Centre, r.UV, 3);
            Assert.True(r.Teleports);
        }

        [Fact]
        public void StartingExactlyOnTheWallCountsAsRoomSide()
        {
            CrossingResult r = PortalCrossing.Detect(End, Mask, new Vec3(5f, 0f, 1.5f), new Vec3(5.05f, 0f, 1.5f));
            Assert.Equal(CrossingKind.Entered, r.Kind);
            Assert.Equal(0f, r.T, 5);
            Rig.Near(new Vec3(5f, 0f, 1.5f), r.Point);
        }

        [Fact]
        public void StoppingOnTheWallThenGoingOnEntersExactlyOnce()
        {
            float[] xs = { 4.9f, 5f, 5f, 5.1f, 5.2f };
            int entries = 0;
            int enteredAtStep = -1;
            for (int i = 1; i < xs.Length; i++)
            {
                CrossingResult r = PortalCrossing.Detect(End, Mask, new Vec3(xs[i - 1], 0f, 1.5f), new Vec3(xs[i], 0f, 1.5f));
                if (r.Kind == CrossingKind.Entered)
                {
                    entries++;
                    enteredAtStep = i;
                }
            }
            Assert.Equal(1, entries);
            Assert.Equal(3, enteredAtStep);
        }

        [Fact]
        public void SlidingAlongTheWallIsNotACrossing()
        {
            // Exactly on the wall, moving 0.3 m sideways.
            CrossingResult r = PortalCrossing.Detect(End, Mask, new Vec3(5f, 0f, 1.5f), new Vec3(5f, -0.3f, 1.5f));
            Assert.Equal(CrossingKind.None, r.Kind);
            // Grazing with float noise either side of the wall (5 micrometres): still not a crossing.
            WallFrame w = Wall;
            Vec3 a = w.ToWorld(new Vec2(1.9f, 1.5f), 5e-6f);
            Vec3 b = w.ToWorld(new Vec2(2.1f, 1.5f), -5e-6f);
            Assert.Equal(CrossingKind.None, PortalCrossing.Detect(End, Mask, a, b).Kind);
            // Skimming 1 mm in front of the wall: nothing.
            Assert.Equal(CrossingKind.None, PortalCrossing.Detect(End, Mask,
                new Vec3(4.999f, 0.2f, 1.5f), new Vec3(4.999f, -0.2f, 1.5f)).Kind);
        }

        [Fact]
        public void BackingOutIsNotAnEntry()
        {
            CrossingResult r = PortalCrossing.Detect(End, Mask, new Vec3(5.1f, 0f, 1.5f), new Vec3(4.9f, 0f, 1.5f));
            Assert.Equal(CrossingKind.Backward, r.Kind);
            Assert.Equal(0.5f, r.T, 4);
            Assert.False(r.Teleports);
        }

        [Fact]
        public void CrossingOnBareWallOrTheWrongColourIsNotPainted()
        {
            // Crosses at uv (3.5, 1.5): on the wall but 1.5 m from the patch centre.
            CrossingResult bare = PortalCrossing.Detect(End, Mask, new Vec3(4.9f, -1.5f, 1.5f), new Vec3(5.1f, -1.5f, 1.5f));
            Assert.Equal(CrossingKind.Entered, bare.Kind);
            Rig.Near(new Vec2(3.5f, 1.5f), bare.UV);
            Assert.True(bare.OnWall);
            Assert.False(bare.Painted);
            Assert.False(bare.Teleports);

            // Crosses past the wall's end (u = 5 on a 4 m wall).
            CrossingResult off = PortalCrossing.Detect(End, Mask, new Vec3(4.9f, -3f, 1.5f), new Vec3(5.1f, -3f, 1.5f));
            Assert.Equal(CrossingKind.Entered, off.Kind);
            Assert.False(off.OnWall);
            Assert.False(off.Painted);

            var blueOnly = new DiscMask(Wall, PaintColor.Blue, Centre, 0.5f);
            CrossingResult wrong = PortalCrossing.Detect(End, blueOnly, new Vec3(4.9f, 0f, 1.5f), new Vec3(5.1f, 0f, 1.5f));
            Assert.Equal(CrossingKind.Entered, wrong.Kind);
            Assert.False(wrong.Painted);
        }

        [Fact]
        public void ObjectThatFitsPasses()
        {
            PassageResult r = PortalCrossing.Passage(End, Mask, Centre, 0.3f, new PortalSettings());
            Assert.True(r.Fits);
            Assert.Equal(17, r.TotalSamples);
            Assert.Equal(17, r.PaintedSamples);
            Assert.Equal(1f, r.Fraction, 5);
        }

        [Fact]
        public void ObjectThatNearlyFitsReportsHowClose()
        {
            // Radius 0.3 footprint 0.25 m right of a 0.5 m patch centre. Outline points within 49.5 degrees of
            // +U stick out: angles 0, +-22.5, +-45 = 5 of 16. Centre and the other 11 are painted.
            Vec2 uv = Centre + new Vec2(0.25f, 0f);
            PassageResult r = PortalCrossing.Passage(End, Mask, uv, 0.3f, new PortalSettings());
            Assert.False(r.Fits);
            Assert.Equal(17, r.TotalSamples);
            Assert.Equal(12, r.PaintedSamples);
            Assert.Equal(12f / 17f, r.Fraction, 5);

            // With 8 outline samples (45 degree steps): 0 and +-45 stick out, 6 of 9 painted.
            var coarse = new PortalSettings { PassageOutlineSamples = 8 };
            PassageResult c = PortalCrossing.Passage(End, Mask, uv, 0.3f, coarse);
            Assert.Equal(9, c.TotalSamples);
            Assert.Equal(6, c.PaintedSamples);
        }

        [Fact]
        public void ObjectThatDoesNotFitIsBlocked()
        {
            // A 0.6 m radius body is wider than the 0.5 m patch: only the centre is on paint.
            PassageResult r = PortalCrossing.Passage(End, Mask, Centre, 0.6f, new PortalSettings());
            Assert.False(r.Fits);
            Assert.Equal(1, r.PaintedSamples);
            Assert.Equal(17, r.TotalSamples);

            // Rectangular patch: a 0.2 m hand fits in a 1 m x 0.3 m strip lengthways only if centred.
            var strip = new RectMask(Wall, PaintColor.Orange, new Vec2(1.5f, 1.35f), new Vec2(2.5f, 1.65f));
            Assert.False(PortalCrossing.Passage(End, strip, Centre, 0.2f, new PortalSettings()).Fits);
            Assert.True(PortalCrossing.Passage(End, strip, Centre, 0.14f, new PortalSettings()).Fits);
        }

        [Fact]
        public void ExitPointOnBareWallIsPulledIntoThePatch()
        {
            // Blue exit on the near wall, largest circle radius 0.5 around uv (1, 1.2).
            PortalEnd exit = Rig.End(Rig.NearWall(), PaintColor.Blue, new Vec2(1f, 1.2f), PortalAnchor.PaintCenter, 0f, 0.5f);

            // 0.6 m right of centre, 0.1 m into the room, object radius 0.2: allowed 0.3 m, so moved 0.3 m.
            Vec3 mapped = exit.Wall.ToWorld(new Vec2(1.6f, 1.2f), 0.1f);
            ExitPlacement p = PortalCrossing.PlaceExit(exit, mapped, 0.2f, new PortalSettings());
            Assert.Equal(0.3f, p.MovedBy, 4);
            Assert.True(p.Fits);
            Rig.Near(new Vec3(0.1f, -0.7f, 1.2f), p.Position);

            // Diagonal: offset (0.3, 0.4) is 0.5 m out, pulled to 0.3 m along the same line.
            ExitPlacement d = PortalCrossing.PlaceExit(exit, exit.Wall.ToWorld(new Vec2(1.3f, 1.6f), 0.05f), 0.2f, true);
            Assert.Equal(0.2f, d.MovedBy, 4);
            Rig.Near(new Vec2(1.18f, 1.44f), exit.Wall.ToUV(d.Position));
            Assert.Equal(0.05f, exit.Wall.SignedDistance(d.Position), 4);

            // Already inside: not moved.
            Vec3 inside = exit.Wall.ToWorld(new Vec2(1.1f, 1.3f), 0.1f);
            ExitPlacement i = PortalCrossing.PlaceExit(exit, inside, 0.2f, true);
            Assert.Equal(0f, i.MovedBy);
            Rig.Near(inside, i.Position);

            // Too big for the patch: put at the centre and flagged.
            ExitPlacement big = PortalCrossing.PlaceExit(exit, mapped, 0.6f, true);
            Assert.False(big.Fits);
            Assert.Equal(0.6f, big.MovedBy, 4);
            Rig.Near(exit.Wall.ToWorld(new Vec2(1f, 1.2f), 0.1f), big.Position);

            // Switched off: untouched.
            ExitPlacement off = PortalCrossing.PlaceExit(exit, mapped, 0.2f, new PortalSettings { ClampExitToOpening = false });
            Assert.Equal(0f, off.MovedBy);
            Rig.Near(mapped, off.Position);
        }

        [Fact]
        public void BallThroughBigPatchLandsInsideSmallPatch()
        {
            // Orange patch radius 0.8 on the far wall, blue patch radius 0.4 on the near wall, facing walls.
            PortalEnd a = Rig.End(Rig.FarWall(), PaintColor.Orange, new Vec2(2f, 1.5f), PortalAnchor.PaintCenter, 0f, 0.8f);
            PortalEnd b = Rig.End(Rig.NearWall(), PaintColor.Blue, new Vec2(1f, 1.2f), PortalAnchor.PaintCenter, 0f, 0.4f);
            var pair = new PortalPair(a, b);
            var mask = new DiscMask(a.Wall, PaintColor.Orange, a.Opening.CenterUV, 0.8f);

            // Ball (radius 0.1) enters 0.5 m right of A's centre.
            Vec3 prev = new Vec3(4.95f, -0.5f, 1.5f);
            Vec3 curr = new Vec3(5.05f, -0.5f, 1.5f);
            CrossingResult hit = PortalCrossing.Detect(a, mask, prev, curr);
            Assert.True(hit.Teleports);
            Assert.True(PortalCrossing.Passage(a, mask, hit.UV, 0.1f, new PortalSettings()).Fits);

            Vec3 mapped = pair.AToB.TransformPoint(curr);
            Rig.Near(new Vec3(0.05f, -1.5f, 1.2f), mapped);
            ExitPlacement p = PortalCrossing.PlaceExit(b, mapped, 0.1f, true);
            Assert.Equal(0.2f, p.MovedBy, 4);
            Rig.Near(new Vec3(0.05f, -1.3f, 1.2f), p.Position);
        }
    }
}
