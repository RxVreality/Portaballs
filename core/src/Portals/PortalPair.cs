using System;

namespace Portaballs.Core
{
    /// <summary>
    /// Two linked portal ends and the maths that carries things between them.
    ///
    /// Going in through A (moving against A's normal, into the wall) comes out of B (moving along B's
    /// normal, out into B's room). Picture standing in doorway A facing out: you are carried to doorway B,
    /// turned half way round about B's up axis so you now face out of B. It is a real turn, never a mirror
    /// image, so your left hand stays your left hand.
    /// </summary>
    public readonly struct PortalPair
    {
        public readonly PortalEnd A;
        public readonly PortalEnd B;

        /// <summary>Full crossing from A to B, for balls, props and hands: carries tilt from leaning walls too.</summary>
        public readonly RigidTransform AToB;

        /// <summary>Full crossing from B to A. Exactly undoes <see cref="AToB"/>.</summary>
        public readonly RigidTransform BToA;

        /// <summary>
        /// Crossing for the player (the head, the play space, the desk camera, and the see-through view).
        ///
        /// Two walls: only a turn about world up plus the move, so the player is never pitched or rolled,
        /// even when a wall leans. Same as <see cref="AToB"/> when both walls stand straight.
        ///
        /// When either end is on a FLOOR or a CEILING it is the full rigid crossing, <see cref="AToB"/>
        /// itself, exactly as in Portal: fall into a floor and you come out of a wall lying on your side.
        /// That tips the player over (<see cref="PlayerKeepsUpright"/> is false); the play space is turned
        /// upright again AFTER the crossing by <see cref="PlaySpaceWalker"/> (its RightingSeconds), never by
        /// this transform, so what a portal shows and what happens at the crossing are the same mapping.
        ///
        /// The move is rigid between the two frame positions, so with the default paint-centre anchor a
        /// player can arrive above or below the exit room's floor: use <see cref="PlayerArrival.Check"/>.
        /// </summary>
        public readonly RigidTransform PlayerAToB;

        /// <summary>Player crossing from B to A. Exactly undoes <see cref="PlayerAToB"/>.</summary>
        public readonly RigidTransform PlayerBToA;

        /// <summary>
        /// True when the player crossing keeps world up pointing up (two walls; or a floor to a ceiling).
        /// False when it tips the player over (a floor or ceiling to a wall, floor to floor, ceiling to
        /// ceiling): the play space then has to be righted after the crossing.
        /// </summary>
        public readonly bool PlayerKeepsUpright;

        /// <summary>
        /// True when the full crossing keeps world up pointing up (both walls vertical, or leaning the same
        /// way). When false, use the player version for the player.
        /// </summary>
        public readonly bool KeepsUpright;

        /// <summary>Half a turn about the frame's up axis (Z): forward becomes backward, left becomes right.</summary>
        public static readonly Quat HalfTurn = new Quat(0f, 0f, 1f, 0f);

        public PortalPair(in PortalEnd a, in PortalEnd b)
        {
            A = a;
            B = b;
            AToB = Between(a.Rotation, a.Position, b.Rotation, b.Position);
            BToA = AToB.Inverse();
            // A floor or a ceiling at either end: the player is carried rigidly, like everything else.
            bool flat = a.Wall.IsFlat || b.Wall.IsFlat;
            PlayerAToB = flat ? AToB : Between(a.UprightRotation, a.Position, b.UprightRotation, b.Position);
            PlayerBToA = PlayerAToB.Inverse();

            Vec3 up = AToB.TransformDirection(Units.Up);
            KeepsUpright = (up - Units.Up).LengthSquared() < 1e-8f;
            Vec3 playerUp = PlayerAToB.TransformDirection(Units.Up);
            PlayerKeepsUpright = (playerUp - Units.Up).LengthSquared() < 1e-8f;
        }

        /// <summary>
        /// Into frame "from" (as frame-local coordinates), half a turn, then out of frame "to".
        /// Rotation = to * halfTurn * inverse(from); a point at "from" lands at "to".
        /// </summary>
        private static RigidTransform Between(Quat fromRot, Vec3 fromPos, Quat toRot, Vec3 toPos)
        {
            Quat r = Quat.Normalize(toRot * HalfTurn * Quat.Conjugate(fromRot));
            return new RigidTransform(r, toPos - Vec3.Transform(fromPos, r));
        }

        /// <summary>The crossing out of the given end (true = going in through A).</summary>
        public RigidTransform Crossing(bool fromA)
        {
            return fromA ? AToB : BToA;
        }

        /// <summary>The player crossing out of the given end (true = going in through A).</summary>
        public RigidTransform PlayerCrossing(bool fromA)
        {
            return fromA ? PlayerAToB : PlayerBToA;
        }
    }
}
