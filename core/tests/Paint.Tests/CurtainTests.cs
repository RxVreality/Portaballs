using System;
using Xunit;
using Xunit.Abstractions;

namespace Portaballs.Core.Tests
{
    /// <summary>The curtain: a big splat's paint slides down into a doorway; small ones don't; height matters.</summary>
    public class CurtainTests
    {
        private readonly ITestOutputHelper _out;

        public CurtainTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private const int Seeds = 6;

        private struct Result
        {
            public float BandW, BandH, BandBottom, BandBottomAtImpact, PatchBottom, PatchBottomAtImpact, SettledAt;
            public int FloorCount;
            public string PerSeed;
        }

        // Average over several seeds of: the door band (at least minWidth wide) at impact and at rest, the patch bottom.
        private static Result Measure(float radius, float height, float minWidth, float strength = 1f)
        {
            var r = new Result();
            for (int k = 0; k < Seeds; k++)
            {
                var s = new PaintSurface(LookSheets.Wall(), new PaintSettings { CurtainStrength = strength });
                var f = new OpeningFinder(s);
                Vec2 hit = new Vec2(2f, height);
                var rng = new Rng(900u + (uint)k);
                s.Splat(PaintColor.Orange, hit, radius, new Vec2(rng.Range(-1.5f, 1.5f), rng.Range(-1f, 1f)), 700u + (uint)k * 13u, 9f);
                var o0 = f.Find(PaintColor.Orange, hit);
                var d0 = f.FindDoor(PaintColor.Orange, minWidth, 0.15f);
                float settled = -1f;
                for (int i = 0; i < 10 * 90; i++)
                {
                    s.Step(1f / 90f);
                    if (settled < 0f && s.IsSettled) settled = (float)s.Time;
                }
                var o1 = f.Find(PaintColor.Orange, hit);
                var d1 = f.FindDoor(PaintColor.Orange, minWidth, 0.15f);
                r.BandW += d1.Width / Seeds;
                r.BandH += d1.Height / Seeds;
                r.BandBottom += d1.BottomV / Seeds;
                r.BandBottomAtImpact += d0.BottomV / Seeds;
                r.PatchBottom += o1.BoundsMin.Y / Seeds;
                r.PatchBottomAtImpact += o0.BoundsMin.Y / Seeds;
                r.SettledAt = MathF.Max(r.SettledAt, settled);
                if (d1.ReachesFloor) r.FloorCount++;
                r.PerSeed += $" [{d1.Width:0.00}x{d1.Height:0.00} from {d1.BottomV:0.00}, patch {o1.BoundsMin.Y:0.00}]";
            }
            return r;
        }

        private void Print(string name, Result r)
        {
            _out.WriteLine($"{name}: band {r.BandW:0.00} m wide x {r.BandH:0.00} m tall, bottom {r.BandBottom:0.00} m (was {r.BandBottomAtImpact:0.00} at impact, moved down {r.BandBottomAtImpact - r.BandBottom:0.00}); " +
                           $"patch bottom {r.PatchBottom:0.00} m (was {r.PatchBottomAtImpact:0.00}); band reaches floor in {r.FloorCount}/{Seeds}; all settled by {r.SettledAt:0.00} s");
            _out.WriteLine("   per seed:" + r.PerSeed);
        }

        [Fact]
        public void BigSplatMakesADoorToTheFloor()
        {
            var r = Measure(0.45f, 1.5f, 0.5f);
            Print("R 0.45 at 1.5 m (band >= 0.5 m wide)", r);
            Assert.True(r.BandW >= 0.5f);
            Assert.True(r.BandH >= 1.2f, $"band only {r.BandH} m tall");
            Assert.True(r.BandBottom <= 0.15f, $"band bottom {r.BandBottom} m above the floor");
            // Averages are the target; one throw in six may have a narrow body whose door is a little short (seed 765).
            Assert.True(r.FloorCount >= Seeds - 1, $"only {r.FloorCount} of {Seeds} reach the floor");
            // 2026-11-19 evening (the tester: "more than three seconds for it to drip"): the paint stays wet for
            // SplatFeel.WetSeconds, so the last creeping drip stops at 7 s. Was: settled by 5.5 s.
            Assert.InRange(r.SettledAt, 6f, 7.5f);
        }

        [Fact]
        public void MediumSplatGrowsButStaysOffTheFloor()
        {
            var r = Measure(0.25f, 1.5f, 0.2f);
            Print("R 0.25 at 1.5 m (band >= 0.2 m wide)", r);
            float grew = r.BandBottomAtImpact - r.BandBottom;
            Assert.InRange(grew, 0.3f, 0.6f);
            Assert.True(r.PatchBottom > 0.2f, "medium splat reached the floor");
        }

        [Fact]
        public void SmallSplatHasNoCurtain()
        {
            var r = Measure(0.1f, 1.5f, 0.08f);
            Print("R 0.10 at 1.5 m (band >= 0.08 m wide)", r);
            Assert.True(r.BandBottomAtImpact - r.BandBottom < 0.03f);
            Assert.True(r.PatchBottomAtImpact - r.PatchBottom < 0.1f);
        }

        [Fact]
        public void HighHitsDoorStaysOffTheFloor()
        {
            // Height still matters: thrown high, the wide part of the curtain (the door band) stops well above the
            // floor. Since the evening of 2026-11-19 paint stays wet longer and runs further, so the thin drips
            // below it now do reach the floor (the tester: "and also pour across the ground"); before, the lowest paint
            // stopped 0.19 m up and this test was called HighHitDoesNotReachTheFloor.
            var r = Measure(0.45f, 2.4f, 0.5f);
            Print("R 0.45 at 2.4 m (band >= 0.5 m wide)", r);
            Assert.Equal(0, r.FloorCount);
            Assert.True(r.BandBottom > 0.3f, $"door band bottom {r.BandBottom}");
            Assert.True(r.PatchBottom < r.BandBottom - 0.2f, "thin drips should run on well below the door band");
        }

        [Fact]
        public void StrengthZeroIsTheOldSag()
        {
            var r0 = Measure(0.45f, 1.5f, 0.5f, 0f);
            var r5 = Measure(0.45f, 1.5f, 0.5f, 0.5f);
            Print("R 0.45 at 1.5 m, strength 0", r0);
            Print("R 0.45 at 1.5 m, strength 0.5", r5);
            Assert.True(r0.BandBottomAtImpact - r0.BandBottom < 0.35f, "strength 0 should only sag a little");
            Assert.True(r5.BandBottom < r0.BandBottom && r5.BandBottom > 0.15f);
        }

        [Fact]
        public void CurtainKeepsTheBudgetAndFrameRateIndependence()
        {
            bool[] a = null;
            foreach (float fps in new[] { 144f, 30f })
            {
                var s = new PaintSurface(LookSheets.Wall());
                s.Splat(PaintColor.Blue, new Vec2(2f, 1.5f), 0.45f, new Vec2(0.5f, 0f), 77u, 9f);
                double dep = s.ComputeBudget().Deposited;
                for (int i = 0; i < (int)(10 * fps); i++)
                {
                    s.Step(1f / fps);
                    if (i % 20 == 0) Assert.True(Math.Abs(s.ComputeBudget().Unaccounted) < dep * 1e-4);
                }
                Assert.True(s.IsSettled);
                var m = new bool[s.Width * s.Height];
                for (int y = 0; y < s.Height; y++)
                for (int x = 0; x < s.Width; x++) m[x + y * s.Width] = s.GetField(PaintColor.Blue, x, y) >= 0.5f;
                if (a == null) a = m;
                else
                {
                    int diff = 0;
                    for (int i = 0; i < m.Length; i++) if (m[i] != a[i]) diff++;
                    _out.WriteLine($"30 fps vs 144 fps: {diff} texels differ at rest");
                    Assert.True(diff <= 5);
                }
                PaintBudget b = s.ComputeBudget();
                _out.WriteLine($"{fps} fps: deposited {b.Deposited * 1e6:0} cm3, on wall {b.OnWall * 1e6:0}, spilled {b.Spilled * 1e6:0.0}, unaccounted {b.Unaccounted:E1}");
            }
        }
    }
}
