using System;
using System.Collections.Generic;
using Xunit;
using Xunit.Abstractions;

namespace Portaballs.Core.Tests
{
    /// <summary>
    /// The tester, 2026-11-19 evening: "more than three seconds for it to drip and also pour across the ground as well as it
    /// puddles at the bottom. That's something naturally that would happen in physics."
    /// Paint follows gravity along its own surface; what reaches the bottom of a wall is reported and poured onto the
    /// floor, where it grows a puddle that stops; a floor splat settles; a ceiling splat lets drops fall; and none of
    /// that loose paint can touch a portal patch.
    /// </summary>
    public class RunOffTests
    {
        private readonly ITestOutputHelper _out;

        public RunOffTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private const float Dt = 1f / 90f;

        // The test wall tipped back from upright by this many degrees (0 = the wall, 90 = a floor).
        private static WallFrame Slope(float degrees)
        {
            float a = degrees * MathF.PI / 180f;
            return new WallFrame(new Vec3(5f, 2f, 0f), new Vec3(0f, -1f, 0f), new Vec3(MathF.Sin(a), 0f, MathF.Cos(a)), 4f, 3f);
        }

        // An upright wall whose own U axis points up, so "down" is along minus U instead of minus V.
        private static WallFrame WallOnItsSide()
        {
            return new WallFrame(new Vec3(5f, 0f, 0f), new Vec3(0f, 0f, 1f), new Vec3(0f, 1f, 0f), 3f, 4f);
        }

        private struct Box
        {
            public float U0, V0, U1, V1;
            public int Texels;
        }

        private static Box Painted(PaintSurface s, PaintColor c)
        {
            var b = new Box { U0 = float.MaxValue, V0 = float.MaxValue, U1 = float.MinValue, V1 = float.MinValue };
            for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++)
            {
                if (s.GetField(c, x, y) < 0.5f) continue;
                Vec2 p = s.TexelCenter(x, y);
                b.U0 = MathF.Min(b.U0, p.X); b.U1 = MathF.Max(b.U1, p.X);
                b.V0 = MathF.Min(b.V0, p.Y); b.V1 = MathF.Max(b.V1, p.Y);
                b.Texels++;
            }
            return b;
        }

        // How far along "down" the leading running head is (metres along the surface's own run direction).
        private static float Front(PaintSurface s)
        {
            Vec2 d = s.RunDirection;
            float far = float.MinValue;
            for (int i = 0; i < s.ActiveDripCount; i++)
            {
                s.GetDrip(i, out Vec2 p, out _, out _, out _);
                far = MathF.Max(far, p.X * d.X + p.Y * d.Y);
            }
            return far;
        }

        private static float[] Field(PaintSurface s, PaintColor c)
        {
            var f = new float[s.Width * s.Height];
            for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++) f[x + y * s.Width] = s.GetField(c, x, y);
            return f;
        }

        private static void AssertBudget(PaintSurface s, string what)
        {
            PaintBudget b = s.ComputeBudget();
            Assert.True(Math.Abs(b.Unaccounted) <= Math.Max(1e-12, b.Deposited * 1e-4), $"{what}: {b.Unaccounted} of {b.Deposited} m3 is unaccounted for");
        }

        // ------------------------------------------------------------------ gravity along the surface

        [Fact]
        public void EachSurfaceWorksOutWhichWayIsDown()
        {
            var wall = new PaintSurface(LookSheets.Wall());
            Assert.Equal(new Vec2(0f, -1f), wall.GravityAlong);
            Assert.Equal(0f, wall.GravityOff);
            Assert.True(wall.PaintRuns);
            Assert.Equal(new Vec2(0f, -1f), wall.RunDirection);
            Assert.False(wall.IsFloorLike || wall.IsCeilingLike);

            var floor = new PaintSurface(LookSheets.FloorUnder(LookSheets.Wall()));
            Assert.Equal(new Vec2(0f, 0f), floor.GravityAlong);
            Assert.Equal(-1f, floor.GravityOff);
            Assert.False(floor.PaintRuns);
            Assert.True(floor.IsFloorLike);
            Assert.True(Vec3.Dot(floor.Wall.Normal, new Vec3(0f, 0f, 1f)) > 0.999f, "the test floor should face up");

            var ceiling = new PaintSurface(LookSheets.CeilingOver(LookSheets.Wall()));
            Assert.Equal(new Vec2(0f, 0f), ceiling.GravityAlong);
            Assert.Equal(1f, ceiling.GravityOff);
            Assert.False(ceiling.PaintRuns);
            Assert.True(ceiling.IsCeilingLike);

            var slope = new PaintSurface(Slope(60f));
            Assert.Equal(0f, slope.GravityAlong.X);
            Assert.InRange(slope.GravityAlong.Y, -0.5001f, -0.4999f);
            Assert.True(slope.PaintRuns);
            Assert.Equal(new Vec2(0f, -1f), slope.RunDirection);
            Assert.True(slope.GravityOff < 0f, "a ramp you could stand on presses its paint on");

            var side = new PaintSurface(WallOnItsSide());
            Assert.Equal(new Vec2(-1f, 0f), side.GravityAlong);
            Assert.Equal(new Vec2(-1f, 0f), side.RunDirection);

            // Nearly level: too little pull for paint to run.
            Assert.False(new PaintSurface(Slope(87f)).PaintRuns);
            Assert.True(new PaintSurface(Slope(80f)).PaintRuns);
        }

        [Fact]
        public void PaintRunsTheWayGravityPullsAlongItsSurface()
        {
            // The same splat on a wall, on a wall whose axes are turned, on a ramp, on a floor and on a ceiling.
            var wall = new PaintSurface(LookSheets.Wall());
            var side = new PaintSurface(WallOnItsSide());
            var ramp = new PaintSurface(Slope(60f));
            var floor = new PaintSurface(LookSheets.FloorUnder(LookSheets.Wall(), 3f));
            var ceiling = new PaintSurface(LookSheets.CeilingOver(LookSheets.Wall(), 3f));

            // The turned wall gets the same splat turned with it: what is (right, up) on the wall is (-V, U) there.
            Vec2 along = new Vec2(0.6f, -0.4f);
            wall.Splat(PaintColor.Orange, new Vec2(2f, 2.2f), 0.42f, along, 31u, 9f);
            side.Splat(PaintColor.Orange, new Vec2(2.2f, 2f), 0.42f, new Vec2(along.Y, -along.X), 31u, 9f);
            ramp.Splat(PaintColor.Orange, new Vec2(2f, 2.2f), 0.42f, along, 31u, 9f);
            floor.Splat(PaintColor.Orange, new Vec2(2f, 1.5f), 0.42f, Vec2.Zero, 31u, 9f);
            ceiling.Splat(PaintColor.Orange, new Vec2(2f, 1.5f), 0.42f, Vec2.Zero, 31u, 9f);
            Box w0 = Painted(wall, PaintColor.Orange), s0 = Painted(side, PaintColor.Orange), r0 = Painted(ramp, PaintColor.Orange);
            Box f0 = Painted(floor, PaintColor.Orange), c0 = Painted(ceiling, PaintColor.Orange);
            float wh0 = Front(wall), sh0 = Front(side), rh0 = Front(ramp);
            Assert.True(wall.ActiveDripCount > 0 && side.ActiveDripCount > 0 && ramp.ActiveDripCount > 0);
            Assert.Equal(0, floor.ActiveDripCount);
            Assert.Equal(0, ceiling.ActiveDripCount);

            // A second in: every running head is still on its way (none has run out, none has reached an edge).
            foreach (var s in new[] { wall, side, ramp, floor, ceiling }) LookSheets.Run(s, 1.0f);
            Box w1 = Painted(wall, PaintColor.Orange), s1 = Painted(side, PaintColor.Orange), r1 = Painted(ramp, PaintColor.Orange);
            // How far the leading running head has gone along "down".
            float wallRan = Front(wall) - wh0, sideRan = Front(side) - sh0, rampRan = Front(ramp) - rh0;
            _out.WriteLine($"after 1 s the leading head had run: wall {wallRan:0.000} m down V; wall on its side {sideRan:0.000} m down U; 60 degree ramp (half the pull) {rampRan:0.000} m ({rampRan / wallRan:0.00} of the wall's)");
            Assert.True(wallRan > 0.3f, $"the wall's paint ran {wallRan} m");
            Assert.True(w0.V0 - w1.V0 > 0.1f && s0.U0 - s1.U0 > 0.1f, "the painted shape should have grown downward");
            Assert.InRange(sideRan / wallRan, 0.9f, 1.1f);   // the same paint, the same distance, whichever way the axes lie
            // Nothing runs UP or sideways.
            Assert.True(MathF.Abs(w1.V1 - w0.V1) < 0.02f && MathF.Abs(w1.U0 - w0.U0) < 0.05f && MathF.Abs(w1.U1 - w0.U1) < 0.05f);
            Assert.True(MathF.Abs(s1.U1 - s0.U1) < 0.02f && MathF.Abs(s1.V0 - s0.V0) < 0.05f && MathF.Abs(s1.V1 - s0.V1) < 0.05f);
            // Half the pull, the same splat the same way up: half as far.
            Assert.InRange(rampRan / wallRan, 0.42f, 0.58f);
            Assert.True(r0.V0 - r1.V0 >= 0f && MathF.Abs(r1.V1 - r0.V1) < 0.02f);

            // To rest. The floor's splat only settles a little; the ceiling's does not move at all.
            foreach (var s in new[] { wall, side, ramp, floor, ceiling })
            {
                LookSheets.Run(s, 9f);
                Assert.True(s.IsSettled);
                AssertBudget(s, "after settling");
            }
            Assert.Equal(0, floor.ActiveDripCount);
            Box f1 = Painted(floor, PaintColor.Orange), c1 = Painted(ceiling, PaintColor.Orange);
            float grew = MathF.Max(MathF.Max(f0.U0 - f1.U0, f1.U1 - f0.U1), MathF.Max(f0.V0 - f1.V0, f1.V1 - f0.V1));
            _out.WriteLine($"floor splat: {f0.Texels} texels at landing, {f1.Texels} at rest; its box grew by at most {grew:0.000} m. Ceiling splat: {c0.Texels} -> {c1.Texels} texels");
            Assert.True(grew < 0.1f, $"the floor splat's box grew {grew} m: something ran");
            Assert.Equal(c0.Texels, c1.Texels);
            Assert.Equal(c0.U0, c1.U0);
            Assert.Equal(c0.V1, c1.V1);
        }

        // ------------------------------------------------------------------ run-off at the bottom edge

        private struct RunOffTotals
        {
            public int Streams;
            public double Volume;
            public float FirstAt, LastAt, MinU, MaxU;
        }

        private RunOffTotals RunWall(PaintSurface wall, float seconds)
        {
            var t = new RunOffTotals { FirstAt = -1f, MinU = float.MaxValue, MaxU = float.MinValue };
            int n = (int)MathF.Round(seconds / Dt);
            for (int i = 1; i <= n; i++)
            {
                wall.Step(Dt);
                for (int k = 0; k < wall.RunOffCount; k++)
                {
                    PaintRunOff r = wall.GetRunOff(k);
                    Assert.False(r.Fell, "a wall's paint runs off, it does not fall");
                    Assert.Equal(0f, r.UV.Y);
                    Assert.InRange(r.UV.X, 0f, wall.Wall.Width);
                    Assert.Equal(PaintColor.Orange, r.Color);
                    Assert.True(r.Volume > 0f && r.HalfWidth > 0f);
                    t.Streams++;
                    t.Volume += r.Volume;
                    if (t.FirstAt < 0f) t.FirstAt = i * Dt;
                    t.LastAt = i * Dt;
                    t.MinU = MathF.Min(t.MinU, r.UV.X);
                    t.MaxU = MathF.Max(t.MaxU, r.UV.X);
                }
            }
            return t;
        }

        [Fact]
        public void PaintReachingTheBottomEdgeIsReportedWithTheRightAmount()
        {
            Vec2 hit = new Vec2(2f, 1.5f);

            // Nothing below (the default): no reports, everything pools at the foot as it always did.
            var alone = new PaintSurface(LookSheets.Wall());
            alone.Splat(PaintColor.Orange, hit, 0.45f, new Vec2(0.6f, -0.4f), 1234u, 10f);
            RunOffTotals none = RunWall(alone, 10f);
            Assert.Equal(0, none.Streams);
            Assert.Equal(0.0, alone.ComputeBudget().HandedOver);
            AssertBudget(alone, "wall with nothing below");

            // A surface lies below: most of what reaches the foot is handed over.
            var wall = new PaintSurface(LookSheets.Wall());
            wall.HandsOverRunOff = true;
            wall.Splat(PaintColor.Orange, hit, 0.45f, new Vec2(0.6f, -0.4f), 1234u, 10f);
            double deposited = wall.ComputeBudget().Deposited;
            RunOffTotals got = RunWall(wall, 10f);
            PaintBudget b = wall.ComputeBudget();
            _out.WriteLine($"R 0.45 thrown at 1.5 m: {got.Streams} streams reached the foot between {got.FirstAt:0.00} and {got.LastAt:0.00} s, u {got.MinU:0.00}..{got.MaxU:0.00} m; {got.Volume * 1e6:0} cm3 of {deposited * 1e6:0} handed over ({100.0 * got.Volume / deposited:0}%); on the wall {b.OnWall * 1e6:0} cm3");
            Assert.True(wall.IsSettled);
            Assert.True(got.Streams >= 3, $"only {got.Streams} streams");
            Assert.InRange(got.MinU, hit.X - 0.6f, hit.X + 0.6f);
            Assert.InRange(got.MaxU, hit.X - 0.6f, hit.X + 0.6f);
            Assert.InRange(got.Volume / b.HandedOver, 0.99999, 1.00001);     // what was reported is what left (reports are single precision)
            Assert.InRange((deposited - b.OnWall - b.Spilled) / b.HandedOver, 0.9999, 1.0001); // and it is what the wall no longer has
            Assert.InRange(got.Volume / deposited, 0.03, 0.6);
            AssertBudget(wall, "wall handing over");
            // The wall keeps less at its foot than it would have alone, by what it handed over.
            Assert.Equal(alone.ComputeBudget().OnWall - b.OnWall, b.HandedOver, 6);

            // The share is the dial: all of it, or none.
            foreach (float share in new[] { 1f, 0f })
            {
                var w = new PaintSurface(LookSheets.Wall(), new PaintSettings { RunOffShare = share });
                w.HandsOverRunOff = true;
                w.Splat(PaintColor.Orange, hit, 0.45f, new Vec2(0.6f, -0.4f), 1234u, 10f);
                RunOffTotals t = RunWall(w, 10f);
                _out.WriteLine($"  RunOffShare {share}: {t.Volume * 1e6:0} cm3 handed over");
                if (share == 0f) Assert.Equal(0.0, t.Volume);
                else Assert.True(t.Volume > got.Volume, "handing all of it over should hand over more");
                AssertBudget(w, $"share {share}");
            }

            // A splat high on the wall hands over little or nothing; the next Step forgets the last one's reports.
            var high = new PaintSurface(LookSheets.Wall());
            high.HandsOverRunOff = true;
            high.Splat(PaintColor.Orange, new Vec2(2f, 2.5f), 0.3f, Vec2.Zero, 5u, 9f);
            RunOffTotals top = RunWall(high, 10f);
            _out.WriteLine($"R 0.30 thrown at 2.5 m: {top.Volume * 1e6:0} cm3 handed over");
            Assert.True(top.Volume < 0.25 * got.Volume);
            high.Step(Dt);
            Assert.Equal(0, high.RunOffCount);
        }

        // ------------------------------------------------------------------ puddles

        private static float Reach(PaintSurface s, PaintColor c, Vec2 from)
        {
            float far = 0f;
            for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++)
                if (s.GetField(c, x, y) >= 0.5f) far = MathF.Max(far, Vec2.Distance(s.TexelCenter(x, y), from));
            return far;
        }

        [Fact]
        public void APuddleGrowsOutFromTheFootOfTheWallAndStops()
        {
            WallFrame frame = LookSheets.FloorUnder(LookSheets.Wall(), 2f);
            Vec2 foot = new Vec2(2f, frame.Height);   // on the floor's edge, under the wall
            float[] volumes = { 0.00005f, 0.0002f, 0.0006f };
            float lastArea = 0f;
            foreach (float volume in volumes)
            {
                var floor = new PaintSurface(frame);
                Assert.True(floor.Pour(PaintColor.Orange, foot, volume, 0.04f, 9u));
                Assert.False(floor.IsSettled);
                Assert.Equal(0, floor.ActiveDripCount);   // a puddle is not a drip
                float cell = floor.TexelSizeU * floor.TexelSizeV;
                float prev = 0f, areaAt1 = 0f, stoppedAt = -1f;
                for (int i = 1; i <= 12 * 90; i++)
                {
                    floor.Step(Dt);
                    if (i % 15 == 0)
                    {
                        float a = Painted(floor, PaintColor.Orange).Texels * cell;
                        Assert.True(a >= prev - 1e-6f, "a puddle never shrinks");
                        prev = a;
                    }
                    if (i == 90) areaAt1 = Painted(floor, PaintColor.Orange).Texels * cell;
                    if (stoppedAt < 0f && floor.IsSettled) stoppedAt = i * Dt;
                }
                float area = Painted(floor, PaintColor.Orange).Texels * cell;
                float reach = Reach(floor, PaintColor.Orange, foot);
                PaintBudget b = floor.ComputeBudget();
                _out.WriteLine($"{volume * 1e6:0} cm3 poured at the foot of the wall: puddle {area:0.000} m2 (film {volume / area * 1000f:0.00} mm), reaching {reach:0.00} m out; {areaAt1 / area * 100f:0}% of it after 1 s, stopped at {stoppedAt:0.00} s");
                Assert.True(stoppedAt > 0.5f && stoppedAt < 9f, $"stopped at {stoppedAt}");
                Assert.True(areaAt1 < 0.9f * area, "it should still be spreading after a second");
                Assert.True(area > lastArea, "more paint, bigger puddle");
                Assert.True(reach <= floor.Settings.PuddleMaxReach + 0.06f, $"reached {reach} m");
                Assert.Equal((double)volume, b.Deposited, 9);
                Assert.Equal((double)volume, b.OnWall, 7);      // limited by how much arrived: all of it is down, no more
                AssertBudget(floor, "puddle");
                lastArea = area;

                // Stopped means stopped.
                ulong before = GoldenTests.Fingerprint(floor);
                bool[] shape = Mask(floor, PaintColor.Orange);
                LookSheets.Run(floor, 20f);
                Assert.Equal(shape, Mask(floor, PaintColor.Orange));
                Assert.True(floor.IsDry);
                _ = before;
            }

            // More paint down the same stream wakes the same puddle and it grows on; a stream further along starts its own.
            var f2 = new PaintSurface(frame);
            f2.Pour(PaintColor.Orange, foot, 0.0001f, 0.04f, 9u);
            LookSheets.Run(f2, 8f);
            int once = Painted(f2, PaintColor.Orange).Texels;
            f2.Pour(PaintColor.Orange, foot + new Vec2(0.03f, 0f), 0.0003f, 0.04f, 10u);
            Assert.False(f2.IsSettled);
            LookSheets.Run(f2, 8f);
            int twice = Painted(f2, PaintColor.Orange).Texels;
            Assert.True(twice > once * 1.5f, $"{once} -> {twice} texels");
            Box one = Painted(f2, PaintColor.Orange);
            f2.Pour(PaintColor.Orange, foot + new Vec2(1.2f, 0f), 0.0003f, 0.04f, 11u);
            LookSheets.Run(f2, 8f);
            Box two = Painted(f2, PaintColor.Orange);
            Assert.True(two.U1 > one.U1 + 0.8f, "a stream 1.2 m along should have made its own puddle there");
            Assert.True(f2.IsSettled);
            AssertBudget(f2, "three pours");
        }

        private static bool[] Mask(PaintSurface s, PaintColor c)
        {
            var m = new bool[s.Width * s.Height];
            for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++) m[x + y * s.Width] = s.GetField(c, x, y) >= 0.5f;
            return m;
        }

        [Fact]
        public void AWallsRunOffPoolsOnTheFloorUnderIt()
        {
            var wall = new PaintSurface(LookSheets.Wall());
            var floor = new PaintSurface(LookSheets.FloorUnder(wall.Wall, 2f));
            wall.HandsOverRunOff = true;
            wall.Splat(PaintColor.Orange, new Vec2(2f, 1.5f), 0.45f, new Vec2(0.6f, -0.4f), 1234u, 10f);
            double poured = 0.0;
            float firstAt = -1f, floorStoppedAt = -1f;
            for (int i = 1; i <= 14 * 90; i++)
            {
                poured += LookSheets.StepWallAndFloor(wall, floor, Dt);
                if (firstAt < 0f && poured > 0.0) firstAt = i * Dt;
                if (floorStoppedAt < 0f && poured > 0.0 && wall.IsSettled && floor.IsSettled) floorStoppedAt = i * Dt;
            }
            Box puddle = Painted(floor, PaintColor.Orange);
            float depth = floor.Wall.Height;
            _out.WriteLine($"paint first reached the floor at {firstAt:0.00} s; {poured * 1e6:0} cm3 poured in all; the puddle is {puddle.U1 - puddle.U0:0.00} m wide and reaches {depth - puddle.V0:0.00} m out from the wall; everything stopped at {floorStoppedAt:0.00} s");
            Assert.True(poured > 0.0 && puddle.Texels > 0);
            Assert.InRange(floor.ComputeBudget().Deposited / wall.ComputeBudget().HandedOver, 0.99999, 1.00001);   // nothing made, nothing lost
            Assert.True(depth - puddle.V1 < 0.03f, "the puddle should start at the foot of the wall");
            Assert.True(depth - puddle.V0 > 0.15f, "the puddle should spread out across the floor");
            Assert.InRange((puddle.U0 + puddle.U1) * 0.5f, 1.5f, 2.5f);                           // under the curtain
            Assert.True(floorStoppedAt > 0f && floorStoppedAt < 13f);
            Assert.Equal(0, floor.ActiveDripCount);
            AssertBudget(wall, "wall");
            AssertBudget(floor, "floor");
        }

        // ------------------------------------------------------------------ the portal rule

        [Fact]
        public void RunOffNeverTouchesAPortalPatch()
        {
            // A blue portal patch on the floor at the foot of the wall, then orange paint poured all round and
            // right onto it, and a ceiling drop's worth more. Every texel of the blue patch must come through
            // untouched, and its opening with it. (The other half of the rule, that a surface holding a colour's
            // patch refuses loose paint of that SAME colour, is the game's: PaintWall.TakeRunOff.)
            WallFrame frame = LookSheets.FloorUnder(LookSheets.Wall(), 2f);
            float depth = frame.Height;
            foreach (bool fresh in new[] { false, true })
            {
                var floor = new PaintSurface(frame);
                var finder = new OpeningFinder(floor);
                Vec2 hit = new Vec2(2f, depth - 0.45f);
                floor.Splat(PaintColor.Blue, hit, 0.45f, Vec2.Zero, 77u, 9f);
                // fresh: the orange arrives while the blue splat is still settling (its shape still changing by itself).
                LookSheets.Run(floor, fresh ? 0.3f : 9f);
                var control = new PaintSurface(frame);
                control.Splat(PaintColor.Blue, hit, 0.45f, Vec2.Zero, 77u, 9f);
                LookSheets.Run(control, fresh ? 0.3f : 9f);

                PortalOpening before = finder.Find(PaintColor.Blue, hit);
                Assert.True(before.Valid);
                floor.TakeShapeChanges(PaintColor.Blue);

                var rng = new Rng(5u);
                double poured = 0.0;
                for (int k = 0; k < 14; k++)
                {
                    // Along the foot of the wall, across the whole width of the blue patch and beyond.
                    Vec2 at = new Vec2(1.2f + 0.12f * k, depth);
                    float v = rng.Range(0.00005f, 0.0003f);
                    Assert.True(floor.Pour(PaintColor.Orange, at, v, rng.Range(0.02f, 0.06f), rng.NextUInt()));
                    poured += v;
                    for (int i = 0; i < 20; i++) { floor.Step(Dt); control.Step(Dt); }
                }
                // And one right in the middle of the patch.
                floor.Pour(PaintColor.Orange, hit, 0.0003f, 0.05f, 3u);
                poured += 0.0003f;
                for (int i = 0; i < 12 * 90; i++) { floor.Step(Dt); control.Step(Dt); }
                Assert.True(floor.IsSettled && control.IsSettled);

                float[] blue = Field(floor, PaintColor.Blue), blueAlone = Field(control, PaintColor.Blue), orange = Field(floor, PaintColor.Orange);
                int differ = 0, overlap = 0, orangeTexels = 0;
                for (int i = 0; i < blue.Length; i++)
                {
                    if (BitConverter.SingleToInt32Bits(blue[i]) != BitConverter.SingleToInt32Bits(blueAlone[i])) differ++;
                    if (orange[i] > 0f && blue[i] + orange[i] > 1.0001f) overlap++;
                    if (orange[i] >= 0.5f) orangeTexels++;
                }
                PortalOpening after = finder.Find(PaintColor.Blue, hit);
                PortalOpening alone = new OpeningFinder(control).Find(PaintColor.Blue, hit);
                PaintBudget b = floor.ComputeBudget();
                _out.WriteLine($"{(fresh ? "blue still settling" : "blue settled")}: {poured * 1e6:0} cm3 of orange poured round and onto the blue patch: {orangeTexels} orange texels; blue texels changed {differ}; orange over blue {overlap}; " +
                               $"blue opening centre ({after.CenterUV.X:0.0000}, {after.CenterUV.Y:0.0000}) radius {after.InscribedRadius:0.0000} area {after.Area:0.0000}; orange that found no bare floor {b.Spilled * 1e6:0} cm3");
                Assert.Equal(0, differ);
                Assert.Equal(0, overlap);
                Assert.True(orangeTexels > 100, "the orange should have found bare floor beside the patch");
                Assert.Equal(alone.Valid, after.Valid);
                Assert.Equal(alone.CenterUV, after.CenterUV);
                Assert.Equal(alone.InscribedRadius, after.InscribedRadius);
                Assert.Equal(alone.Area, after.Area);
                Assert.Equal(alone.BoundsMin, after.BoundsMin);
                Assert.Equal(alone.BoundsMax, after.BoundsMax);
                if (!fresh)
                {
                    Assert.Equal(before.Revision, after.Revision);                      // the patch never even looked changed
                    Assert.True(floor.TakeShapeChanges(PaintColor.Blue).IsEmpty);
                }
                // The settled blue patch's paint film is untouched too: orange laid none of its paint on it.
                // (A patch that is still settling may creep over loose orange beside it and bury it: its own right.)
                if (!fresh)
                {
                    for (int y = 0; y < floor.Height; y++)
                    for (int x = 0; x < floor.Width; x++)
                        if (floor.GetField(PaintColor.Blue, x, y) >= 1f)
                            Assert.Equal(control.GetThickness(x, y), floor.GetThickness(x, y));
                }
                AssertBudget(floor, "floor with a blue patch");
            }
        }

        // ------------------------------------------------------------------ floors and ceilings

        [Fact]
        public void AFloorSplatSettlesALittleAndStops()
        {
            WallFrame frame = LookSheets.FloorUnder(LookSheets.Wall(), 3f);
            foreach (Vec2 along in new[] { Vec2.Zero, new Vec2(5f, 1f) })
            {
                var floor = new PaintSurface(frame);
                floor.Splat(PaintColor.Blue, new Vec2(2f, 1.5f), 0.42f, along, 90u, 9f);
                int landed = Painted(floor, PaintColor.Blue).Texels;
                Assert.Equal(0, floor.ActiveDripCount);
                Assert.True(floor.ActivePoolCount > 0);
                float stoppedAt = -1f;
                for (int i = 1; i <= 8 * 90; i++)
                {
                    floor.Step(Dt);
                    Assert.Equal(0, floor.ActiveDripCount);
                    Assert.Equal(0, floor.RunOffCount);
                    if (stoppedAt < 0f && floor.IsSettled) stoppedAt = i * Dt;
                }
                int rest = Painted(floor, PaintColor.Blue).Texels;
                _out.WriteLine($"floor splat, along the floor at {along.Length():0.0} m/s: {landed} texels at landing, {rest} at rest (+{100f * (rest - landed) / landed:0.0}%), settled at {stoppedAt:0.00} s");
                Assert.InRange((rest - landed) / (float)landed, 0.02f, 0.35f);
                Assert.True(stoppedAt > 0.5f && stoppedAt < 6f);
                AssertBudget(floor, "floor splat");
            }

            // A droplet's dab on the floor stays exactly as it lands (nothing may change by itself near a patch).
            var f2 = new PaintSurface(frame);
            f2.Splat(PaintColor.Orange, new Vec2(2f, 1.5f), SplatFeel.DropletSplatRadiusMax, new Vec2(0.5f, 0f), 7u, 2f);
            Assert.True(f2.IsSettled);
            bool[] dab = Mask(f2, PaintColor.Orange);
            LookSheets.Run(f2, 3f);
            Assert.Equal(dab, Mask(f2, PaintColor.Orange));
        }

        [Fact]
        public void ACeilingSplatLetsDropsFallAndKeepsItsShape()
        {
            WallFrame frame = LookSheets.CeilingOver(LookSheets.Wall(), 3f);
            var s = new PaintSurface(frame);
            Vec2 hit = new Vec2(2f, 1.5f);
            s.Splat(PaintColor.Orange, hit, 0.42f, Vec2.Zero, 91u, 9f);
            bool[] landed = Mask(s, PaintColor.Orange);
            Assert.Equal(0, s.ActiveDripCount);
            Assert.True(s.HangingDropCount >= s.Settings.CeilingDropsMin);
            Assert.False(s.IsSettled);   // drops are still to come
            var times = new List<float>();
            double fell = 0.0;
            for (int i = 1; i <= 10 * 90; i++)
            {
                s.Step(Dt);
                Assert.Equal(0, s.ActiveDripCount);
                for (int k = 0; k < s.RunOffCount; k++)
                {
                    PaintRunOff r = s.GetRunOff(k);
                    Assert.True(r.Fell);
                    Assert.Equal(PaintColor.Orange, r.Color);
                    Assert.True(Vec2.Distance(r.UV, hit) <= 0.6f * 0.42f + 1e-3f, "a drop falls from inside the splat");
                    Assert.True(r.Volume > 0f);
                    times.Add(i * Dt);
                    fell += r.Volume;
                }
            }
            _out.WriteLine($"ceiling splat R 0.42: {times.Count} drops fell, at {string.Join(", ", times.ConvertAll(t => t.ToString("0.00")))} s; {fell * 1e6:0} cm3 of {s.ComputeBudget().Deposited * 1e6:0}");
            Assert.InRange(times.Count, s.Settings.CeilingDropsMin, s.Settings.CeilingDropsMax);
            Assert.True(times[0] < 1.0f, "the first drop comes soon");
            Assert.True(times[times.Count - 1] <= s.Settings.DryTime, "nothing falls off dry paint");
            // Quickly at first, then more slowly: the last wait is longer than the first.
            if (times.Count >= 3) Assert.True(times[times.Count - 1] - times[times.Count - 2] > times[1] - times[0]);
            Assert.True(s.IsSettled);
            Assert.Equal(landed, Mask(s, PaintColor.Orange));
            Assert.InRange(fell / s.ComputeBudget().HandedOver, 0.99999, 1.00001);
            AssertBudget(s, "ceiling");

            // A droplet-sized splat hangs on: no drops.
            var small = new PaintSurface(frame);
            small.Splat(PaintColor.Blue, hit, SplatFeel.DropletSplatRadiusMax, Vec2.Zero, 3u, 2f);
            Assert.Equal(0, small.HangingDropCount);
            Assert.True(small.IsSettled);
        }

        // ------------------------------------------------------------------ wet on wet

        // How far along "down" the leading running head of one colour is.
        private static float Front(PaintSurface s, PaintColor c)
        {
            float far = float.MinValue;
            for (int i = 0; i < s.ActiveDripCount; i++)
            {
                s.GetDrip(i, out Vec2 p, out _, out PaintColor colour, out _);
                if (colour == c) far = MathF.Max(far, -p.Y);
            }
            return far;
        }

        [Fact]
        public void PaintThrownOnWetPaintRunsMoreThanOnDryPaint()
        {
            // The same blue splat lands on the same orange splat: once while the orange is half a second old and
            // wet, once when it has long dried. On the wet one its running paint gets going faster.
            float[] ran = new float[2];
            for (int pass = 0; pass < 2; pass++)
            {
                var s = new PaintSurface(LookSheets.Wall());
                s.Splat(PaintColor.Orange, new Vec2(2f, 2.3f), 0.45f, Vec2.Zero, 61u, 9f);
                LookSheets.Run(s, pass == 0 ? 0.5f : 20f);
                s.Splat(PaintColor.Blue, new Vec2(2.05f, 2.25f), 0.42f, new Vec2(0.3f, -0.2f), 62u, 9f);
                float start = Front(s, PaintColor.Blue);
                LookSheets.Run(s, 1.0f);
                ran[pass] = Front(s, PaintColor.Blue) - start;
                LookSheets.Run(s, 12f);
                Assert.True(s.IsSettled);
                AssertBudget(s, pass == 0 ? "wet on wet" : "wet on dry");
            }
            _out.WriteLine($"blue thrown onto orange, its leading head after 1 s: {ran[0]:0.000} m on wet orange, {ran[1]:0.000} m on dry orange ({ran[0] / ran[1]:0.00}x)");
            Assert.True(ran[0] > ran[1] * 1.1f, $"{ran[0]} m on wet paint, {ran[1]} m on dry");
            Assert.True(ran[0] < ran[1] * (1f + SplatFeel.WetOnWetBoost) + 0.1f, "never more than the dial allows");

            // The dial at 0 switches it off.
            var off = new PaintSurface(LookSheets.Wall(), new PaintSettings { WetOnWetBoost = 0f });
            off.Splat(PaintColor.Orange, new Vec2(2f, 2.3f), 0.45f, Vec2.Zero, 61u, 9f);
            LookSheets.Run(off, 0.5f);
            off.Splat(PaintColor.Blue, new Vec2(2.05f, 2.25f), 0.42f, new Vec2(0.3f, -0.2f), 62u, 9f);
            float from = Front(off, PaintColor.Blue);
            LookSheets.Run(off, 1.0f);
            Assert.InRange(Front(off, PaintColor.Blue) - from, ran[1] - 0.03f, ran[1] + 0.03f);
        }

        // ------------------------------------------------------------------ the opening while paint runs under it

        [Fact]
        public void TheOpeningStaysSteadyWhilePaintRunsBelowIt()
        {
            // Longer drips change the patch's shape for longer. The opening found in it (the largest circle that
            // fits, which is where the portal sits) must not hop about while that goes on.
            // What is NOT new: while the sag and curtain form, in the first second and a half, the largest circle
            // slides down from the splat's body into the wider shape below it, sometimes half a metre in one look.
            // It did exactly that an hour ago too. What matters here: longer drips must not make that go on longer,
            // and from then on, while paint creeps for five more seconds, the opening must stand still.
            float stepBefore = 0f, quietBefore = 0f;
            foreach (bool hourAgo in new[] { true, false })
            {
            float worstStep = 0f, worstShrink = 0f, lastMoveAt = 0f, totalDrift = 0f, worstLate = 0f;
            for (int seed = 0; seed < 6; seed++)
            {
                var s = new PaintSurface(LookSheets.Wall(), hourAgo ? GoldenTests.AsAnHourAgo() : new PaintSettings());
                var f = new OpeningFinder(s);
                var rng = new Rng(300u + (uint)seed);
                Vec2 hit = new Vec2(2f, rng.Range(1.6f, 2.3f));
                s.Splat(PaintColor.Orange, hit, 0.45f, new Vec2(rng.Range(-1.5f, 1.5f), rng.Range(-1f, 0.5f)), 800u + (uint)seed * 11u, 9f);
                LookSheets.Run(s, 0.1f);
                PortalOpening first = f.Find(PaintColor.Orange, hit), prev = first;
                Assert.True(prev.Valid);
                for (int k = 2; k <= 95; k++)
                {
                    LookSheets.Run(s, 0.1f);   // the game looks again ten times a second while paint moves
                    PortalOpening o = f.Find(PaintColor.Orange, hit);
                    Assert.True(o.Valid, $"seed {seed}: the opening closed at {k * 0.1f:0.0} s");
                    float step = Vec2.Distance(o.CenterUV, prev.CenterUV);
                    if (step > 0.1f) _out.WriteLine($"   {(hourAgo ? "an hour ago" : "now")}, throw {seed}, {k * 0.1f:0.0} s: centre ({prev.CenterUV.X:0.000}, {prev.CenterUV.Y:0.000}) r {prev.InscribedRadius:0.000} -> ({o.CenterUV.X:0.000}, {o.CenterUV.Y:0.000}) r {o.InscribedRadius:0.000}");
                    if (step > worstStep) worstStep = step;
                    if (k * 0.1f > 2.55f && step > worstLate) worstLate = step;
                    if (step > 0.004f) lastMoveAt = MathF.Max(lastMoveAt, k * 0.1f);
                    worstShrink = MathF.Max(worstShrink, prev.InscribedRadius - o.InscribedRadius);
                    prev = o;
                }
                Assert.True(s.IsSettled);
                totalDrift = MathF.Max(totalDrift, Vec2.Distance(prev.CenterUV, first.CenterUV));
            }
            _out.WriteLine($"{(hourAgo ? "an hour ago" : "now")}: 6 throws, opening looked for every 0.1 s for 9.5 s: its centre moved at most {worstStep * 1000f:0.0} mm between two looks ({totalDrift * 1000f:0} mm in all at most), last moved more than 4 mm at {lastMoveAt:0.0} s, at most {worstLate * 1000f:0.0} mm in one look after 2.5 s; its circle never shrank by more than {worstShrink * 1000f:0.0} mm");
            Assert.True(worstShrink < 0.01f, $"the opening shrank by {worstShrink} m");
            if (hourAgo)
            {
                stepBefore = worstStep;
                quietBefore = lastMoveAt;
            }
            else
            {
                Assert.True(worstStep <= stepBefore + 0.02f, $"the opening's biggest hop is {worstStep} m now, {stepBefore} m before");
                Assert.True(lastMoveAt <= quietBefore + 0.5f, $"the opening went on moving until {lastMoveAt} s now, {quietBefore} s before");
                Assert.True(worstLate < 0.004f, $"the opening moved {worstLate} m in one look while the paint was only creeping");
            }
            }
        }
    }
}
