using System;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Portaballs.Core.Tests
{
    /// <summary>
    /// Prints what the paint costs on a 256 x 192 wall. Only fails on something absurd (10x over budget), so a busy
    /// machine can't make it flaky; read the printed numbers.
    /// </summary>
    public class TimingTests
    {
        private readonly ITestOutputHelper _out;

        public TimingTests(ITestOutputHelper output)
        {
            _out = output;
        }

        [Fact]
        public void PrintCosts()
        {
#if DEBUG
            _out.WriteLine("Build: Debug (slower than the game will be). Run with -c Release for game-like numbers.");
#else
            _out.WriteLine("Build: Release.");
#endif
            var s = new PaintSurface(LookSheets.Wall());
            // Warm up the JIT.
            s.Splat(PaintColor.Orange, new Vec2(2f, 2f), 0.45f, new Vec2(1f, 0f), 1u, 10f);
            for (int i = 0; i < 60; i++) s.Step(1f / 90f);
            var finder = new OpeningFinder(s);
            finder.Find(PaintColor.Orange, new Vec2(2f, 2f));
            s.Clear();

            // ---- Stamp ----
            var sw = new Stopwatch();
            int stamps = 30;
            double big = 0, small = 0;
            for (int i = 0; i < stamps; i++)
            {
                s.Clear();
                sw.Restart();
                s.Splat(PaintColor.Orange, new Vec2(2f, 1.8f), 0.45f, new Vec2(2f, 0f), (uint)(i + 10), 12f);
                big += sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                s.Splat(PaintColor.Blue, new Vec2(1f, 1f), 0.15f, new Vec2(2f, 0f), (uint)(i + 50), 12f);
                small += sw.Elapsed.TotalMilliseconds;
            }
            _out.WriteLine($"Stamp, big splat R=0.45 m: {big / stamps:0.000} ms; small splat R=0.15 m: {small / stamps:0.000} ms");

            // ---- Step with about 64 active drips ----
            s.Clear();
            uint seed = 100;
            float[] xs = { 0.5f, 1.2f, 1.9f, 2.6f, 3.3f, 0.9f, 2.3f, 3.0f, 1.6f };
            int k = 0;
            while (s.ActiveDripCount < 64 && k < 40)
            {
                s.Splat(k % 2 == 0 ? PaintColor.Orange : PaintColor.Blue, new Vec2(xs[k % xs.Length], 2.5f - 0.1f * (k % 3)), 0.4f, Vec2.Zero, seed++, 10f);
                k++;
            }
            // Let the sheets turn into drips, then top up to 64 again.
            for (int i = 0; i < 40; i++) s.Step(1f / 90f);
            while (s.ActiveDripCount < 64 && k < 80)
            {
                s.Splat(k % 2 == 0 ? PaintColor.Orange : PaintColor.Blue, new Vec2(xs[k % xs.Length], 2.6f), 0.35f, Vec2.Zero, seed++, 10f);
                k++;
            }
            byte[] tex = new byte[s.RgbaByteCount];
            double stepMs = 0, uploadMs = 0, worst = 0;
            int frames = 0, dripSum = 0;
            for (int i = 0; i < 90; i++)
            {
                dripSum += s.ActiveDripCount;
                sw.Restart();
                s.Step(1f / 90f);
                double t = sw.Elapsed.TotalMilliseconds;
                stepMs += t;
                worst = Math.Max(worst, t);
                sw.Restart();
                s.CopyDirtyToRgba(tex);
                uploadMs += sw.Elapsed.TotalMilliseconds;
                frames++;
            }
            _out.WriteLine($"Step(1/90 s) with on average {dripSum / (float)frames:0} active heads: {stepMs / frames:0.000} ms average, {worst:0.000} ms worst; copying the dirty texels {uploadMs / frames:0.000} ms");

            double step30 = 0;
            for (int i = 0; i < 20; i++)
            {
                sw.Restart();
                s.Step(1f / 30f);
                step30 += sw.Elapsed.TotalMilliseconds;
            }
            _out.WriteLine($"Step(1/30 s) (4 inner steps), {s.ActiveDripCount} heads left: {step30 / 20:0.000} ms");

            // ---- Opening search on a big patch (forced to recompute every time) ----
            s.Clear();
            var f = new OpeningFinder(s);
            Vec2 hit = new Vec2(2f, 2.1f);
            s.Splat(PaintColor.Orange, hit, 0.5f, Vec2.Zero, 7u, 10f);
            for (int i = 0; i < 900; i++) s.Step(1f / 90f);
            double typical = 0;
            int typicalVisited = 0;
            for (int i = 0; i < 20; i++)
            {
                if (i % 2 == 0) s.EraseDisc(hit + new Vec2(0.1f, 0f), 0.04f);
                else s.PaintDisc(PaintColor.Orange, hit + new Vec2(0.1f, 0f), 0.05f);
                sw.Restart();
                f.Find(PaintColor.Orange, hit);
                typical += sw.Elapsed.TotalMilliseconds;
                typicalVisited = f.LastTexelsVisited;
            }
            _out.WriteLine($"Opening search, one settled R=0.5 splat, {f.Last(PaintColor.Orange).Area:0.00} m2 ({typicalVisited} texels visited): {typical / 20:0.000} ms");

            double door = 0;
            for (int i = 0; i < 20; i++)
            {
                f.FindDoor(PaintColor.Orange, 0.5f + 0.001f * i, 0.15f); // a new width each time, so never cached
                sw.Restart();
                f.FindDoor(PaintColor.Orange, 0.6f + 0.001f * i, 0.15f);
                door += sw.Elapsed.TotalMilliseconds;
            }
            sw.Restart();
            f.FindDoor(PaintColor.Orange, 0.6f + 0.001f * 19, 0.15f);
            _out.WriteLine($"Door band query on that patch: {door / 20:0.000} ms; asked again unchanged (cached): {sw.Elapsed.TotalMilliseconds:0.0000} ms");

            s.PaintBox(PaintColor.Orange, new Vec2(0.2f, 0.2f), new Vec2(3.8f, 0.8f)); // make the patch huge: joins the drips
            double search = 0;
            int searches = 20, visited = 0;
            for (int i = 0; i < searches; i++)
            {
                // A change to the patch forces a fresh search: alternately punch a small hole and fill it again.
                if (i % 2 == 0) s.EraseDisc(hit + new Vec2(0.1f, 0f), 0.04f);
                else s.PaintDisc(PaintColor.Orange, hit + new Vec2(0.1f, 0f), 0.05f);
                sw.Restart();
                var o = f.Find(PaintColor.Orange, hit);
                search += sw.Elapsed.TotalMilliseconds;
                visited = f.LastTexelsVisited;
                Assert.False(f.LastWasCached);
            }
            sw.Restart();
            f.Find(PaintColor.Orange, hit);
            double cached = sw.Elapsed.TotalMilliseconds;
            var last = f.Last(PaintColor.Orange);
            _out.WriteLine($"Opening search, patch {last.Area:0.00} m2 ({visited} texels visited): {search / searches:0.000} ms; unchanged (cached): {cached:0.0000} ms");

            Assert.True(stepMs / frames < 3.0, "step is more than 10x over budget");
        }
    }
}
