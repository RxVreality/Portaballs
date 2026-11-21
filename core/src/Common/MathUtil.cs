using System;

namespace Portaballs.Core
{
    public static class MathUtil
    {
        public static float Saturate(float x)
        {
            return x < 0f ? 0f : (x > 1f ? 1f : x);
        }

        public static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        /// <summary>0 at or below edge0, 1 at or above edge1, smooth in between.</summary>
        public static float SmoothStep(float edge0, float edge1, float x)
        {
            if (edge1 == edge0) return x < edge0 ? 0f : 1f;
            float t = Saturate((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        /// <summary>Unit vector, or <paramref name="fallback"/> when the input is too short to have a direction.</summary>
        public static Vec3 SafeNormalize(Vec3 v, Vec3 fallback)
        {
            float lenSq = v.LengthSquared();
            if (lenSq < 1e-12f || float.IsNaN(lenSq)) return fallback;
            return v / MathF.Sqrt(lenSq);
        }

        /// <summary>Angle in radians between two vectors (any length). 0 when either is near zero.</summary>
        public static float AngleBetween(Vec3 a, Vec3 b)
        {
            float la = a.Length();
            float lb = b.Length();
            if (la < 1e-6f || lb < 1e-6f) return 0f;
            // atan2 form stays accurate for tiny and near-180 degree angles, unlike acos(dot).
            float cross = Vec3.Cross(a, b).Length();
            float dot = Vec3.Dot(a, b);
            return MathF.Atan2(cross, dot);
        }

        /// <summary>
        /// Rotate unit direction <paramref name="from"/> toward unit direction <paramref name="to"/> by fraction t in [0, 1]
        /// along the shortest arc. Returns a unit vector.
        /// </summary>
        public static Vec3 SlerpDirection(Vec3 from, Vec3 to, float t)
        {
            from = SafeNormalize(from, new Vec3(1f, 0f, 0f));
            to = SafeNormalize(to, from);
            t = Saturate(t);
            float angle = AngleBetween(from, to);
            if (angle < 1e-5f) return from;

            Vec3 axis = Vec3.Cross(from, to);
            if (axis.LengthSquared() < 1e-10f)
            {
                // Opposite directions: any perpendicular axis works; pick a stable one.
                Vec3 helper = MathF.Abs(from.Z) < 0.9f ? new Vec3(0f, 0f, 1f) : new Vec3(1f, 0f, 0f);
                axis = Vec3.Cross(from, helper);
            }
            axis = Vec3.Normalize(axis);
            Quat q = Quat.CreateFromAxisAngle(axis, angle * t);
            return Vec3.Normalize(Vec3.Transform(from, q));
        }
    }
}
