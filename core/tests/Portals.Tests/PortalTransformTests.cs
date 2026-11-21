using System;
using Xunit;

namespace Portaballs.Core.Tests
{
    /// <summary>Portal frames and the crossing transform.</summary>
    public class PortalTransformTests
    {
        private static readonly Vec3 BallVelocity = new Vec3(3f, 0.5f, -1f);

        [Fact]
        public void FrameAxesFollowTheWall()
        {
            PortalEnd a = Rig.End(Rig.FarWall(), PaintColor.Orange, new Vec2(2f, 1.5f), PortalAnchor.PaintCenter);
            Rig.Near(new Vec3(-1f, 0f, 0f), Vec3.Transform(Vec3.UnitX, a.Rotation));
            Rig.Near(new Vec3(0f, -1f, 0f), Vec3.Transform(Vec3.UnitY, a.Rotation));
            Rig.Near(new Vec3(0f, 0f, 1f), Vec3.Transform(Vec3.UnitZ, a.Rotation));
            Rig.Near(new Vec3(5f, 0f, 1.5f), a.Position);
            Assert.Equal(PortalAnchor.PaintCenter, a.AnchorUsed);
        }

        [Fact]
        public void FloorAlignedFrameSitsUnderThePatchAtFloorHeight()
        {
            // Wall starts 0.5 m above a floor at z = 0; patch centre 1 m up the wall, so 1.5 m above the floor.
            PortalEnd a = Rig.End(Rig.FarWall(0.5f), PaintColor.Orange, new Vec2(2f, 1f), PortalAnchor.FloorAligned, 0f);
            Assert.Equal(PortalAnchor.FloorAligned, a.AnchorUsed);
            Rig.Near(new Vec3(5f, 0f, 0f), a.Position);
            Rig.Near(new Vec3(5f, 0f, 1.5f), a.PaintCenter);
        }

        [Fact]
        public void FloorAlignedOnLeaningWallSlidesDownTheWall()
        {
            PortalEnd b = Rig.End(Rig.LeaningNearWall(), PaintColor.Blue, new Vec2(1f, 1.2f), PortalAnchor.FloorAligned, 0f);
            Assert.Equal(PortalAnchor.FloorAligned, b.AnchorUsed);
            // 1.2 m up a wall leaning 30 degrees: back to the wall's bottom edge, which is on the floor.
            Rig.Near(new Vec3(0f, -1f, 0f), b.Position);
            Assert.Equal(0f, b.Wall.SignedDistance(b.Position), Rig.Precision);
        }

        [Fact]
        public void FloorAlignedFallsBackToPaintCentreOnAFloor()
        {
            var floor = new WallFrame(new Vec3(0f, 0f, 0f), new Vec3(0f, -1f, 0f), new Vec3(1f, 0f, 0f), 4f, 4f);
            Rig.Near(new Vec3(0f, 0f, 1f), floor.Normal);
            PortalEnd e = Rig.End(floor, PaintColor.Blue, new Vec2(1f, 2f), PortalAnchor.FloorAligned, -3f);
            Assert.Equal(PortalAnchor.PaintCenter, e.AnchorUsed);
            Rig.Near(new Vec3(2f, -1f, 0f), e.Position);
        }

        [Fact]
        public void FacingWallsCarryPointsAndVelocityStraightThrough()
        {
            PortalEnd a = Rig.End(Rig.FarWall(), PaintColor.Orange, new Vec2(2f, 1.5f), PortalAnchor.PaintCenter);
            PortalEnd b = Rig.End(Rig.NearWall(), PaintColor.Blue, new Vec2(1f, 1.2f), PortalAnchor.PaintCenter);
            var pair = new PortalPair(a, b);

            // Far wall at x = 5 and near wall at x = 0 face each other: walking through keeps you heading +X.
            Rig.SameRotation(Quat.Identity, pair.AToB.Rotation);
            Rig.Near(new Vec3(0f, -1f, 1.2f), pair.AToB.TransformPoint(new Vec3(5f, 0f, 1.5f)));
            Rig.Near(new Vec3(0.1f, -0.8f, 1.3f), pair.AToB.TransformPoint(new Vec3(5.1f, 0.2f, 1.6f)));
            Rig.Near(BallVelocity, pair.AToB.TransformDirection(BallVelocity));
            Rig.Near(new Vec3(1f, 2f, 3f), pair.AToB.TransformDirection(new Vec3(1f, 2f, 3f)));
            Assert.True(pair.KeepsUpright);
        }

        [Fact]
        public void IntoAMeansOutOfB()
        {
            PortalEnd a = Rig.End(Rig.FarWall(), PaintColor.Orange, new Vec2(2f, 1.5f), PortalAnchor.PaintCenter);
            PortalEnd b = Rig.End(Rig.LeftWall(), PaintColor.Blue, new Vec2(2f, 1.5f), PortalAnchor.PaintCenter);
            var pair = new PortalPair(a, b);
            Rig.Near(b.Normal, pair.AToB.TransformDirection(-a.Normal));
            Rig.Near(a.Normal, pair.BToA.TransformDirection(-b.Normal));
            // A proper turn, never a mirror: the determinant of the rotation is +1.
            var m = System.Numerics.Matrix4x4.CreateFromQuaternion(pair.AToB.Rotation);
            Assert.Equal(1f, m.GetDeterminant(), Rig.Precision);
        }

        [Fact]
        public void PerpendicularWallsTurnAQuarter()
        {
            PortalEnd a = Rig.End(Rig.FarWall(), PaintColor.Orange, new Vec2(2f, 1.5f), PortalAnchor.PaintCenter);
            PortalEnd b = Rig.End(Rig.LeftWall(), PaintColor.Blue, new Vec2(2f, 1.5f), PortalAnchor.PaintCenter);
            var pair = new PortalPair(a, b);

            // Going +X into the far wall, coming out of the left wall heading -Y: a quarter turn clockwise.
            Rig.SameRotation(Quat.CreateFromAxisAngle(Vec3.UnitZ, -MathF.PI / 2f), pair.AToB.Rotation);
            Rig.Near(new Vec3(2f, 3f, 1.5f), pair.AToB.TransformPoint(new Vec3(5f, 0f, 1.5f)));
            Rig.Near(new Vec3(2.2f, 2.9f, 1.6f), pair.AToB.TransformPoint(new Vec3(5.1f, 0.2f, 1.6f)));

            // Ball velocity before (3, 0.5, -1) and after.
            Rig.Near(new Vec3(0.5f, -3f, -1f), pair.AToB.TransformDirection(BallVelocity));
            // Spin about world up stays the same; spin about +X becomes spin about -Y.
            Rig.Near(new Vec3(0f, 0f, 2f), pair.AToB.TransformDirection(new Vec3(0f, 0f, 2f)));
            Rig.Near(new Vec3(0f, -4f, 0f), pair.AToB.TransformDirection(new Vec3(4f, 0f, 0f)));
            // Orientation of the ball turns by the same quarter.
            Rig.SameRotation(Quat.CreateFromAxisAngle(Vec3.UnitZ, -MathF.PI / 2f),
                pair.AToB.TransformRotation(Quat.Identity));
            Assert.True(pair.KeepsUpright);
        }

        [Fact]
        public void SameWallTurnsHalfWayRoundWithoutMirroring()
        {
            PortalEnd a = Rig.End(Rig.FarWall(), PaintColor.Orange, new Vec2(1f, 1.5f), PortalAnchor.PaintCenter);
            PortalEnd b = Rig.End(Rig.FarWall(), PaintColor.Blue, new Vec2(3f, 1.5f), PortalAnchor.PaintCenter);
            var pair = new PortalPair(a, b);

            Rig.SameRotation(Quat.CreateFromAxisAngle(Vec3.UnitZ, MathF.PI), pair.AToB.Rotation);
            Rig.Near(new Vec3(5f, -1f, 1.5f), pair.AToB.TransformPoint(new Vec3(5f, 1f, 1.5f)));
            // 0.1 m deep and 0.2 m to the right of A's centre (right = -Y while facing the wall) comes out
            // 0.1 m into the room and 0.2 m to the traveller's right, which now is +Y.
            Rig.Near(new Vec3(4.9f, -0.8f, 1.5f), pair.AToB.TransformPoint(new Vec3(5.1f, 0.8f, 1.5f)));
            Rig.Near(new Vec3(-3f, -0.5f, -1f), pair.AToB.TransformDirection(BallVelocity));
            Rig.Near(new Vec3(-1f, -2f, 3f), pair.AToB.TransformDirection(new Vec3(1f, 2f, 3f)));
            Assert.True(pair.KeepsUpright);
        }

        [Fact]
        public void WallsOfDifferentHeightsLineUpFloorsWhenFloorAligned()
        {
            // Floor aligned is the optional, non-default policy; paint centre (the default) is compared below.
            // Room A floor at z = 0, patch centre 1.5 m up. Room B is raised: floor and wall base at z = 2,
            // patch centre only 0.8 m up its wall.
            WallFrame wallA = Rig.FarWall();
            WallFrame wallB = Rig.NearWall(2f);
            Vec2 centreA = new Vec2(2f, 1.5f);
            Vec2 centreB = new Vec2(1f, 0.8f);

            var floorPair = new PortalPair(
                Rig.End(wallA, PaintColor.Orange, centreA, PortalAnchor.FloorAligned, 0f),
                Rig.End(wallB, PaintColor.Blue, centreB, PortalAnchor.FloorAligned, 2f));
            Rig.Near(new Vec3(5f, 0f, 0f), floorPair.A.Position);
            Rig.Near(new Vec3(0f, -1f, 2f), floorPair.B.Position);
            // Feet on floor A come out on floor B; a head 1.7 m up stays 1.7 m above the floor.
            Rig.Near(new Vec3(0.1f, -1f, 2f), floorPair.PlayerAToB.TransformPoint(new Vec3(5.1f, 0f, 0f)));
            Rig.Near(new Vec3(0.1f, -1f, 3.7f), floorPair.PlayerAToB.TransformPoint(new Vec3(5.1f, 0f, 1.7f)));

            // Paint centre instead lines up the patch centres: the same head lands 0.2 m above B's centre.
            var paintPair = new PortalPair(
                Rig.End(wallA, PaintColor.Orange, centreA, PortalAnchor.PaintCenter),
                Rig.End(wallB, PaintColor.Blue, centreB, PortalAnchor.PaintCenter));
            Rig.Near(new Vec3(0.1f, -1f, 3.0f), paintPair.AToB.TransformPoint(new Vec3(5.1f, 0f, 1.7f)));
            Rig.Near(new Vec3(0f, -1f, 2.8f), paintPair.AToB.TransformPoint(new Vec3(5f, 0f, 1.5f)));
        }

        [Fact]
        public void RoundTripIsIdentity()
        {
            var rng = new Rng(77u);
            PortalPair[] pairs =
            {
                new PortalPair(
                    Rig.End(Rig.FarWall(), PaintColor.Orange, new Vec2(2f, 1.5f), PortalAnchor.FloorAligned),
                    Rig.End(Rig.LeftWall(), PaintColor.Blue, new Vec2(2f, 1.5f), PortalAnchor.FloorAligned)),
                new PortalPair(
                    Rig.End(Rig.FarWall(), PaintColor.Orange, new Vec2(1f, 1.5f), PortalAnchor.PaintCenter),
                    Rig.End(Rig.LeaningNearWall(), PaintColor.Blue, new Vec2(3f, 1f), PortalAnchor.PaintCenter)),
                new PortalPair(
                    Rig.End(RandomWall(ref rng), PaintColor.Orange, new Vec2(1f, 1f), PortalAnchor.PaintCenter),
                    Rig.End(RandomWall(ref rng), PaintColor.Blue, new Vec2(2f, 0.5f), PortalAnchor.FloorAligned, -1f)),
            };

            foreach (PortalPair pair in pairs)
            {
                RigidTransform full = pair.AToB.Then(pair.BToA);
                RigidTransform player = pair.PlayerAToB.Then(pair.PlayerBToA);
                Rig.SameRotation(Quat.Identity, full.Rotation, 5);
                Rig.SameRotation(Quat.Identity, player.Rotation, 5);
                for (int i = 0; i < 50; i++)
                {
                    Vec3 p = new Vec3(rng.Range(-10f, 10f), rng.Range(-10f, 10f), rng.Range(-10f, 10f));
                    Vec3 d = rng.OnUnitSphere() * 5f;
                    Quat r = Quat.CreateFromAxisAngle(rng.OnUnitSphere(), rng.Range(-3f, 3f));
                    Rig.Near(p, pair.BToA.TransformPoint(pair.AToB.TransformPoint(p)), 3);
                    Rig.Near(p, pair.PlayerBToA.TransformPoint(pair.PlayerAToB.TransformPoint(p)), 3);
                    Rig.Near(d, pair.BToA.TransformDirection(pair.AToB.TransformDirection(d)), 4);
                    Rig.SameRotation(r, pair.BToA.TransformRotation(pair.AToB.TransformRotation(r)), 5);
                }
            }
        }

        [Fact]
        public void LeaningWallBreaksUpButPlayerVersionNeverPitchesOrRolls()
        {
            PortalEnd a = Rig.End(Rig.FarWall(), PaintColor.Orange, new Vec2(2f, 1.5f), PortalAnchor.FloorAligned);
            PortalEnd b = Rig.End(Rig.LeaningNearWall(), PaintColor.Blue, new Vec2(1f, 1.2f), PortalAnchor.FloorAligned);
            var pair = new PortalPair(a, b);

            // Full transform: into A comes out along the leaning normal, and world up comes out tilted 30 degrees.
            Assert.False(pair.KeepsUpright);
            float s = MathF.Sin(MathF.PI / 6f), c = MathF.Cos(MathF.PI / 6f);
            Rig.Near(new Vec3(c, 0f, s), pair.AToB.TransformDirection(Vec3.UnitX));
            Rig.Near(new Vec3(-s, 0f, c), pair.AToB.TransformDirection(Vec3.UnitZ));

            // Player version: a pure turn about world up (here no turn at all) plus the move.
            Quat q = pair.PlayerAToB.Rotation;
            Assert.Equal(0f, q.X, 6);
            Assert.Equal(0f, q.Y, 6);
            Rig.Near(Vec3.UnitZ, pair.PlayerAToB.TransformDirection(Vec3.UnitZ), 5);
            Rig.Near(new Vec3(0.1f, -1f, 1.7f), pair.PlayerAToB.TransformPoint(new Vec3(5.1f, 0f, 1.7f)));

            // A head pitched down 10 degrees and rolled 5 degrees keeps exactly that pitch and roll.
            Quat head = Quat.CreateFromAxisAngle(Vec3.UnitZ, 0.4f)
                        * Quat.CreateFromAxisAngle(Vec3.UnitY, 10f * MathF.PI / 180f)
                        * Quat.CreateFromAxisAngle(Vec3.UnitX, 5f * MathF.PI / 180f);
            Quat after = pair.PlayerAToB.TransformRotation(head);
            Assert.Equal(Vec3.Transform(Vec3.UnitX, head).Z, Vec3.Transform(Vec3.UnitX, after).Z, 5);
            Assert.Equal(Vec3.Transform(Vec3.UnitY, head).Z, Vec3.Transform(Vec3.UnitY, after).Z, 5);
            Assert.Equal(Vec3.Transform(Vec3.UnitZ, head).Z, Vec3.Transform(Vec3.UnitZ, after).Z, 5);
        }

        [Fact]
        public void PlayerVersionOnTwoLeaningWallsStillOnlyTurnsAboutUp()
        {
            var rng = new Rng(4242u);
            for (int i = 0; i < 20; i++)
            {
                var pair = new PortalPair(
                    Rig.End(RandomWall(ref rng), PaintColor.Orange, new Vec2(1f, 1f), PortalAnchor.FloorAligned),
                    Rig.End(RandomWall(ref rng), PaintColor.Blue, new Vec2(1f, 1f), PortalAnchor.FloorAligned));
                Quat q = pair.PlayerAToB.Rotation;
                Assert.Equal(0f, q.X, 5);
                Assert.Equal(0f, q.Y, 5);
                Rig.Near(Vec3.UnitZ, pair.PlayerAToB.TransformDirection(Vec3.UnitZ), 5);
            }
        }

        /// <summary>A wall at a random place, facing a random way, leaning up to 40 degrees.</summary>
        private static WallFrame RandomWall(ref Rng rng)
        {
            float yaw = rng.Range(-MathF.PI, MathF.PI);
            float lean = rng.Range(-0.7f, 0.7f);
            Vec3 u = new Vec3(MathF.Cos(yaw), MathF.Sin(yaw), 0f);
            Vec3 outward = Vec3.Cross(u, Vec3.UnitZ);
            Vec3 v = Vec3.UnitZ * MathF.Cos(lean) + outward * MathF.Sin(lean);
            Vec3 origin = new Vec3(rng.Range(-5f, 5f), rng.Range(-5f, 5f), rng.Range(-1f, 1f));
            return new WallFrame(origin, u, v, 4f, 3f);
        }
    }
}
