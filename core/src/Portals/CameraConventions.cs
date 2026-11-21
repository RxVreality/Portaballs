using System;

namespace Portaballs.Core
{
    /// <summary>
    /// THE ONE PLACE that decides how camera matrices are laid out. Everything else in Portals that touches
    /// a view or projection matrix goes through here, so if s&box turns out to use different rules only this
    /// file needs to change.
    ///
    /// Assumed rules (these are System.Numerics' own):
    ///   1. Row vectors: a point is transformed as v * M (so translation sits in M41, M42, M43).
    ///      Column-vector maths (M * v, as in most papers and in OpenGL docs) is the transpose of this.
    ///   2. Camera ("view") space is right-handed with +X right, +Y up and the camera looking down -Z.
    ///   3. Clip space depth runs 0 at the near plane to 1 at the far plane (Direct3D / Vulkan style,
    ///      not OpenGL's -1 to 1). Matrix4x4.CreatePerspectiveFieldOfView produces exactly this.
    ///   4. A camera's pose is given the core way: position plus a rotation that turns world X (forward),
    ///      Y (left), Z (up) onto the camera's forward, left and up.
    ///   5. A plane (Plane3, or a Vec4 (a, b, c, d)) keeps the points where a*x + b*y + c*z + d is positive.
    ///
    /// Type names: Mat4, Vec4 and Plane3 are the System.Numerics types (see Common\Aliases.cs). The bare
    /// names must not be used here, because inside s&box they mean the engine's own, different types.
    /// </summary>
    public static class CameraConventions
    {
        /// <summary>
        /// Camera-local core axes (X forward, Y left, Z up) to view-space axes (X right, Y up, -Z forward).
        /// </summary>
        public static Vec3 LocalToView(Vec3 local)
        {
            return new Vec3(-local.Y, local.Z, -local.X);
        }

        /// <summary>World to camera space for a camera at <paramref name="position"/> with <paramref name="rotation"/>.</summary>
        public static Mat4 ViewMatrix(Vec3 position, Quat rotation)
        {
            // Camera axes in world space.
            Vec3 forward = Vec3.Transform(new Vec3(1f, 0f, 0f), rotation);
            Vec3 left = Vec3.Transform(new Vec3(0f, 1f, 0f), rotation);
            Vec3 up = Vec3.Transform(new Vec3(0f, 0f, 1f), rotation);
            Vec3 right = -left;
            Vec3 back = -forward;
            // Row-vector layout: column j of the 3x3 part holds view axis j expressed in world space.
            return new Mat4(
                right.X, up.X, back.X, 0f,
                right.Y, up.Y, back.Y, 0f,
                right.Z, up.Z, back.Z, 0f,
                -Vec3.Dot(right, position), -Vec3.Dot(up, position), -Vec3.Dot(back, position), 1f);
        }

        /// <summary>A world point in the camera's view space.</summary>
        public static Vec3 WorldToView(Vec3 world, Vec3 cameraPosition, Quat cameraRotation)
        {
            Vec3 local = Vec3.Transform(world - cameraPosition, Quat.Conjugate(cameraRotation));
            return LocalToView(local);
        }

        /// <summary>
        /// A world-space plane re-expressed in the camera's view space, as (a, b, c, d) with the same
        /// "positive side is kept" meaning. This is what the oblique projection wants.
        /// </summary>
        public static Vec4 WorldPlaneToView(Plane3 worldPlane, Vec3 cameraPosition, Quat cameraRotation)
        {
            Quat inv = Quat.Conjugate(cameraRotation);
            Vec3 n = LocalToView(Vec3.Transform(worldPlane.Normal, inv));
            // A point on the plane, carried into view space, fixes d.
            Vec3 onPlane = worldPlane.Normal * (-worldPlane.D / worldPlane.Normal.LengthSquared());
            Vec3 q = WorldToView(onPlane, cameraPosition, cameraRotation);
            return new Vec4(n.X, n.Y, n.Z, -Vec3.Dot(n, q));
        }

        /// <summary>Standard perspective projection in this file's rules (vertical field of view in radians).</summary>
        public static Mat4 Perspective(float verticalFov, float aspect, float near, float far)
        {
            return Mat4.CreatePerspectiveFieldOfView(verticalFov, aspect, near, far);
        }

        /// <summary>
        /// Eric Lengyel's oblique near plane: changes the projection so its near plane becomes the given
        /// view-space plane (here: the exit portal's wall), so nothing between the virtual camera and the
        /// wall is drawn. The left, right, top and bottom of the view stay exactly the same. The far plane
        /// is tilted as little as possible so that it still passes through the far corner of the original
        /// view on the kept side.
        ///
        /// Returns false and leaves the projection unchanged when the camera is not behind the plane
        /// (the plane must keep its positive side and the camera must be on the negative side).
        ///
        /// In this file's rules the third column of the matrix (M13, M23, M33, M43) makes clip-space depth.
        /// It is replaced by a * plane.
        /// </summary>
        public static bool ObliqueNearPlane(Mat4 projection, Vec4 viewPlane, out Mat4 result)
        {
            result = projection;
            if (!(viewPlane.W < 0f)) return false;
            if (!Mat4.Invert(projection, out Mat4 inverse)) return false;

            // The far corner of the view on the side the plane's normal leans toward, in clip space,
            // carried back to view space.
            var corner = new Vec4(Sign(viewPlane.X), Sign(viewPlane.Y), 1f, 1f);
            Vec4 q = Vec4.Transform(corner, inverse);

            var wColumn = new Vec4(projection.M14, projection.M24, projection.M34, projection.M44);
            float planeAtQ = Vec4.Dot(viewPlane, q);
            if (MathF.Abs(planeAtQ) < 1e-12f) return false;
            float a = Vec4.Dot(wColumn, q) / planeAtQ;
            if (!(a > 0f)) return false;

            Vec4 c = viewPlane * a;
            result.M13 = c.X;
            result.M23 = c.Y;
            result.M33 = c.Z;
            result.M43 = c.W;
            return true;
        }

        private static float Sign(float x)
        {
            return x > 0f ? 1f : (x < 0f ? -1f : 0f);
        }
    }
}
