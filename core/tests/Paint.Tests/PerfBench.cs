using System;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Portaballs.Core.Tests
{
    /// <summary>
    /// The worst realistic case for a headset frame: a wall 8 x 3.5 m at 64 texels per metre (512 x 224), splats of
    /// radius 0.42 to 0.5 m with the full curtain, and a big merged patch that reaches the floor.
    /// Prints what each call the game makes costs. Never fails on timing (a busy PC would make it flaky): read the
    /// printed numbers. Run it in both builds:
    ///   dotnet test ... --filter "FullyQualifiedName~PerfBench" --logger "console;verbosity=detailed"   (add -c Release)
    /// </summary>
    public class PerfBench
    {
        private readonly ITestOutputHelper _out;

        public PerfBench(ITestOutputHelper output)
        {
            _out = output;
        }

        internal static WallFrame BigWall()
        {
            return LookSheets.Wall(8f, 3.5f);
        }

        internal struct Stat
        {
            public double Median, Worst;

            public override string ToString()
            {
                return Median.ToString("0.000") + " ms median, " + Worst.ToString("0.000") + " ms worst";
            }
        }

        internal static Stat Summarise(double[] ms, int n)
        {
            double[] a = new double[n];
            Array.Copy(ms, a, n);
            Array.Sort(a);
            return new Stat { Median = a[n / 2], Worst = a[n - 1] };
        }

        internal static double Percentile(double[] ms, int n, double share)
        {
            double[] a = new double[n];
            Array.Copy(ms, a, n);
            Array.Sort(a);
            return a[Math.Min(n - 1, (int)(n * share))];
        }

        internal static double Sum(double[] ms, int n)
        {
            double t = 0;
            for (int i = 0; i < n; i++) t += ms[i];
            return t;
        }

        /// <summary>Three joined R=0.5 splats thrown at chest height, run until the curtains have reached the floor and settled.</summary>
        internal static void BuildFloorPatch(PaintSurface s, out Vec2 seed)
        {
            s.Splat(PaintColor.Blue, new Vec2(3.5f, 1.55f), 0.5f, new Vec2(0.8f, -0.5f), 21u, 11f);
            s.Splat(PaintColor.Blue, new Vec2(4.3f, 1.65f), 0.5f, new Vec2(-0.6f, -0.2f), 22u, 11f);
            s.Splat(PaintColor.Blue, new Vec2(5.0f, 1.5f), 0.48f, new Vec2(0.5f, -0.6f), 23u, 11f);
            LookSheets.Run(s, 10f);
            seed = new Vec2(4.3f, 1.65f);
        }

        /// <summary>The same three splats 0.4 s after they hit: curtains on the move.</summary>
        internal static void BuildMoving(PaintSurface s)
        {
            s.Splat(PaintColor.Blue, new Vec2(3.5f, 1.55f), 0.5f, new Vec2(0.8f, -0.5f), 21u, 11f);
            s.Splat(PaintColor.Blue, new Vec2(4.3f, 1.65f), 0.5f, new Vec2(-0.6f, -0.2f), 22u, 11f);
            s.Splat(PaintColor.Blue, new Vec2(5.0f, 1.5f), 0.48f, new Vec2(0.5f, -0.6f), 23u, 11f);
            LookSheets.Run(s, 0.4f);
        }

        // A change to the patch forces a fresh search: alternately punch a small hole and fill it again.
        internal static void Nudge(PaintSurface s, PaintColor c, Vec2 at, int i)
        {
            if (i % 2 == 0) s.EraseDisc(at, 0.04f);
            else s.PaintDisc(c, at, 0.05f);
        }

        private static string BuildName()
        {
#if DEBUG
            return "Debug";
#else
            return "Release";
#endif
        }

        // Everything the measurements call, enough times for the runtime to finish optimising it.
        private static void WarmUp()
        {
            for (int pass = 0; pass < 2; pass++)
            {
                var w = new PaintSurface(BigWall());
                var wf = new OpeningFinder(w);
                byte[] tex = new byte[w.RgbaByteCount];
                for (int i = 0; i < 70; i++)
                {
                    if (i % 6 == 0) w.Clear();
                    PaintColor c = i % 2 == 0 ? PaintColor.Orange : PaintColor.Blue;
                    Vec2 at = new Vec2(1f + 0.09f * i, 1.6f);
                    w.Splat(c, at, 0.45f, new Vec2(1f + 0.2f * (i % 9), -0.5f), (uint)i, 11f);
                    for (int k = 0; k < 20; k++) { w.Step(1f / 90f); w.CopyDirtyToRgba(tex); }
                    wf.Find(c, at);
                    wf.FindDoor(c, 0.4f + 0.001f * i, 0.15f);
                    Nudge(w, c, at, i);
                    wf.Find(c, at);
                    if (i % 5 == 4) w.EraseColor(c);
#if !PAINT_OLD_API
                    var pacer = new PaintPacer();
                    pacer.Add(w, wf);
                    w.QueueSplat(c, at + new Vec2(0.3f, 0.2f), 0.4f, new Vec2(2f, 0f), (uint)(i + 500), 11f);
                    wf.Request(c, at);
                    while (pacer.HasWork)
                    {
                        if (i % 2 == 0) pacer.Advance(20000); else pacer.AdvanceFor(0.05);
                    }
#endif
                }
                // The runtime swaps in its optimised code on a background thread a moment after a method turns hot.
                System.Threading.Thread.Sleep(400);
            }
        }

        [Fact]
        public void PrintWorstCase()
        {
            _out.WriteLine("Build: " + BuildName() + ". Wall 8 x 3.5 m, 512 x 224 texels.");
            var sw = new Stopwatch();
            const int N = 40;
            double[] ms = new double[N];
            WarmUp();

            // ---- One splat, laid in one call ----
            float[] radii = { 0.42f, 0.5f };
            foreach (float R in radii)
            {
                var s = new PaintSurface(BigWall());
                for (int i = 0; i < N; i++)
                {
                    s.Clear();
                    Vec2 vel = new Vec2(1.5f + 0.1f * (i % 7), -1f + 0.2f * (i % 5));
                    sw.Restart();
                    s.Splat(PaintColor.Orange, new Vec2(4f, 1.6f), R, vel, (uint)(100 + i), 12f);
                    ms[i] = sw.Elapsed.TotalMilliseconds;
                }
                _out.WriteLine($"Splat R={R:0.00} m, one call: {Summarise(ms, N)}");
            }
            {
                var s = new PaintSurface(BigWall());
                for (int i = 0; i < N; i++)
                {
                    s.Clear();
                    sw.Restart();
                    s.Splat(PaintColor.Orange, new Vec2(4f, 1.6f), 0.5f, new Vec2(9f, -2f), (uint)(300 + i), 10f);
                    ms[i] = sw.Elapsed.TotalMilliseconds;
                }
                _out.WriteLine($"Splat R=0.50 m, glancing 9 m/s, one call: {Summarise(ms, N)}");
            }

            // ---- Opening search in one call, forced to recompute every time ----
            {
                var s = new PaintSurface(BigWall());
                var f = new OpeningFinder(s);
                Vec2 hit = new Vec2(4f, 2.4f);
                s.Splat(PaintColor.Orange, hit, 0.3f, new Vec2(0.5f, 0f), 5u, 9f);
                LookSheets.Run(s, 10f);
                for (int i = 0; i < N; i++)
                {
                    Nudge(s, PaintColor.Orange, hit + new Vec2(0.05f, 0f), i);
                    sw.Restart();
                    f.Find(PaintColor.Orange, hit);
                    ms[i] = sw.Elapsed.TotalMilliseconds;
                }
                _out.WriteLine($"Opening search, small patch {f.Last(PaintColor.Orange).Area:0.00} m2 ({f.LastTexelsVisited} texels), one call: {Summarise(ms, N)}");
            }
            {
                var s = new PaintSurface(BigWall());
                var f = new OpeningFinder(s);
                Vec2 hit = new Vec2(4f, 2.3f);
                s.Splat(PaintColor.Orange, hit, 0.5f, new Vec2(0.5f, -0.3f), 7u, 10f);
                LookSheets.Run(s, 10f);
                for (int i = 0; i < N; i++)
                {
                    Nudge(s, PaintColor.Orange, hit + new Vec2(0.1f, 0f), i);
                    sw.Restart();
                    f.Find(PaintColor.Orange, hit);
                    ms[i] = sw.Elapsed.TotalMilliseconds;
                }
                _out.WriteLine($"Opening search, large patch {f.Last(PaintColor.Orange).Area:0.00} m2 ({f.LastTexelsVisited} texels), one call: {Summarise(ms, N)}");
            }
            {
                var s = new PaintSurface(BigWall());
                var f = new OpeningFinder(s);
                BuildFloorPatch(s, out Vec2 seed);
                for (int i = 0; i < N; i++)
                {
                    Nudge(s, PaintColor.Blue, seed + new Vec2(0.1f, 0f), i);
                    sw.Restart();
                    f.Find(PaintColor.Blue, seed);
                    ms[i] = sw.Elapsed.TotalMilliseconds;
                }
                var o = f.Last(PaintColor.Blue);
                _out.WriteLine($"Opening search, floor-reaching merged patch {o.Area:0.00} m2, {o.BoundsMax.X - o.BoundsMin.X:0.00} x {o.BoundsMax.Y - o.BoundsMin.Y:0.00} m from v={o.BoundsMin.Y:0.00} ({f.LastTexelsVisited} texels), one call: {Summarise(ms, N)}");
                _out.WriteLine($"   that opening: centre ({o.CenterUV.X:0.0000}, {o.CenterUV.Y:0.0000}), radius {o.InscribedRadius:0.0000}, valid {o.Valid}");

                // The door band on that patch, never cached (a new width each time).
                for (int i = 0; i < N; i++)
                {
                    sw.Restart();
                    f.FindDoor(PaintColor.Blue, 0.5f + 0.001f * i, 0.15f);
                    ms[i] = sw.Elapsed.TotalMilliseconds;
                }
                _out.WriteLine($"Door band on that patch, one call: {Summarise(ms, N)}");
            }

            // ---- The other per-frame calls on the same wall, for scale ----
            {
                var s = new PaintSurface(BigWall());
                byte[] tex = new byte[s.RgbaByteCount];
                BuildMoving(s);
                double[] step = new double[90], copy = new double[90];
                for (int i = 0; i < 90; i++)
                {
                    sw.Restart();
                    s.Step(1f / 90f);
                    step[i] = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    s.CopyDirtyToRgba(tex);
                    copy[i] = sw.Elapsed.TotalMilliseconds;
                }
                _out.WriteLine($"Step(1/90 s) with three fresh curtains running: {Summarise(step, 90)}; copy dirty texels (the first copy holds all three splats): {Summarise(copy, 90)}");

                for (int i = 0; i < N; i++)
                {
                    s.Clear();
                    BuildMoving(s);
                    sw.Restart();
                    s.EraseColor(PaintColor.Blue);
                    ms[i] = sw.Elapsed.TotalMilliseconds;
                }
                _out.WriteLine($"EraseColor of three running curtains: {Summarise(ms, N)}");
            }

#if !PAINT_OLD_API
            // ---- The same work in slices: what any one frame's call costs, and how many frames it takes ----
#if DEBUG
            int unitsPerMs = PaintSurface.WorkUnitsPerMillisecondUnoptimised;
#else
            int unitsPerMs = PaintSurface.WorkUnitsPerMillisecond;
#endif
            double[] slice = new double[200000];
            foreach (bool glancing in new[] { false, true })
            {
                foreach (int byClock in new[] { 1, 0 })
                {
                    var s = new PaintSurface(BigWall());
                    var pacer = new PaintPacer();
                    pacer.Add(s, null);
                    int slices = 0, frames = 0, mostFrames = 0;
                    for (int i = 0; i < N; i++)
                    {
                        s.Clear();
                        Vec2 vel = glancing ? new Vec2(9f, -2f) : new Vec2(1.5f + 0.1f * (i % 7), -1f + 0.2f * (i % 5));
                        s.QueueSplat(PaintColor.Orange, new Vec2(4f, 1.6f), 0.5f, vel, (uint)(100 + i), 12f);
                        int f0 = slices;
                        while (s.QueuedSplatCount > 0)
                        {
                            sw.Restart();
                            if (byClock == 1) pacer.AdvanceFor(1.0); else pacer.Advance(unitsPerMs);
                            slice[slices++] = sw.Elapsed.TotalMilliseconds;
                        }
                        frames += slices - f0;
                        mostFrames = Math.Max(mostFrames, slices - f0);
                    }
                    string how = byClock == 1 ? "AdvanceFor(1.0 ms)" : $"Advance({unitsPerMs} units)";
                    _out.WriteLine($"Sliced splat R=0.50 m{(glancing ? ", glancing 9 m/s" : "")}, {how} per frame: lands after {frames / (double)N:0.0} frames on average (most {mostFrames}); one frame's call: {Summarise(slice, slices)}");
                }
            }
            foreach (int byClock in new[] { 1, 0 })
            {
                var s = new PaintSurface(BigWall());
                var f = new OpeningFinder(s);
                var pacer = new PaintPacer();
                pacer.Add(s, f);
                BuildFloorPatch(s, out Vec2 seed);
                int slices = 0, mostFrames = 0;
                for (int i = 0; i < N; i++)
                {
                    Nudge(s, PaintColor.Blue, seed + new Vec2(0.1f, 0f), i);
                    f.Request(PaintColor.Blue, seed);
                    int f0 = slices;
                    while (f.HasWork)
                    {
                        sw.Restart();
                        if (byClock == 1) pacer.AdvanceFor(1.0); else pacer.Advance(unitsPerMs);
                        slice[slices++] = sw.Elapsed.TotalMilliseconds;
                    }
                    mostFrames = Math.Max(mostFrames, slices - f0);
                }
                string how = byClock == 1 ? "AdvanceFor(1.0 ms)" : $"Advance({unitsPerMs} units)";
                _out.WriteLine($"Sliced opening search, floor-reaching merged patch, {how} per frame: answer after {slices / (double)N:0.0} frames on average (most {mostFrames}); one frame's call: {Summarise(slice, slices)}");
            }

            // ---- A whole session the way the game would run it: balls keep landing on one wall, paint runs,
            //      openings are searched ten times a second, and each frame calls Step, AdvanceFor and the copy.
            //      Run twice: with the paint as the tester saw it an hour before they asked for longer drips (it stopped in
            //      under 3 s), and as it is now (it runs for 7 s, so more of it is alive at once). The session lasts
            //      until the last splat has stopped either way. ----
            foreach (bool hourAgo in new[] { true, false })
            {
                var s = new PaintSurface(BigWall(), hourAgo ? GoldenTests.AsAnHourAgo() : new PaintSettings());
                var f = new OpeningFinder(s);
                var pacer = new PaintPacer();
                pacer.Add(s, f);
                byte[] tex = new byte[s.RgbaByteCount];
                Vec2[] hits = { new Vec2(3.5f, 1.55f), new Vec2(4.3f, 1.65f), new Vec2(5.0f, 1.5f), new Vec2(1.6f, 1.7f), new Vec2(2.2f, 1.6f), new Vec2(6.4f, 1.8f), new Vec2(4.0f, 2.4f), new Vec2(5.6f, 1.4f) };
                PaintColor[] cols = { PaintColor.Blue, PaintColor.Blue, PaintColor.Blue, PaintColor.Orange, PaintColor.Orange, PaintColor.Orange, PaintColor.Blue, PaintColor.Blue };
                Vec2[] seedOf = { hits[3], hits[0] };
                const int Frames = 90 * 12;
                double[] stepMs = new double[Frames], paceMs = new double[Frames], copyMs = new double[Frames], frameMs = new double[Frames];
                int landedSeen = 0, sinceSearch = 0, searches = 0, mostHeads = 0, busyFrames = 0;
                long headSum = 0;
                for (int frame = 0; frame < Frames; frame++)
                {
                    if (frame % 45 == 0 && frame / 45 < hits.Length)
                    {
                        int k = frame / 45;
                        s.QueueSplat(cols[k], hits[k], 0.5f, new Vec2(1f - 0.3f * k, -0.6f), (uint)(700 + k), 11f);
                        seedOf[(int)cols[k]] = hits[k];
                    }
                    mostHeads = Math.Max(mostHeads, s.ActiveDripCount);
                    headSum += s.ActiveDripCount;
                    if (!s.IsDry) busyFrames++;
                    sw.Restart();
                    s.Step(1f / 90f);
                    stepMs[frame] = sw.Elapsed.TotalMilliseconds;

                    sinceSearch++;
                    bool landed = s.SplatsLanded != landedSeen;
                    landedSeen = s.SplatsLanded;
                    if (landed || (sinceSearch >= 9 && !s.IsSettled && !f.HasWork))
                    {
                        f.Request(PaintColor.Orange, seedOf[0]);
                        f.Request(PaintColor.Blue, seedOf[1]);
                        sinceSearch = 0;
                        searches++;
                    }
                    sw.Restart();
                    pacer.AdvanceFor(1.0);
                    paceMs[frame] = sw.Elapsed.TotalMilliseconds;

                    sw.Restart();
                    s.CopyDirtyToRgba(tex);
                    copyMs[frame] = sw.Elapsed.TotalMilliseconds;
                    frameMs[frame] = stepMs[frame] + paceMs[frame] + copyMs[frame];
                }
                _out.WriteLine($"Session of {Frames} frames ({(hourAgo ? "paint as it was an hour ago: wet 2.9 s" : "paint as it is now: wet 7 s")}), 8 big splats on one wall, {searches} search rounds, AdvanceFor(1.0 ms) each frame; {busyFrames} frames had wet paint; moving heads {headSum / (double)Frames:0.0} on average, {mostHeads} at most:");
                _out.WriteLine($"   Step: {Summarise(stepMs, Frames)}, {Percentile(stepMs, Frames, 0.95):0.000} ms 95th percentile, {Sum(stepMs, Frames):0.0} ms in all");
                _out.WriteLine($"   AdvanceFor: {Summarise(paceMs, Frames)}, {Percentile(paceMs, Frames, 0.95):0.000} ms 95th percentile, {Sum(paceMs, Frames):0.0} ms in all");
                _out.WriteLine($"   copy dirty texels: {Summarise(copyMs, Frames)}, {Percentile(copyMs, Frames, 0.95):0.000} ms 95th percentile, {Sum(copyMs, Frames):0.0} ms in all");
                _out.WriteLine($"   all three together in one frame: {Summarise(frameMs, Frames)}, {Percentile(frameMs, Frames, 0.95):0.000} ms 95th percentile, {Percentile(frameMs, Frames, 0.99):0.000} ms 99th, {Sum(frameMs, Frames):0.0} ms in all");
                var ob = f.Last(PaintColor.Blue);
                _out.WriteLine($"   blue opening at the end: centre ({ob.CenterUV.X:0.000}, {ob.CenterUV.Y:0.000}), radius {ob.InscribedRadius:0.000}, valid {ob.Valid}");
            }
#endif
        }
    }
}
