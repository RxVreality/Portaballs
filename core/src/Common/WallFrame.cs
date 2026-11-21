using System;

namespace Portaballs.Core
{
    /// <summary>
    /// A flat rectangular surface and its own 2D coordinates, shared by paint and portals.
    /// Stand in the room facing the wall: Origin is the rectangle's lower-left corner, U points to
    /// your right along the wall, V points up the wall, Normal points out of the wall toward you.
    /// U x V = Normal. Wall coordinates (u, v) are metres from Origin.
    ///
    /// A floor or a ceiling is a surface too: U and V lie in it and Normal = U x V still points INTO the
    /// room (+Z for a floor, -Z for a ceiling), so there V is level, not up. Nothing in this struct
    /// assumes V is up; code that needs to know asks <see cref="IsFloor"/> / <see cref="IsCeiling"/>.
    /// </summary>
    public readonly struct WallFrame
    {
        public readonly Vec3 Origin;
        public readonly Vec3 U;
        public readonly Vec3 V;
        public readonly Vec3 Normal;
        public readonly float Width;
        public readonly float Height;

        /// <param name="origin">World position of the lower-left corner.</param>
        /// <param name="u">Direction along the wall to the viewer's right (any length).</param>
        /// <param name="v">Direction up the wall (any length; made perpendicular to u).</param>
        /// <param name="width">Size along U in metres.</param>
        /// <param name="height">Size along V in metres.</param>
        public WallFrame(Vec3 origin, Vec3 u, Vec3 v, float width, float height)
        {
            Origin = origin;
            U = Vec3.Normalize(u);
            Vec3 vOrtho = v - U * Vec3.Dot(v, U);
            V = Vec3.Normalize(vOrtho);
            Normal = Vec3.Normalize(Vec3.Cross(U, V));
            Width = width;
            Height = height;
        }

        /// <summary>Wall coordinates of a world point (its projection onto the wall plane).</summary>
        public Vec2 ToUV(Vec3 world)
        {
            Vec3 d = world - Origin;
            return new Vec2(Vec3.Dot(d, U), Vec3.Dot(d, V));
        }

        /// <summary>Signed distance from the wall plane: positive on the room side.</summary>
        public float SignedDistance(Vec3 world)
        {
            return Vec3.Dot(world - Origin, Normal);
        }

        /// <summary>World position of wall coordinates, optionally lifted off the wall along Normal.</summary>
        public Vec3 ToWorld(Vec2 uv, float distance = 0f)
        {
            return Origin + U * uv.X + V * uv.Y + Normal * distance;
        }

        /// <summary>True when (u, v) lies on the rectangle.</summary>
        public bool Contains(Vec2 uv)
        {
            return uv.X >= 0f && uv.X <= Width && uv.Y >= 0f && uv.Y <= Height;
        }

        /// <summary>How far the normal must point up or down (its Z, 0 to 1) for the surface to count as a floor or ceiling: steeper than 45 degrees.</summary>
        public const float FlatNormalZ = 0.7071f;

        /// <summary>True for a floor: the surface faces up, things stand on it and fall into a hole in it.</summary>
        public bool IsFloor
        {
            get { return Normal.Z > FlatNormalZ; }
        }

        /// <summary>True for a ceiling: the surface faces down.</summary>
        public bool IsCeiling
        {
            get { return Normal.Z < -FlatNormalZ; }
        }

        /// <summary>True for a floor or a ceiling (false for a wall, upright or leaning up to 45 degrees).</summary>
        public bool IsFlat
        {
            get { return Normal.Z > FlatNormalZ || Normal.Z < -FlatNormalZ; }
        }

        /// <summary>World position of the rectangle's centre.</summary>
        public Vec3 Center
        {
            get { return ToWorld(new Vec2(Width * 0.5f, Height * 0.5f)); }
        }

        /// <summary>
        /// Where the segment from a to b crosses the wall plane, if it does.
        /// Returns false when both ends are on the same side or the segment lies in the plane.
        /// </summary>
        public bool SegmentCrossing(Vec3 a, Vec3 b, out Vec2 uv, out float t)
        {
            float da = SignedDistance(a);
            float db = SignedDistance(b);
            uv = default;
            t = 0f;
            if ((da > 0f) == (db > 0f)) return false;
            float denom = da - db;
            if (MathF.Abs(denom) < 1e-9f) return false;
            t = da / denom;
            uv = ToUV(a + (b - a) * t);
            return true;
        }
    }
}
