using Xunit;

namespace Portaballs.Core.Tests
{
    /// <summary>Which orange and blue patches are the live portal pair.</summary>
    public class PortalLinkerTests
    {
        private static PortalOpening Patch(PaintColor c, int revision, bool valid = true)
        {
            PortalOpening o = Rig.Opening(c, new Vec2(2f, 1.5f), 0.5f, revision);
            o.Valid = valid;
            return o;
        }

        [Fact]
        public void SequenceOfSplats()
        {
            var linker = new PortalLinker(new PortalSettings { Anchor = PortalAnchor.PaintCenter });
            WallFrame w1 = Rig.FarWall();
            WallFrame w2 = Rig.NearWall();
            WallFrame w3 = Rig.LeftWall();
            const PaintColor O = PaintColor.Orange;
            const PaintColor B = PaintColor.Blue;

            // 1. Orange on wall 1: no pair yet.
            Assert.False(linker.Report(1, w1, 0f, Patch(O, 1)));
            Assert.False(linker.HasPair);
            Assert.Equal(1, linker.LiveWall(O));
            Assert.Equal(-1, linker.LiveWall(B));
            Assert.Equal(0, linker.PairRevision);

            // 2. Blue on wall 2: linked.
            Assert.True(linker.Report(2, w2, 0f, Patch(B, 1)));
            Assert.True(linker.HasPair);
            Assert.Equal(1, linker.PairRevision);
            Rig.Near(new Vec3(5f, 0f, 1.5f), linker.Pair.A.Position);
            Rig.Near(new Vec3(0f, 0f, 1.5f), linker.Pair.B.Position);

            // 3. Same blue report again (paint code reports every frame): nothing changes.
            Assert.False(linker.Report(2, w2, 0f, Patch(B, 1)));
            Assert.Equal(1, linker.PairRevision);

            // 4. Newer orange splat on wall 3 replaces the orange on wall 1.
            Assert.True(linker.Report(3, w3, 0f, Patch(O, 1)));
            Assert.Equal(3, linker.LiveWall(O));
            Assert.Equal(2, linker.PairRevision);
            Rig.Near(new Vec3(2f, 3f, 1.5f), linker.Pair.A.Position);

            // 5. Wall 1's orange is splatted again (its revision goes up): it is the newest orange now.
            Assert.True(linker.Report(1, w1, 0f, Patch(O, 2)));
            Assert.Equal(1, linker.LiveWall(O));
            Assert.Equal(3, linker.PairRevision);

            // 6. Wall 3 repeats its unchanged report: it must not take the portal back.
            Assert.False(linker.Report(3, w3, 0f, Patch(O, 1)));
            Assert.Equal(1, linker.LiveWall(O));
            Assert.Equal(3, linker.PairRevision);

            // 7. Orange splat on wall 2, the same wall as blue: allowed.
            Assert.True(linker.Report(2, w2, 0f, Patch(O, 1)));
            Assert.Equal(2, linker.LiveWall(O));
            Assert.Equal(2, linker.LiveWall(B));
            Assert.True(linker.HasPair);
            Assert.Equal(4, linker.PairRevision);

            // 8. The live orange patch grows (shape change, revision 2): still linked, pair changed.
            Assert.True(linker.Report(2, w2, 0f, Patch(O, 2)));
            Assert.Equal(5, linker.PairRevision);

            // 9. Blue is painted over: unlinked.
            Assert.True(linker.Report(2, w2, 0f, Patch(B, 2, valid: false)));
            Assert.False(linker.HasPair);
            Assert.Equal(-1, linker.LiveWall(B));
            Assert.Equal(6, linker.PairRevision);

            // 10. An old, already replaced orange patch losing validity changes nothing.
            Assert.False(linker.Report(1, w1, 0f, Patch(O, 3, valid: false)));
            Assert.Equal(2, linker.LiveWall(O));
            Assert.Equal(6, linker.PairRevision);

            // 11. New blue splat on wall 2: linked again.
            Assert.True(linker.Report(2, w2, 0f, Patch(B, 3)));
            Assert.True(linker.HasPair);
            Assert.Equal(7, linker.PairRevision);

            // 12. Orange painted over while linked: unlinked, and an old orange does not fall back in.
            Assert.True(linker.Report(2, w2, 0f, Patch(O, 3, valid: false)));
            Assert.False(linker.HasPair);
            Assert.Equal(-1, linker.LiveWall(O));
            Assert.Equal(8, linker.PairRevision);
            Assert.False(linker.Report(3, w3, 0f, Patch(O, 1)));
            Assert.False(linker.HasPair);
        }

        [Fact]
        public void PairUsesTheAnchorSetting()
        {
            var linker = new PortalLinker(new PortalSettings { Anchor = PortalAnchor.FloorAligned });
            linker.Report(1, Rig.FarWall(), 0f, Patch(PaintColor.Orange, 1));
            linker.Report(2, Rig.NearWall(2f), 2f, Patch(PaintColor.Blue, 1));
            Assert.True(linker.HasPair);
            Rig.Near(new Vec3(5f, 0f, 0f), linker.Pair.A.Position);
            Rig.Near(new Vec3(0f, 0f, 2f), linker.Pair.B.Position);
        }
    }
}
