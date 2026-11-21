namespace Portaballs.Core
{
    /// <summary>
    /// A turn followed by a move, with no stretching and no mirroring. This is how anything is carried
    /// from one portal to the other: world point p becomes Rotation * p + Translation.
    /// </summary>
    public readonly struct RigidTransform
    {
        /// <summary>The turn. Always a unit quaternion.</summary>
        public readonly Quat Rotation;

        /// <summary>The move, applied after the turn.</summary>
        public readonly Vec3 Translation;

        public RigidTransform(Quat rotation, Vec3 translation)
        {
            Rotation = Quat.Normalize(rotation);
            Translation = translation;
        }

        /// <summary>Does nothing.</summary>
        public static RigidTransform Identity
        {
            get { return new RigidTransform(Quat.Identity, Vec3.Zero); }
        }

        /// <summary>Carries a position (a ball's centre, a hand, an eye).</summary>
        public Vec3 TransformPoint(Vec3 p)
        {
            return Vec3.Transform(p, Rotation) + Translation;
        }

        /// <summary>
        /// Carries a direction or anything that only turns and never moves: a facing direction, linear
        /// velocity, angular velocity (spin axis times speed), a surface normal.
        /// </summary>
        public Vec3 TransformDirection(Vec3 d)
        {
            return Vec3.Transform(d, Rotation);
        }

        /// <summary>Carries an object's orientation.</summary>
        public Quat TransformRotation(Quat r)
        {
            return Quat.Normalize(Rotation * r);
        }

        /// <summary>The transform that undoes this one.</summary>
        public RigidTransform Inverse()
        {
            Quat inv = Quat.Conjugate(Rotation);
            return new RigidTransform(inv, -Vec3.Transform(Translation, inv));
        }

        /// <summary>This transform first, then <paramref name="next"/>.</summary>
        public RigidTransform Then(in RigidTransform next)
        {
            return new RigidTransform(next.Rotation * Rotation, next.TransformPoint(Translation));
        }
    }
}
