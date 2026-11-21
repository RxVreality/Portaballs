using System;
using Xunit;
using Xunit.Abstractions;

namespace Portaballs.Core.Tests
{
    /// <summary>The opening finder against shapes whose largest inscribed circle is known.</summary>
    public class OpeningTests
    {
        private readonly ITestOutputHelper _out;

        public OpeningTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private const float Texel = 1f / 64f;

        private static void AssertCircleInsidePaint(PaintSurface s, PortalOpening o)
        {
            Assert.True(s.IsPainted(o.Color, o.CenterUV));
            for (int k = 0; k < 720; k++)
            {
                float a = k * MathF.PI / 360f;
                Vec2 d = new Vec2(MathF.Cos(a), MathF.Sin(a));
                Assert.True(s.IsPainted(o.Color, o.CenterUV + d * (o.InscribedRadius - 0.05f * Texel)),
                    $"circle edge at {k / 2f} deg is off the paint");
                Assert.True(s.IsPainted(o.Color, o.CenterUV + d * (o.InscribedRadius * 0.5f)));
            }
        }

        private void Check(string name, PaintSurface s, PortalOpening o, float trueR, Vec2? trueCenter, float tolTexels = 2f)
        {
            _out.WriteLine($"{name}: found r={o.InscribedRadius:0.0000} (true {trueR:0.0000}, off by {(o.InscribedRadius - trueR) / Texel:0.00} texels), centre {o.CenterUV}, area {o.Area:0.000} m2");
            Assert.InRange(o.InscribedRadius, trueR - tolTexels * Texel, trueR + 0.25f * Texel);
            if (trueCenter.HasValue) Assert.True(Vector2Distance(o.CenterUV, trueCenter.Value) <= tolTexels * Texel, "centre is off");
            AssertCircleInsidePaint(s, o);
        }

        private static float Vector2Distance(Vec2 a, Vec2 b)
        {
            return (a - b).Length();
        }

        [Fact]
        public void Disc()
        {
            var s = new PaintSurface(LookSheets.Wall());
            var f = new OpeningFinder(s);
            Vec2 c = new Vec2(1.73f, 1.41f);
            s.PaintDisc(PaintColor.Orange, c, 0.5f);
            var o = f.Find(PaintColor.Orange, c + new Vec2(0.3f, 0.1f));
            Check("disc", s, o, 0.5f, c);
            Assert.True(o.Valid);
            Assert.InRange(o.Area, MathF.PI * 0.25f * 0.97f, MathF.PI * 0.25f * 1.03f);
            Assert.InRange(o.BoundsMin.X, c.X - 0.5f - 2 * Texel, c.X - 0.5f + 2 * Texel);
            Assert.InRange(o.BoundsMax.Y, c.Y + 0.5f - 2 * Texel, c.Y + 0.5f + 2 * Texel);
        }

        [Fact]
        public void Rectangle()
        {
            var s = new PaintSurface(LookSheets.Wall());
            var f = new OpeningFinder(s);
            s.PaintBox(PaintColor.Blue, new Vec2(0.6f, 0.5f), new Vec2(2.4f, 1.3f));
            var o = f.Find(PaintColor.Blue, new Vec2(1f, 1f));
            Check("rectangle 1.8 x 0.8", s, o, 0.4f, null);
            Assert.InRange(o.CenterUV.Y, 0.9f - 2 * Texel, 0.9f + 2 * Texel);
            Assert.InRange(o.CenterUV.X, 0.6f + 0.4f - Texel, 2.4f - 0.4f + Texel);
        }

        [Fact]
        public void LShape()
        {
            var s = new PaintSurface(LookSheets.Wall());
            var f = new OpeningFinder(s);
            s.PaintBox(PaintColor.Orange, new Vec2(0.5f, 0.5f), new Vec2(2.5f, 1.1f));
            s.PaintBox(PaintColor.Orange, new Vec2(0.5f, 0.5f), new Vec2(1.1f, 2.5f));
            var o = f.Find(PaintColor.Orange, new Vec2(0.8f, 2.3f));
            // Arms 0.6 wide: the biggest circle sits in the elbow, touching both outer sides and the inner corner.
            float r = 0.6f * MathF.Sqrt(2f) / (1f + MathF.Sqrt(2f));
            Check("L shape", s, o, r, new Vec2(0.5f + r, 0.5f + r), 2.5f);
        }

        [Fact]
        public void TwoPatchesJoinedByANeck()
        {
            var s = new PaintSurface(LookSheets.Wall());
            var f = new OpeningFinder(s);
            Vec2 big = new Vec2(1.1f, 1.5f), small = new Vec2(2.9f, 1.5f);
            s.PaintDisc(PaintColor.Blue, big, 0.5f);
            s.PaintDisc(PaintColor.Blue, small, 0.35f);
            s.PaintBox(PaintColor.Blue, new Vec2(1.5f, 1.47f), new Vec2(2.6f, 1.53f));
            var o = f.Find(PaintColor.Blue, small);
            Check("neck, seeded in the small disc", s, o, 0.5f, big);
            int rev = o.Revision;

            // Cut the neck: now the small disc is its own patch.
            s.EraseDisc(new Vec2(2.05f, 1.5f), 0.1f);
            var o2 = f.Find(PaintColor.Blue, small);
            Check("neck cut, seeded in the small disc", s, o2, 0.35f, small);
            Assert.True(o2.Valid);
            Assert.True(o2.Revision > rev);
        }

        [Fact]
        public void ValidFlipsAtTheMinimumRadius()
        {
            float min = new PaintSettings().OpeningMinRadius;
            float flipAt = -1f;
            for (float r = 0.25f; r <= 0.35f; r += 0.0025f)
            {
                var s = new PaintSurface(LookSheets.Wall());
                var f = new OpeningFinder(s);
                Vec2 c = new Vec2(2f, 1.5f);
                s.PaintDisc(PaintColor.Orange, c, r);
                var o = f.Find(PaintColor.Orange, c);
                Assert.Equal(o.InscribedRadius >= min, o.Valid);
                if (r < min - 2f * Texel) Assert.False(o.Valid, $"r {r} should be too small");
                if (r > min + 0.25f * Texel) Assert.True(o.Valid, $"r {r} should be valid");
                if (o.Valid && flipAt < 0f) flipAt = r;
            }
            _out.WriteLine($"Valid first true for a painted disc of radius {flipAt:0.0000} (setting {min})");
        }

        [Fact]
        public void RevisionOnlyMovesWhenTheShapeChanges()
        {
            var s = new PaintSurface(LookSheets.Wall());
            var f = new OpeningFinder(s);
            Vec2 c = new Vec2(1.5f, 1.5f);
            var none = f.Find(PaintColor.Orange, c);
            Assert.False(none.Valid);

            s.PaintDisc(PaintColor.Orange, c, 0.4f);
            var a = f.Find(PaintColor.Orange, c);
            Assert.True(a.Valid);
            Assert.True(a.Revision > none.Revision);

            var b = f.Find(PaintColor.Orange, c);
            Assert.True(f.LastWasCached);
            Assert.Equal(a.Revision, b.Revision);

            // Paint well away from the patch: no search needed, same revision.
            s.PaintDisc(PaintColor.Orange, new Vec2(3.5f, 0.5f), 0.1f);
            s.PaintDisc(PaintColor.Blue, new Vec2(3.5f, 2.5f), 0.1f);
            var c2 = f.Find(PaintColor.Orange, c);
            Assert.True(f.LastWasCached);
            Assert.Equal(a.Revision, c2.Revision);

            // Paint touching the patch: shape changed.
            s.PaintDisc(PaintColor.Orange, c + new Vec2(0.45f, 0f), 0.1f);
            var d = f.Find(PaintColor.Orange, c);
            Assert.False(f.LastWasCached);
            Assert.True(d.Revision > a.Revision);

            // Blue biting into it changes it too.
            s.PaintDisc(PaintColor.Blue, c + new Vec2(-0.4f, 0f), 0.1f);
            var e = f.Find(PaintColor.Orange, c);
            Assert.True(e.Revision > d.Revision);

            // Re-searching an unchanged patch from a different seed inside it keeps the revision.
            var g = f.Find(PaintColor.Orange, c + new Vec2(0.1f, 0.1f));
            Assert.Equal(e.Revision, g.Revision);
        }

        private void CheckDoor(string name, DoorBand d, float l, float r, float b, float t)
        {
            _out.WriteLine($"{name}: door {d.LeftU:0.000}..{d.RightU:0.000} x {d.BottomV:0.000}..{d.TopV:0.000} (true {l:0.000}..{r:0.000} x {b:0.000}..{t:0.000})");
            Assert.True(d.Found);
            float tol = 1.01f * Texel;
            Assert.InRange(d.LeftU, l - tol, l + tol);
            Assert.InRange(d.RightU, r - tol, r + tol);
            Assert.InRange(d.BottomV, b - tol, b + tol);
            Assert.InRange(d.TopV, t - tol, t + tol);
        }

        [Fact]
        public void DoorBandOnKnownShapes()
        {
            // Rectangle: the whole rectangle.
            var s = new PaintSurface(LookSheets.Wall());
            var f = new OpeningFinder(s);
            s.PaintBox(PaintColor.Blue, new Vec2(0.6f, 0.1f), new Vec2(2.4f, 1.3f));
            f.Find(PaintColor.Blue, new Vec2(1f, 1f));
            var d = f.FindDoor(PaintColor.Blue, 0.5f, 0.15f);
            CheckDoor("rectangle", d, 0.6f, 2.4f, 0.1f, 1.3f);
            Assert.True(d.ReachesFloor);
            Assert.False(f.FindDoor(PaintColor.Blue, 0.5f, 0.05f).ReachesFloor);
            Assert.False(f.FindDoor(PaintColor.Blue, 1.9f, 0.15f).Found); // wider than the rectangle

            // L shape: the tall arm.
            s = new PaintSurface(LookSheets.Wall());
            f = new OpeningFinder(s);
            s.PaintBox(PaintColor.Orange, new Vec2(0.5f, 0.5f), new Vec2(2.5f, 1.1f));
            s.PaintBox(PaintColor.Orange, new Vec2(0.5f, 0.5f), new Vec2(1.1f, 2.5f));
            f.Find(PaintColor.Orange, new Vec2(2f, 0.8f));
            CheckDoor("L shape", f.FindDoor(PaintColor.Orange, 0.5f, 0.15f), 0.5f, 1.1f, 0.5f, 2.5f);

            // Disc: a band 0.5 wide centred in a 0.5 m disc is 2 * sqrt(0.25 - 0.0625) = 0.866 tall.
            s = new PaintSurface(LookSheets.Wall());
            f = new OpeningFinder(s);
            Vec2 c = new Vec2(2f, 1.5f);
            s.PaintDisc(PaintColor.Orange, c, 0.5f);
            f.Find(PaintColor.Orange, c);
            var dd = f.FindDoor(PaintColor.Orange, 0.5f, 0.15f);
            float hh = MathF.Sqrt(0.25f - 0.0625f);
            _out.WriteLine($"disc: door {dd.Width:0.000} x {dd.Height:0.000} (true 0.5 x {2 * hh:0.000})");
            Assert.InRange(dd.Height, 2 * hh - 3 * Texel, 2 * hh + 2 * Texel);
            Assert.InRange(dd.Width, 0.5f - Texel, 0.5f + 2 * Texel);
            Assert.InRange((dd.BottomV + dd.TopV) * 0.5f, c.Y - 1.5f * Texel, c.Y + 1.5f * Texel);
            Assert.False(dd.ReachesFloor);

            // A body with a narrow drip hanging under it: the drip is too narrow to count.
            s = new PaintSurface(LookSheets.Wall());
            f = new OpeningFinder(s);
            c = new Vec2(2f, 2f);
            s.PaintDisc(PaintColor.Blue, c, 0.4f);
            s.PaintBox(PaintColor.Blue, new Vec2(1.98f, 0.05f), new Vec2(2.02f, 1.7f));
            f.Find(PaintColor.Blue, c);
            var db = f.FindDoor(PaintColor.Blue, 0.3f, 0.15f);
            float h2 = MathF.Sqrt(0.16f - 0.0225f);
            _out.WriteLine($"body + drip: door {db.Width:0.000} x {db.Height:0.000}, bottom {db.BottomV:0.000} (true bottom {c.Y - h2:0.000})");
            Assert.InRange(db.BottomV, c.Y - h2 - 2 * Texel, c.Y - h2 + 3 * Texel);
            Assert.False(db.ReachesFloor);
            // Asking for a band as thin as the drip does find the floor.
            var thin = f.FindDoor(PaintColor.Blue, 0.03f, 0.15f);
            Assert.True(thin.ReachesFloor);

            // Cached until the patch changes.
            f.FindDoor(PaintColor.Blue, 0.3f, 0.15f);
            f.FindDoor(PaintColor.Blue, 0.3f, 0.15f);
            Assert.True(f.LastWasCached);
        }

        [Fact]
        public void SplatOpeningIsInsideThePaint()
        {
            var s = new PaintSurface(LookSheets.Wall());
            var f = new OpeningFinder(s);
            Vec2 hit = new Vec2(2f, 2.1f);
            s.Splat(PaintColor.Orange, hit, 0.45f, new Vec2(0.4f, -0.3f), 9u, 10f);
            for (int i = 0; i < 720; i++)
            {
                s.Step(1f / 90f);
                if (i % 45 == 0)
                {
                    var o = f.Find(PaintColor.Orange, hit);
                    Assert.True(o.InscribedRadius > 0.25f);
                    AssertCircleInsidePaint(s, o);
                }
            }
        }
    }
}
