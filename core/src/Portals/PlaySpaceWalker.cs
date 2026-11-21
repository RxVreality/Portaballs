using System;

namespace Portaballs.Core
{
    /// <summary>What <see cref="PlaySpaceWalker.Update"/> did in one frame.</summary>
    public struct PlaySpaceFrame
    {
        /// <summary>Head position in the world after everything, metres.</summary>
        public Vec3 Head;

        /// <summary>+1 = turned right this frame (one snap turn, or any smooth turning), -1 = left, 0 = no turn.</summary>
        public int Turned;

        /// <summary>How far the play space was turned this frame, degrees: positive to the right.</summary>
        public float TurnedDegrees;

        /// <summary>The stick movement actually applied this frame, after the walls, metres.</summary>
        public Vec3 Moved;

        /// <summary>True when a wall (or the rim of a hole the body is in) held back part of the stick movement.</summary>
        public bool Blocked;

        /// <summary>True when the head went through a linked patch this frame and the play space was carried across.</summary>
        public bool Crossed;

        /// <summary>
        /// When crossed: true = the head was carried through by flying or falling (its own velocity), false =
        /// by walking with the stick or by the real person moving their head.
        /// </summary>
        public bool CrossedFlying;

        /// <summary>The details of that crossing.</summary>
        public PlayerStep Crossing;

        /// <summary>Head position just before the crossing was applied, metres.</summary>
        public Vec3 HeadBeforeCrossing;

        /// <summary>When crossed: the player's velocity just before the crossing, and what it became (m/s, world).</summary>
        public Vec3 VelocityBeforeCrossing;
        public Vec3 VelocityAfterCrossing;

        /// <summary>
        /// How far the head rose (+) or dropped (-) this frame from flying, falling or being lifted, metres.
        /// For a crossing made by walking it happened after the crossing; for one made by falling, before it.
        /// </summary>
        public float Dropped;

        /// <summary>The player's own velocity after the frame, m/s, world. Zero while standing on the floor.</summary>
        public Vec3 Velocity;

        /// <summary>True while the player is in the air (flying, falling, rising) or being lifted, after the frame.</summary>
        public bool InAir;

        /// <summary>True when there was no floor under the body this frame: it is over, or in, an open portal in the floor.</summary>
        public bool OverHole;

        /// <summary>True in the frame the feet came to rest on the floor.</summary>
        public bool Landed;

        /// <summary>How far the play space was turned toward level this frame, radians.</summary>
        public float Righted;

        /// <summary>How far the play space is still tipped over after the frame, radians. 0 = level.</summary>
        public float Tilt;
    }

    /// <summary>
    /// Everything that moves the player's play space, in one place and with no engine in it: stick walking
    /// (relative to the head, stopped by walls), turning (smooth or snap, about the head), being carried
    /// through a linked patch when the HEAD passes through it, flying and falling with a real velocity,
    /// and being turned upright again after a crossing that tipped the play space over.
    /// The VR rig and the desk camera both run this.
    ///
    /// THE BODY. The head is where the tracker (or the desk camera) says. The body is the upright line from
    /// the head straight down by the head's own height in the play space (headLocal.Z), ending at the
    /// feet. On a solid floor the feet rest on it. Where the body is over a linked patch in the floor big
    /// enough for it (<see cref="BodyRadius"/>) there is no floor: it falls in, and when the head passes
    /// the floor plane the play space is carried across.
    ///
    /// THROUGH A FLOOR OR CEILING, LIKE PORTAL. The crossing is the pair's rigid mapping
    /// (<see cref="PortalPair.PlayerAToB"/>), applied in one frame to the play space (so to the head) and
    /// to the velocity: fall into the floor, fly out of a wall sideways at the same speed. That leaves
    /// the play space tipped over; it is then turned about the head, by the smallest turn that brings its
    /// up back to world up, over <see cref="RightingSeconds"/> (0 = in the crossing frame). Coming up out
    /// of a floor the player is thrown at least high enough to get the feet clear
    /// (<see cref="FloorExitRise"/>) and can steer with the walking stick (<see cref="AirSteer"/>).
    /// Between two walls nothing here is different from before: no tip, no righting, a plain drop.
    ///
    /// Each frame: set <see cref="Anchor"/> if something else moved the play space, call
    /// <see cref="Update"/> with the head's pose inside the play space and the two sticks, then put the
    /// play space at <see cref="Anchor"/>. Hands and anything else kept relative to the anchor follow.
    /// </summary>
    public sealed class PlaySpaceWalker
    {
        /// <summary>Walking speed with the stick fully pushed, m/s.</summary>
        public float MoveSpeed = 2f;

        /// <summary>True (the default, the tester 2026-11-19) = the turning stick turns smoothly; false = snap turns.</summary>
        public bool SmoothTurn = true;

        /// <summary>Smooth turning: speed with the stick fully over, degrees a second.</summary>
        public float SmoothTurnDegreesPerSecond = 120f;

        /// <summary>Smooth turning: stick deflection (0 to 1) below which the turning stick is ignored.</summary>
        public float TurnDeadZone = 0.2f;

        /// <summary>Snap turning: size of one turn, degrees.</summary>
        public float SnapTurnDegrees = 30f;

        /// <summary>Stick deflection (0 to 1) below which the walking stick is ignored.</summary>
        public float DeadZone = 0.15f;

        /// <summary>Radius of the head for "does it fit through the patch", metres.</summary>
        public float HeadRadius = 0.10f;

        /// <summary>How far solid wall keeps the head's centre away when walking with the stick, metres.</summary>
        public float WallClearance = 0.20f;

        /// <summary>
        /// Radius of the body, metres. A patch in the floor is a hole for the player when a circle this big
        /// around the point under the head is all its paint: that decides both "big enough" and "over it".
        /// </summary>
        public float BodyRadius = 0.20f;

        /// <summary>
        /// How long the play space takes to swing upright after a crossing tipped it over, seconds.
        /// 0 = at once, in the crossing frame. The head does not move while it turns.
        /// </summary>
        public float RightingSeconds = 0.25f;

        /// <summary>
        /// Coming UP out of a portal in the floor, the player is thrown at least fast enough for the feet to
        /// rise this far above the floor, metres (as Portal gives a minimum exit speed out of floor portals),
        /// so they can steer off the hole instead of dropping straight back in. Less than 0 = no minimum.
        /// </summary>
        public float FloorExitRise = 0.4f;

        /// <summary>How much of the walking speed the walking stick gives while in the air (0 to 1).</summary>
        public float AirSteer = 1f;

        /// <summary>
        /// Landing with sideways speed (flung out of a wall), the player slides to a stop, losing this much
        /// speed a second, m/s^2. 0 or less = stops dead on landing.
        /// </summary>
        public float SlideFriction = 15f;

        /// <summary>Gravity, the top fall speed and the lift speed; and the up-and-down state.</summary>
        public readonly PlayerFall Fall = new PlayerFall();

        /// <summary>The snap-turn trigger (its thresholds are settings too).</summary>
        public readonly SnapTurnLatch TurnLatch = new SnapTurnLatch();

        /// <summary>Where the play space is: carries a point of the tracked space into the world.</summary>
        public RigidTransform Anchor = RigidTransform.Identity;

        /// <summary>
        /// The player's own velocity, m/s, world: what falling, a crossing and the pop out of a floor gave
        /// it. Walking with the stick is not in it. Zero while standing on the floor.
        /// </summary>
        public Vec3 Velocity
        {
            get { return _velocity; }
        }

        /// <summary>True while the player is in the air (flying, falling, rising) or being lifted.</summary>
        public bool InAir
        {
            get { return Fall.Falling || Fall.Lifting; }
        }

        /// <summary>How far the play space is tipped over right now, radians. 0 = level.</summary>
        public float Tilt
        {
            get { return PlaySpace.TiltOf(Anchor.Rotation); }
        }

        /// <summary>True while the play space is still being turned upright after a crossing.</summary>
        public bool Righting
        {
            get { return Tilt > LevelEnough; }
        }

        /// <summary>A play space tipped by less than this (radians) counts as level and is left alone.</summary>
        public const float LevelEnough = 1e-4f;

        private bool _hasPreviousHead;
        private Vec3 _previousHead;
        private Vec3 _velocity;
        private float _rightingRate;                 // radians a second while swinging upright
        private Vec3 _upsideDownAxis = new Vec3(1f, 0f, 0f);

        /// <summary>Forget last frame's head position (after placing the player somewhere by hand).</summary>
        public void ForgetHead()
        {
            _hasPreviousHead = false;
        }

        /// <summary>The player was placed somewhere by hand: forget the head, any velocity, fall or righting in progress.</summary>
        public void Reset()
        {
            _hasPreviousHead = false;
            _velocity = Vec3.Zero;
            _rightingRate = 0f;
            Fall.Reset();
            TurnLatch.Reset();
        }

        /// <summary>Give the player a velocity of their own, m/s, world (a script or a test; a crossing does it by itself).</summary>
        public void SetVelocity(Vec3 velocity)
        {
            _velocity = velocity;
            Fall.Speed = -velocity.Z;
        }

        /// <summary>
        /// Upward speed (m/s) that carries a head starting at floor level high enough for feet
        /// <paramref name="bodyHeight"/> below it to rise <see cref="FloorExitRise"/> above the floor. 0 when switched off.
        /// </summary>
        public float FloorExitSpeed(float bodyHeight)
        {
            if (FloorExitRise < 0f) return 0f;
            float rise = MathF.Max(0f, bodyHeight) + FloorExitRise;
            return MathF.Sqrt(2f * MathF.Max(0f, Fall.Gravity) * rise);
        }

        /// <summary>
        /// Turn the play space about the upright line through the head (the desk camera's mouse uses this;
        /// <see cref="Update"/> does it for the turning stick).
        /// </summary>
        /// <param name="headLocal">Head position inside the play space, metres.</param>
        /// <param name="rightRadians">How far to turn, radians: positive to the right (clockwise seen from above).</param>
        public void Turn(Vec3 headLocal, float rightRadians)
        {
            if (rightRadians == 0f) return;
            Vec3 head = Anchor.TransformPoint(headLocal);
            Anchor = Anchor.Then(PlaySpace.TurnAbout(head, -rightRadians));
        }

        /// <summary>One frame, driven by the two sticks.</summary>
        /// <param name="headLocal">Head position inside the play space, metres.</param>
        /// <param name="headLocalRotation">Head orientation inside the play space.</param>
        /// <param name="moveStick">Walking stick: X = right, Y = forward, each -1 to 1.</param>
        /// <param name="turnStickX">Turning stick's sideways value, -1 (left) to 1 (right).</param>
        /// <param name="dt">Frame time, seconds.</param>
        /// <param name="floorHeight">World Z of the room's floor.</param>
        /// <param name="hasPair">False when nothing is linked.</param>
        /// <param name="walls">The room's surfaces: walls, and the floor and ceiling if they can hold a portal.</param>
        public PlaySpaceFrame Update(Vec3 headLocal, Quat headLocalRotation, Vec2 moveStick, float turnStickX,
            float dt, float floorHeight, bool hasPair, in PortalPair pair, IPaintMask maskA, IPaintMask maskB,
            WallFrame[] walls, int wallCount, PortalSettings settings)
        {
            if (dt < 0f) dt = 0f;

            // 1. Turning, about the head so the player turns on the spot.
            int turned;
            float degrees;
            if (SmoothTurn)
            {
                degrees = PlaySpace.SmoothTurnRate(turnStickX, TurnDeadZone) * SmoothTurnDegreesPerSecond * dt;
                turned = degrees > 0f ? 1 : degrees < 0f ? -1 : 0;
                TurnLatch.Reset();
            }
            else
            {
                turned = TurnLatch.Update(turnStickX);
                degrees = turned * SnapTurnDegrees;
            }
            Turn(headLocal, degrees * (MathF.PI / 180f));      // right = clockwise = negative yaw

            // 2. Stick walking, relative to where the head faces now.
            float headYaw = PlaySpace.YawOf(Anchor.TransformRotation(headLocalRotation));
            Vec3 wish = PlaySpace.StickMove(moveStick, headYaw, MoveSpeed, dt, DeadZone);

            PlaySpaceFrame frame = Move(headLocal, headLocalRotation, wish, MoveSpeed * dt, dt, floorHeight, hasPair,
                pair, maskA, maskB, walls, wallCount, settings);
            frame.Turned = turned;
            frame.TurnedDegrees = degrees;
            return frame;
        }

        /// <summary>
        /// One frame, driven by a movement already worked out (the desk camera's keys, a scripted walk):
        /// everything <see cref="Update"/> does except reading the sticks and turning.
        /// </summary>
        /// <param name="wish">Where the player wants to walk this frame, metres, world, level (as from <see cref="PlaySpace.StickMove"/>).</param>
        /// <param name="maxPushOut">How far a wall the head is too near may move it out this frame, metres (speed times frame time).</param>
        public PlaySpaceFrame Move(Vec3 headLocal, Quat headLocalRotation, Vec3 wish, float maxPushOut, float dt,
            float floorHeight, bool hasPair, in PortalPair pair, IPaintMask maskA, IPaintMask maskB,
            WallFrame[] walls, int wallCount, PortalSettings settings)
        {
            var frame = new PlaySpaceFrame();
            if (dt < 0f) dt = 0f;
            Vec3 head = Anchor.TransformPoint(headLocal);
            // The feet are this far below the head. Not clamped: with a level play space the feet are then
            // always exactly on the play space's own floor, whatever the tracker says the head is doing.
            float body = headLocal.Z;
            Vec3 v = _velocity;
            bool crossed = false;

            // 1. Walking. Walls stop it; a linked patch does not. In the air the stick only steers.
            if (Fall.Falling || Fall.Lifting) wish *= MathUtil.Saturate(AirSteer);
            if (wish != Vec3.Zero)
            {
                bool blocked;
                Vec3 allowed = PlayerPassage.ResolveWalls(hasPair, pair, maskA, maskB, walls, wallCount, head,
                    head + wish, HeadRadius, WallClearance, maxPushOut, settings, out blocked);
                if (HeldByHole(hasPair, pair, maskA, maskB, head, ref allowed, body, floorHeight, settings)) blocked = true;
                frame.Blocked = blocked;
                frame.Moved = allowed - head;
                Anchor = Anchor.Then(PlaySpace.Shift(frame.Moved));
                head = allowed;
            }

            // 2. The portal: did the head (moved by the stick or by the real person) pass through a linked patch?
            if (hasPair && _hasPreviousHead
                && PlayerPassage.TryCross(pair, maskA, maskB, _previousHead, head, HeadRadius, settings, out PlayerStep step, BodyRadius))
            {
                Cross(ref frame, step, pair, headLocalRotation, body, ref head, ref v, false);
                crossed = true;
            }

            // 3. The player's own velocity (only ever there in the air), then up and down: falling, rising,
            //    sinking into a hole in the floor, being lifted out of a solid one.
            Vec3 before = head;
            Vec3 fly = new Vec3(v.X, v.Y, 0f) * dt;
            bool meetsWalls = false;
            if (fly != Vec3.Zero)
            {
                Vec3 target = head + fly;
                if (HeldByHole(hasPair, pair, maskA, maskB, head, ref target, body, floorHeight, settings))
                {
                    v.X = 0f;
                    v.Y = 0f;
                }
                else
                {
                    meetsWalls = true;
                }
                head = target;
            }

            float height = head.Z - body - floorHeight;
            bool noFloor = PlayerPassage.FloorIsOpenUnder(hasPair, pair, maskA, maskB, head, floorHeight, BodyRadius, settings);
            frame.OverHole = noFloor;
            Fall.Speed = -v.Z;
            float newHeight = Fall.Step(height, dt, noFloor);
            v.Z = -Fall.Speed;
            head.Z += newHeight - height;
            if (v.Z > 0f) meetsWalls = true;                // rising: the ceiling is a wall for the head

            // A plain drop never touches the walls (as before). Flying sideways or upward does.
            if (meetsWalls)
            {
                float speed = MathF.Sqrt(v.X * v.X + v.Y * v.Y) + MathF.Max(0f, v.Z);
                bool blocked;
                Vec3 allowed = PlayerPassage.ResolveWalls(hasPair, pair, maskA, maskB, walls, wallCount, before, head,
                    HeadRadius, WallClearance, maxPushOut + speed * dt, settings, out blocked);
                if (blocked)
                {
                    // Whatever part of the velocity ran into the wall is gone; the rest carries on.
                    Vec3 push = allowed - head;
                    float len = push.Length();
                    if (len > 1e-7f)
                    {
                        Vec3 n = push / len;
                        float into = Vec3.Dot(v, n);
                        if (into < 0f) v -= n * into;
                    }
                    head = allowed;
                    Fall.Speed = -v.Z;
                }
            }

            Vec3 airMove = head - before;
            if (airMove != Vec3.Zero) Anchor = Anchor.Then(PlaySpace.Shift(airMove));
            frame.Dropped = airMove.Z;

            // On the floor: nothing up or down, and any sideways speed left from a fling slides to a stop.
            // (While being lifted the sideways part carries on.)
            if (!Fall.Falling && !Fall.Lifting)
            {
                frame.Landed = Fall.JustLanded;
                v.Z = 0f;
                float level = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
                float left = SlideFriction > 0f ? level - SlideFriction * dt : 0f;
                if (left <= 0f || level <= 0f)
                {
                    v = Vec3.Zero;
                }
                else
                {
                    v.X *= left / level;
                    v.Y *= left / level;
                }
            }

            // 4. The portal again: did flying or falling carry the head through? At most one crossing a frame.
            if (!crossed && hasPair && airMove != Vec3.Zero
                && PlayerPassage.TryCross(pair, maskA, maskB, before, head, HeadRadius, settings, out PlayerStep flown, BodyRadius))
            {
                Cross(ref frame, flown, pair, headLocalRotation, body, ref head, ref v, true);
                crossed = true;
            }

            // 5. Upright again. The crossing itself is rigid (the view through the portal is that same
            //    mapping); this comes after it, turns about the head, and ends exactly level.
            float tilt = PlaySpace.TiltOf(Anchor.Rotation);
            if (tilt > LevelEnough)
            {
                float turn;
                if (RightingSeconds <= 0f) turn = tilt;
                else if (crossed)
                {
                    // The crossing frame shows exactly the rigid mapping; the swing starts next frame.
                    _rightingRate = tilt / RightingSeconds;
                    turn = 0f;
                }
                else
                {
                    float rate = _rightingRate > 0f ? _rightingRate : (MathF.PI * 0.5f) / RightingSeconds;
                    turn = MathF.Min(tilt, rate * dt);
                }

                if (turn > 0f)
                {
                    Anchor = Anchor.Then(PlaySpace.RightAbout(Anchor.Rotation, head, turn, _upsideDownAxis));
                    frame.Righted = turn;
                    tilt -= turn;
                    if (tilt <= LevelEnough)
                    {
                        // Exactly level, facing the same way, the head exactly where it was.
                        Quat level = PlaySpace.Levelled(Anchor.Rotation);
                        Anchor = new RigidTransform(level, head - Vec3.Transform(headLocal, level));
                        _rightingRate = 0f;
                        tilt = 0f;
                    }
                }
                frame.Tilt = tilt;
            }

            _velocity = v;
            _previousHead = head;
            _hasPreviousHead = true;
            frame.Head = head;
            frame.Velocity = v;
            frame.InAir = Fall.Falling || Fall.Lifting;
            return frame;
        }

        /// <summary>
        /// Carry the play space and the velocity through the pair, and give the pop out of a floor.
        /// </summary>
        private void Cross(ref PlaySpaceFrame frame, in PlayerStep step, in PortalPair pair, Quat headLocalRotation,
            float body, ref Vec3 head, ref Vec3 v, bool flying)
        {
            frame.Crossed = true;
            frame.CrossedFlying = flying;
            frame.Crossing = step;
            frame.HeadBeforeCrossing = head;
            frame.VelocityBeforeCrossing = v;

            Anchor = Anchor.Then(step.Crossing);
            head = step.Head;
            v = step.Crossing.TransformDirection(v);           // same speed, mapped direction

            // Up out of a floor: at least fast enough to get the feet clear of it.
            PortalEnd exit = step.FromA ? pair.B : pair.A;
            float top = Fall.MaxFallSpeed;
            if (exit.Wall.IsFloor)
            {
                float pop = FloorExitSpeed(body);
                float outward = Vec3.Dot(v, exit.Normal);
                if (outward < pop) v += exit.Normal * (pop - outward);
                if (pop > top) top = pop;
            }

            // The top speed holds through a portal too: sideways and upward (downward is the fall's own limit).
            float level = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
            if (level > Fall.MaxFallSpeed && level > 0f)
            {
                float k = Fall.MaxFallSpeed / level;
                v.X *= k;
                v.Y *= k;
            }
            if (v.Z > top) v.Z = top;
            if (v.Z < -Fall.MaxFallSpeed) v.Z = -Fall.MaxFallSpeed;

            frame.VelocityAfterCrossing = v;
            Fall.Speed = -v.Z;

            // Upside down afterwards (floor to floor), every level axis rights it equally fast. Turning back
            // about the crossing's own axis undoes the crossing's turn, so the player faces the way they did.
            Quat q = step.Crossing.Rotation;
            if (q.X * q.X + q.Y * q.Y > 1e-8f) _upsideDownAxis = new Vec3(q.X, q.Y, 0f);
            else
            {
                Vec3 forward = Vec3.Transform(new Vec3(1f, 0f, 0f), Anchor.TransformRotation(headLocalRotation));
                _upsideDownAxis = forward.X * forward.X + forward.Y * forward.Y > 1e-6f ? new Vec3(forward.X, forward.Y, 0f) : new Vec3(1f, 0f, 0f);
            }
        }

        /// <summary>
        /// A body that is part-way down a hole in the floor (feet below the floor plane) cannot move
        /// sideways off the hole: the floor around it is solid. Puts the move's X and Y back when it would.
        /// </summary>
        private bool HeldByHole(bool hasPair, in PortalPair pair, IPaintMask maskA, IPaintMask maskB, Vec3 head,
            ref Vec3 wanted, float body, float floorHeight, PortalSettings settings)
        {
            if (!hasPair) return false;
            if (wanted.X == head.X && wanted.Y == head.Y) return false;
            if (head.Z - body - floorHeight >= -Fall.Tolerance) return false;                       // feet not below the floor
            if (!PlayerPassage.FloorIsOpenUnder(hasPair, pair, maskA, maskB, head, floorHeight, BodyRadius, settings)) return false;
            if (PlayerPassage.FloorIsOpenUnder(hasPair, pair, maskA, maskB, wanted, floorHeight, BodyRadius, settings)) return false;
            wanted.X = head.X;
            wanted.Y = head.Y;
            return true;
        }
    }
}
