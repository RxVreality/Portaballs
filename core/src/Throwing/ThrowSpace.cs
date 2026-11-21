namespace Portaballs.Core
{
    /// <summary>
    /// Carries a throw from the space it was measured in to the world.
    ///
    /// What it is for: the hand's pose history is kept in a space that never jumps (the play area's
    /// own space, or the helper's), so that moving the player through a portal does not look like the
    /// hand flying across the room. The throw is worked out there, with gravity turned into that space
    /// first, and only the finished result is carried into the world, using wherever the play area is
    /// at the moment of the throw.
    /// </summary>
    public static class ThrowSpace
    {
        /// <summary>
        /// World gravity as it looks from inside a space. Give this to ThrowSettings.Gravity when the
        /// pose history is in that space.
        /// </summary>
        /// <param name="localToWorld">Carries a point of the space into the world.</param>
        public static Vec3 GravityInLocal(in RigidTransform localToWorld, Vec3 worldGravity)
        {
            return Vec3.Transform(worldGravity, Quat.Conjugate(localToWorld.Rotation));
        }

        /// <summary>
        /// A throw worked out in a space, carried into the world. Times are untouched. Positions are
        /// moved and turned; velocities and spin are only turned.
        /// </summary>
        public static ThrowResult ToWorld(in ThrowResult local, in RigidTransform localToWorld)
        {
            ThrowResult r = local;
            r.ReleasePosition = localToWorld.TransformPoint(local.ReleasePosition);
            r.NowPosition = localToWorld.TransformPoint(local.NowPosition);
            r.ReleaseVelocity = localToWorld.TransformDirection(local.ReleaseVelocity);
            r.NowVelocity = localToWorld.TransformDirection(local.NowVelocity);
            r.AngularVelocity = localToWorld.TransformDirection(local.AngularVelocity);

            r.Diagnostics.HandVelocity = localToWorld.TransformDirection(local.Diagnostics.HandVelocity);
            r.Diagnostics.LeverVelocity = localToWorld.TransformDirection(local.Diagnostics.LeverVelocity);
            r.Diagnostics.FitVelocity = localToWorld.TransformDirection(local.Diagnostics.FitVelocity);
            r.Diagnostics.PeakVelocity = localToWorld.TransformDirection(local.Diagnostics.PeakVelocity);
            return r;
        }

        /// <summary>A steady look found in a space, carried into the world.</summary>
        public static GazeFixation ToWorld(in GazeFixation local, in RigidTransform localToWorld)
        {
            GazeFixation f = local;
            f.Origin = localToWorld.TransformPoint(local.Origin);
            f.Direction = localToWorld.TransformDirection(local.Direction);
            return f;
        }
    }
}
