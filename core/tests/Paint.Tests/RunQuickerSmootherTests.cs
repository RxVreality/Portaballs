using System;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Portaballs.Core.Tests
{
    /// <summary>
    /// The tester's requests on 2026-11-19 (see SplatFeel): running paint looks smoother (frame rate), drips run quicker,
    /// and (that evening) they run for longer. "Before" is <see cref="GoldenTests.AsBefore"/> (the settings as they
    /// were before the day's first request) or <see cref="GoldenTests.AsAnHourAgo"/> (as the tester saw them in the headset).
    /// </summary>
    public class RunQuickerSmootherTests
    {
        private readonly ITestOutputHelper _out;

        public RunQuickerSmootherTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static PaintSettings Before()
        {
            return GoldenTests.AsBefore();
        }

        // Lowest wall height (m) with solid paint of that colour anywhere on the wall.
        private static float LowestPaint(PaintSurface s, PaintColor c)
        {
            for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++)
                if (s.GetField(c, x, y) >= 0.5f) return (y + 0.5f) * s.TexelSizeV;
            return float.NaN;
        }

        private static bool[] Mask(PaintSurface s, PaintColor c)
        {
            var m = new bool[s.Width * s.Height];
            for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++) m[x + y * s.Width] = s.GetField(c, x, y) >= 0.5f;
            return m;
        }

        private struct Run
        {
            public float SettledAt, BottomAtImpact, BottomAt1s, BottomAtRest;
            public int Painted;
            public bool[] Mask;
            /// <summary>Painted texels every half second from 0 to 10 s (index = seconds * 2).</summary>
            public int[] PaintedAt;
        }

        private static int Count(PaintSurface s, PaintColor c)
        {
            int n = 0;
            for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++) if (s.GetField(c, x, y) >= 0.5f) n++;
            return n;
        }

        private static Run Throw(PaintSettings set, float R, Vec2 hit, uint seed, Vec2 along)
        {
            var s = new PaintSurface(LookSheets.Wall(), set);
            s.Splat(PaintColor.Orange, hit, R, along, seed, 9f);
            var r = new Run { SettledAt = -1f, BottomAtImpact = LowestPaint(s, PaintColor.Orange), PaintedAt = new int[21] };
            r.PaintedAt[0] = Count(s, PaintColor.Orange);
            const float dt = 1f / 90f;
            for (int i = 1; i <= 10 * 90; i++)
            {
                s.Step(dt);
                if (i == 90) r.BottomAt1s = LowestPaint(s, PaintColor.Orange);
                if (i % 45 == 0) r.PaintedAt[i / 45] = Count(s, PaintColor.Orange);
                if (r.SettledAt < 0f && s.IsSettled) r.SettledAt = i * dt;
            }
            r.BottomAtRest = LowestPaint(s, PaintColor.Orange);
            r.Mask = Mask(s, PaintColor.Orange);
            foreach (bool b in r.Mask) if (b) r.Painted++;
            return r;
        }

        /// <summary>
        /// The tester, 2026-11-19 evening, after watching a splat run at 1.75 times the old speed and stop in under three
        /// seconds: "I love it, however, I would suggest more than three seconds for it to drip". So: it starts as
        /// fast as they saw it, keeps running for six to seven and a half seconds, is still on the move at five,
        /// slows the whole way (fast at first, creeping at the end), and gets further down the wall.
        /// "Before" is the paint exactly as they saw it (<see cref="GoldenTests.AsAnHourAgo"/>).
        /// </summary>
        [Fact]
        public void DripsStartAsFastAndRunForLonger()
        {
            const int Seeds = 6;
            const float R = 0.42f;
            float settleBefore = 0f, settleNow = 0f, fellBefore = 0f, fellNow = 0f, lowerBy = 0f;
            float early = 0f, late = 0f, earlyBefore = 0f, lateBefore = 0f;
            int movingAt5 = 0, paintedBefore = 0, paintedNow = 0;
            float soonest = float.MaxValue, latest = 0f;
            int[] perSecondNow = new int[10], perSecondBefore = new int[10];
            for (int k = 0; k < Seeds; k++)
            {
                var rng = new Rng(4000u + (uint)k);
                // High on the wall, so the floor does not cut the run short.
                Vec2 hit = new Vec2(2f, rng.Range(2.3f, 2.6f));
                Vec2 along = new Vec2(rng.Range(-1.5f, 1.5f), rng.Range(-1f, 0.5f));
                uint seed = 500u + (uint)k * 7u;
                Run a = Throw(GoldenTests.AsAnHourAgo(), R, hit, seed, along);
                Run b = Throw(new PaintSettings(), R, hit, seed, along);
                settleBefore += a.SettledAt / Seeds;
                settleNow += b.SettledAt / Seeds;
                soonest = MathF.Min(soonest, b.SettledAt);
                latest = MathF.Max(latest, b.SettledAt);
                fellBefore += (a.BottomAtImpact - a.BottomAt1s) / Seeds;
                fellNow += (b.BottomAtImpact - b.BottomAt1s) / Seeds;
                lowerBy += (a.BottomAtRest - b.BottomAtRest) / Seeds;
                paintedBefore += a.Painted;
                paintedNow += b.Painted;
                if (b.PaintedAt[11] > b.PaintedAt[9]) movingAt5++;          // between 4.5 and 5.5 s
                for (int sec = 0; sec < 10; sec++)
                {
                    perSecondNow[sec] += b.PaintedAt[sec * 2 + 2] - b.PaintedAt[sec * 2];
                    perSecondBefore[sec] += a.PaintedAt[sec * 2 + 2] - a.PaintedAt[sec * 2];
                }
                // Paint laid in the first second and a half, and in the last second and a half before it stopped.
                early += b.PaintedAt[3] - b.PaintedAt[0];
                int end = (int)MathF.Ceiling(b.SettledAt * 2f);
                late += b.PaintedAt[end] - b.PaintedAt[end - 3];
                earlyBefore += a.PaintedAt[3] - a.PaintedAt[0];
                int endA = (int)MathF.Ceiling(a.SettledAt * 2f);
                lateBefore += a.PaintedAt[endA] - a.PaintedAt[Math.Max(0, endA - 3)];
            }
            _out.WriteLine($"R {R:0.00} thrown high on the wall, {Seeds} throws, at 90 fps. Before = as seen in the headset an hour earlier:");
            _out.WriteLine($"  all running paint stopped after {settleBefore:0.00} s before, {settleNow:0.00} s now (between {soonest:0.00} and {latest:0.00} s)");
            _out.WriteLine($"  lowest paint fell {fellBefore:0.000} m in the first second before, {fellNow:0.000} m now ({fellNow / MathF.Max(1e-4f, fellBefore):0.00}x)");
            _out.WriteLine($"  still laying new paint between 4.5 and 5.5 s in {movingAt5} of {Seeds} throws");
            _out.WriteLine($"  new wall painted in each second (cm2, average per throw), now:    {PerSecond(perSecondNow, Seeds)}");
            _out.WriteLine($"                                                            before: {PerSecond(perSecondBefore, Seeds)}");
            _out.WriteLine($"  new texels painted in the first 1.5 s: {early:0}; in the last 1.5 s before it stopped: {late:0} ({early / MathF.Max(1f, late):0.0}x fewer at the end). Before: {earlyBefore:0} and {lateBefore:0} ({earlyBefore / MathF.Max(1f, lateBefore):0.0}x)");
            _out.WriteLine($"  at rest the lowest paint is {lowerBy:0.000} m lower than before on average, and {100.0 * (paintedNow - paintedBefore) / paintedBefore:0.0}% more wall is painted");

            Assert.InRange(settleNow, 6f, 7.5f);
            Assert.True(soonest >= 5.5f, $"one throw stopped after only {soonest} s");
            Assert.True(settleNow > 2f * settleBefore, $"runs for {settleNow} s now, {settleBefore} s before");
            Assert.InRange(fellNow / fellBefore, 0.85f, 1.2f);                 // it starts as fast as they saw it
            Assert.Equal(Seeds, movingAt5);                                    // still running at 5 s
            Assert.True(early > 6f * MathF.Max(1f, late), "fast at first, creeping at the end");
            Assert.True(lowerBy > 0.1f, $"only {lowerBy} m further down the wall");
            Assert.True(paintedNow > paintedBefore);
        }

        // Texel counts per second as square centimetres of wall per throw (a texel is 1/64 m square).
        private static string PerSecond(int[] texels, int throws)
        {
            var parts = new string[texels.Length];
            for (int i = 0; i < texels.Length; i++) parts[i] = (texels[i] / (float)throws * (10000f / (64f * 64f))).ToString("0");
            return string.Join("  ", parts);
        }

        [Fact]
        public void ASmallSplatWithNoCurtainStillStopsWhenItsPaintRunsOut()
        {
            // Longer wetness does not make a small sag crawl on for seven seconds: with little paint it runs out
            // first, about when it did, a little further down (its trail thins as it goes).
            const int Seeds = 6;
            float settleBefore = 0f, settleNow = 0f, lowerBy = 0f;
            for (int k = 0; k < Seeds; k++)
            {
                var rng = new Rng(4000u + (uint)k);
                Vec2 hit = new Vec2(2f, rng.Range(1.6f, 2.3f));
                Vec2 along = new Vec2(rng.Range(-1.5f, 1.5f), rng.Range(-1f, 0.5f));
                uint seed = 500u + (uint)k * 7u;
                PaintSettings before = GoldenTests.AsAnHourAgo();
                before.CurtainStrength = 0f;
                Run a = Throw(before, 0.30f, hit, seed, along);
                Run b = Throw(new PaintSettings { CurtainStrength = 0f }, 0.30f, hit, seed, along);
                settleBefore += a.SettledAt / Seeds;
                settleNow += b.SettledAt / Seeds;
                lowerBy += (a.BottomAtRest - b.BottomAtRest) / Seeds;
            }
            _out.WriteLine($"R 0.30, no curtain, {Seeds} throws: stopped after {settleBefore:0.00} s before, {settleNow:0.00} s now; lowest paint {lowerBy * 1000f:0} mm lower at rest");
            Assert.InRange(settleNow, settleBefore - 0.3f, 7.5f);
            Assert.InRange(lowerBy, -0.02f, 0.3f);
        }

        // How much paint lies in one column below the impact, in texels of solid paint (a smooth measure: the soft
        // edge makes it move by fractions of a texel). Its change per frame is how far the paint there ran that frame.
        private static float ColumnLength(PaintSurface s, PaintColor c, int x, int belowRow)
        {
            float sum = 0f;
            for (int y = 0; y < belowRow; y++) sum += s.GetField(c, x, y);
            return sum;
        }

        private struct Smoothness
        {
            public float MeanStep, BiggestStep;
            public int Frames, StillFrames;
        }

        private static Smoothness Measure(PaintSettings set, float fps, uint seed)
        {
            var s = new PaintSurface(LookSheets.Wall(), set);
            Vec2 hit = new Vec2(2f, 2.2f);
            s.Splat(PaintColor.Orange, hit, 0.42f, new Vec2(0.2f, -0.3f), seed, 9f);
            int col = (int)(hit.X / s.TexelSizeU);
            int below = (int)((hit.Y - 0.2f) / s.TexelSizeV);
            float dt = 1f / fps;
            float prev = ColumnLength(s, PaintColor.Orange, col, below);
            var m = new Smoothness();
            float sum = 0f;
            // Watch from 0.25 s (the sag has started everywhere) for 0.6 s, while the curtain is clearly on the move.
            for (int i = 1; i <= (int)(0.85f * fps); i++)
            {
                s.Step(dt);
                float now = ColumnLength(s, PaintColor.Orange, col, below);
                if (i * dt >= 0.25f)
                {
                    float d = now - prev;
                    m.Frames++;
                    sum += d;
                    if (d > m.BiggestStep) m.BiggestStep = d;
                    if (d < 0.02f) m.StillFrames++;
                }
                prev = now;
            }
            m.MeanStep = sum / Math.Max(1, m.Frames);
            return m;
        }

        [Fact]
        public void RunningPaintMovesEveryFrame()
        {
            foreach (float fps in new[] { 90f, 72f, 120f })
            {
                Smoothness before = default, now = default;
                const int Seeds = 4;
                for (int k = 0; k < Seeds; k++)
                {
                    // "Before" at today's running speed, so only the frame-rate part differs.
                    PaintSettings old = Before();
                    old.RunSpeedUp = new PaintSettings().RunSpeedUp;
                    Smoothness a = Measure(old, fps, 61u + (uint)k);
                    Smoothness b = Measure(new PaintSettings(), fps, 61u + (uint)k);
                    before.Frames += a.Frames; before.StillFrames += a.StillFrames; before.MeanStep += a.MeanStep / Seeds; before.BiggestStep = MathF.Max(before.BiggestStep, a.BiggestStep);
                    now.Frames += b.Frames; now.StillFrames += b.StillFrames; now.MeanStep += b.MeanStep / Seeds; now.BiggestStep = MathF.Max(now.BiggestStep, b.BiggestStep);
                }
                _out.WriteLine($"{fps} fps, curtain front under the hit, 0.25 to 0.85 s after it:");
                _out.WriteLine($"  before: no visible move in {before.StillFrames} of {before.Frames} frames; per frame {before.MeanStep:0.00} texels on average, {before.BiggestStep:0.00} at most ({before.BiggestStep / before.MeanStep:0.0}x the average)");
                _out.WriteLine($"  now:    no visible move in {now.StillFrames} of {now.Frames} frames; per frame {now.MeanStep:0.00} texels on average, {now.BiggestStep:0.00} at most ({now.BiggestStep / now.MeanStep:0.0}x the average)");

                Assert.True(now.StillFrames <= now.Frames / 20, $"{fps} fps: the paint stood still in {now.StillFrames} of {now.Frames} frames");
                Assert.True(now.BiggestStep < before.BiggestStep, $"{fps} fps: biggest jump {now.BiggestStep} texels now vs {before.BiggestStep} before");
                Assert.True(now.BiggestStep <= 3.5f * now.MeanStep, $"{fps} fps: biggest jump is {now.BiggestStep / now.MeanStep}x the average");
                Assert.True(before.StillFrames > now.StillFrames, $"{fps} fps: the old way was not steppier ({before.StillFrames} still frames)");
            }
        }

        // ---- What it costs ----

        private static double[] Frames(PaintSettings set, Action<PaintSurface> build, WallFrame wall, int frames, out double[] copyMs, out double heads)
        {
            var s = new PaintSurface(wall, set);
            byte[] tex = new byte[s.RgbaByteCount];
            build(s);
            s.CopyDirtyToRgba(tex);
            var sw = new Stopwatch();
            double[] ms = new double[frames];
            copyMs = new double[frames];
            heads = 0;
            for (int i = 0; i < frames; i++)
            {
                heads += s.ActiveDripCount / (double)frames;
                sw.Restart();
                s.Step(1f / 90f);
                ms[i] = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                s.CopyDirtyToRgba(tex);
                copyMs[i] = sw.Elapsed.TotalMilliseconds;
            }
            return ms;
        }

        private static double Mean(double[] a)
        {
            double t = 0;
            foreach (double v in a) t += v;
            return t / a.Length;
        }

        [Fact]
        public void PrintStepCostBeforeAndNow()
        {
#if DEBUG
            _out.WriteLine("Build: Debug (slower than the game will be). Run with -c Release for game-like numbers.");
#else
            _out.WriteLine("Build: Release.");
#endif
            Action<PaintSurface> threeCurtains = PerfBench.BuildMoving;
            Action<PaintSurface> manyDrips = s =>
            {
                float[] xs = { 0.5f, 1.2f, 1.9f, 2.6f, 3.3f, 0.9f, 2.3f, 3.0f, 1.6f };
                uint seed = 100;
                for (int k = 0; k < 9; k++)
                    s.Splat(k % 2 == 0 ? PaintColor.Orange : PaintColor.Blue, new Vec2(xs[k], 2.5f - 0.1f * (k % 3)), 0.4f, Vec2.Zero, seed++, 10f);
                for (int i = 0; i < 20; i++) s.Step(1f / 90f);
            };

            // Warm the runtime up on both paths first.
            for (int w = 0; w < 3; w++)
            {
                Frames(Before(), threeCurtains, PerfBench.BigWall(), 60, out _, out _);
                Frames(new PaintSettings(), threeCurtains, PerfBench.BigWall(), 60, out _, out _);
                Frames(Before(), manyDrips, LookSheets.Wall(), 60, out _, out _);
                Frames(new PaintSettings(), manyDrips, LookSheets.Wall(), 60, out _, out _);
            }

            double worstNowAverage = 0;
            foreach (bool curtains in new[] { true, false })
            {
                foreach (bool now in new[] { false, true })
                {
                    PaintSettings set = now ? new PaintSettings() : Before();
                    double[] step = curtains
                        ? Frames(set, threeCurtains, PerfBench.BigWall(), 180, out double[] copy, out double heads)
                        : Frames(set, manyDrips, LookSheets.Wall(), 180, out copy, out heads);
                    PerfBench.Stat st = PerfBench.Summarise(step, step.Length);
                    PerfBench.Stat ct = PerfBench.Summarise(copy, copy.Length);
                    string scene = curtains ? "three R=0.5 curtains on the 8 x 3.5 m wall, from 0.4 s" : "nine R=0.4 splats' drips on a 4 x 3 m wall";
                    _out.WriteLine($"{(now ? "now   " : "before")}: {scene}, 2 s at 90 fps, {heads:0} heads on average: Step {Mean(step):0.000} ms average, {st}; copy dirty texels {Mean(copy):0.000} ms average, {ct}");
                    if (now) worstNowAverage = Math.Max(worstNowAverage, Mean(step));
                }
            }
            Assert.True(worstNowAverage < 3.0, $"Step averages {worstNowAverage} ms: more than 10x over budget");
        }
    }
}
