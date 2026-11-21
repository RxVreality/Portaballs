using System;
using Xunit;

namespace Portaballs.Core.Tests
{
    /// <summary>Checks for the shared pieces in core/src/Common.</summary>
    public class CommonTests
    {
        // A wall across the far end of a room: the viewer looks along +X, so the wall's normal is -X.
        // Viewer's right is -Y (because +Y is left), up is +Z.
        private static WallFrame FarWall()
        {
            return new WallFrame(
                origin: new Vec3(5f, 2f, 0f),
                u: new Vec3(0f, -1f, 0f),
                v: new Vec3(0f, 0f, 1f),
                width: 4f,
                height: 3f);
        }

        [Fact]
        public void WallNormalPointsBackAtTheViewer()
        {
            WallFrame wall = FarWall();
            Assert.Equal(-1f, wall.Normal.X, 5);
            Assert.Equal(0f, wall.Normal.Y, 5);
            Assert.Equal(0f, wall.Normal.Z, 5);
        }

        [Fact]
        public void WallCoordinatesRoundTrip()
        {
            WallFrame wall = FarWall();
            Vec2 uv = new Vec2(1.25f, 2.5f);
            Vec3 world = wall.ToWorld(uv, 0.3f);
            Vec2 back = wall.ToUV(world);
            Assert.Equal(uv.X, back.X, 5);
            Assert.Equal(uv.Y, back.Y, 5);
            Assert.Equal(0.3f, wall.SignedDistance(world), 5);
            // 0.3 m off the wall toward the room means 0.3 m less far along +X.
            Assert.Equal(4.7f, world.X, 5);
        }

        [Fact]
        public void SegmentThroughWallReportsTheCrossingPoint()
        {
            WallFrame wall = FarWall();
            Vec3 inRoom = wall.ToWorld(new Vec2(1f, 1f), 1f);
            Vec3 behind = wall.ToWorld(new Vec2(2f, 2f), -1f);
            Assert.True(wall.SegmentCrossing(inRoom, behind, out Vec2 uv, out float t));
            Assert.Equal(0.5f, t, 5);
            Assert.Equal(1.5f, uv.X, 5);
            Assert.Equal(1.5f, uv.Y, 5);
            Assert.False(wall.SegmentCrossing(inRoom, inRoom + new Vec3(0f, 1f, 0f), out _, out _));
        }

        [Fact]
        public void SlerpDirectionMovesTheRightFraction()
        {
            Vec3 from = new Vec3(1f, 0f, 0f);
            Vec3 to = new Vec3(0f, 1f, 0f);
            Vec3 mid = MathUtil.SlerpDirection(from, to, 0.5f);
            Assert.Equal(MathF.PI / 4f, MathUtil.AngleBetween(from, mid), 4);
            Assert.Equal(1f, mid.Length(), 5);
            Vec3 opposite = MathUtil.SlerpDirection(from, -from, 1f);
            Assert.Equal(-1f, opposite.X, 4);
        }

        [Fact]
        public void RngIsRepeatableAndInRange()
        {
            Rng a = new Rng(1234u);
            Rng b = new Rng(1234u);
            double sum = 0;
            for (int i = 0; i < 10000; i++)
            {
                float x = a.NextFloat();
                Assert.Equal(x, b.NextFloat());
                Assert.InRange(x, 0f, 0.9999999f);
                sum += x;
            }
            Assert.InRange(sum / 10000.0, 0.48, 0.52);
        }
    }
}
