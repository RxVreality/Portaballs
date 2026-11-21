using System;
using Xunit;

namespace Portaballs.Core.Tests
{
    /// <summary>
    /// The work that can be spread over frames (queued splats, requested opening searches, the pacer) must end in
    /// exactly what the all-at-once calls give, bit for bit, however it is sliced.
    /// </summary>
    public class SlicedTests
    {
        internal static bool SameBits(float a, float b)
        {
            return BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
        }

        /// <summary>Both colours' fields and the paint film, texel for texel (and the wetness too when asked).</summary>
        internal static void AssertSameGrid(PaintSurface a, PaintSurface b, bool wetnessToo, string what)
        {
            Assert.Equal(a.Width, b.Width);
            Assert.Equal(a.Height, b.Height);
            for (int y = 0; y < a.Height; y++)
            {
                for (int x = 0; x < a.Width; x++)
                {
                    if (!SameBits(a.GetField(PaintColor.Orange, x, y), b.GetField(PaintColor.Orange, x, y))
                        || !SameBits(a.GetField(PaintColor.Blue, x, y), b.GetField(PaintColor.Blue, x, y))
                        || !SameBits(a.GetThickness(x, y), b.GetThickness(x, y))
                        || (wetnessToo && !SameBits(a.GetWetness(x, y), b.GetWetness(x, y))))
                    {
                        Assert.Fail($"{what}: texel ({x}, {y}) differs: orange {a.GetField(PaintColor.Orange, x, y):g9} / {b.GetField(PaintColor.Orange, x, y):g9}, "
                            + $"blue {a.GetField(PaintColor.Blue, x, y):g9} / {b.GetField(PaintColor.Blue, x, y):g9}, "
                            + $"film {a.GetThickness(x, y):g9} / {b.GetThickness(x, y):g9}, wet {a.GetWetness(x, y):g9} / {b.GetWetness(x, y):g9}");
                    }
                }
            }
        }

        internal static void AssertSameDrips(PaintSurface a, PaintSurface b)
        {
            Assert.Equal(a.ActiveDripCount, b.ActiveDripCount);
            for (int k = 0; k < a.ActiveDripCount; k++)
            {
                a.GetDrip(k, out Vec2 pa, out float ha, out PaintColor ca, out float va);
                b.GetDrip(k, out Vec2 pb, out float hb, out PaintColor cb, out float vb);
                Assert.True(SameBits(pa.X, pb.X) && SameBits(pa.Y, pb.Y) && SameBits(ha, hb) && ca == cb && SameBits(va, vb), $"drip {k} differs");
            }
            PaintBudget ba = a.ComputeBudget(), bb = b.ComputeBudget();
            Assert.Equal(ba.Deposited, bb.Deposited);
            Assert.Equal(ba.OnWall, bb.OnWall);
            Assert.Equal(ba.InDrips, bb.InDrips);
        }

        internal static void AssertSameOpening(PortalOpening a, PortalOpening b, bool revisionToo = true)
        {
            Assert.Equal(a.Valid, b.Valid);
            Assert.Equal(a.Color, b.Color);
            Assert.True(SameBits(a.CenterUV.X, b.CenterUV.X) && SameBits(a.CenterUV.Y, b.CenterUV.Y), $"centre {a.CenterUV} / {b.CenterUV}");
            Assert.True(SameBits(a.InscribedRadius, b.InscribedRadius), $"radius {a.InscribedRadius:g9} / {b.InscribedRadius:g9}");
            Assert.Equal(a.BoundsMin, b.BoundsMin);
            Assert.Equal(a.BoundsMax, b.BoundsMax);
            Assert.True(SameBits(a.Area, b.Area), "area");
            if (revisionToo) Assert.Equal(a.Revision, b.Revision);
        }

        // ------------------------------------------------------------------ queued splats

        [Theory]
        [InlineData(1)]
        [InlineData(333)]
        [InlineData(5000)]
        [InlineData(40000)]
        [InlineData(1000000)]
        public void QueuedSplat_HoweverSliced_IsTheSplat(int budget)
        {
            var direct = new PaintSurface(PerfBench.BigWall());
            var queued = new PaintSurface(PerfBench.BigWall());
            // Something already on the wall and still running, so landing has other paint and drips to deal with.
            direct.Splat(PaintColor.Blue, new Vec2(3.7f, 1.9f), 0.4f, new Vec2(1f, 0f), 3u, 9f);
            queued.Splat(PaintColor.Blue, new Vec2(3.7f, 1.9f), 0.4f, new Vec2(1f, 0f), 3u, 9f);
            LookSheets.Run(direct, 0.5f);
            LookSheets.Run(queued, 0.5f);

            direct.Splat(PaintColor.Orange, new Vec2(4f, 1.6f), 0.5f, new Vec2(3f, -1f), 77u, 12f);
            queued.QueueSplat(PaintColor.Orange, new Vec2(4f, 1.6f), 0.5f, new Vec2(3f, -1f), 77u, 12f);

            Assert.Equal(1, queued.QueuedSplatCount);
            Assert.False(queued.IsSettled);
            int landedBefore = queued.SplatsLanded;
            int slices = 0, total = 0;
            while (queued.QueuedSplatCount > 0)
            {
                // Nothing may show on the wall before the splat lands.
                Assert.False(queued.IsPainted(PaintColor.Orange, new Vec2(4f, 1.6f)));
                int used = queued.AdvanceSplats(budget);
                Assert.True(used > 0, "a call with work waiting must make progress");
                // One slice may run over by one indivisible step, never by more.
                Assert.True(used <= budget + 30000, $"a slice of budget {budget} used {used} units");
                total += used;
                slices++;
                Assert.True(slices < 2000000);
            }
            Assert.Equal(landedBefore + 1, queued.SplatsLanded);
            Assert.Equal(0, queued.AdvanceSplats(budget));
            if (budget < 20000) Assert.True(slices > 3, "a small budget must really slice the work");

            AssertSameGrid(direct, queued, true, $"budget {budget} ({slices} slices, {total} units)");
            AssertSameDrips(direct, queued);
            Assert.Equal(direct.DirtyRect.ToString(), queued.DirtyRect.ToString());
            Assert.Equal(direct.TakeShapeChanges(PaintColor.Orange).ToString(), queued.TakeShapeChanges(PaintColor.Orange).ToString());
            Assert.Equal(direct.TakeShapeChanges(PaintColor.Blue).ToString(), queued.TakeShapeChanges(PaintColor.Blue).ToString());

            // And they stay the same from here on.
            LookSheets.Run(direct, 9f);
            LookSheets.Run(queued, 9f);
            AssertSameGrid(direct, queued, true, "after settling");
        }

        [Theory]
        [InlineData(1f / 90f, 9000)]
        [InlineData(1f / 144f, 2500)]
        [InlineData(1f / 30f, 60000)]
        public void QueuedSplat_PreparedWhileTheWallKeepsStepping_SettlesIntoTheSameWall(float dt, int unitsPerFrame)
        {
            // One wall gets the splat in one call; the other prepares it a slice per frame with Step running in
            // between, at different frame rates. The splat lands a few frames later there, so the two walls are
            // compared once both have settled and dried: same shape, same film, same opening.
            Vec2 hit = new Vec2(4f, 1.55f);
            var direct = new PaintSurface(PerfBench.BigWall());
            var queued = new PaintSurface(PerfBench.BigWall());
            direct.Splat(PaintColor.Orange, hit, 0.5f, new Vec2(1f, -0.5f), 4242u, 11f);
            queued.QueueSplat(PaintColor.Orange, hit, 0.5f, new Vec2(1f, -0.5f), 4242u, 11f);
            int frames = 0;
            while (queued.QueuedSplatCount > 0)
            {
                queued.Step(dt);
                queued.AdvanceSplats(unitsPerFrame);
                frames++;
            }
            Assert.True(frames >= 2, "the budget should have spread the splat over several frames");

            LookSheets.Run(direct, 14f, dt);
            LookSheets.Run(queued, 14f, dt);
            Assert.True(direct.IsDry && queued.IsDry);
            AssertSameGrid(direct, queued, true, $"dt {dt}");

            var fd = new OpeningFinder(direct);
            var fq = new OpeningFinder(queued);
            AssertSameOpening(fd.Find(PaintColor.Orange, hit), fq.Find(PaintColor.Orange, hit));
            Assert.True(fd.Last(PaintColor.Orange).Valid);
            Assert.True(fd.FindDoor(PaintColor.Orange, 0.5f, 0.15f).ReachesFloor, "this splat's curtain should reach the floor");
        }

        [Fact]
        public void QueuedSplats_LandInOrder_AndOtherPaintingWaitsForThem()
        {
            var direct = new PaintSurface(LookSheets.Wall());
            var queued = new PaintSurface(LookSheets.Wall());

            // Three overlapping splats: the order they land in decides which colour is on top.
            direct.Splat(PaintColor.Orange, new Vec2(1.8f, 1.9f), 0.4f, Vec2.Zero, 1u);
            direct.Splat(PaintColor.Blue, new Vec2(2.1f, 1.9f), 0.35f, new Vec2(2f, 0f), 2u);
            direct.Splat(PaintColor.Orange, new Vec2(2.3f, 2.0f), 0.3f, new Vec2(0f, 3f), 3u);
            queued.QueueSplat(PaintColor.Orange, new Vec2(1.8f, 1.9f), 0.4f, Vec2.Zero, 1u);
            queued.QueueSplat(PaintColor.Blue, new Vec2(2.1f, 1.9f), 0.35f, new Vec2(2f, 0f), 2u);
            queued.AdvanceSplats(7000);                         // part-way into the first one
            Assert.Equal(2, queued.QueuedSplatCount);
            queued.Splat(PaintColor.Orange, new Vec2(2.3f, 2.0f), 0.3f, new Vec2(0f, 3f), 3u);   // lands the two waiting ones first
            Assert.Equal(0, queued.QueuedSplatCount);
            Assert.Equal(3, queued.SplatsLanded);
            AssertSameGrid(direct, queued, true, "queue then Splat");
            AssertSameDrips(direct, queued);

            // Erasing and painting shapes also let waiting splats land first.
            direct.Splat(PaintColor.Blue, new Vec2(1.2f, 1.4f), 0.3f, Vec2.Zero, 4u);
            direct.EraseColor(PaintColor.Blue);
            queued.QueueSplat(PaintColor.Blue, new Vec2(1.2f, 1.4f), 0.3f, Vec2.Zero, 4u);
            queued.EraseColor(PaintColor.Blue);
            AssertSameGrid(direct, queued, true, "queue then EraseColor");

            direct.Splat(PaintColor.Blue, new Vec2(1.2f, 1.4f), 0.3f, Vec2.Zero, 5u);
            direct.PaintDisc(PaintColor.Orange, new Vec2(1.2f, 1.4f), 0.1f);
            direct.EraseDisc(new Vec2(1.3f, 1.4f), 0.05f);
            queued.QueueSplat(PaintColor.Blue, new Vec2(1.2f, 1.4f), 0.3f, Vec2.Zero, 5u);
            queued.PaintDisc(PaintColor.Orange, new Vec2(1.2f, 1.4f), 0.1f);
            queued.QueueSplat(PaintColor.Blue, new Vec2(3.5f, 0.5f), 0.0f, Vec2.Zero, 6u);      // radius 0: ignored, as Splat ignores it
            queued.EraseDisc(new Vec2(1.3f, 1.4f), 0.05f);
            AssertSameGrid(direct, queued, true, "queue then PaintDisc and EraseDisc");

            // Clear throws waiting splats away with everything else.
            queued.QueueSplat(PaintColor.Blue, new Vec2(2f, 2f), 0.4f, Vec2.Zero, 7u);
            queued.AdvanceSplats(5000);
            queued.Clear();
            Assert.Equal(0, queued.QueuedSplatCount);
            Assert.True(queued.IsSettled);
            Assert.Equal(0, queued.AdvanceSplats(100000));
            Assert.Equal(0.0, queued.ComputeBudget().Deposited);
            direct.Clear();
            direct.Splat(PaintColor.Orange, new Vec2(2f, 1.5f), 0.45f, new Vec2(1f, 1f), 8u);
            queued.Splat(PaintColor.Orange, new Vec2(2f, 1.5f), 0.45f, new Vec2(1f, 1f), 8u);
            AssertSameGrid(direct, queued, true, "after Clear");
        }

        [Fact]
        public void QueueOverflow_FinishesTheOldestOnTheSpot()
        {
            var direct = new PaintSurface(LookSheets.Wall());
            var queued = new PaintSurface(LookSheets.Wall());
            int n = PaintSurface.MaxQueuedSplats + 3;
            for (int i = 0; i < n; i++)
            {
                Vec2 uv = new Vec2(0.5f + 0.27f * i, 1.2f + 0.1f * (i % 4));
                PaintColor c = i % 2 == 0 ? PaintColor.Orange : PaintColor.Blue;
                direct.Splat(c, uv, 0.25f, new Vec2(i - 5f, 0f), (uint)(40 + i));
                queued.QueueSplat(c, uv, 0.25f, new Vec2(i - 5f, 0f), (uint)(40 + i));
            }
            Assert.Equal(PaintSurface.MaxQueuedSplats, queued.QueuedSplatCount);
            Assert.Equal(3, queued.SplatsLanded);
            // A splat that misses the wall completely still counts as dealt with.
            queued.QueueSplat(PaintColor.Blue, new Vec2(-30f, 1f), 0.3f, Vec2.Zero, 99u);
            direct.Splat(PaintColor.Blue, new Vec2(-30f, 1f), 0.3f, Vec2.Zero, 99u);
            queued.FinishQueuedSplats();
            Assert.Equal(n + 1, queued.SplatsLanded);
            Assert.Equal(direct.SplatsLanded, queued.SplatsLanded);
            AssertSameGrid(direct, queued, true, "overflow");
            AssertSameDrips(direct, queued);
        }

        // ------------------------------------------------------------------ requested opening searches

        private static void BuildTwins(out PaintSurface a, out PaintSurface b, out Vec2 seed)
        {
            a = new PaintSurface(PerfBench.BigWall());
            b = new PaintSurface(PerfBench.BigWall());
            PerfBench.BuildMoving(a);
            PerfBench.BuildMoving(b);
            seed = new Vec2(4.3f, 1.65f);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(700)]
        [InlineData(9000)]
        [InlineData(2000000)]
        public void RequestedSearch_HoweverSliced_IsFind(int budget)
        {
            BuildTwins(out PaintSurface a, out PaintSurface b, out Vec2 seed);
            var direct = new OpeningFinder(a);
            var sliced = new OpeningFinder(b);

            // Three rounds while the curtains are still running, so the patch is different each time.
            for (int round = 0; round < 3; round++)
            {
                PortalOpening want = direct.Find(PaintColor.Blue, seed);
                DoorBand wantDoor = direct.FindDoor(PaintColor.Blue, 0.5f, 0.15f);

                int before = sliced.SearchesFinished(PaintColor.Blue);
                sliced.Request(PaintColor.Blue, seed);
                Assert.True(sliced.IsPending(PaintColor.Blue));
                Assert.True(sliced.HasWork);
                int slices = 0;
                while (sliced.IsPending(PaintColor.Blue))
                {
                    int used = sliced.Advance(budget);
                    Assert.True(used > 0);
                    Assert.True(used <= budget + 12000, $"a slice of budget {budget} used {used} units");
                    slices++;
                    // The paint moves on while the search is being sliced (on both walls, to keep them twins);
                    // the answer must be the one for the paint as it was when the search started.
                    if (budget < 100000)
                    {
                        a.Step(1f / 90f);
                        b.Step(1f / 90f);
                        if (slices == 2)
                        {
                            // A hole punched in the patch after the search has started.
                            a.EraseDisc(seed + new Vec2(0.25f * round - 0.2f, 0.1f), 0.07f);
                            b.EraseDisc(seed + new Vec2(0.25f * round - 0.2f, 0.1f), 0.07f);
                        }
                    }
                    Assert.True(slices < 3000000);
                }
                Assert.Equal(before + 1, sliced.SearchesFinished(PaintColor.Blue));
                Assert.False(sliced.HasWork);
                Assert.Equal(0, sliced.Advance(budget));
                Assert.False(sliced.LastWasCached, "the patch changed since the last search, so this one had real work to do");
                if (budget < 1000) Assert.True(slices > 5, "a small budget must really slice the search");

                AssertSameOpening(want, sliced.Last(PaintColor.Blue));
                DoorBand gotDoor = sliced.FindDoor(PaintColor.Blue, 0.5f, 0.15f);
                Assert.Equal(wantDoor.Found, gotDoor.Found);
                Assert.Equal(wantDoor.LeftU, gotDoor.LeftU);
                Assert.Equal(wantDoor.RightU, gotDoor.RightU);
                Assert.Equal(wantDoor.BottomV, gotDoor.BottomV);
                Assert.Equal(wantDoor.TopV, gotDoor.TopV);
                for (int y = 0; y < a.Height; y += 3)
                    for (int x = 0; x < a.Width; x += 3)
                        Assert.Equal(direct.InLastPatch(PaintColor.Blue, x, y), sliced.InLastPatch(PaintColor.Blue, x, y));

                for (int i = 0; i < 40; i++) { a.Step(1f / 90f); b.Step(1f / 90f); }
            }
        }

        [Fact]
        public void RequestedSearch_NothingChanged_IsFreeAndBothColoursTakeTurns()
        {
            var s = new PaintSurface(LookSheets.Wall());
            var f = new OpeningFinder(s);
            s.PaintDisc(PaintColor.Orange, new Vec2(1f, 1.5f), 0.45f);
            s.PaintDisc(PaintColor.Blue, new Vec2(3f, 1.5f), 0.4f);
            f.Request(PaintColor.Orange, new Vec2(1f, 1.5f));
            f.Request(PaintColor.Blue, new Vec2(3f, 1.5f));
            int guard = 0;
            while (f.HasWork && guard++ < 100000) f.Advance(2000);
            Assert.Equal(1, f.SearchesFinished(PaintColor.Orange));
            Assert.Equal(1, f.SearchesFinished(PaintColor.Blue));
            Assert.True(f.Last(PaintColor.Orange).Valid);
            Assert.True(f.Last(PaintColor.Blue).Valid);
            Assert.InRange(f.Last(PaintColor.Orange).InscribedRadius, 0.42f, 0.455f);
            Assert.InRange(f.Last(PaintColor.Blue).InscribedRadius, 0.37f, 0.405f);
            int rev = f.Last(PaintColor.Orange).Revision;

            // Asking again with nothing changed costs next to nothing and keeps the revision.
            f.Request(PaintColor.Orange, new Vec2(1f, 1.5f));
            int used = f.Advance(1000000);
            Assert.True(used < 200, $"an unchanged patch should be answered from memory, used {used} units");
            Assert.True(f.LastWasCached);
            Assert.Equal(2, f.SearchesFinished(PaintColor.Orange));
            Assert.Equal(rev, f.Last(PaintColor.Orange).Revision);

            // No paint at the seed: the answer is "no opening", and that is news once.
            f.Request(PaintColor.Orange, new Vec2(2f, 2.8f));
            f.Advance(1000000);
            Assert.False(f.Last(PaintColor.Orange).Valid);
            Assert.Equal(rev + 1, f.Last(PaintColor.Orange).Revision);
        }

        [Fact]
        public void FindInTheMiddleOfASlicedSearch_BothStillAnswerRight()
        {
            BuildTwins(out PaintSurface a, out PaintSurface b, out Vec2 seed);
            a.PaintDisc(PaintColor.Orange, new Vec2(1.2f, 2.2f), 0.5f);
            b.PaintDisc(PaintColor.Orange, new Vec2(1.2f, 2.2f), 0.5f);
            var direct = new OpeningFinder(a);
            var sliced = new OpeningFinder(b);

            PortalOpening wantBlue = direct.Find(PaintColor.Blue, seed);
            PortalOpening wantOrange = direct.Find(PaintColor.Orange, new Vec2(1.2f, 2.2f));

            sliced.Request(PaintColor.Blue, seed);
            sliced.Advance(6000);
            sliced.Advance(6000);
            Assert.True(sliced.IsSearching);
            // The game asks for orange right now, in one call, while blue is part-way through.
            PortalOpening gotOrange = sliced.Find(PaintColor.Orange, new Vec2(1.2f, 2.2f));
            AssertSameOpening(wantOrange, gotOrange);
            Assert.True(sliced.IsPending(PaintColor.Blue), "the interrupted search must still be owed");
            Assert.False(sliced.InLastPatch(PaintColor.Blue, (int)(seed.X / b.TexelSizeU), (int)(seed.Y / b.TexelSizeV)), "blue has no finished search yet");
            int guard = 0;
            while (sliced.HasWork && guard++ < 100000) sliced.Advance(6000);
            Assert.Equal(1, sliced.SearchesFinished(PaintColor.Blue));
            AssertSameOpening(wantBlue, sliced.Last(PaintColor.Blue));
            Assert.True(sliced.InLastPatch(PaintColor.Blue, (int)(seed.X / b.TexelSizeU), (int)(seed.Y / b.TexelSizeV)));
            // Orange's patch labels survived blue's search.
            Assert.True(sliced.InLastPatch(PaintColor.Orange, (int)(1.2f / b.TexelSizeU), (int)(2.2f / b.TexelSizeV)));
            AssertSameOpening(wantOrange, sliced.Last(PaintColor.Orange));
        }

        // ------------------------------------------------------------------ the pacer

        [Theory]
        [InlineData(0)]         // by counted work units
        [InlineData(1)]         // by the clock
        public void Pacer_DoesEveryWallsWork_AndEndsWhereTheDirectCallsDo(int mode)
        {
            const int Walls = 3;
            var direct = new PaintSurface[Walls];
            var paced = new PaintSurface[Walls];
            var directFinder = new OpeningFinder[Walls];
            var pacedFinder = new OpeningFinder[Walls];
            var pacer = new PaintPacer();
            Assert.False(pacer.HasWork);
            Assert.Equal(0, pacer.Advance(100000));
            Assert.Equal(0.0, pacer.AdvanceFor(1.0));
            for (int i = 0; i < Walls; i++)
            {
                direct[i] = new PaintSurface(LookSheets.Wall(4f + i, 3f));
                paced[i] = new PaintSurface(LookSheets.Wall(4f + i, 3f));
                directFinder[i] = new OpeningFinder(direct[i]);
                pacedFinder[i] = new OpeningFinder(paced[i]);
                pacer.Add(paced[i], pacedFinder[i]);
            }
            Assert.Equal(Walls, pacer.WallCount);

            Vec2 hit = new Vec2(2f, 1.6f);
            for (int i = 0; i < Walls; i++)
            {
                PaintColor c = i % 2 == 0 ? PaintColor.Orange : PaintColor.Blue;
                direct[i].Splat(c, hit, 0.45f + 0.02f * i, new Vec2(i, -1f), (uint)(900 + i), 11f);
                paced[i].QueueSplat(c, hit, 0.45f + 0.02f * i, new Vec2(i, -1f), (uint)(900 + i), 11f);
                pacedFinder[i].Request(c, hit);
            }
            Assert.True(pacer.HasWork);
            int calls = 0;
            while (pacer.HasWork)
            {
                if (mode == 0) Assert.True(pacer.Advance(20000) > 0);
                else Assert.True(pacer.AdvanceFor(0.05) > 0.0);
                calls++;
                Assert.True(calls < 1000000);
            }
            Assert.True(calls > 3, "the work should have taken several calls");

            for (int i = 0; i < Walls; i++)
            {
                PaintColor c = i % 2 == 0 ? PaintColor.Orange : PaintColor.Blue;
                Assert.Equal(0, paced[i].QueuedSplatCount);
                AssertSameGrid(direct[i], paced[i], true, "wall " + i);
                AssertSameDrips(direct[i], paced[i]);
                // The searches ran after the splats had landed, so they saw the new paint.
                Assert.Equal(1, pacedFinder[i].SearchesFinished(c));
                AssertSameOpening(directFinder[i].Find(c, hit), pacedFinder[i].Last(c));
            }

            pacer.Clear();
            Assert.Equal(0, pacer.WallCount);
            Assert.False(pacer.HasWork);
        }

        [Fact]
        public void Pacer_TimeBudget_CutsTheWorkIntoCalls()
        {
            // Not a speed test (a busy PC could fail one): only that a small time budget really cuts the work into
            // many calls, and that a generous one finishes it in a single call.
            var s = new PaintSurface(PerfBench.BigWall());
            var pacer = new PaintPacer();
            pacer.Add(s, null);
            s.QueueSplat(PaintColor.Orange, new Vec2(4f, 1.6f), 0.5f, new Vec2(9f, -2f), 5u, 12f);
            int calls = 0;
            while (pacer.HasWork)
            {
                pacer.AdvanceFor(0.02);
                calls++;
                Assert.True(calls < 100000);
            }
            Assert.True(calls >= 5, $"a 0.02 ms budget finished a big splat in {calls} calls");

            s.QueueSplat(PaintColor.Blue, new Vec2(4f, 1.6f), 0.5f, new Vec2(9f, -2f), 6u, 12f);
            pacer.AdvanceFor(5000.0);
            Assert.False(pacer.HasWork);
        }

        [Fact]
        public void WarmUp_RunsAndLeavesNothingBehind()
        {
            PaintPacer.WarmUp();
            PaintPacer.WarmUp();
        }
    }
}
