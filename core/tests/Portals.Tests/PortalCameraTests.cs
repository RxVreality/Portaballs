using System;
using System.Numerics;
using Xunit;

namespace Portaballs.Core.Tests
{
    /// <summary>Virtual camera, clip plane, oblique projection and the head-clip guard.</summary>
    public class PortalCameraTests
    {
        private static PortalPair PerpendicularPair()
        {
            return new PortalPair(
                Rig.End(Rig.FarWall(), PaintColor.Orange, new Vec2(2f, 1.5f), PortalAnchor.PaintCenter),
                Rig.End(Rig.LeftWall(), PaintColor.Blue, new Vec2(2f, 1.5f), PortalAnchor.PaintCenter));
        }

        [Fact]
        public void ViewerInFrontOfFacingWallsSeesAlongBsNormalFromBehindB()
        {
            var pair = new PortalPair(
                Rig.End(Rig.FarWall(), PaintColor.Orange, new Vec2(2f, 1.5f), PortalAnchor.PaintCenter),
                Rig.End(Rig.NearWall(), PaintColor.Blue, new Vec2(1f, 1.2f), PortalAnchor.PaintCenter));
            // Standing 2 m in front of A, looking straight at it (+X).
            PortalCamera.VirtualEye(pair.PlayerAToB, new Vec3(3f, 0f, 1.5f), Quat.Identity, out Vec3 pos, out Quat rot);
            Rig.Near(new Vec3(-2f, -1f, 1.2f), pos);
            Rig.Near(pair.B.Normal, Vec3.Transform(Vec3.UnitX, rot));
            Assert.Equal(-2f, pair.B.Wall.SignedDistance(pos), 4);
        }

        [Fact]
        public void ViewerInFrontOfPerpendicularWallsSeesAlongBsNormalFromBehindB()
        {
            PortalPair pair = PerpendicularPair();
            PortalCamera.VirtualEye(pair.PlayerAToB, new Vec3(3f, 0f, 1.5f), Quat.Identity, out Vec3 pos, out Quat rot);
            Rig.Near(new Vec3(2f, 5f, 1.5f), pos);
            Rig.Near(new Vec3(0f, -1f, 0f), Vec3.Transform(Vec3.UnitX, rot));
            Rig.Near(new Vec3(0f, 0f, 1f), Vec3.Transform(Vec3.UnitZ, rot));
            Assert.Equal(-2f, pair.B.Wall.SignedDistance(pos), 4);
        }

        [Fact]
        public void ClipPlaneSitsOnBsWallInWorldAndCameraSpace()
        {
            PortalPair pair = PerpendicularPair();
            Plane world = PortalCamera.ClipPlaneWorld(pair.B);
            Rig.Near(new Vec3(0f, -1f, 0f), world.Normal);
            Assert.Equal(3f, world.D, 5);

            PortalCamera.VirtualEye(pair.PlayerAToB, new Vec3(3f, 0f, 1.5f), Quat.Identity, out Vec3 pos, out Quat rot);
            Vector4 view = PortalCamera.ClipPlaneView(pair.B, pos, rot);
            // The camera looks straight at the wall 2 m ahead: in view space (looking down -Z) that is z = -2.
            Assert.Equal(0f, view.X, 5);
            Assert.Equal(0f, view.Y, 5);
            Assert.Equal(-1f, view.Z, 5);
            Assert.Equal(-2f, view.W, 4);

            // Plane values agree in both spaces for any point.
            Matrix4x4 v = CameraConventions.ViewMatrix(pos, rot);
            var rng = new Rng(9u);
            for (int i = 0; i < 20; i++)
            {
                Vec3 p = new Vec3(rng.Range(-5f, 5f), rng.Range(-5f, 5f), rng.Range(-5f, 5f));
                Vec3 pv = Vec3.Transform(p, v);
                Rig.Near(CameraConventions.WorldToView(p, pos, rot), pv);
                Assert.Equal(Plane.DotCoordinate(world, p), Vector4.Dot(view, new Vector4(pv, 1f)), 4);
            }

            // The offset setting slides the plane into B's room.
            Plane moved = PortalCamera.ClipPlaneWorld(pair.B, 0.01f);
            Assert.Equal(0f, Plane.DotCoordinate(moved, new Vec3(2f, 2.99f, 1f)), 5);
        }

        private static float NdcDepth(Matrix4x4 proj, Vec3 viewPoint)
        {
            Vector4 c = Vector4.Transform(new Vector4(viewPoint, 1f), proj);
            return c.Z / c.W;
        }

        [Fact]
        public void ObliqueProjectionPutsNearPlaneOnAParallelPortal()
        {
            Matrix4x4 proj = CameraConventions.Perspective(MathF.PI / 2f, 1f, 0.1f, 100f);
            var plane = new Vector4(0f, 0f, -1f, -2f); // z = -2, keep everything further away
            Assert.True(CameraConventions.ObliqueNearPlane(proj, plane, out Matrix4x4 obl));

            Assert.Equal(0f, NdcDepth(obl, new Vec3(0.5f, -0.3f, -2f)), 5);
            Assert.Equal(1f, NdcDepth(obl, new Vec3(0f, 0f, -100f)), 4);
            Assert.True(NdcDepth(obl, new Vec3(0f, 0f, -1f)) < 0f); // between camera and portal: cut
            Assert.True(NdcDepth(obl, new Vec3(0f, 0f, -3f)) > 0f);
            // a = far / (far - 2): new depth column is a * plane.
            float a = 100f / 98f;
            Assert.Equal(-a, obl.M33, 5);
            Assert.Equal(-2f * a, obl.M43, 5);
        }

        [Fact]
        public void ObliqueProjectionWithTiltedPortalKeepsTheSidesAndAFarPlaneThatContainsTheView()
        {
            float near = 0.1f, far = 50f;
            Matrix4x4 proj = CameraConventions.Perspective(MathF.PI / 2f, 1.5f, near, far);
            Vec3 n = Vec3.Normalize(new Vec3(0.4f, 0.2f, -1f));
            Vec3 onPlane = new Vec3(0.3f, -0.1f, -3f);
            var plane = new Vector4(n, -Vec3.Dot(n, onPlane));
            Assert.True(plane.W < 0f);
            Assert.True(CameraConventions.ObliqueNearPlane(proj, plane, out Matrix4x4 obl));

            // Left/right/top/bottom and w untouched: only the depth column changed.
            Assert.Equal(proj.M11, obl.M11);
            Assert.Equal(proj.M22, obl.M22);
            Assert.Equal(proj.M34, obl.M34);
            Assert.Equal(proj.M44, obl.M44);

            float tanY = MathF.Tan(MathF.PI / 4f);
            float tanX = tanY * 1.5f;
            int onPlaneChecked = 0, keptChecked = 0;
            for (int ix = -4; ix <= 4; ix++)
            for (int iy = -4; iy <= 4; iy++)
            {
                // A ray from the eye through the view rectangle.
                Vec3 dir = new Vec3(tanX * ix / 4f, tanY * iy / 4f, -1f);
                // Where the ray meets the portal plane: depth 0 exactly.
                float s = -plane.W / Vec3.Dot(n, dir);
                if (s > near && s < far)
                {
                    Assert.Equal(0f, NdcDepth(obl, dir * s), 3);
                    // Just before the plane is cut, just after is kept.
                    Assert.True(NdcDepth(obl, dir * (s * 0.95f)) < 0f);
                    Assert.True(NdcDepth(obl, dir * (s * 1.05f)) > 0f);
                    onPlaneChecked++;
                }
                // Every point of the original view on the kept side stays within depth 0..1 (sane far plane).
                for (int k = 1; k <= 20; k++)
                {
                    float depth = near + (far - near) * k / 20f;
                    Vec3 p = dir * depth;
                    if (Vector4.Dot(plane, new Vector4(p, 1f)) <= 0f) continue;
                    float z = NdcDepth(obl, p);
                    Assert.InRange(z, 0f, 1.0001f);
                    keptChecked++;
                }
            }
            Assert.True(onPlaneChecked > 40);
            Assert.True(keptChecked > 1000);

            // The far corner on the side the plane leans to lands exactly on depth 1.
            Vec3 corner = new Vec3(tanX, tanY, -1f) * far;
            Assert.Equal(1f, NdcDepth(obl, corner), 3);
        }

        [Fact]
        public void ObliqueProjectionRefusesWhenTheCameraIsInFrontOfThePortal()
        {
            Matrix4x4 proj = CameraConventions.Perspective(MathF.PI / 2f, 1f, 0.1f, 100f);
            // Plane z = -2 but keeping the side nearer the camera: the camera is on the kept side.
            Assert.False(CameraConventions.ObliqueNearPlane(proj, new Vector4(0f, 0f, 1f, 2f), out Matrix4x4 same));
            Assert.Equal(proj, same);
        }

        [Fact]
        public void VirtualViewCutsWhatIsBehindBAndDrawsBsRoom()
        {
            PortalPair pair = PerpendicularPair();
            Matrix4x4 proj = CameraConventions.Perspective(MathF.PI / 2f, 1f, 0.05f, 100f);
            Assert.True(PortalCamera.VirtualView(pair, true, new Vec3(3f, 0f, 1.5f), Quat.Identity, proj, 0f,
                out Vec3 pos, out Quat rot, out Matrix4x4 obl));
            Matrix4x4 view = CameraConventions.ViewMatrix(pos, rot);

            // A crate 1 m out into B's room in front of the portal: drawn.
            float inRoom = NdcDepth(obl, Vec3.Transform(new Vec3(2f, 2f, 1.5f), view));
            Assert.InRange(inRoom, 0f, 1f);
            // Something in the space behind B's wall, between the virtual camera and the wall: cut.
            float behind = NdcDepth(obl, Vec3.Transform(new Vec3(2f, 4f, 1.5f), view));
            Assert.True(behind < 0f);
        }

        [Fact]
        public void HeadClipGuardDistances()
        {
            float fov = MathF.PI / 2f;
            // Near 0.1 m, 90 degrees, square: the near rectangle is 0.2 x 0.2 m, corners sqrt(0.03) away.
            Assert.Equal(0.1732051f, HeadClipGuard.NearCornerDistanceFov(0.1f, fov, 1f), 5);
            Assert.Equal(0.1832051f, HeadClipGuard.RequiredThickness(0.1f, fov, 1f, 0.01f), 5);
            // Uneven VR-style frustum: tangents 1.2 sideways, 1.0 up/down, near 5 cm.
            Assert.Equal(MathF.Sqrt(0.0086f), HeadClipGuard.NearCornerDistance(0.05f, 1.2f, 1f), 5);

            PortalEnd a = Rig.End(Rig.FarWall(), PaintColor.Orange, new Vec2(2f, 1.5f), PortalAnchor.FloorAligned);
            Assert.Equal(0f, HeadClipGuard.RequiredPushBack(a, new Vec3(4.5f, 0f, 1.5f), 0.1f, fov, 1f, 0.01f), 5);
            Assert.Equal(0.1332051f, HeadClipGuard.RequiredPushBack(a, new Vec3(4.95f, 0f, 1.5f), 0.1f, fov, 1f, 0.01f), 4);
            Assert.Equal(0.1832051f, HeadClipGuard.RequiredPushBack(a, new Vec3(5.02f, 0f, 1.5f), 0.1f, fov, 1f, 0.01f), 5);
        }
    }
}
