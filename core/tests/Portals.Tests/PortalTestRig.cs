using System;
using Xunit;

namespace Portaballs.Core.Tests
{
    /// <summary>A round patch of one colour on one wall.</summary>
    internal sealed class DiscMask : IPaintMask
    {
        private readonly PaintColor _color;
        private readonly Vec2 _center;
        private readonly float _radius;

        public DiscMask(WallFrame wall, PaintColor color, Vec2 center, float radius)
        {
            Wall = wall;
            _color = color;
            _center = center;
            _radius = radius;
        }

        public WallFrame Wall { get; }

        public bool IsPainted(PaintColor color, Vec2 uv)
        {
            return color == _color && Vec2.Distance(uv, _center) <= _radius;
        }
    }

    /// <summary>A rectangle of paint of one colour.</summary>
    internal sealed class RectMask : IPaintMask
    {
        private readonly PaintColor _color;
        private readonly Vec2 _min;
        private readonly Vec2 _max;

        public RectMask(WallFrame wall, PaintColor color, Vec2 min, Vec2 max)
        {
            Wall = wall;
            _color = color;
            _min = min;
            _max = max;
        }

        public WallFrame Wall { get; }

        public bool IsPainted(PaintColor color, Vec2 uv)
        {
            return color == _color && uv.X >= _min.X && uv.X <= _max.X && uv.Y >= _min.Y && uv.Y <= _max.Y;
        }
    }

    /// <summary>
    /// A 6 x 6 room with its floor at z = 0. Looking along +X: the far wall is at x = 5, the near wall at
    /// x = 0, the left wall at y = +3.
    /// </summary>
    internal static class Rig
    {
        public const int Precision = 4;

        /// <summary>Wall at x = 5 facing -X. U = -Y, V = +Z.</summary>
        public static WallFrame FarWall(float baseZ = 0f)
        {
            return new WallFrame(new Vec3(5f, 2f, baseZ), new Vec3(0f, -1f, 0f), new Vec3(0f, 0f, 1f), 4f, 3f);
        }

        /// <summary>Wall at x = 0 facing +X. U = +Y, V = +Z.</summary>
        public static WallFrame NearWall(float baseZ = 0f)
        {
            return new WallFrame(new Vec3(0f, -2f, baseZ), new Vec3(0f, 1f, 0f), new Vec3(0f, 0f, 1f), 4f, 3f);
        }

        /// <summary>Wall at y = 3 facing -Y. U = +X, V = +Z.</summary>
        public static WallFrame LeftWall()
        {
            return new WallFrame(new Vec3(0f, 3f, 0f), new Vec3(1f, 0f, 0f), new Vec3(0f, 0f, 1f), 5f, 3f);
        }

        /// <summary>The floor (plane z = 0, facing up), as the game's room builds it: U = +X, V = +Y, corner at (0, -3).</summary>
        public static WallFrame Floor()
        {
            return new WallFrame(new Vec3(0f, -3f, 0f), new Vec3(1f, 0f, 0f), new Vec3(0f, 1f, 0f), 5f, 6f);
        }

        /// <summary>The ceiling (plane z = 3, facing down), as the game's room builds it: U = -X, V = +Y, corner at (5, -3).</summary>
        public static WallFrame Ceiling()
        {
            return new WallFrame(new Vec3(5f, -3f, 3f), new Vec3(-1f, 0f, 0f), new Vec3(0f, 1f, 0f), 5f, 6f);
        }

        /// <summary>Near wall leaning back 30 degrees, so its normal points out and a bit up.</summary>
        public static WallFrame LeaningNearWall()
        {
            float s = MathF.Sin(MathF.PI / 6f);
            float c = MathF.Cos(MathF.PI / 6f);
            return new WallFrame(new Vec3(0f, -2f, 0f), new Vec3(0f, 1f, 0f), new Vec3(-s, 0f, c), 4f, 3f);
        }

        public static PortalOpening Opening(PaintColor color, Vec2 center, float radius = 0.5f, int revision = 1)
        {
            return new PortalOpening
            {
                Valid = true,
                Color = color,
                CenterUV = center,
                InscribedRadius = radius,
                BoundsMin = center - new Vec2(radius, radius),
                BoundsMax = center + new Vec2(radius, radius),
                Area = MathF.PI * radius * radius,
                Revision = revision,
            };
        }

        public static PortalEnd End(WallFrame wall, PaintColor color, Vec2 center, PortalAnchor anchor,
            float floor = 0f, float radius = 0.5f)
        {
            return PortalEnd.Create(wall, Opening(color, center, radius), anchor, floor);
        }

        /// <summary>Each component within 10^-precision (an absolute tolerance, not digit rounding).</summary>
        public static void Near(Vec3 expected, Vec3 actual, int precision = Precision)
        {
            double tol = Math.Pow(10, -precision);
            Assert.Equal(expected.X, actual.X, tol);
            Assert.Equal(expected.Y, actual.Y, tol);
            Assert.Equal(expected.Z, actual.Z, tol);
        }

        public static void Near(Vec2 expected, Vec2 actual, int precision = Precision)
        {
            double tol = Math.Pow(10, -precision);
            Assert.Equal(expected.X, actual.X, tol);
            Assert.Equal(expected.Y, actual.Y, tol);
        }

        /// <summary>Same rotation (q and -q are the same rotation).</summary>
        public static void SameRotation(Quat expected, Quat actual, int precision = Precision)
        {
            float dot = MathF.Abs(Quat.Dot(Quat.Normalize(expected), Quat.Normalize(actual)));
            Assert.Equal(1f, dot, precision);
        }
    }
}
