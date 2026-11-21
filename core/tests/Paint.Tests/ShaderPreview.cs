using System;
using System.IO;

namespace Portaballs.Core.Tests
{
    /// <summary>
    /// A CPU copy of what the game's paint shader is meant to do, so pictures can be judged before there is an
    /// engine. It reads the same RGBA bytes the game would upload and, per screen pixel:
    ///   - samples them bilinearly (like a GPU),
    ///   - cuts the paint edge at 0.5 with a smoothstep one screen pixel wide (anti-aliased outline),
    ///   - builds a height from the field near the edge and a normal from its slope, so paint looks bulged,
    ///   - lights it with a soft light from the upper left plus a glossy highlight that is stronger while wet,
    ///   - lays orange and blue over a grey wall with faint 0.5 m tile lines for scale.
    /// Since 2026-11-19 the game's paint is NEON (self-lit, a brighter borderline, a halo on the wall beside
    /// it: the "neon" dials of portaballs_paint.shader). <see cref="Neon"/> = true (the default) previews
    /// that with the shader's own numbers; false gives the old matte paint.
    /// </summary>
    public static class ShaderPreview
    {
        private static readonly float[] Orange = { 1.00f, 0.45f, 0.06f };
        private static readonly float[] Blue = { 0.10f, 0.40f, 1.00f };

        /// <summary>True = the neon look the game has now; false = the matte paint it had before.</summary>
        public static bool Neon = true;

        // The shader's neon dials (portaballs_paint.shader: PaintGlow, PaintEdgeGlow, PaintForm, PaintHalo, PaintHaloGlow).
        private const float PaintGlow = 1.0f, PaintEdgeGlow = 1.2f, PaintForm = 1.4f, PaintHalo = 0.7f, PaintHaloGlow = 1.6f;

        private static float ToLinear(float v) { return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f); }
        private static float ToScreen(float v) { v = MathUtil.Saturate(v); return v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f; }

        /// <summary>Render the wall area [u0,u1] x [v0,v1] (metres) at pxPerMetre.</summary>
        public static Picture Render(PaintSurface s, float u0, float v0, float u1, float v1, float pxPerMetre)
        {
            byte[] rgba = new byte[s.RgbaByteCount];
            s.CopyAllToRgba(rgba);
            return Render(s, rgba, u0, v0, u1, v1, pxPerMetre);
        }

        /// <summary>Render the whole wall.</summary>
        public static Picture RenderWall(PaintSurface s, float pxPerMetre)
        {
            return Render(s, 0f, 0f, s.Wall.Width, s.Wall.Height, pxPerMetre);
        }

        public static Picture Render(PaintSurface s, byte[] rgba, float u0, float v0, float u1, float v1, float pxPerMetre)
        {
            int w = Math.Max(1, (int)MathF.Round((u1 - u0) * pxPerMetre));
            int h = Math.Max(1, (int)MathF.Round((v1 - v0) * pxPerMetre));
            var pic = new Picture(w, h);
            float px = 1f / pxPerMetre;
            float tex = Math.Min(s.TexelSizeU, s.TexelSizeV);
            Vec3 light = Vec3.Normalize(new Vec3(-0.45f, 0.6f, 0.65f));
            Vec3 half = Vec3.Normalize(light + new Vec3(0f, 0f, 1f));

            for (int py = 0; py < h; py++)
            {
                float v = v1 - (py + 0.5f) * px;
                for (int pxi = 0; pxi < w; pxi++)
                {
                    float u = u0 + (pxi + 0.5f) * px;

                    // Wall: grey with faint tile lines every 0.5 m.
                    float wall = 0.50f;
                    float lu = u / 0.5f, lv = v / 0.5f;
                    float du = MathF.Abs(lu - MathF.Round(lu)) * 0.5f, dv = MathF.Abs(lv - MathF.Round(lv)) * 0.5f;
                    if (du < px * 0.6f || dv < px * 0.6f) wall = 0.44f;
                    if (u < 0f || v < 0f || u > s.Wall.Width || v > s.Wall.Height) wall = 0.2f;

                    float fo = Sample(s, rgba, 0, u, v);
                    float fb = Sample(s, rgba, 1, u, v);
                    float wet = Sample(s, rgba, 2, u, v);
                    float thick = Sample(s, rgba, 3, u, v);

                    // Screen-space width of the edge (what fwidth() gives a shader).
                    float fwo = MathF.Abs(Sample(s, rgba, 0, u + px, v) - fo) + MathF.Abs(Sample(s, rgba, 0, u, v + px) - fo);
                    float fwb = MathF.Abs(Sample(s, rgba, 1, u + px, v) - fb) + MathF.Abs(Sample(s, rgba, 1, u, v + px) - fb);
                    float co = MathUtil.SmoothStep(0.5f - MathF.Max(fwo, 1e-3f) * 0.6f, 0.5f + MathF.Max(fwo, 1e-3f) * 0.6f, fo);
                    float cb = MathUtil.SmoothStep(0.5f - MathF.Max(fwb, 1e-3f) * 0.6f, 0.5f + MathF.Max(fwb, 1e-3f) * 0.6f, fb);
                    float cov = MathF.Max(co, cb);

                    float r = wall, g = wall, b = wall * 1.03f;
                    if (cov > 0f)
                    {
                        // Height: rises across the inner half of the edge ramp, plus a little from paint thickness.
                        float e = tex * 0.5f;
                        float hc = Height(s, rgba, u, v);
                        float hx = (Height(s, rgba, u + e, v) - Height(s, rgba, u - e, v)) / (2f * e);
                        float hy = (Height(s, rgba, u, v + e) - Height(s, rgba, u, v - e)) / (2f * e);
                        const float Bump = 0.010f; // metres of bulge
                        Vec3 n = Vec3.Normalize(new Vec3(-hx * Bump, -hy * Bump, 1f));
                        float diff = MathF.Max(0f, Vec3.Dot(n, light));
                        float gloss = 30f + 90f * wet;
                        float spec = MathF.Pow(MathF.Max(0f, Vec3.Dot(n, half)), gloss) * (0.18f + 0.55f * wet);
                        float rim = 1f - n.Z; // darker where the paint curves away
                        float wo = co / MathF.Max(1e-4f, co + cb);
                        float pr = Orange[0] * wo + Blue[0] * (1f - wo);
                        float pg = Orange[1] * wo + Blue[1] * (1f - wo);
                        float pb = Orange[2] * wo + Blue[2] * (1f - wo);
                        float shade = 0.42f + 0.68f * diff - 0.6f * rim + 0.04f * hc;
                        float sr = pr * shade + spec, sg = pg * shade + spec, sb = pb * shade + spec;
                        if (Neon)
                        {
                            // Self-lit, as the shader does it: worked out in linear light, each channel cut off at 1
                            // (which is what turns a strong orange toward amber), then back to screen values.
                            float border = 1f - MathUtil.SmoothStep(0.42f, 1f, MathF.Max(fo, fb));
                            float relief = Math.Clamp(1f + PaintForm * (n.X * -0.6f + n.Y * 0.8f), 0.6f, 1.5f) * (0.9f + 0.3f * MathUtil.Saturate(thick));
                            float glow = PaintGlow * relief + PaintEdgeGlow * border * border;
                            float glint = spec * 2.5f;
                            sr = ToScreen(ToLinear(pr) * glow + glint);
                            sg = ToScreen(ToLinear(pg) * glow + glint);
                            sb = ToScreen(ToLinear(pb) * glow + glint);
                        }
                        r = r * (1f - cov) + sr * cov;
                        g = g * (1f - cov) + sg * cov;
                        b = b * (1f - cov) + sb * cov;
                    }
                    else
                    {
                        // A faint shadow of nearby paint on the wall gives it some depth.
                        float near = MathF.Max(fo, fb);
                        if (Neon)
                        {
                            // The halo: the paint's light on the bare wall, as far as the paint data reaches.
                            float spill = MathUtil.Saturate((fo + fb) * 2f);
                            float a = PaintHalo * spill * spill;
                            float wo = fo / MathF.Max(1e-4f, fo + fb);
                            r = ToScreen(ToLinear(r) * (1f - a) + ToLinear(Orange[0] * wo + Blue[0] * (1f - wo)) * PaintHaloGlow * a);
                            g = ToScreen(ToLinear(g) * (1f - a) + ToLinear(Orange[1] * wo + Blue[1] * (1f - wo)) * PaintHaloGlow * a);
                            b = ToScreen(ToLinear(b) * (1f - a) + ToLinear(Orange[2] * wo + Blue[2] * (1f - wo)) * PaintHaloGlow * a);
                        }
                        else
                        {
                            float sh = 1f - 0.25f * MathUtil.SmoothStep(0f, 0.5f, near);
                            r *= sh; g *= sh; b *= sh;
                        }
                    }
                    _ = thick;
                    pic.SetF(pxi, py, r, g, b);
                }
            }
            return pic;
        }

        private static float Height(PaintSurface s, byte[] rgba, float u, float v)
        {
            float f = MathF.Max(Sample(s, rgba, 0, u, v), Sample(s, rgba, 1, u, v));
            float t = Sample(s, rgba, 3, u, v);
            return MathUtil.SmoothStep(0.45f, 1f, f) * (0.75f + 0.25f * MathUtil.Saturate(t * 1.5f));
        }

        /// <summary>Bilinear sample of one byte channel at wall metres, texel centres at (i + 0.5) * size, clamped at the edges.</summary>
        public static float Sample(PaintSurface s, byte[] rgba, int ch, float u, float v)
        {
            int W = s.Width, H = s.Height;
            float fx = u / s.TexelSizeU - 0.5f, fy = v / s.TexelSizeV - 0.5f;
            if (fx < 0f) fx = 0f; else if (fx > W - 1) fx = W - 1;
            if (fy < 0f) fy = 0f; else if (fy > H - 1) fy = H - 1;
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = Math.Min(x0 + 1, W - 1), y1 = Math.Min(y0 + 1, H - 1);
            float tx = fx - x0, ty = fy - y0;
            float a = rgba[(x0 + y0 * W) * 4 + ch], b = rgba[(x1 + y0 * W) * 4 + ch];
            float c = rgba[(x0 + y1 * W) * 4 + ch], d = rgba[(x1 + y1 * W) * 4 + ch];
            float ab = a + (b - a) * tx, cd = c + (d - c) * tx;
            return (ab + (cd - ab) * ty) / 255f;
        }

        // ------------------------------------------------------------------ overlays

        /// <summary>Wall metres to picture pixels for a picture made by Render with these bounds.</summary>
        public static void ToPixel(float u, float v, float u0, float v1, float pxPerMetre, out float x, out float y)
        {
            x = (u - u0) * pxPerMetre;
            y = (v1 - v) * pxPerMetre;
        }

        /// <summary>Draw what the opening finder found: patch outline (yellow), bounding box (red), inscribed circle (white, green when valid).</summary>
        public static void DrawOpening(Picture pic, PaintSurface s, OpeningFinder f, PortalOpening o, float u0, float v1, float ppm)
        {
            // Patch outline: texels in the patch with a neighbour outside it.
            for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++)
            {
                if (!f.InLastPatch(o.Color, x, y)) continue;
                bool edge = x == 0 || y == 0 || x == s.Width - 1 || y == s.Height - 1
                            || !f.InLastPatch(o.Color, x - 1, y) || !f.InLastPatch(o.Color, x + 1, y)
                            || !f.InLastPatch(o.Color, x, y - 1) || !f.InLastPatch(o.Color, x, y + 1);
                if (!edge) continue;
                Vec2 c = s.TexelCenter(x, y);
                ToPixel(c.X, c.Y, u0, v1, ppm, out float px, out float py);
                pic.Set((int)px, (int)py, 255, 235, 40);
            }
            ToPixel(o.BoundsMin.X, o.BoundsMax.Y, u0, v1, ppm, out float bx0, out float by0);
            ToPixel(o.BoundsMax.X, o.BoundsMin.Y, u0, v1, ppm, out float bx1, out float by1);
            pic.Rect((int)bx0, (int)by0, (int)bx1, (int)by1, 230, 30, 30);
            ToPixel(o.CenterUV.X, o.CenterUV.Y, u0, v1, ppm, out float cx, out float cy);
            byte cr = o.Valid ? (byte)60 : (byte)255, cg = 255, cbb = o.Valid ? (byte)90 : (byte)255;
            pic.Circle(cx, cy, o.InscribedRadius * ppm, cr, cg, cbb);
            for (int k = -4; k <= 4; k++) { pic.Set((int)cx + k, (int)cy, cr, cg, cbb); pic.Set((int)cx, (int)cy + k, cr, cg, cbb); }
        }

        /// <summary>Draw a door band as a cyan rectangle (two pixels thick).</summary>
        public static void DrawDoor(Picture pic, DoorBand d, float u0, float v1, float ppm)
        {
            if (!d.Found) return;
            ToPixel(d.LeftU, d.TopV, u0, v1, ppm, out float x0, out float y0);
            ToPixel(d.RightU, d.BottomV, u0, v1, ppm, out float x1, out float y1);
            pic.Rect((int)x0, (int)y0, (int)x1 - 1, (int)y1 - 1, 40, 230, 255);
            pic.Rect((int)x0 + 1, (int)y0 + 1, (int)x1 - 2, (int)y1 - 2, 40, 230, 255);
        }

        // ------------------------------------------------------------------ sheets

        /// <summary>Lay panels out in a grid with a gap, on a dark background.</summary>
        public static Picture Sheet(Picture[] panels, int columns, int gap = 8)
        {
            int cw = 0, ch = 0;
            foreach (var p in panels) { cw = Math.Max(cw, p.Width); ch = Math.Max(ch, p.Height); }
            int rows = (panels.Length + columns - 1) / columns;
            var sheet = new Picture(columns * cw + (columns + 1) * gap, rows * ch + (rows + 1) * gap);
            sheet.Fill(28, 28, 32);
            for (int i = 0; i < panels.Length; i++)
            {
                int col = i % columns, row = i / columns;
                sheet.Blit(panels[i], gap + col * (cw + gap), gap + row * (ch + gap));
            }
            return sheet;
        }

        /// <summary>The tests' output folder: core/tests/Paint.Tests/out.</summary>
        public static string OutDir()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "Paint.Tests.csproj"))) return Path.Combine(dir, "out");
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            return Path.Combine(AppContext.BaseDirectory, "out");
        }
    }
}
