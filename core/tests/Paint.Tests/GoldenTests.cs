using System;
using Xunit;
using Xunit.Abstractions;

namespace Portaballs.Core.Tests
{
    /// <summary>
    /// Fingerprints of whole walls (every texel's two fields, wetness and film, the texture bytes, and the opening
    /// found) for a handful of scenes. The expected values were printed by the paint code as it was on 2026-11-19
    /// BEFORE it was made faster and sliceable, so a pass here means the faster code still paints the very same
    /// wall, bit for bit. If you change how paint looks on purpose, these will fail: run the test, read the new
    /// values it prints, and paste them in.
    ///
    /// Each scene runs twice (2026-11-19, the tester's request, see SplatFeel):
    ///   asBefore = true:  the settings as they were before that request (fixed steps, strokes every 0.4 head
    ///                     widths, drips at the old speed). These are the original values: that path is unchanged.
    ///   asBefore = false: today's defaults (even steps, a stroke every frame, drips 1.75x quicker). These values
    ///                     were printed by the code that added them.
    ///
    /// The asBefore = false values were regenerated on purpose on the evening of 2026-11-19, when the tester asked for
    /// longer drips (paint stays wet 7 s, slows as it dries, thinning trail, wet-on-wet, joined drips run faster:
    /// see SplatFeel). The asBefore = true values were NOT touched and still pass: with those settings the code
    /// that now follows gravity along any surface paints the very same wall, bit for bit, as before it could.
    /// </summary>
    public class GoldenTests
    {
        private readonly ITestOutputHelper _out;

        public GoldenTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>The paint settings as they were before the tester's 2026-11-19 request (see SplatFeel).</summary>
        internal static PaintSettings AsBefore(PaintSettings s = null)
        {
            s = s ?? new PaintSettings();
            s.EvenSubsteps = false;
            s.StrokeEveryFrame = false;
            s.RunSpeedUp = 1f;
            // ...and before that evening's second request (longer drips, wet-on-wet, joined drips running faster).
            s.DryTime = 5f;
            s.WetFadeTime = 3f;
            s.WetStepLevels = 0f;
            s.SlowDownCurve = 0.5f;
            s.SheetEndSpeed = 0.45f;
            s.TrailThinEnd = 1f;
            s.TrailDriesWithPaint = false;
            s.WetOnWetBoost = 0f;
            s.JoinSpeedBoost = 1f;
            return s;
        }

        /// <summary>
        /// The paint settings as the tester saw them in the headset on the evening of 2026-11-19, before they asked for longer
        /// drips: today's smooth stepping, drips 1.75 times quicker AND drying 1.75 times sooner (5 s on a clock
        /// running 1.75 times fast is 2.86 real seconds), the old slowing-down shape, no thinning trail.
        /// </summary>
        internal static PaintSettings AsAnHourAgo(PaintSettings s = null)
        {
            s = s ?? new PaintSettings();
            s.DryTime = 5f / 1.75f;
            s.WetFadeTime = 3f;
            s.WetStepLevels = 0f;
            s.SlowDownCurve = 0.5f;
            s.SheetEndSpeed = 0.45f;
            s.TrailThinEnd = 1f;
            s.TrailDriesWithPaint = false;
            s.WetOnWetBoost = 0f;
            s.JoinSpeedBoost = 1f;
            return s;
        }

        private static PaintSettings Pick(bool asBefore, PaintSettings s = null)
        {
            return asBefore ? AsBefore(s) : (s ?? new PaintSettings());
        }

        private static ulong Mix(ulong h, uint v)
        {
            h ^= v;
            h *= 0x100000001B3UL;
            return h;
        }

        internal static ulong Fingerprint(PaintSurface s)
        {
            ulong h = 0xCBF29CE484222325UL;
            for (int y = 0; y < s.Height; y++)
            {
                for (int x = 0; x < s.Width; x++)
                {
                    h = Mix(h, (uint)BitConverter.SingleToInt32Bits(s.GetField(PaintColor.Orange, x, y)));
                    h = Mix(h, (uint)BitConverter.SingleToInt32Bits(s.GetField(PaintColor.Blue, x, y)));
                    h = Mix(h, (uint)BitConverter.SingleToInt32Bits(s.GetWetness(x, y)));
                    h = Mix(h, (uint)BitConverter.SingleToInt32Bits(s.GetThickness(x, y)));
                }
            }
            byte[] rgba = new byte[s.RgbaByteCount];
            s.CopyAllToRgba(rgba);
            for (int i = 0; i < rgba.Length; i++) h = Mix(h, rgba[i]);
            h = Mix(h, (uint)s.ActiveDripCount);
            return h;
        }

        internal static ulong Fingerprint(ulong h, PortalOpening o, DoorBand d)
        {
            h = Mix(h, o.Valid ? 1u : 0u);
            h = Mix(h, (uint)BitConverter.SingleToInt32Bits(o.CenterUV.X));
            h = Mix(h, (uint)BitConverter.SingleToInt32Bits(o.CenterUV.Y));
            h = Mix(h, (uint)BitConverter.SingleToInt32Bits(o.InscribedRadius));
            h = Mix(h, (uint)BitConverter.SingleToInt32Bits(o.BoundsMin.X));
            h = Mix(h, (uint)BitConverter.SingleToInt32Bits(o.BoundsMin.Y));
            h = Mix(h, (uint)BitConverter.SingleToInt32Bits(o.BoundsMax.X));
            h = Mix(h, (uint)BitConverter.SingleToInt32Bits(o.BoundsMax.Y));
            h = Mix(h, (uint)BitConverter.SingleToInt32Bits(o.Area));
            h = Mix(h, (uint)o.Revision);
            h = Mix(h, d.Found ? 1u : 0u);
            h = Mix(h, (uint)BitConverter.SingleToInt32Bits(d.LeftU));
            h = Mix(h, (uint)BitConverter.SingleToInt32Bits(d.RightU));
            h = Mix(h, (uint)BitConverter.SingleToInt32Bits(d.BottomV));
            h = Mix(h, (uint)BitConverter.SingleToInt32Bits(d.TopV));
            return h;
        }

        private string _wrong;

        private void Check(string name, ulong expected, ulong actual)
        {
            _out.WriteLine($"{name}: 0x{actual:X16}UL");
            if (expected != actual && _wrong == null)
                _wrong = $"{name}: the wall is not the one expected (expected 0x{expected:X16}, got 0x{actual:X16})";
        }

        // Called at the end of each test, so a failing run still prints every value.
        private void Done()
        {
            Assert.True(_wrong == null, _wrong);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void BigSplatWithCurtain_Settled(bool asBefore)
        {
            var s = new PaintSurface(LookSheets.Wall(), Pick(asBefore));
            var f = new OpeningFinder(s);
            Vec2 hit = new Vec2(2f, 1.5f);
            s.Splat(PaintColor.Orange, hit, 0.45f, new Vec2(0.6f, -0.4f), 1234u, 10f);
            Check("fresh", 0x4466926B0ED42C45UL, Fingerprint(s));
            LookSheets.Run(s, 1f);
            ulong mid = Fingerprint(Fingerprint(s), f.Find(PaintColor.Orange, hit), f.FindDoor(PaintColor.Orange, 0.5f, 0.15f));
            Check("after 1 s, with opening", asBefore ? 0x36179F7697398592UL : 0x9A60780A28720F70UL, mid);
            LookSheets.Run(s, 9f);
            ulong end = Fingerprint(Fingerprint(s), f.Find(PaintColor.Orange, hit), f.FindDoor(PaintColor.Orange, 0.5f, 0.15f));
            Check("settled, with opening", asBefore ? 0x92F5AF8A6649EDEEUL : 0x0759D5CCAF4AE131UL, end);
            Done();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void BlueOverOrange_GlancingHits_AndErasing(bool asBefore)
        {
            var s = new PaintSurface(LookSheets.Wall(), Pick(asBefore));
            var f = new OpeningFinder(s);
            s.Splat(PaintColor.Orange, new Vec2(1.7f, 1.9f), 0.4f, new Vec2(0.5f, 0f), 42u, 9f);
            LookSheets.Run(s, 0.6f);
            s.Splat(PaintColor.Blue, new Vec2(2.3f, 2.0f), 0.32f, new Vec2(-4f, 0f), 43u, 9f);     // lands on running orange drips
            LookSheets.Run(s, 0.4f);
            s.Splat(PaintColor.Orange, new Vec2(3.1f, 2.2f), 0.3f, new Vec2(12f, 3f), 44u, 6f);    // a hard glancing hit
            s.Splat(PaintColor.Blue, new Vec2(0.2f, 0.3f), 0.5f, new Vec2(0f, -8f), 45u, -1f);     // half off the wall, default speed
            Check("four splats", asBefore ? 0x9326F691B2EE8540UL : 0x2D132DA06B62495CUL, Fingerprint(s));
            LookSheets.Run(s, 2f, 1f / 144f);
            s.EraseDisc(new Vec2(2.0f, 1.8f), 0.3f);
            s.EraseDisc(new Vec2(3.0f, 2.0f), 0.2f, PaintColor.Orange);
            LookSheets.Run(s, 8f, 1f / 60f);
            ulong h = Fingerprint(s);
            h = Fingerprint(h, f.Find(PaintColor.Orange, new Vec2(1.7f, 1.9f)), f.FindDoor(PaintColor.Orange, 0.3f, 0.15f));
            h = Fingerprint(h, f.Find(PaintColor.Blue, new Vec2(2.3f, 2.0f)), f.FindDoor(PaintColor.Blue, 0.3f, 0.15f));
            Check("settled after erasing", asBefore ? 0x4FE66E56F641F324UL : 0x69DEE0684830A06CUL, h);
            s.EraseColor(PaintColor.Blue);
            h = Fingerprint(Fingerprint(s), f.Find(PaintColor.Blue, new Vec2(2.3f, 2.0f)), f.FindDoor(PaintColor.Blue, 0.3f, 0.15f));
            Check("blue erased", asBefore ? 0x09634724300B790FUL : 0x0BC2F5C33D97C818UL, h);
            Done();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void HeadsetWorstCase_ThreeJoinedCurtainsOnTheBigWall(bool asBefore)
        {
            var s = new PaintSurface(PerfBench.BigWall(), Pick(asBefore));
            var f = new OpeningFinder(s);
            PerfBench.BuildFloorPatch(s, out Vec2 seed);
            PortalOpening o = f.Find(PaintColor.Blue, seed);
            DoorBand d = f.FindDoor(PaintColor.Blue, 0.6f, 0.15f);
            _out.WriteLine($"opening: centre ({o.CenterUV.X:0.0000}, {o.CenterUV.Y:0.0000}) radius {o.InscribedRadius:0.0000} area {o.Area:0.00} m2; door {d.Width:0.00} x {d.Height:0.00} m, reaches floor {d.ReachesFloor}");
            Assert.True(o.Valid);
            Assert.True(d.ReachesFloor);
            Check("floor patch with opening", asBefore ? 0xFE3141D5981471B9UL : 0x08909C4E7CA0F633UL, Fingerprint(Fingerprint(s), o, d));
            Done();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ManySmallSplats_OtherSettings(bool asBefore)
        {
            // Coarser grid, softer edge, no edge noise, half-strength curtain: the paths the default settings skip.
            var set = new PaintSettings { TexelsPerMeter = 48f, EdgeSoftnessTexels = 2.2f, EdgeNoise = 0f, CurtainStrength = 0.5f };
            var s = new PaintSurface(LookSheets.Wall(3.3f, 2.7f), Pick(asBefore, set));
            var f = new OpeningFinder(s);
            var rng = new Rng(2026u);
            for (int i = 0; i < 24; i++)
            {
                PaintColor c = i % 3 == 0 ? PaintColor.Blue : PaintColor.Orange;
                Vec2 uv = new Vec2(rng.Range(-0.1f, 3.4f), rng.Range(0.2f, 2.8f));
                s.Splat(c, uv, rng.Range(0.04f, 0.42f), new Vec2(rng.Range(-9f, 9f), rng.Range(-9f, 9f)), rng.NextUInt(), rng.Range(2f, 16f));
                LookSheets.Run(s, rng.Range(0f, 0.5f), 1f / 72f);
            }
            LookSheets.Run(s, 10f);
            ulong h = Fingerprint(s);
            h = Fingerprint(h, f.Find(PaintColor.Orange, new Vec2(1.6f, 1.4f)), f.FindDoor(PaintColor.Orange, 0.4f, 0.2f));
            h = Fingerprint(h, f.Find(PaintColor.Blue, new Vec2(1.6f, 1.4f)), f.FindDoor(PaintColor.Blue, 0.4f, 0.2f));
            Check("24 splats", asBefore ? 0xA71CA533A0107CD5UL : 0xB12438FB8310FCA1UL, h);
            Done();
        }
    }
}
