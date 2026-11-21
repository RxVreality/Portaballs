using System;

namespace Portaballs.Core
{
    /// <summary>
    /// Helpers for drawing the view through a portal. To show what is behind portal A, a second
    /// ("virtual") camera is placed where the player's eye would be after walking through A, it looks out of
    /// B into B's room, and everything between that camera and B's wall is cut away.
    /// None of this depends on matrix layout; anything that does is in <see cref="CameraConventions"/>.
    /// </summary>
    public static class PortalCamera
    {
        /// <summary>
        /// Pose of the virtual camera that draws B's side as seen through A: the real eye pushed through the
        /// crossing. Pass <see cref="PortalPair.PlayerAToB"/> for the player's eyes so the picture matches
        /// where the player will actually end up (it equals the full crossing when both walls stand straight).
        /// For VR call this once per eye.
        /// </summary>
        public static void VirtualEye(in RigidTransform crossing, Vec3 eyePosition, Quat eyeRotation,
            out Vec3 virtualPosition, out Quat virtualRotation)
        {
            virtualPosition = crossing.TransformPoint(eyePosition);
            virtualRotation = crossing.TransformRotation(eyeRotation);
        }

        /// <summary>
        /// The world-space clip plane on the exit portal's wall. Its normal is the exit wall's Normal, so the
        /// exit room (in front of the wall) is kept and everything behind the wall, between the virtual
        /// camera and the wall, is cut. <paramref name="offset"/> slides the plane into the room (positive)
        /// or behind the wall (negative); see <see cref="PortalSettings.ClipPlaneOffset"/>.
        /// </summary>
        public static Plane3 ClipPlaneWorld(in PortalEnd exit, float offset = 0f)
        {
            Vec3 n = exit.Wall.Normal;
            float d = -Vec3.Dot(n, exit.Wall.Origin) - offset;
            return new Plane3(n, d);
        }

        /// <summary>The exit clip plane in a camera's view space (see <see cref="CameraConventions"/> for the axes).</summary>
        public static Vec4 ClipPlaneView(in PortalEnd exit, Vec3 cameraPosition, Quat cameraRotation,
            float offset = 0f)
        {
            return CameraConventions.WorldPlaneToView(ClipPlaneWorld(exit, offset), cameraPosition, cameraRotation);
        }

        /// <summary>
        /// Everything in one go: the virtual camera pose for the eye, and its projection with the near plane
        /// moved onto the exit wall. Returns false when the oblique projection could not be made (the virtual
        /// camera is in front of the exit wall, which happens while the eye is crossing); then draw with the
        /// normal projection and a plain clip plane, or skip the view for that frame.
        /// </summary>
        public static bool VirtualView(in PortalPair pair, bool throughA, Vec3 eyePosition, Quat eyeRotation,
            Mat4 projection, float clipOffset, out Vec3 virtualPosition, out Quat virtualRotation,
            out Mat4 obliqueProjection)
        {
            RigidTransform crossing = pair.PlayerCrossing(throughA);
            VirtualEye(crossing, eyePosition, eyeRotation, out virtualPosition, out virtualRotation);
            PortalEnd exit = throughA ? pair.B : pair.A;
            Vec4 plane = ClipPlaneView(exit, virtualPosition, virtualRotation, clipOffset);
            return CameraConventions.ObliqueNearPlane(projection, plane, out obliqueProjection);
        }
    }

    /// <summary>
    /// Keeps the player's camera from cutting into the portal surface while their head goes through.
    ///
    /// The camera does not see from a point but from a small rectangle (its near plane) a little in front of
    /// the eye. When the eye gets closer to the portal surface than the far corners of that rectangle, part
    /// of the screen pokes through and shows the wall behind for a frame. The fix is to make the portal
    /// surface a box this deep behind the wall, or to push the flat surface back by this much.
    /// </summary>
    public static class HeadClipGuard
    {
        /// <summary>
        /// Distance from the eye to the corners of the near-plane rectangle, from tangents of the half angles
        /// (VR headsets have uneven left/right/up/down angles: pass the biggest of each pair).
        /// </summary>
        public static float NearCornerDistance(float near, float tanHalfWidth, float tanHalfHeight)
        {
            float w = near * tanHalfWidth;
            float h = near * tanHalfHeight;
            return MathF.Sqrt(near * near + w * w + h * h);
        }

        /// <summary>Same, for a symmetric camera given its vertical field of view (radians) and width/height ratio.</summary>
        public static float NearCornerDistanceFov(float near, float verticalFov, float aspect)
        {
            float tanH = MathF.Tan(verticalFov * 0.5f);
            return NearCornerDistance(near, tanH * aspect, tanH);
        }

        /// <summary>
        /// How deep (metres) the portal surface must be thickened behind the wall so the camera never clips
        /// through it, whatever the head does. Constant for a given camera.
        /// </summary>
        public static float RequiredThickness(float near, float verticalFov, float aspect, float margin)
        {
            return NearCornerDistanceFov(near, verticalFov, aspect) + margin;
        }

        /// <summary>
        /// How far (metres) a flat portal surface must be pushed back behind the wall (along minus the wall
        /// normal) right now, for an eye at this position. 0 while the eye is far enough away. When the eye is
        /// already behind the wall (it is about to be carried through) the full thickness is returned.
        /// </summary>
        public static float RequiredPushBack(in PortalEnd end, Vec3 eyePosition, float near, float verticalFov,
            float aspect, float margin)
        {
            float need = RequiredThickness(near, verticalFov, aspect, margin);
            float d = end.Wall.SignedDistance(eyePosition);
            if (d <= 0f) return need;
            return MathF.Max(0f, need - d);
        }
    }
}
