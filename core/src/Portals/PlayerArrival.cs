using System;

namespace Portaballs.Core
{
    /// <summary>
    /// Where the player's feet ended up after the crossing, compared with the exit room's floor.
    ///
    /// With paint-centre anchoring (the default, as in Portal) the player keeps their height relative to the
    /// patch centre, not to the floor. Coming out of a patch that is higher than the one they went into leaves
    /// them in the air (they fall, which is wanted); coming out of a lower one would sink their feet into the
    /// floor, so the game must lift them by <see cref="LiftNeeded"/>.
    /// </summary>
    public readonly struct PlayerArrival
    {
        /// <summary>Feet height above the exit floor in metres: positive = in the air, negative = sunk in.</summary>
        public readonly float HeightAboveFloor;

        /// <summary>How far the player will fall (metres). 0 when on or below the floor.</summary>
        public float FallDistance
        {
            get { return MathF.Max(HeightAboveFloor, 0f); }
        }

        /// <summary>How far the game must lift the player so their feet are on the floor. 0 when on or above it.</summary>
        public float LiftNeeded
        {
            get { return MathF.Max(-HeightAboveFloor, 0f); }
        }

        /// <summary>The feet position after lifting, if a lift was needed (otherwise the mapped position).</summary>
        public readonly Vec3 Feet;

        private PlayerArrival(float heightAboveFloor, Vec3 feet)
        {
            HeightAboveFloor = heightAboveFloor;
            Feet = feet;
        }

        /// <summary>
        /// Compare the feet position (already carried through <see cref="PortalPair.PlayerAToB"/> or
        /// <see cref="PortalPair.PlayerBToA"/>) with the world Z of the exit room's floor.
        /// </summary>
        public static PlayerArrival Check(Vec3 mappedFeet, float destinationFloorHeight)
        {
            float h = mappedFeet.Z - destinationFloorHeight;
            Vec3 feet = h < 0f ? new Vec3(mappedFeet.X, mappedFeet.Y, destinationFloorHeight) : mappedFeet;
            return new PlayerArrival(h, feet);
        }
    }
}
