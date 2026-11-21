using System;
using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace Portaballs.Core.Tests
{
    /// <summary>
    /// Writes the contact sheets to core/tests/Paint.Tests/out so the look can be judged by eye.
    /// These only fail if something crashes; the judging is done by looking.
    /// </summary>
    public class LookSheets
    {
        private readonly ITestOutputHelper _out;

        public LookSheets(ITestOutputHelper output)
        {
            _out = output;
        }

        internal static WallFrame Wall(float w = 4f, float h = 3f)
        {
            return new WallFrame(new Vec3(5f, 2f, 0f), new Vec3(0f, -1f, 0f), new Vec3(0f, 0f, 1f), w, h);
        }

        internal static void Run(PaintSurface s, float seconds, float dt = 1f / 90f)
        {
            int n = (int)MathF.Round(seconds / dt);
            for (int i = 0; i < n; i++) s.Step(dt);
        }

        private string Save(Picture p, string name)
        {
            string path = Path.Combine(ShaderPreview.OutDir(), name);
            p.SavePng(path);
            _out.WriteLine(path);
            return path;
        }

        [Fact]
        public void A_BigSplatOverTime()
        {
            var s = new PaintSurface(Wall());
            s.Splat(PaintColor.Orange, new Vec2(2.0f, 2.15f), 0.42f, new Vec2(0.6f, -0.4f), 1234u, 10f);
            float[] times = { 0f, 0.25f, 0.5f, 1f, 2f, 4f, 8f };
            var panels = new Picture[times.Length + 1];
            float t = 0f;
            const float ppm = 128f;
            for (int i = 0; i < times.Length; i++)
            {
                Run(s, times[i] - t);
                t = times[i];
                var p = ShaderPreview.RenderWall(s, ppm);
                p.Text(6, 6, "T=" + times[i].ToString("0.##") + "S  DRIPS " + s.ActiveDripCount);
                panels[i] = p;
            }
            // A close-up of the settled splat to judge the edge and highlights.
            var z = ShaderPreview.Render(s, 1.2f, 1.0f, 2.8f, 2.2f, 320f);
            z.Text(6, 6, "CLOSE-UP AT 8S");
            var sheet = ShaderPreview.Sheet(panels[..7], 4);
            Save(sheet, "a_big_splat_over_time.png");
            Save(z, "a2_big_splat_closeup.png");
        }

        [Fact]
        public void B_TwelveSeeds()
        {
            var panels = new Picture[12];
            for (int i = 0; i < 12; i++)
            {
                var s = new PaintSurface(Wall(2f, 2.4f));
                uint seed = (uint)(1000 + i * 7919);
                var rng = new Rng(seed);
                Vec2 vel = new Vec2(rng.Range(-3f, 3f), rng.Range(-3f, 3f));
                s.Splat(PaintColor.Orange, new Vec2(1.0f, 1.55f), 0.3f, vel, seed, 9f);
                Run(s, 6f);
                var p = ShaderPreview.RenderWall(s, 128f);
                p.Text(6, 6, "SEED " + seed);
                panels[i] = p;
            }
            Save(ShaderPreview.Sheet(panels, 6), "b_twelve_seeds.png");
        }

        [Fact]
        public void C_SmallMediumLarge()
        {
            float[] radii = { 0.1f, 0.22f, 0.45f };
            var panels = new Picture[3];
            for (int i = 0; i < 3; i++)
            {
                var s = new PaintSurface(Wall());
                s.Splat(PaintColor.Blue, new Vec2(2f, 2.1f), radii[i], new Vec2(0.3f, 0f), 77u + (uint)i, 9f);
                Run(s, 8f);
                var p = ShaderPreview.RenderWall(s, 110f);
                p.Text(6, 6, "R=" + radii[i].ToString("0.00") + "M");
                panels[i] = p;
            }
            Save(ShaderPreview.Sheet(panels, 3), "c_small_medium_large.png");
        }

        [Fact]
        public void D_GlancingHits()
        {
            Vec2[] vel = { new Vec2(12f, 0f), new Vec2(-12f, 0f), new Vec2(0f, 12f), new Vec2(0f, -12f) };
            string[] names = { "THROWN RIGHT", "THROWN LEFT", "THROWN UP", "THROWN DOWN" };
            var panels = new Picture[8];
            for (int i = 0; i < 4; i++)
            {
                var s = new PaintSurface(Wall());
                s.Splat(PaintColor.Orange, new Vec2(2f, 1.9f), 0.3f, vel[i], 500u + (uint)i, 6f);
                var p0 = ShaderPreview.RenderWall(s, 100f);
                p0.Text(6, 6, names[i] + " T=0");
                Run(s, 8f);
                var p = ShaderPreview.RenderWall(s, 100f);
                p.Text(6, 6, names[i] + " AT REST");
                panels[i] = p0;
                panels[i + 4] = p;
            }
            Save(ShaderPreview.Sheet(panels, 4), "d_glancing_hits.png");
        }

        [Fact]
        public void E_BlueOnOrange()
        {
            var s = new PaintSurface(Wall());
            s.Splat(PaintColor.Orange, new Vec2(1.7f, 1.9f), 0.4f, new Vec2(0.5f, 0f), 42u, 9f);
            Run(s, 3f);
            var p0 = ShaderPreview.RenderWall(s, 110f);
            p0.Text(6, 6, "ORANGE AFTER 3S");
            s.Splat(PaintColor.Blue, new Vec2(2.3f, 2.0f), 0.32f, new Vec2(-4f, 0f), 43u, 9f);
            var p1 = ShaderPreview.RenderWall(s, 110f);
            p1.Text(6, 6, "BLUE LANDS");
            Run(s, 8f);
            var p2 = ShaderPreview.RenderWall(s, 110f);
            p2.Text(6, 6, "BOTH AT REST");
            var z = ShaderPreview.Render(s, 1.6f, 1.2f, 2.8f, 2.6f, 300f);
            z.Text(6, 6, "CLOSE-UP OF THE BORDER");
            Save(ShaderPreview.Sheet(new[] { p0, p1, p2 }, 3), "e_blue_on_orange.png");
            Save(z, "e2_border_closeup.png");
        }

        [Fact]
        public void G_CurtainOverTime()
        {
            var s = new PaintSurface(Wall());
            var f = new OpeningFinder(s);
            Vec2 hit = new Vec2(2f, 1.5f);
            s.Splat(PaintColor.Orange, hit, 0.45f, new Vec2(0.6f, -0.4f), 1234u, 10f);
            float[] times = { 0f, 0.5f, 1f, 2f, 3f, 5f, 8f };
            var panels = new Picture[times.Length];
            float t = 0f;
            const float ppm = 128f;
            for (int i = 0; i < times.Length; i++)
            {
                Run(s, times[i] - t);
                t = times[i];
                f.Find(PaintColor.Orange, hit);
                var d = f.FindDoor(PaintColor.Orange, 0.5f, 0.15f);
                var p = ShaderPreview.RenderWall(s, ppm);
                ShaderPreview.DrawDoor(p, d, 0f, s.Wall.Height, ppm);
                p.Text(6, 6, "T=" + times[i].ToString("0.#") + "S  DOOR " + d.Width.ToString("0.00") + " X " + d.Height.ToString("0.00")
                             + " BOTTOM " + d.BottomV.ToString("0.00") + (d.ReachesFloor ? " FLOOR" : ""));
                panels[i] = p;
            }
            Save(ShaderPreview.Sheet(panels, 4), "g_curtain_over_time.png");
            var z = ShaderPreview.Render(s, 1.2f, 0f, 2.8f, 1.6f, 300f);
            z.Text(6, 6, "CURTAIN CLOSE-UP AT 8S");
            Save(z, "g2_curtain_closeup.png");
        }

        [Fact]
        public void H_CurtainStrengths()
        {
            float[] strengths = { 0f, 0.25f, 0.5f, 0.75f, 1f };
            var panels = new Picture[strengths.Length];
            const float ppm = 100f;
            for (int i = 0; i < strengths.Length; i++)
            {
                var s = new PaintSurface(Wall(), new PaintSettings { CurtainStrength = strengths[i] });
                var f = new OpeningFinder(s);
                Vec2 hit = new Vec2(2f, 1.5f);
                s.Splat(PaintColor.Blue, hit, 0.45f, new Vec2(0.6f, -0.4f), 1234u, 10f);
                Run(s, 8f);
                f.Find(PaintColor.Blue, hit);
                var d = f.FindDoor(PaintColor.Blue, 0.5f, 0.15f);
                var p = ShaderPreview.RenderWall(s, ppm);
                ShaderPreview.DrawDoor(p, d, 0f, s.Wall.Height, ppm);
                p.Text(6, 6, "STRENGTH " + strengths[i].ToString("0.00") + "  DOOR " + d.Height.ToString("0.00") + " TALL");
                panels[i] = p;
            }
            Save(ShaderPreview.Sheet(panels, 5), "h_curtain_strengths.png");
        }

        // ---- 2026-11-19 evening: longer drips, run-off, puddles, floors and ceilings ----

        /// <summary>A floor at the foot of <see cref="Wall"/>: same U as the wall, V running toward the wall, so its top edge is the wall's foot.</summary>
        internal static WallFrame FloorUnder(WallFrame wall, float depth = 1.5f)
        {
            Vec3 into = wall.Normal;   // out of the wall, into the room
            return new WallFrame(wall.Origin + into * depth, wall.U, into * -1f, wall.Width, depth);
        }

        /// <summary>A ceiling over the same strip of room: same U as the wall, V running away from the wall.</summary>
        internal static WallFrame CeilingOver(WallFrame wall, float depth = 1.5f)
        {
            Vec3 into = wall.Normal;
            return new WallFrame(wall.Origin + wall.V * wall.Height, wall.U, into, wall.Width, depth);
        }

        /// <summary>
        /// What the game does each frame for a wall standing on a floor: step both, and pour whatever ran off the
        /// wall's bottom edge onto the floor at the same place (PaintWall.PassOnRunOff / TakeRunOff).
        /// Returns the paint handed over (cubic metres).
        /// </summary>
        internal static double StepWallAndFloor(PaintSurface wall, PaintSurface floor, float dt)
        {
            double poured = 0.0;
            wall.Step(dt);
            for (int i = 0; i < wall.RunOffCount; i++)
            {
                PaintRunOff r = wall.GetRunOff(i);
                if (r.Fell) continue;
                Vec2 at = floor.Wall.ToUV(wall.Wall.ToWorld(r.UV));
                if (floor.Pour(r.Color, at, r.Volume, r.HalfWidth, r.Seed)) poured += r.Volume;
            }
            floor.Step(dt);
            return poured;
        }

        // The wall with the floor at its foot folded down flat beneath it (the shared edge is where the two meet).
        private static Picture WallOverFloor(PaintSurface wall, PaintSurface floor, float ppm)
        {
            var w = ShaderPreview.RenderWall(wall, ppm);
            var f = ShaderPreview.RenderWall(floor, ppm);
            var both = new Picture(Math.Max(w.Width, f.Width), w.Height + f.Height);
            both.Blit(w, 0, 0);
            both.Blit(f, 0, w.Height);
            return both;
        }

        [Fact]
        public void I_LongDripsOverTime()
        {
            // The splat the tester watched for five seconds, every second to eight: still running at 5 s, creeping at 6,
            // stopped by 7 or so. Beside it, the same throw as it was an hour before (stopped in under 3 s).
            float[] times = { 0f, 0.5f, 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f };
            const float ppm = 150f;
            for (int pass = 0; pass < 2; pass++)
            {
                var panels = new Picture[times.Length];
                var s = new PaintSurface(Wall(), pass == 0 ? new PaintSettings() : GoldenTests.AsAnHourAgo());
                s.Splat(PaintColor.Orange, new Vec2(2.0f, 2.3f), 0.42f, new Vec2(0.6f, -0.4f), 1234u, 10f);
                float t = 0f;
                for (int i = 0; i < times.Length; i++)
                {
                    Run(s, times[i] - t);
                    t = times[i];
                    var p = ShaderPreview.Render(s, 1.15f, 0f, 2.85f, 3f, ppm);
                    p.Text(6, 6, "T=" + times[i].ToString("0.#") + "S MOVING " + s.ActiveDripCount);
                    panels[i] = p;
                }
                Save(ShaderPreview.Sheet(panels, times.Length), pass == 0 ? "i_long_drips_over_time.png" : "i2_same_throw_an_hour_ago.png");
            }
        }

        [Fact]
        public void J_WallRunsOffOntoTheFloor()
        {
            // A chest-height throw: the curtain reaches the foot of the wall, and what it still carries pours out
            // across the floor as a puddle that grows and stops. Wall above, floor folded down beneath it.
            var wall = new PaintSurface(Wall());
            var floor = new PaintSurface(FloorUnder(wall.Wall));
            wall.HandsOverRunOff = true;
            wall.Splat(PaintColor.Orange, new Vec2(2f, 1.5f), 0.45f, new Vec2(0.6f, -0.4f), 1234u, 10f);
            float[] times = { 0.5f, 1.5f, 2.5f, 3.5f, 5f, 7f, 10f };
            const float ppm = 96f;
            var panels = new Picture[times.Length];
            float t = 0f;
            double poured = 0.0;
            for (int i = 0; i < times.Length; i++)
            {
                int n = (int)MathF.Round((times[i] - t) * 90f);
                for (int k = 0; k < n; k++) poured += StepWallAndFloor(wall, floor, 1f / 90f);
                t = times[i];
                var p = WallOverFloor(wall, floor, ppm);
                p.Text(6, 6, "T=" + times[i].ToString("0.#") + "S  POURED " + (poured * 1e6).ToString("0") + " CM3  PUDDLES GROWING " + floor.ActivePoolCount);
                panels[i] = p;
            }
            Save(ShaderPreview.Sheet(panels, 4), "j_wall_runs_off_onto_floor.png");
            var z = ShaderPreview.Render(floor, 1.2f, 0.9f, 2.8f, 1.5f, 400f);
            z.Text(6, 6, "THE PUDDLE AT 10S (WALL FOOT IS THE TOP EDGE)");
            Save(z, "j2_puddle_closeup.png");
            _out.WriteLine($"poured {poured * 1e6:0} cm3 onto the floor; floor settled {floor.IsSettled}, wall settled {wall.IsSettled}");
        }

        [Fact]
        public void K_FloorAndCeilingSplats()
        {
            // A splat on a floor (straight down, and glancing) settles into a rounder pool; one on a ceiling keeps its
            // shape and lets drops fall. Top row: just landed. Bottom row: at rest.
            const float ppm = 150f;
            var panels = new Picture[6];
            Vec2[] along = { Vec2.Zero, new Vec2(6f, 1f), Vec2.Zero };
            string[] names = { "FLOOR, STRAIGHT DOWN", "FLOOR, GLANCING", "CEILING" };
            for (int i = 0; i < 3; i++)
            {
                WallFrame frame = i < 2 ? FloorUnder(Wall(2.4f, 2f), 2f) : CeilingOver(Wall(2.4f, 2f), 2f);
                var s = new PaintSurface(frame);
                s.Splat(PaintColor.Blue, new Vec2(1.2f, 1.0f), 0.42f, along[i], 90u + (uint)i, 9f);
                var p0 = ShaderPreview.RenderWall(s, ppm);
                p0.Text(6, 6, names[i] + " T=0");
                int fell = 0;
                for (int k = 0; k < 8 * 90; k++)
                {
                    s.Step(1f / 90f);
                    for (int r = 0; r < s.RunOffCount; r++) if (s.GetRunOff(r).Fell) fell++;
                }
                var p1 = ShaderPreview.RenderWall(s, ppm);
                p1.Text(6, 6, names[i] + " AT REST" + (i == 2 ? "  DROPS FELL " + fell : ""));
                panels[i] = p0;
                panels[i + 3] = p1;
            }
            Save(ShaderPreview.Sheet(panels, 3), "k_floor_and_ceiling.png");
        }

        [Fact]
        public void L_RunOffBesideABluePortalPatch()
        {
            // The portal rule with run-off: a blue patch on the floor at the foot of the wall, then an orange splat on
            // the wall above whose run-off pours down right beside and onto it. The blue patch (its opening drawn)
            // must come through untouched: the orange only fills bare floor round it.
            var wall = new PaintSurface(Wall());
            var floor = new PaintSurface(FloorUnder(wall.Wall, 2f));
            var finder = new OpeningFinder(floor);
            wall.HandsOverRunOff = true;
            Vec2 blueAt = new Vec2(2.25f, 2f - 0.5f);
            floor.Splat(PaintColor.Blue, blueAt, 0.42f, Vec2.Zero, 77u, 9f);
            Run(floor, 9f);
            var o0 = finder.Find(PaintColor.Blue, blueAt);
            const float ppm = 110f;
            var p0 = WallOverFloor(wall, floor, ppm);
            ShaderPreview.DrawOpening(p0, floor, finder, o0, 0f, wall.Wall.Height + floor.Wall.Height, ppm);
            wall.Splat(PaintColor.Orange, new Vec2(1.9f, 1.5f), 0.45f, new Vec2(0.6f, -0.4f), 1234u, 10f);
            for (int i = 0; i < 12 * 90; i++) StepWallAndFloor(wall, floor, 1f / 90f);
            var o1 = finder.Find(PaintColor.Blue, blueAt);
            var p1 = WallOverFloor(wall, floor, ppm);
            p0.Text(6, 6, "BLUE PATCH ON THE FLOOR  R=" + o0.InscribedRadius.ToString("0.000"));
            p1.Text(6, 6, "ORANGE RAN OFF BESIDE IT  BLUE R=" + o1.InscribedRadius.ToString("0.000") + (o1.Revision == o0.Revision ? " UNCHANGED" : " CHANGED"));
            var z = ShaderPreview.Render(floor, 1.3f, 0.9f, 3.1f, 2f, 300f);
            z.Text(6, 6, "WHERE THEY MEET (WALL FOOT IS THE TOP EDGE)");
            Save(ShaderPreview.Sheet(new[] { p0, p1 }, 2), "l_runoff_beside_a_portal_patch.png");
            Save(z, "l2_where_they_meet.png");
        }

        [Fact]
        public void F_OpeningFinder()
        {
            const float ppm = 110f;
            var panels = new Picture[4];

            var s = new PaintSurface(Wall());
            var f = new OpeningFinder(s);
            Vec2 hit = new Vec2(2f, 2.1f);
            s.Splat(PaintColor.Orange, hit, 0.45f, new Vec2(0.4f, -0.3f), 9u, 10f);
            Run(s, 8f);
            var o = f.Find(PaintColor.Orange, hit);
            panels[0] = ShaderPreview.RenderWall(s, ppm);
            ShaderPreview.DrawOpening(panels[0], s, f, o, 0f, s.Wall.Height, ppm);
            panels[0].Text(6, 6, "BIG SPLAT R=" + o.InscribedRadius.ToString("0.000") + (o.Valid ? " VALID" : " TOO SMALL"));

            var s2 = new PaintSurface(Wall());
            var f2 = new OpeningFinder(s2);
            s2.Splat(PaintColor.Blue, new Vec2(2f, 1.8f), 0.24f, new Vec2(0f, 0f), 10u, 8f);
            Run(s2, 8f);
            var o2 = f2.Find(PaintColor.Blue, new Vec2(2f, 1.8f));
            panels[1] = ShaderPreview.RenderWall(s2, ppm);
            ShaderPreview.DrawOpening(panels[1], s2, f2, o2, 0f, s2.Wall.Height, ppm);
            panels[1].Text(6, 6, "MEDIUM R=" + o2.InscribedRadius.ToString("0.000") + (o2.Valid ? " VALID" : " TOO SMALL"));

            var s3 = new PaintSurface(Wall());
            var f3 = new OpeningFinder(s3);
            s3.PaintBox(PaintColor.Orange, new Vec2(0.5f, 0.5f), new Vec2(2.5f, 1.1f));
            s3.PaintBox(PaintColor.Orange, new Vec2(0.5f, 0.5f), new Vec2(1.1f, 2.5f));
            var o3 = f3.Find(PaintColor.Orange, new Vec2(2.2f, 0.8f));
            panels[2] = ShaderPreview.RenderWall(s3, ppm);
            ShaderPreview.DrawOpening(panels[2], s3, f3, o3, 0f, s3.Wall.Height, ppm);
            panels[2].Text(6, 6, "L SHAPE R=" + o3.InscribedRadius.ToString("0.000") + " TRUE 0.351");

            var s4 = new PaintSurface(Wall());
            var f4 = new OpeningFinder(s4);
            s4.PaintDisc(PaintColor.Blue, new Vec2(1.1f, 1.5f), 0.5f);
            s4.PaintDisc(PaintColor.Blue, new Vec2(2.9f, 1.5f), 0.35f);
            s4.PaintBox(PaintColor.Blue, new Vec2(1.5f, 1.47f), new Vec2(2.6f, 1.53f));
            var o4 = f4.Find(PaintColor.Blue, new Vec2(2.9f, 1.5f));
            panels[3] = ShaderPreview.RenderWall(s4, ppm);
            ShaderPreview.DrawOpening(panels[3], s4, f4, o4, 0f, s4.Wall.Height, ppm);
            panels[3].Text(6, 6, "NECK, SEED RIGHT R=" + o4.InscribedRadius.ToString("0.000") + " TRUE 0.500");

            Save(ShaderPreview.Sheet(panels, 2), "f_opening_finder.png");
        }
    }
}
