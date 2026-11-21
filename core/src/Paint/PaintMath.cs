using System;

namespace Portaballs.Core
{
    /// <summary>
    /// Small pure maths helpers for the paint: shape distance functions and repeatable noise.
    /// Nothing here keeps any state; the same inputs always give the same answer.
    /// "Signed distance" means: negative inside the shape, zero on its edge, positive outside, in metres.
    /// </summary>
    internal static class PaintMath
    {
        /// <summary>Blend two shapes so they melt together with a fillet of size k instead of a hard crease.</summary>
        public static float SmoothMin(float a, float b, float k)
        {
            if (k <= 0f) return a < b ? a : b;
            float h = k - MathF.Abs(a - b);
            if (h <= 0f) return a < b ? a : b;
            h /= k;
            return (a < b ? a : b) - h * h * k * 0.25f;
        }

        /// <summary>
        /// Distance to a rounded stick from a (half-width ra) to b (half-width rb). With rb smaller than ra it is a
        /// tapering spike or a drip trail. Close to exact while the taper is gentle, which is all we need.
        /// </summary>
        public static float Capsule(float px, float py, float ax, float ay, float bx, float by, float ra, float rb)
        {
            float bax = bx - ax, bay = by - ay;
            float pax = px - ax, pay = py - ay;
            float len2 = bax * bax + bay * bay;
            float t = len2 > 1e-12f ? (pax * bax + pay * bay) / len2 : 0f;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            float dx = pax - bax * t, dy = pay - bay * t;
            return MathF.Sqrt(dx * dx + dy * dy) - (ra + (rb - ra) * t);
        }

        /// <summary>A well-mixed 32-bit hash of three integers. Used to make repeatable noise.</summary>
        public static uint Hash(int x, int y, uint seed)
        {
            uint h = seed * 0x9E3779B1u;
            h ^= (uint)x * 0x85EBCA77u;
            h = (h << 13) | (h >> 19);
            h ^= (uint)y * 0xC2B2AE3Du;
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            h ^= h >> 16;
            return h;
        }

        /// <summary>Hash turned into a number in [0, 1).</summary>
        public static float Hash01(int x, int y, uint seed)
        {
            return (Hash(x, y, seed) >> 8) * (1f / 16777216f);
        }

        /// <summary>Smooth random hills in 2D, values in [0, 1), one hill roughly every unit.</summary>
        public static float ValueNoise(float x, float y, uint seed)
        {
            float fx = MathF.Floor(x), fy = MathF.Floor(y);
            int ix = (int)fx, iy = (int)fy;
            float tx = x - fx, ty = y - fy;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);
            float a = Hash01(ix, iy, seed);
            float b = Hash01(ix + 1, iy, seed);
            float c = Hash01(ix, iy + 1, seed);
            float d = Hash01(ix + 1, iy + 1, seed);
            float ab = a + (b - a) * tx;
            float cd = c + (d - c) * tx;
            return ab + (cd - ab) * ty;
        }

        /// <summary>Smooth random wiggle in 1D, values in [-1, 1).</summary>
        public static float Wiggle(float x, uint seed)
        {
            float fx = MathF.Floor(x);
            int ix = (int)fx;
            float t = x - fx;
            t = t * t * (3f - 2f * t);
            float a = Hash01(ix, 7, seed);
            float b = Hash01(ix + 1, 7, seed);
            return (a + (b - a) * t) * 2f - 1f;
        }

        /// <summary>Turn a signed distance into the 0..1 paint field: 0.5 on the edge, 1 inside, 0 outside, with a
        /// linear ramp of total width 2 * halfWidth so a bilinear sample finds the edge to a fraction of a texel.</summary>
        public static float FieldFromDistance(float signedDistance, float halfWidth)
        {
            float f = 0.5f - signedDistance / (2f * halfWidth);
            return f < 0f ? 0f : (f > 1f ? 1f : f);
        }
    }
}
