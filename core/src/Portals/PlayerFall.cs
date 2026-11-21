using System;

namespace Portaballs.Core
{
    /// <summary>
    /// The up-and-down part of the player's movement: falling to the floor, being thrown up out of a floor
    /// portal and coming down again, sinking into a hole in the floor, and being lifted when a crossing
    /// left the feet below the floor. Deliberately plain: gravity up to a top speed, no bounce; straight
    /// up at a fixed speed when lifted. Sideways movement is not its business (see
    /// <see cref="PlaySpaceWalker"/>, which owns the player's velocity).
    ///
    /// Feed it the feet height above the floor each frame (see <see cref="PlayerArrival"/>) and move the
    /// player by the difference it returns.
    /// </summary>
    public sealed class PlayerFall
    {
        /// <summary>Downward acceleration while in the air, m/s^2.</summary>
        public float Gravity = 9.81f;

        /// <summary>The fall never gets faster than this, m/s. Keeps a long drop (and an endless loop) steady.</summary>
        public float MaxFallSpeed = 6f;

        /// <summary>How fast feet that arrived below a solid floor are raised, m/s. 0 or less = at once.</summary>
        public float LiftSpeed = 3f;

        /// <summary>Heights closer to the floor than this (metres) count as standing on it.</summary>
        public float Tolerance = 1e-4f;

        /// <summary>
        /// Current downward speed, m/s: positive while falling, NEGATIVE while rising (thrown up out of a
        /// floor portal). 0 when standing or being lifted. The walker sets it after a portal crossing.
        /// </summary>
        public float Speed { get; set; }

        /// <summary>True while the last step left the player in the air (rising or falling).</summary>
        public bool Falling { get; private set; }

        /// <summary>True while the last step was a lift that has not finished yet.</summary>
        public bool Lifting { get; private set; }

        /// <summary>True when the last step brought the feet to rest on the floor out of the air.</summary>
        public bool JustLanded { get; private set; }

        /// <summary>Forget any fall in progress (the player was placed somewhere by hand).</summary>
        public void Reset()
        {
            Speed = 0f;
            Falling = false;
            Lifting = false;
            JustLanded = false;
        }

        /// <summary>
        /// Advance by one frame over a solid floor. Returns the new feet height above the floor (0 = standing on it).
        /// </summary>
        /// <param name="heightAboveFloor">Feet height now, metres: positive in the air, negative sunk in.</param>
        /// <param name="dt">Frame time, seconds.</param>
        public float Step(float heightAboveFloor, float dt)
        {
            return Step(heightAboveFloor, dt, false);
        }

        /// <summary>
        /// Advance by one frame. Returns the new feet height above the floor plane (0 = standing on it;
        /// negative = below it, which only lasts while <paramref name="noFloor"/> or while being lifted).
        /// </summary>
        /// <param name="heightAboveFloor">Feet height now, metres: positive in the air, negative below the floor plane.</param>
        /// <param name="dt">Frame time, seconds.</param>
        /// <param name="noFloor">
        /// True when there is no floor under the feet: they are over (or in) an open portal in the floor.
        /// Nothing to land on and nothing to be lifted to: the player just keeps falling (or rising) through
        /// the floor plane.
        /// </param>
        public float Step(float heightAboveFloor, float dt, bool noFloor)
        {
            if (dt < 0f) dt = 0f;
            JustLanded = false;
            float h = heightAboveFloor;

            // In the air: above the floor, over a hole, or on the way up from floor level.
            if (h > Tolerance || noFloor || (Speed < 0f && h >= -Tolerance))
            {
                Lifting = false;
                Speed += Gravity * dt;
                if (Speed > MaxFallSpeed) Speed = MaxFallSpeed;
                float next = h - Speed * dt;
                if (!noFloor && next <= 0f && Speed >= 0f)
                {
                    Speed = 0f;
                    JustLanded = true;
                    Falling = false;
                    return 0f;
                }
                Falling = true;
                return next;
            }

            Speed = 0f;
            Falling = false;

            if (h < -Tolerance)
            {
                if (LiftSpeed <= 0f)
                {
                    Lifting = false;
                    return 0f;
                }
                float next = h + LiftSpeed * dt;
                if (next >= 0f)
                {
                    Lifting = false;
                    JustLanded = true;
                    return 0f;
                }
                Lifting = true;
                return next;
            }

            Lifting = false;
            return 0f;
        }
    }
}
