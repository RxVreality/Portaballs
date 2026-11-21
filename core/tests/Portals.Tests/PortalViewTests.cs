using System;
using System.Numerics;
using Xunit;

namespace Portaballs.Core.Tests
{
    /// <summary>The see-through view's maths: per-eye projection, the clipped virtual view, screen cover, lens angles.</summary>
    public class PortalViewTests
    {
        private const float Inches = 39.3701f;

        private static PortalPair FacingPair()
        {
            return new PortalPair(
                Rig.End(Rig.FarWall(), PaintColor.Orange, new Vec2(2f, 1.5f), PortalAnchor.PaintCenter),
                Rig.End(Rig.NearWall(), PaintColor.Blue, new Vec2(2f, 1.5f), PortalAnchor.PaintCenter));
        }

        private static Vec4 Project(Mat4 rowLayout, Vec3 view)
        {
            return Vec4.Transform(new Vec4(view, 1f), rowLayout);
        }

        private static Quat RandomRotation(ref Rng rng)
        {
            return Quat.Normalize(Quat.CreateFromAxisAngle(rng.OnUnitSphere(), rng.Range(-3.1f, 3.1f)));
        }

        [Fact]
        public void SymmetricLensMatchesTheEnginesHorizontalFieldOfView()
        {
            LensTangents lens = LensTangents.Symmetric(80f, 16f / 9f);
            Assert.Equal(MathF.Tan(40f * MathF.PI / 180f), lens.Right, 5);
            Assert.Equal(-lens.Right, lens.Left, 6);
            Assert.Equal(lens.Right * 9f / 16f, lens.Up, 5);
            Assert.Equal(16f / 9f, lens.Aspect, 4);
            lens.ToDegrees(out float l, out float r, out float d, out float u);
            Assert.Equal(-40f, l, 3);
            Assert.Equal(40f, r, 3);
            Assert.Equal(-u, d, 4);
        }

        [Fact]
        public void ProjectionPutsTheLensEdgesOnTheScreenEdgesAndDepthFromZeroToOne()
        {
            // A lopsided lens, like a VR eye.
            var lens = new LensTangents(-1.25f, 0.9f, -1.1f, 1.05f);
            Mat4 p = PortalViewMath.Projection(lens, 0.5f, 10000f);
            float depth = 37f;
            Vec4 left = Project(p, new Vec3(lens.Left * depth, 0f, -depth));
            Vec4 right = Project(p, new Vec3(lens.Right * depth, 0f, -depth));
            Vec4 up = Project(p, new Vec3(0f, lens.Up * depth, -depth));
            Vec4 down = Project(p, new Vec3(0f, lens.Down * depth, -depth));
            Assert.Equal(-1f, left.X / left.W, 4);
            Assert.Equal(1f, right.X / right.W, 4);
            Assert.Equal(1f, up.Y / up.W, 4);
            Assert.Equal(-1f, down.Y / down.W, 4);
            Vec4 near = Project(p, new Vec3(0f, 0f, -0.5f));
            Vec4 far = Project(p, new Vec3(0f, 0f, -10000f));
            Assert.Equal(0f, near.Z / near.W, 4);
            Assert.Equal(1f, far.Z / far.W, 3);
        }

        [Fact]
        public void TransposedProjectionIsTheEnginesCreateProjectionLayout()
        {
            // The engine's Matrix.CreateProjection, written out (Matrix.cs:257-273), for a symmetric camera.
            float zn = 0.5f, zf = 10000f, fov = 80f, aspect = 16f / 9f;
            float tanX = MathF.Tan(MathF.PI / 180f * (fov * 0.5f));
            float tanY = tanX / aspect;
            float l = -zn * tanX, r = zn * tanX, b = -zn * tanY, t = zn * tanY;
            Mat4 engine = default;
            engine.M11 = 2f * zn / (r - l);
            engine.M22 = 2f * zn / (t - b);
            engine.M13 = (l + r) / (r - l);
            engine.M23 = (t + b) / (t - b);
            engine.M33 = zf / (zn - zf);
            engine.M34 = zn * zf / (zn - zf);
            engine.M43 = -1f;

            Mat4 ours = Mat4.Transpose(PortalViewMath.Projection(LensTangents.Symmetric(fov, aspect), zn, zf));
            float[] e = { engine.M11, engine.M12, engine.M13, engine.M14, engine.M21, engine.M22, engine.M23, engine.M24, engine.M31, engine.M32, engine.M33, engine.M34, engine.M41, engine.M42, engine.M43, engine.M44 };
            float[] o = { ours.M11, ours.M12, ours.M13, ours.M14, ours.M21, ours.M22, ours.M23, ours.M24, ours.M31, ours.M32, ours.M33, ours.M34, ours.M41, ours.M42, ours.M43, ours.M44 };
            for (int i = 0; i < 16; i++) Assert.Equal(e[i], o[i], 5);
        }

        [Fact]
        public void BuildPlacesTheVirtualEyeBehindTheExitAndCutsExactlyOnTheWall()
        {
            PortalPair pair = FacingPair();
            LensTangents lens = LensTangents.Symmetric(80f, 16f / 9f);
            var eye = new Vec3(3.4f, 0.3f, 1.6f);   // 1.6 m in front of orange (the wall at x = 5)
            Quat look = Quat.CreateFromAxisAngle(Vec3.UnitZ, 0.2f);
            Assert.True(PortalViewMath.Build(pair, true, eye, look, lens, 0.5f, 10000f, Inches, 0f, out PortalEyeView view));
            Assert.Equal(1.6f, view.BehindExit, 4);
            Assert.Equal(-1.6f, pair.B.Wall.SignedDistance(view.Position), 4);

            // A point on the exit wall has depth 0; points in the exit room have depth between 0 and 1;
            // points behind the exit wall (between the virtual eye and the wall) come out negative.
            var rng = new Rng(31u);
            for (int i = 0; i < 200; i++)
            {
                Vec2 uv = new Vec2(rng.Range(0f, 4f), rng.Range(0f, 3f));
                float outOfWall = rng.Range(-1.5f, 6f);
                Vec3 world = pair.B.Wall.ToWorld(uv, outOfWall);
                Vec3 v = CameraConventions.WorldToView(world, view.Position, view.Rotation) * Inches;
                if (!(v.Z < -0.01f)) continue;   // behind the virtual camera
                Vec4 clip = Project(view.Projection, v);
                float depth = clip.Z / clip.W;
                if (outOfWall > 0.001f) Assert.True(depth > 0f, $"in the exit room but cut: {outOfWall} m out, depth {depth}");
                if (outOfWall < -0.001f) Assert.True(depth < 0f, $"behind the exit wall but kept: {outOfWall} m, depth {depth}");
            }
            Vec3 onWall = CameraConventions.WorldToView(pair.B.Wall.ToWorld(new Vec2(1.2f, 0.7f)), view.Position, view.Rotation) * Inches;
            Vec4 c0 = Project(view.Projection, onWall);
            Assert.Equal(0f, c0.Z / c0.W, 4);
        }

        [Fact]
        public void BuildKeepsScreenPositionsOfTheEyesOwnProjection()
        {
            // Only depth changes: where a point lands on screen is the eye's own projection's answer.
            PortalPair pair = FacingPair();
            var lens = new LensTangents(-1.2f, 0.8f, -1.0f, 1.1f);
            Assert.True(PortalViewMath.Build(pair, true, new Vec3(4.2f, -0.4f, 1.3f), Quat.Identity, lens, 0.5f, 10000f, Inches, 0.004f, out PortalEyeView view));
            Mat4 plain = PortalViewMath.Projection(lens, 0.5f, 10000f);
            var rng = new Rng(5u);
            for (int i = 0; i < 100; i++)
            {
                var v = new Vec3(rng.Range(-80f, 80f), rng.Range(-80f, 80f), -rng.Range(1f, 300f));
                Vec4 a = Project(plain, v), b = Project(view.Projection, v);
                Assert.Equal(a.X, b.X, 3);
                Assert.Equal(a.Y, b.Y, 3);
                Assert.Equal(a.W, b.W, 3);
            }
        }

        [Fact]
        public void BuildWorksWithTheEyeAlmostTouchingTheWallAndRefusesBehindIt()
        {
            PortalPair pair = FacingPair();
            LensTangents lens = LensTangents.Symmetric(80f, 16f / 9f);
            float[] distances = { 0.3f, 0.02f, 0.004f, 0.0005f };
            foreach (float d in distances)
            {
                var eye = pair.A.Wall.ToWorld(new Vec2(2f, 1.5f), d);
                Assert.True(PortalViewMath.Build(pair, true, eye, Quat.Identity, lens, 0.5f, 10000f, Inches, 0f, out PortalEyeView view), $"refused at {d} m");
                Assert.Equal(d, view.BehindExit, 5);
                // A point 2 m into the exit room, dead ahead, is kept and inside the depth range.
                Vec3 ahead = CameraConventions.WorldToView(pair.B.Wall.ToWorld(new Vec2(2f, 1.5f), 2f), view.Position, view.Rotation) * Inches;
                Vec4 clip = Project(view.Projection, ahead);
                float depth = clip.Z / clip.W;
                Assert.True(depth > 0f && depth < 1f, $"at {d} m the point ahead has depth {depth}");
            }
            // An eye already behind the entry wall is carried in front of the exit wall: no cut can be made.
            var behind = pair.A.Wall.ToWorld(new Vec2(2f, 1.5f), -0.01f);
            Assert.False(PortalViewMath.Build(pair, true, behind, Quat.Identity, lens, 0.5f, 10000f, Inches, 0f, out _));
        }

        [Fact]
        public void IdentityCrossingWithAPlaneIsTheWindowTest()
        {
            // The window test: no crossing at all, cut on a free-standing plane facing away from the eye.
            LensTangents lens = LensTangents.Symmetric(80f, 16f / 9f);
            var keep = new Plane3(new Vec3(1f, 0f, 0f), -2f);   // keeps x > 2
            var eye = new Vec3(0.5f, 0.1f, 1.5f);
            Assert.True(PortalViewMath.Build(RigidTransform.Identity, keep, eye, Quat.Identity, lens, 0.5f, 10000f, Inches, out PortalEyeView view));
            Rig.Near(eye, view.Position);
            Assert.Equal(1.5f, view.BehindExit, 4);
            Vec4 before = Project(view.Projection, CameraConventions.WorldToView(new Vec3(1.5f, 0f, 1.5f), eye, Quat.Identity) * Inches);
            Vec4 after = Project(view.Projection, CameraConventions.WorldToView(new Vec3(3.5f, 0f, 1.5f), eye, Quat.Identity) * Inches);
            Assert.True(before.Z / before.W < 0f);
            Assert.True(after.Z / after.W > 0f && after.Z / after.W < 1f);
        }

        [Fact]
        public void ColumnLayoutClosedFormEqualsTheCoreObliqueTransposed()
        {
            // This is the formula the engine patch carries. It must be the core's, number for number.
            var rng = new Rng(2026u);
            int made = 0;
            for (int i = 0; i < 3000; i++)
            {
                var lens = new LensTangents(-rng.Range(0.5f, 1.4f), rng.Range(0.5f, 1.4f), -rng.Range(0.5f, 1.4f), rng.Range(0.5f, 1.4f));
                float near = rng.Range(0.3f, 10f), far = rng.Range(2000f, 20000f);
                Mat4 core = PortalViewMath.Projection(lens, near, far);
                Vec3 n = rng.OnUnitSphere();
                var plane = new Vec4(n, -rng.Range(0.01f, 400f));
                bool okCore = CameraConventions.ObliqueNearPlane(core, plane, out Mat4 expected);
                bool okColumn = PortalViewMath.ObliqueNearPlaneColumnLayout(Mat4.Transpose(core), plane, out Mat4 column);
                Assert.Equal(okCore, okColumn);
                if (!okCore) continue;
                made++;
                Mat4 back = Mat4.Transpose(column);
                // Both write "a * plane" into the depth column, so they can only differ in the one number a.
                // The core finds a through a general 4x4 inverse in single precision, which is good to a few
                // parts in ten thousand with near 0.3 and far 20000; the closed form has no inverse in it.
                // The plane itself (the direction of the depth column) must agree far more tightly than that.
                var e = new Vec4(expected.M13, expected.M23, expected.M33, expected.M43);
                var c = new Vec4(back.M13, back.M23, back.M33, back.M43);
                float ratio = c.Length() / e.Length();
                Assert.True(MathF.Abs(ratio - 1f) < 3e-3f, $"scale of the depth column differs: {ratio}");
                Vec4 direction = Vec4.Normalize(c) - Vec4.Normalize(e);
                Assert.True(direction.Length() < 1e-5f, $"the cut plane differs: {direction.Length()}");
                Assert.Equal(expected.M11, back.M11, 5);
                Assert.Equal(expected.M22, back.M22, 5);
                Assert.Equal(expected.M34, back.M34, 5);
            }
            Assert.True(made > 1500, $"only {made} cases could be made");
        }

        [Fact]
        public void LensTangentsComeBackOutOfTheSidePlanesWhicheverWayTheNormalsPoint()
        {
            var rng = new Rng(88u);
            for (int i = 0; i < 200; i++)
            {
                var lens = new LensTangents(-rng.Range(0.4f, 1.5f), rng.Range(0.4f, 1.5f), -rng.Range(0.4f, 1.5f), rng.Range(0.4f, 1.5f));
                // Camera-local axes: X forward, Y left, Z up. The right edge is the plane through the eye
                // holding the directions (1, -tanRight, 0) and (0, 0, 1).
                Vec3 right = Vec3.Normalize(Vec3.Cross(new Vec3(1f, -lens.Right, 0f), Vec3.UnitZ));
                Vec3 left = Vec3.Normalize(Vec3.Cross(new Vec3(1f, -lens.Left, 0f), Vec3.UnitZ));
                Vec3 top = Vec3.Normalize(Vec3.Cross(new Vec3(1f, 0f, lens.Up), Vec3.UnitY));
                Vec3 bottom = Vec3.Normalize(Vec3.Cross(new Vec3(1f, 0f, lens.Down), Vec3.UnitY));
                float flip = i % 2 == 0 ? 1f : -1f;
                Assert.True(PortalViewMath.TangentsFromSidePlanes(right * flip, left * -flip, top * flip, bottom * -flip, out LensTangents got));
                Assert.Equal(lens.Left, got.Left, 4);
                Assert.Equal(lens.Right, got.Right, 4);
                Assert.Equal(lens.Down, got.Down, 4);
                Assert.Equal(lens.Up, got.Up, 4);
            }
            Assert.False(PortalViewMath.TangentsFromSidePlanes(Vec3.UnitX, Vec3.UnitX, Vec3.UnitX, Vec3.UnitX, out _));
        }

        [Fact]
        public void CoverSaysWhenAPatchIsOnScreenAndHowBig()
        {
            WallFrame wall = Rig.FarWall();   // at x = 5, facing -X
            LensTangents lens = LensTangents.Symmetric(90f, 1f);   // edges at 45 degrees
            Vec2 min = new Vec2(1.5f, 1.0f), max = new Vec2(2.5f, 2.0f);   // 1 m square, centre world (5, 0, 1.5)

            // Square on, 2 m away: 1 m at 2 m is tangent 0.25 each side of the middle, a quarter of the half-screen.
            ScreenCover c = PortalViewMath.CoverOf(wall, min, max, 0f, new Vec3(3f, 0f, 1.5f), Quat.Identity, lens, 0.0127f);
            Assert.True(c.Visible);
            Assert.Equal(0.25f, c.WidthFraction, 3);
            Assert.Equal(0.25f, c.HeightFraction, 3);
            Assert.Equal(-0.25f, c.MinX, 3);
            Assert.Equal(0.25f, c.MaxY, 3);

            // Looking the other way: not visible.
            Quat away = Quat.CreateFromAxisAngle(Vec3.UnitZ, MathF.PI);
            Assert.False(PortalViewMath.CoverOf(wall, min, max, 0f, new Vec3(3f, 0f, 1.5f), away, lens, 0.0127f).Visible);

            // Turned 80 degrees aside: off the edge of a 90 degree view.
            Quat aside = Quat.CreateFromAxisAngle(Vec3.UnitZ, 80f * MathF.PI / 180f);
            Assert.False(PortalViewMath.CoverOf(wall, min, max, 0f, new Vec3(3f, 0f, 1.5f), aside, lens, 0.0127f).Visible);

            // Behind the wall: never visible.
            Assert.False(PortalViewMath.CoverOf(wall, min, max, 0f, new Vec3(5.5f, 0f, 1.5f), away, lens, 0.0127f).Visible);

            // Nose almost on it: fills the screen, and nothing breaks.
            ScreenCover close = PortalViewMath.CoverOf(wall, min, max, 0f, new Vec3(4.98f, 0f, 1.5f), Quat.Identity, lens, 0.0127f);
            Assert.True(close.Visible);
            Assert.Equal(1f, close.WidthFraction, 3);
            Assert.Equal(1f, close.HeightFraction, 3);

            // Far away it is small.
            WallFrame distant = new WallFrame(new Vec3(205f, 2f, 0f), new Vec3(0f, -1f, 0f), new Vec3(0f, 0f, 1f), 4f, 3f);
            ScreenCover far = PortalViewMath.CoverOf(distant, min, max, 0f, new Vec3(5f, 0f, 1.5f), Quat.Identity, lens, 0.0127f);
            Assert.True(far.Visible);
            Assert.True(far.WidthFraction < 0.004f);
        }

        [Fact]
        public void CoverAgreesWithProjectingTheCornersWhenAllAreInFront()
        {
            var rng = new Rng(404u);
            WallFrame wall = Rig.LeftWall();
            var lens = new LensTangents(-1.1f, 0.9f, -0.8f, 1.0f);
            Mat4 p = PortalViewMath.Projection(lens, 0.01f, 1000f);
            for (int i = 0; i < 200; i++)
            {
                Vec2 min = new Vec2(rng.Range(0.2f, 2f), rng.Range(0.2f, 1.2f));
                Vec2 max = min + new Vec2(rng.Range(0.3f, 1.5f), rng.Range(0.3f, 1.5f));
                var eye = new Vec3(rng.Range(0.5f, 4.5f), rng.Range(-2f, 1.5f), rng.Range(0.5f, 2.5f));
                Quat look = Quat.CreateFromAxisAngle(Vec3.UnitZ, MathF.PI / 2f + rng.Range(-0.3f, 0.3f));   // toward the wall (+Y)
                float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
                bool allInFront = true;
                for (int k = 0; k < 4; k++)
                {
                    Vec2 uv = new Vec2(k == 0 || k == 3 ? min.X : max.X, k < 2 ? min.Y : max.Y);
                    Vec3 v = CameraConventions.WorldToView(wall.ToWorld(uv), eye, look);
                    if (!(v.Z < -0.02f)) { allInFront = false; break; }
                    Vec4 clip = Project(p, v);
                    minX = MathF.Min(minX, clip.X / clip.W); maxX = MathF.Max(maxX, clip.X / clip.W);
                    minY = MathF.Min(minY, clip.Y / clip.W); maxY = MathF.Max(maxY, clip.Y / clip.W);
                }
                if (!allInFront) continue;
                ScreenCover c = PortalViewMath.CoverOf(wall, min, max, 0f, eye, look, lens, 0.01f);
                bool expectVisible = MathF.Min(maxX, 1f) > MathF.Max(minX, -1f) && MathF.Min(maxY, 1f) > MathF.Max(minY, -1f);
                Assert.Equal(expectVisible, c.Visible);
                if (!c.Visible) continue;
                Assert.Equal(MathF.Max(minX, -1f), c.MinX, 3);
                Assert.Equal(MathF.Min(maxX, 1f), c.MaxX, 3);
                Assert.Equal(MathF.Max(minY, -1f), c.MinY, 3);
                Assert.Equal(MathF.Min(maxY, 1f), c.MaxY, 3);
            }
        }

        [Fact]
        public void FarWallMapCarriesThePatchCentreToThePatchCentreAndIsExactEverywhere()
        {
            // A low patch on the far wall and a higher one on the left wall, different walls, different sizes.
            var pair = new PortalPair(
                Rig.End(Rig.FarWall(), PaintColor.Orange, new Vec2(2.4f, 1.2f), PortalAnchor.PaintCenter),
                Rig.End(Rig.LeftWall(), PaintColor.Blue, new Vec2(1.5f, 1.9f), PortalAnchor.PaintCenter));
            WallFrame a = pair.A.Wall, b = pair.B.Wall;
            PortalViewMath.FarWallMap(a, b, pair.PlayerAToB, out Vec2 origin, out Vec2 alongU, out Vec2 alongV);

            // The middle of orange comes out at the middle of blue.
            Vec2 centre = origin + alongU * (2.4f / a.Width) + alongV * (1.2f / a.Height);
            Assert.Equal(1.5f / b.Width, centre.X, 4);
            Assert.Equal(1.9f / b.Height, centre.Y, 4);

            // Going through turns you round: your right on the way in is your left on the way out,
            // so "across" runs the other way on the exit wall. "Up" stays up.
            Assert.True(alongU.X < 0f);
            Assert.Equal(0f, alongU.Y, 5);
            Assert.Equal(0f, alongV.X, 5);
            Assert.True(alongV.Y > 0f);
            // Lengths carry over: one entry-wall width is that many exit-wall widths.
            Assert.Equal(a.Width / b.Width, -alongU.X, 4);
            Assert.Equal(a.Height / b.Height, alongV.Y, 4);

            // The flat map agrees with carrying each point through the crossing, anywhere on the wall.
            var rng = new Rng(61u);
            for (int i = 0; i < 200; i++)
            {
                float u = rng.Range(0f, 1f), v = rng.Range(0f, 1f);
                Vec2 viaMap = origin + alongU * u + alongV * v;
                Vec3 world = a.ToWorld(new Vec2(u * a.Width, v * a.Height));
                Vec3 carried = pair.PlayerAToB.TransformPoint(world);
                Assert.Equal(0f, b.SignedDistance(carried), 4);          // it lands on the exit wall's plane
                Vec2 direct = b.ToUV(carried);
                Assert.Equal(direct.X / b.Width, viaMap.X, 4);
                Assert.Equal(direct.Y / b.Height, viaMap.Y, 4);
            }

            // A point 0.7 m below the orange centre comes out 0.7 m below the blue centre: here that is
            // 1.2 m up the exit wall, still on it. A metre and a half further down it is under the floor
            // (below 0 on the exit wall): no paint can be there, so nothing is shown through that part.
            Vec2 low = PortalViewMath.FarPlace(a, b, pair.PlayerAToB, 2.4f / a.Width, 0.5f / a.Height);
            Assert.Equal(1.2f / b.Height, low.Y, 4);
            var lowPair = new PortalPair(
                Rig.End(Rig.FarWall(), PaintColor.Orange, new Vec2(2.4f, 1.9f), PortalAnchor.PaintCenter),
                Rig.End(Rig.LeftWall(), PaintColor.Blue, new Vec2(1.5f, 1.2f), PortalAnchor.PaintCenter));
            Vec2 under = PortalViewMath.FarPlace(lowPair.A.Wall, lowPair.B.Wall, lowPair.PlayerAToB, 2.4f / a.Width, 0.2f / a.Height);
            Assert.True(under.Y < 0f, $"expected under the exit room's floor, got {under.Y}");
        }

        [Fact]
        public void FarWallMapThereAndBackIsTheIdentity()
        {
            var pair = new PortalPair(
                Rig.End(Rig.FarWall(), PaintColor.Orange, new Vec2(1.1f, 1.4f), PortalAnchor.PaintCenter),
                Rig.End(Rig.NearWall(), PaintColor.Blue, new Vec2(2.7f, 1.6f), PortalAnchor.PaintCenter));
            PortalViewMath.FarWallMap(pair.A.Wall, pair.B.Wall, pair.PlayerAToB, out Vec2 o1, out Vec2 u1, out Vec2 v1);
            PortalViewMath.FarWallMap(pair.B.Wall, pair.A.Wall, pair.PlayerBToA, out Vec2 o2, out Vec2 u2, out Vec2 v2);
            var rng = new Rng(62u);
            for (int i = 0; i < 100; i++)
            {
                var p = new Vec2(rng.Range(0f, 1f), rng.Range(0f, 1f));
                Vec2 there = o1 + u1 * p.X + v1 * p.Y;
                Vec2 back = o2 + u2 * there.X + v2 * there.Y;
                Rig.Near(p, back, 4);
            }
        }

        [Fact]
        public void GuardDistanceIsTheNearCornerPlusMarginPlusPanel()
        {
            LensTangents lens = LensTangents.Symmetric(80f, 16f / 9f);
            float near = 0.0127f;
            float corner = HeadClipGuard.NearCornerDistance(near, lens.Right, lens.Up);
            Assert.Equal(corner + 0.01f + 0.003f, PortalViewMath.GuardDistance(near, lens, 0.01f, 0.003f), 6);
            Assert.True(corner > near && corner < near * 1.6f);
            // A lopsided lens uses its widest side.
            var vr = new LensTangents(-1.3f, 0.9f, -1.2f, 1.0f);
            Assert.Equal(HeadClipGuard.NearCornerDistance(near, 1.3f, 1.2f), PortalViewMath.GuardDistance(near, vr, 0f, 0f), 6);
        }
    }

    /// <summary>
    /// Kept far-side pictures: drawing only the part an opening covers, looking a kept picture up from a
    /// moved head or for a portal seen through a portal, the half-rate turns, and the per-eye close-up rule.
    /// </summary>
    public class PortalPictureTests
    {
        private const float Inches = 39.3701f;

        // The test rig's room: x 0..5, y -3..3 (the walls used here span y -2..2), z 0..3.
        private static readonly Vec3 RoomMin = new Vec3(0f, -3f, 0f);
        private static readonly Vec3 RoomMax = new Vec3(5f, 3f, 3f);

        private static PortalPair FacingPair()
        {
            return new PortalPair(
                Rig.End(Rig.FarWall(), PaintColor.Orange, new Vec2(2f, 1.5f), PortalAnchor.PaintCenter),
                Rig.End(Rig.NearWall(), PaintColor.Blue, new Vec2(2f, 1.5f), PortalAnchor.PaintCenter));
        }

        private static Vec3 RayOf(Quat rotation, float tanX, float tanY)
        {
            // forward + tanX * right + tanY * up, in core axes (X forward, Y left, Z up).
            return Vec3.Transform(new Vec3(1f, -tanX, tanY), rotation);
        }

        private static Vec2 ScreenUv(LensTangents lens, float tanX, float tanY)
        {
            return new Vec2((tanX - lens.Left) / (lens.Right - lens.Left), (lens.Up - tanY) / (lens.Up - lens.Down));
        }

        [Fact]
        public void CropStretchesItsRectangleOverThePictureAndLeavesDepthAlone()
        {
            var lens = new LensTangents(-1.25f, 0.9f, -1.1f, 1.05f);
            Mat4 full = PortalViewMath.Projection(lens, 0.5f, 10000f);
            var rect = new ScreenRect(-0.3f, 0.1f, 0.45f, 0.8f);
            Mat4 crop = PortalPictureMath.Crop(full, rect);
            var rng = new Rng(11);
            for (int i = 0; i < 200; i++)
            {
                var v = new Vec4(rng.Range(-40f, 40f), rng.Range(-40f, 40f), -rng.Range(1f, 300f), 1f);
                Vec4 a = Vec4.Transform(v, full);
                Vec4 b = Vec4.Transform(v, crop);
                float x = a.X / a.W, y = a.Y / a.W;
                Assert.Equal((2f * x - (rect.MinX + rect.MaxX)) / (rect.MaxX - rect.MinX), b.X / b.W, 3);
                Assert.Equal((2f * y - (rect.MinY + rect.MaxY)) / (rect.MaxY - rect.MinY), b.Y / b.W, 3);
                Assert.Equal(a.Z, b.Z, 4);
                Assert.Equal(a.W, b.W, 4);
            }
        }

        [Fact]
        public void TheColumnLayoutCropIsTheTransposeOfTheRowLayoutOne()
        {
            var lens = new LensTangents(-1.1f, 1.3f, -0.9f, 1.0f);
            Mat4 full = PortalViewMath.Projection(lens, 0.5f, 10000f);
            var rect = new ScreenRect(-0.9f, -0.2f, 0.1f, 0.35f);
            Mat4 row = PortalPictureMath.Crop(full, rect);
            Mat4 column = PortalPictureMath.CropColumnLayout(Mat4.Transpose(full), rect);
            Mat4 back = Mat4.Transpose(column);
            Assert.Equal(row.M11, back.M11, 5);
            Assert.Equal(row.M31, back.M31, 5);
            Assert.Equal(row.M22, back.M22, 5);
            Assert.Equal(row.M32, back.M32, 5);
            Assert.Equal(row.M33, back.M33, 5);
            Assert.Equal(row.M43, back.M43, 5);
            Assert.Equal(row.M34, back.M34, 5);
        }

        [Fact]
        public void ACroppedCutOffProjectionIsStillCutAtTheSamePlane()
        {
            PortalPair pair = FacingPair();
            var lens = LensTangents.Symmetric(80f, 16f / 9f);
            Vec3 eye = new Vec3(3.2f, 0.2f, 1.6f);
            Assert.True(PortalViewMath.Build(pair, true, eye, Quat.Identity, lens, 0.5f, 10000f, Inches, 0.005f, out PortalEyeView view));
            var rect = new ScreenRect(-0.2f, -0.3f, 0.4f, 0.5f);
            Mat4 crop = PortalPictureMath.Crop(view.Projection, rect);
            // A point ON the cut plane has depth 0 in both; a point beyond it has the same positive depth.
            Vec3 onPlaneWorld = pair.B.Wall.ToWorld(new Vec2(2.1f, 1.4f), 0.005f);
            Vec3 beyondWorld = pair.B.Wall.ToWorld(new Vec2(2.1f, 1.4f), 2f);
            foreach (Vec3 world in new[] { onPlaneWorld, beyondWorld })
            {
                Vec3 v = CameraConventions.WorldToView(world, view.Position, view.Rotation) * Inches;
                Vec4 a = Vec4.Transform(new Vec4(v, 1f), view.Projection);
                Vec4 b = Vec4.Transform(new Vec4(v, 1f), crop);
                Assert.Equal(a.Z / a.W, b.Z / b.W, 5);
            }
            Vec3 on = CameraConventions.WorldToView(onPlaneWorld, view.Position, view.Rotation) * Inches;
            Vec4 clip = Vec4.Transform(new Vec4(on, 1f), crop);
            Assert.True(MathF.Abs(clip.Z / clip.W) < 1e-3f);
        }

        [Fact]
        public void SnapToPixelsOnlyEverGrowsTheRectangle()
        {
            var rng = new Rng(5);
            for (int i = 0; i < 300; i++)
            {
                float x0 = rng.Range(-1f, 0.9f), y0 = rng.Range(-1f, 0.9f);
                var rect = new ScreenRect(x0, y0, MathF.Min(1f, x0 + rng.Range(0.001f, 1.5f)), MathF.Min(1f, y0 + rng.Range(0.001f, 1.5f)));
                ScreenRect snapped = PortalPictureMath.SnapToPixels(rect, 1983, 1983, out int x, out int y, out int w, out int h);
                Assert.True(snapped.Contains(rect) || w == 1983 || h == 1983 || w == 8 || h == 8, $"{rect.MinX} {rect.MinY} {rect.MaxX} {rect.MaxY}");
                Assert.True(x >= 0 && y >= 0 && w >= 8 && h >= 8 && x + w <= 1983 && y + h <= 1983);
                // The pixels it names are exactly the rectangle it returns.
                Assert.Equal(2f * x / 1983f - 1f, snapped.MinX, 5);
                Assert.Equal(1f - 2f * y / 1983f, snapped.MaxY, 5);
                Assert.Equal(2f * (x + w) / 1983f - 1f, snapped.MaxX, 5);
                Assert.Equal(1f - 2f * (y + h) / 1983f, snapped.MinY, 5);
            }
        }

        [Fact]
        public void BoundsOnScreenOfAPatchAheadBehindAndAcrossTheEye()
        {
            WallFrame wall = Rig.FarWall();
            var lens = LensTangents.Symmetric(90f, 1f);
            Vec3 a = wall.ToWorld(new Vec2(1.6f, 1.1f)), b = wall.ToWorld(new Vec2(2.4f, 1.1f));
            Vec3 c = wall.ToWorld(new Vec2(2.4f, 1.9f)), d = wall.ToWorld(new Vec2(1.6f, 1.9f));

            // Square on from 2 m: 0.8 m wide at 2 m with a 90 degree lens is 0.4 of the half-width each way.
            ScreenRect ahead = PortalPictureMath.BoundsOnScreen(a, b, c, d, new Vec3(3f, 0f, 1.5f), Quat.Identity, lens, 0.0127f);
            Assert.False(ahead.IsEmpty);
            Assert.Equal(-0.2f, ahead.MinX, 3);
            Assert.Equal(0.2f, ahead.MaxX, 3);
            Assert.Equal(-0.2f, ahead.MinY, 3);
            Assert.Equal(0.04f, ahead.Share, 3);

            // Looking the other way: nothing.
            Quat about = Quat.CreateFromAxisAngle(new Vec3(0f, 0f, 1f), MathF.PI);
            Assert.True(PortalPictureMath.BoundsOnScreen(a, b, c, d, new Vec3(3f, 0f, 1.5f), about, lens, 0.0127f).IsEmpty);

            // Standing in the wall's plane beside the patch, looking along the wall: part of it is behind
            // the eye, and what is left is still a sane rectangle inside the view.
            Quat along = Quat.CreateFromAxisAngle(new Vec3(0f, 0f, 1f), -MathF.PI / 2f);   // facing -Y = the wall's U
            ScreenRect across = PortalPictureMath.BoundsOnScreen(a, b, c, d, wall.ToWorld(new Vec2(2.0f, 1.5f), 0.02f), along, lens, 0.0127f);
            Assert.False(across.IsEmpty);
            Assert.True(across.MinX >= -1f && across.MaxX <= 1f && across.MinY >= -1f && across.MaxY <= 1f);
        }

        [Fact]
        public void LooksAwayFromIsOnlyTrueWhenNoCornerOfTheViewClosesOnTheWall()
        {
            var lens = new LensTangents(-1.67f, 1.2f, -1.75f, 1.16f);   // a headset eye: 59 / 50 / 60 / 49 degrees
            Vec3 normal = new Vec3(-1f, 0f, 0f);                        // the far wall's
            Assert.False(PortalPictureMath.LooksAwayFrom(Quat.Identity, lens, normal));            // facing it
            Quat about = Quat.CreateFromAxisAngle(new Vec3(0f, 0f, 1f), MathF.PI);
            Assert.True(PortalPictureMath.LooksAwayFrom(about, lens, normal));                      // back to it
            // Turned 40 degrees from straight away: the wide edge of the lens still sees along the wall and in.
            Quat turned = Quat.CreateFromAxisAngle(new Vec3(0f, 0f, 1f), MathF.PI - 40f * MathF.PI / 180f);
            Assert.False(PortalPictureMath.LooksAwayFrom(turned, lens, normal));
            // Brute force agrees over many poses.
            var rng = new Rng(3);
            for (int i = 0; i < 300; i++)
            {
                Quat q = Quat.Normalize(Quat.CreateFromAxisAngle(rng.OnUnitSphere(), rng.Range(-3.1f, 3.1f)));
                float least = float.MaxValue;
                for (int sx = 0; sx <= 8; sx++)
                {
                    for (int sy = 0; sy <= 8; sy++)
                    {
                        float tx = lens.Left + (lens.Right - lens.Left) * sx / 8f;
                        float ty = lens.Down + (lens.Up - lens.Down) * sy / 8f;
                        least = MathF.Min(least, Vec3.Dot(RayOf(q, tx, ty), normal));
                    }
                }
                Assert.Equal(least >= 0f, PortalPictureMath.LooksAwayFrom(q, lens, normal));
            }
        }

        [Fact]
        public void RoomExitFindsTheFarSideFromInsideAndFromBehindAWall()
        {
            Vec3 inside = PortalPictureMath.RoomExit(new Vec3(1f, 0f, 1f), new Vec3(1f, 0f, 0f), RoomMin, RoomMax);
            Assert.Equal(5f, inside.X, 4);
            // From half a metre behind the near wall (x = 0), through it, to the far wall.
            Vec3 behind = PortalPictureMath.RoomExit(new Vec3(-0.5f, 0.2f, 1.5f), new Vec3(1f, 0.1f, 0f), RoomMin, RoomMax);
            Assert.Equal(5f, behind.X, 4);
            Assert.Equal(0.2f + 0.55f, behind.Y, 4);
            // Upward: the ceiling.
            Vec3 up = PortalPictureMath.RoomExit(new Vec3(2f, 0f, 1f), new Vec3(0.1f, 0f, 1f), RoomMin, RoomMax);
            Assert.Equal(3f, up.Z, 4);
            // A line that runs along an axis exactly does not divide by zero.
            Vec3 flat = PortalPictureMath.RoomExit(new Vec3(2f, 0f, 1f), new Vec3(0f, -1f, 0f), RoomMin, RoomMax);
            Assert.Equal(-3f, flat.Y, 4);
        }

        [Fact]
        public void LookingAKeptPictureUpFromWhereItWasTakenIsThePasteByScreenPosition()
        {
            // Nothing has moved: whatever the depth of the scene, each line of sight must land on its own pixel.
            PortalPair pair = FacingPair();
            RigidTransform crossing = pair.PlayerCrossing(true);
            var lens = new LensTangents(-1.25f, 0.9f, -1.1f, 1.05f);
            var rng = new Rng(21);
            for (int i = 0; i < 100; i++)
            {
                Vec3 eye = new Vec3(rng.Range(2.5f, 4.6f), rng.Range(-1f, 1f), rng.Range(1.0f, 2.0f));
                Quat rotation = Quat.CreateFromYawPitchRoll(0f, 0f, 0f) * Quat.CreateFromAxisAngle(new Vec3(0f, 0f, 1f), rng.Range(-0.4f, 0.4f));
                PortalCamera.VirtualEye(crossing, eye, rotation, out Vec3 virtualEye, out Quat virtualRotation);
                Mat4 kept = PortalPictureMath.ViewProjection(virtualEye, virtualRotation, PortalViewMath.Projection(lens, 0.0127f, 254f));
                float tx = rng.Range(lens.Left, lens.Right), ty = rng.Range(lens.Down, lens.Up);
                Assert.True(PortalPictureMath.Lookup(crossing, eye, RayOf(rotation, tx, ty), RoomMin, RoomMax, kept, out Vec2 uv, out _));
                Vec2 expected = ScreenUv(lens, tx, ty);
                Assert.Equal(expected.X, uv.X, 3);
                Assert.Equal(expected.Y, uv.Y, 3);
            }
        }

        [Fact]
        public void AfterTheHeadMovesTheLookUpIsExactForTheRoomsWallsAndCloseForThingsInTheRoom()
        {
            PortalPair pair = FacingPair();
            RigidTransform crossing = pair.PlayerCrossing(true);
            var lens = LensTangents.Symmetric(100f, 1f);
            Mat4 projection = PortalViewMath.Projection(lens, 0.0127f, 254f);

            // The kept picture: taken one frame ago.
            Vec3 eyeThen = new Vec3(3.4f, 0.1f, 1.6f);
            Quat rotationThen = Quat.Identity;
            PortalCamera.VirtualEye(crossing, eyeThen, rotationThen, out Vec3 keptEye, out Quat keptRotation);
            Mat4 kept = PortalPictureMath.ViewProjection(keptEye, keptRotation, projection);

            // Now: the head has moved 2.2 cm (stick walking at 2 m/s for one 90th of a second) and turned 2 degrees.
            Vec3 eyeNow = eyeThen + new Vec3(0.022f, 0.004f, 0f);
            Quat rotationNow = Quat.CreateFromAxisAngle(new Vec3(0f, 0f, 1f), 2f * MathF.PI / 180f);
            PortalCamera.VirtualEye(crossing, eyeNow, rotationNow, out Vec3 virtualNow, out _);

            var rng = new Rng(8);
            float worstBox = 0f;
            for (int i = 0; i < 100; i++)
            {
                // A point of the room's walls, seen through the opening.
                Vec3 wallPoint = new Vec3(5f, rng.Range(-1.5f, 1.5f), rng.Range(0.5f, 2.5f));
                Vec3 rayFar = wallPoint - virtualNow;                       // from the carried eye, in the exit room
                Vec3 ray = crossing.Inverse().TransformDirection(rayFar);    // the same line of sight at the real eye
                Assert.True(PortalPictureMath.Lookup(crossing, eyeNow, ray, RoomMin, RoomMax, kept, out Vec2 uv, out Vec3 found));
                Assert.True(Vec3.Distance(found, wallPoint) < 1e-3f);
                Vec4 clip = Vec4.Transform(new Vec4(wallPoint, 1f), kept);
                Assert.Equal(0.5f + 0.5f * clip.X / clip.W, uv.X, 4);
                Assert.Equal(0.5f - 0.5f * clip.Y / clip.W, uv.Y, 4);

                // A thing standing 2 m into the room on the same line of sight: where it really is in the kept
                // picture, against where the look-up reads (the wall behind it). The error is the parallax of
                // 2.2 cm of head movement between 2 m and the wall.
                Vec3 thing = virtualNow + Vec3.Normalize(rayFar) * 2f;
                Vec4 thingClip = Vec4.Transform(new Vec4(thing, 1f), kept);
                float dx = (0.5f + 0.5f * thingClip.X / thingClip.W) - uv.X;
                float dy = (0.5f - 0.5f * thingClip.Y / thingClip.W) - uv.Y;
                worstBox = MathF.Max(worstBox, MathF.Sqrt(dx * dx + dy * dy));
            }
            // Under half a percent of the picture's width (about 9 pixels of 1983): the size of the ghosting a
            // near thing can show on a re-projected frame at full stick speed. Standing still it is zero.
            Assert.True(worstBox < 0.005f, $"worst {worstBox}");
            Assert.True(worstBox > 0.0005f, "the test should really be measuring a parallax");
        }

        [Fact]
        public void APortalSeenThroughAPortalLandsWhereTheNextLevelBelongsAndShrinksWithDistance()
        {
            // Two facing portals 5 m apart. The eye stands 1.5 m in front of orange. Through orange it sees
            // the room from behind blue (level 1), and in that picture orange again, 5 m further off (level 2).
            PortalPair pair = FacingPair();
            RigidTransform crossing = pair.PlayerCrossing(true);
            var lens = LensTangents.Symmetric(100f, 1f);
            Mat4 projection = PortalViewMath.Projection(lens, 0.0127f, 254f);
            Vec3 eye = new Vec3(3.5f, 0.15f, 1.55f);
            Quat rotation = Quat.Identity;

            // Level 1's camera, and its picture kept from last frame.
            PortalCamera.VirtualEye(crossing, eye, rotation, out Vec3 eye1, out Quat rotation1);
            Mat4 kept = PortalPictureMath.ViewProjection(eye1, rotation1, projection);

            Vec3 centre = pair.A.Wall.ToWorld(pair.A.Opening.CenterUV);
            Vec3 edge = centre + pair.A.Wall.U * 0.3f;

            // While level 1 is drawn, its camera sees the orange opening on the real wall (level 2 of the
            // tunnel). Inside that opening the next level belongs: the orange wall again, one room further on.
            // The line of sight to where the NEXT opening's centre should appear is the line to that centre
            // carried back through the crossing once.
            RigidTransform back = crossing.Inverse();
            Vec3 nextCentre = back.TransformPoint(centre);
            Vec3 nextEdge = back.TransformPoint(edge);
            Assert.True(PortalPictureMath.Lookup(crossing, eye1, nextCentre - eye1, RoomMin, RoomMax, kept, out Vec2 uvCentre, out Vec3 hitCentre));
            Assert.True(PortalPictureMath.Lookup(crossing, eye1, nextEdge - eye1, RoomMin, RoomMax, kept, out Vec2 uvEdge, out Vec3 hitEdge));

            // The look-up finds the real wall points exactly (they are on the room's box)...
            Assert.True(Vec3.Distance(hitCentre, centre) < 1e-3f, "the look-up followed the line to the wrong place");
            Assert.True(Vec3.Distance(hitEdge, edge) < 1e-3f);
            // ...and reads the kept picture exactly where it shows the orange opening itself. So what is
            // pasted into the opening is the opening again, with whatever it held last frame: the levels nest.
            Vec4 c1 = Vec4.Transform(new Vec4(centre, 1f), kept);
            Vec4 e1 = Vec4.Transform(new Vec4(edge, 1f), kept);
            Assert.Equal(0.5f + 0.5f * c1.X / c1.W, uvCentre.X, 4);
            Assert.Equal(0.5f - 0.5f * c1.Y / c1.W, uvCentre.Y, 4);
            Assert.Equal(0.5f + 0.5f * e1.X / e1.W, uvEdge.X, 4);

            // Size on level 1's screen: the opening it sees directly, against the next one inside it. Each
            // level is further off by the distance between the walls, so it is smaller by the ratio of the
            // distances: the tunnel recedes, it is NOT the same picture pasted again at the same size.
            Vec4 c2 = Vec4.Transform(new Vec4(nextCentre, 1f), kept);
            Vec4 e2 = Vec4.Transform(new Vec4(nextEdge, 1f), kept);
            float sizeDirect = MathF.Abs(e1.X / e1.W - c1.X / c1.W);
            float sizeNext = MathF.Abs(e2.X / e2.W - c2.X / c2.W);
            float distanceDirect = 5f - eye1.X;            // level 1's camera to the orange wall
            float distanceNext = distanceDirect + 5f;      // one more room
            Assert.Equal(distanceDirect / distanceNext, sizeNext / sizeDirect, 2);
            Assert.True(sizeNext < sizeDirect * 0.7f, "the next level must be clearly smaller");
        }

        [Fact]
        public void HalfRateDrawsHalfThePicturesEveryFrameAndEveryPictureEveryOtherFrame()
        {
            // One portal in view, two eyes: one picture a frame, the eyes taking turns.
            for (int tick = 0; tick < 6; tick++)
            {
                bool left = PortalSchedule.IsFresh(PortalRateMode.HalfRate, tick, 0, 0, 1, 2);
                bool right = PortalSchedule.IsFresh(PortalRateMode.HalfRate, tick, 1, 0, 1, 2);
                Assert.NotEqual(left, right);
                Assert.NotEqual(left, PortalSchedule.IsFresh(PortalRateMode.HalfRate, tick + 1, 0, 0, 1, 2));
            }
            // Both in view: two a frame, the ends taking turns with both eyes together.
            for (int tick = 0; tick < 6; tick++)
            {
                bool orangeLeft = PortalSchedule.IsFresh(PortalRateMode.HalfRate, tick, 0, 0, 2, 2);
                Assert.Equal(orangeLeft, PortalSchedule.IsFresh(PortalRateMode.HalfRate, tick, 1, 0, 2, 2));
                Assert.NotEqual(orangeLeft, PortalSchedule.IsFresh(PortalRateMode.HalfRate, tick, 0, 1, 2, 2));
            }
            Assert.Equal(2, PortalSchedule.WorstFrame(PortalRateMode.EveryFrame, 1, 2));
            Assert.Equal(4, PortalSchedule.WorstFrame(PortalRateMode.EveryFrame, 2, 2));
            Assert.Equal(1, PortalSchedule.WorstFrame(PortalRateMode.HalfRate, 1, 2));
            Assert.Equal(2, PortalSchedule.WorstFrame(PortalRateMode.HalfRate, 2, 2));
            Assert.Equal(1, PortalSchedule.WorstFrame(PortalRateMode.OnePerFrame, 1, 2));
            Assert.Equal(1, PortalSchedule.WorstFrame(PortalRateMode.OnePerFrame, 2, 2));
            // A flat screen (one eye): the one picture is drawn every other frame.
            Assert.True(PortalSchedule.IsFresh(PortalRateMode.HalfRate, 0, 0, 0, 1, 1));
            Assert.False(PortalSchedule.IsFresh(PortalRateMode.HalfRate, 1, 0, 0, 1, 1));
            Assert.True(PortalSchedule.IsFresh(PortalRateMode.EveryFrame, 1, 0, 0, 1, 1));
        }

        [Fact]
        public void OnePerFrameGetsRoundToEveryPicture()
        {
            int[] drawn = new int[4];
            for (int tick = 0; tick < 8; tick++)
            {
                int n = 0;
                for (int eye = 0; eye < 2; eye++)
                {
                    for (int end = 0; end < 2; end++)
                    {
                        if (!PortalSchedule.IsFresh(PortalRateMode.OnePerFrame, tick, eye, end, 2, 2)) continue;
                        drawn[eye * 2 + end]++;
                        n++;
                    }
                }
                Assert.Equal(1, n);
            }
            foreach (int count in drawn) Assert.Equal(2, count);
        }

        [Fact]
        public void TheGuardDistanceCoversTheNearCutAndAFramesTravel()
        {
            var lens = new LensTangents(-1.67f, 1.2f, -1.75f, 1.16f);
            float still = EyeGuard.Distance(0.0127f, lens, 0.01f, 0.003f, 0f, 1f / 90f);
            // The far corner of a half-inch near cut with a 60 degree lens is about 3.3 cm from the eye.
            Assert.InRange(still, 0.04f, 0.06f);
            // Falling through a floor portal at 6 m/s is 6.7 cm a frame at 90 frames a second.
            float falling = EyeGuard.Distance(0.0127f, lens, 0.01f, 0.003f, 6f, 1f / 90f);
            Assert.Equal(still + 6f / 90f, falling, 4);
            // A stalled frame does not blow the distance up without limit.
            Assert.True(EyeGuard.Distance(0.0127f, lens, 0.01f, 0.003f, 6f, 1f) <= still + 0.3001f);
        }

        [Fact]
        public void EachEyeIsJudgedByItsOwnPlaceNotTheHeads()
        {
            // The head's centre is 1 cm in front of the wall, turned 45 degrees: one eye leads by 2.5 cm
            // (it is already 1.5 cm past the wall), the other trails 3.5 cm in front.
            float guard = 0.055f;
            float leading = 0.01f - 0.025f, trailing = 0.01f + 0.025f;
            Assert.True(EyeGuard.SheetDraws(leading, guard));
            Assert.True(EyeGuard.SheetDraws(trailing, guard));
            Assert.False(EyeGuard.SheetDraws(0.30f, guard));

            // The leading eye stands in the opening: onward it sees the far side, back the room it is leaving.
            Assert.Equal(EyeGuard.Sight.FarSide, EyeGuard.WhatIsSeen(leading, 0.7f, true));
            Assert.Equal(EyeGuard.Sight.ThisSide, EyeGuard.WhatIsSeen(leading, -0.2f, true));
            // The trailing eye is an ordinary eye in front of the wall.
            Assert.Equal(EyeGuard.Sight.FarSide, EyeGuard.WhatIsSeen(trailing, 0.7f, true));
            Assert.Equal(EyeGuard.Sight.ThisSide, EyeGuard.WhatIsSeen(trailing, 0.7f, false));
            Assert.Equal(EyeGuard.Sight.ThisSide, EyeGuard.WhatIsSeen(trailing, -0.2f, true));
        }

        [Fact]
        public void AnEyeLeftBehindTheExitSeesWhatItSawTheFrameBefore()
        {
            // The frame before the head is carried over: the trailing eye is 2 cm in front of orange and looks
            // into it. The frame after: the same eye is 2 cm BEHIND blue, looking out. Line of sight by line of
            // sight, the far side it saw before is the room it now looks out into, and the room it could see
            // beside the opening before is what it now sees looking back into blue.
            PortalPair pair = FacingPair();
            RigidTransform crossing = pair.PlayerCrossing(true);
            RigidTransform back = pair.PlayerCrossing(false);
            Vec3 eyeBefore = pair.A.Wall.ToWorld(pair.A.Opening.CenterUV, 0.02f);
            Vec3 eyeAfter = crossing.TransformPoint(eyeBefore);
            Assert.Equal(-0.02f, pair.B.Wall.SignedDistance(eyeAfter), 4);

            var rng = new Rng(4);
            for (int i = 0; i < 200; i++)
            {
                Vec3 rayBefore = rng.OnUnitSphere();
                Vec3 rayAfter = crossing.TransformDirection(rayBefore);
                float intoOrange = -Vec3.Dot(rayBefore, pair.A.Wall.Normal);
                float intoBlue = -Vec3.Dot(rayAfter, pair.B.Wall.Normal);
                if (MathF.Abs(intoOrange) < 0.05f) continue;
                // A wide opening: every line of sight that closes on the wall from 2 cm goes through it.
                EyeGuard.Sight before = EyeGuard.WhatIsSeen(0.02f, intoOrange, true);
                EyeGuard.Sight after = EyeGuard.WhatIsSeen(-0.02f, intoBlue, true);
                // Before: far side = blue's room from the carried eye. After: this side = blue's room from the eye itself.
                Assert.NotEqual(before, after);
                // And they are the same picture: the camera that draws "far side" before is the eye after...
                Vec3 farCameraBefore = crossing.TransformPoint(eyeBefore);
                Assert.True(Vec3.Distance(farCameraBefore, eyeAfter) < 1e-5f);
                // ...and the camera that draws "far side" after (through blue, back to orange's room) is the eye before.
                Vec3 farCameraAfter = back.TransformPoint(eyeAfter);
                Assert.True(Vec3.Distance(farCameraAfter, eyeBefore) < 1e-4f);
            }
        }
    }
}
