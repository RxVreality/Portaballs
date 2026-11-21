using System;

namespace Portaballs.Core
{
    /// <summary>Where a held thing (a hand, the ball in it) is against the linked pair. Answer from <see cref="PortalReach"/>.</summary>
    public struct ReachState
    {
        /// <summary>
        /// True when it is at an opening: close enough to one end's wall to cut it with its footprint inside
        /// the opening, or through it. Then it is drawn a second time at the other end (its twin).
        /// </summary>
        public bool AtPortal;

        /// <summary>The end it is at (true = A, orange). The twin belongs at the other end.</summary>
        public bool FromA;

        /// <summary>Its centre's distance from that end's wall, metres: plus = still on the room side, minus = through.</summary>
        public float Depth;

        /// <summary>Wall coordinates (metres) under its centre on that end's wall.</summary>
        public Vec2 UV;

        /// <summary>Its centre is past the wall: it is really in the room the other end opens into.</summary>
        public bool Through
        {
            get { return AtPortal && Depth < 0f; }
        }
    }

    /// <summary>
    /// What is remembered about one hand between frames: once it has gone in through an opening it is in
    /// the far room, and stays there wherever it moves, until it comes back to the room side of that wall.
    /// </summary>
    public struct ReachLatch
    {
        public bool Through;
        public bool FromA;
    }

    /// <summary>Answer from <see cref="PortalReach.Launch"/>: where a ball let go at a portal really starts.</summary>
    public struct HeldLaunch
    {
        /// <summary>False = nowhere near an opening: an ordinary throw, nothing below was changed.</summary>
        public bool AtPortal;

        /// <summary>True when the ball's centre was through: position, velocity and spin below are on the far side.</summary>
        public bool Carried;

        /// <summary>The end it was at (true = A, orange).</summary>
        public bool FromA;

        /// <summary>The centre's distance from that end's wall at the let-go, metres (minus = through).</summary>
        public float Depth;

        public Vec3 Position;
        public Vec3 Velocity;
        public Vec3 Spin;
    }

    /// <summary>What a let-go ball is doing at an opening. Answer from <see cref="PortalReach.Where"/> and <see cref="PortalReach.Step"/>.</summary>
    public enum MouthOutcome
    {
        /// <summary>Not touching any opening: an ordinary ball from here on.</summary>
        Clear = 0,

        /// <summary>Cutting an opening's plane, or within the touch margin of it, with all of it over the opening. There is no wall there: keep flying it.</summary>
        InMouth = 1,

        /// <summary>It met the wall beside the opening, or the opening's edge. Hand it over as it is: it is a ball hitting a wall.</summary>
        OffPaint = 2,
    }

    /// <summary>A let-go ball that is still at an opening, flown without the physics engine (which would call the opening a wall).</summary>
    public struct MouthBall
    {
        /// <summary>Centre, world metres, always in the room the centre is really in.</summary>
        public Vec3 Position;
        public Vec3 Velocity;
        public Vec3 Spin;

        /// <summary>How many times its centre has been carried through since it was let go.</summary>
        public int Crossings;

        // ---- the latest of those crossings, for the log ----
        public bool LastFromA;
        public Vec2 EntryUV;
        public Vec3 PositionBefore;
        public Vec3 VelocityBefore;
        public Vec3 PositionAfter;
        public Vec3 VelocityAfter;
        public float ExitMovedBy;
    }

    /// <summary>
    /// A HAND at a portal. Hands have no physics, so they need three answers that balls get from
    /// <see cref="BallTransit"/>: is this hand in an opening (so it must be drawn at both ends), is it
    /// through it, and where does a ball start that is let go there. Works for any wall: nothing here
    /// knows which way is up except through the gravity it is handed. Nothing here allocates memory.
    ///
    /// WHICH CROSSING. A twin is drawn with the pair's rigid crossing and never slid along the exit wall
    /// (a ball in flight is slid onto the far patch when the patches differ in shape; a hand is wherever
    /// the arm puts it). <see cref="Crossing"/> picks between the full crossing, which balls travel by, and
    /// the upright player crossing; they are the same turn-and-move for every pair of standing walls.
    ///
    /// THE RULE for "in the opening": the paint under the hand, a rim's width around its centre, is all
    /// this end's paint, AND the same is true at the place that point comes out on the other wall. That is
    /// what the opening looks like on screen, and where the core lets the player's head through.
    /// </summary>
    public static class PortalReach
    {
        /// <summary>The opening starts this far inside the paint's edge, metres: the painted rim drawn round every opening.</summary>
        public const float OpeningRim = 0.04f;

        /// <summary>A hand further than this behind an opening's wall is not counted as through it, metres (longer than any arm).</summary>
        public const float ArmReach = 1.5f;

        /// <summary>The crossing a twin is drawn with, out of the given end (true = going in through A).</summary>
        /// <param name="playerCrossing">True = the upright crossing (a turn about world up only); false = the full crossing.</param>
        public static RigidTransform Crossing(in PortalPair pair, bool fromA, bool playerCrossing)
        {
            return playerCrossing ? pair.PlayerCrossing(fromA) : pair.Crossing(fromA);
        }

        /// <summary>
        /// Is this spot on one end's wall inside the opening? Paint of this end's colour all round it, and
        /// paint of the other end's colour all round the place it comes out.
        /// </summary>
        /// <param name="uv">Wall coordinates on the end named by <paramref name="fromA"/>, metres.</param>
        /// <param name="footprint">Radius of the circle that must be all paint, metres.</param>
        public static bool OpenAt(in PortalPair pair, IPaintMask maskA, IPaintMask maskB, bool fromA, Vec2 uv,
            float footprint, bool playerCrossing, PortalSettings settings)
        {
            PortalEnd entry = fromA ? pair.A : pair.B;
            PortalEnd exit = fromA ? pair.B : pair.A;
            IPaintMask entryMask = fromA ? maskA : maskB;
            IPaintMask exitMask = fromA ? maskB : maskA;
            if (!PortalCrossing.Passage(entry, entryMask, uv, footprint, settings).Fits) return false;

            Vec3 comesOut = Crossing(pair, fromA, playerCrossing).TransformPoint(entry.Wall.ToWorld(uv));
            return PortalCrossing.Passage(exit, exitMask, exit.Wall.ToUV(comesOut), footprint, settings).Fits;
        }

        /// <summary>
        /// Where is this point against the pair right now, with nothing remembered? At a portal when it is
        /// within <paramref name="reachFront"/> in front of an end's wall, or up to <paramref name="reachBehind"/>
        /// behind it, and over that end's opening. Costs two plane distances when it is not near either wall.
        /// </summary>
        /// <param name="centre">The thing's centre, world metres.</param>
        /// <param name="footprint">See <see cref="OpenAt"/>.</param>
        /// <param name="reachFront">How far in front of the wall still counts, metres (the thing's own radius, plus a little).</param>
        public static ReachState Find(in PortalPair pair, IPaintMask maskA, IPaintMask maskB, Vec3 centre,
            float footprint, float reachFront, float reachBehind, bool playerCrossing, PortalSettings settings)
        {
            var best = new ReachState();
            float bestAbs = float.MaxValue;
            for (int k = 0; k < 2; k++)
            {
                bool fromA = k == 0;
                PortalEnd end = fromA ? pair.A : pair.B;
                float d = end.Wall.SignedDistance(centre);
                if (d > reachFront || d < -reachBehind) continue;

                Vec2 uv = end.Wall.ToUV(centre);
                PortalOpening opening = end.Opening;
                if (uv.X < opening.BoundsMin.X || uv.X > opening.BoundsMax.X || uv.Y < opening.BoundsMin.Y || uv.Y > opening.BoundsMax.Y) continue;
                if (!OpenAt(pair, maskA, maskB, fromA, uv, footprint, playerCrossing, settings)) continue;

                float abs = MathF.Abs(d);
                if (abs >= bestAbs) continue;
                bestAbs = abs;
                best = new ReachState { AtPortal = true, FromA = fromA, Depth = d, UV = uv };
            }
            return best;
        }

        /// <summary>
        /// The same question for a hand that is followed from frame to frame. A hand that has gone in through
        /// an opening stays "through" wherever it then moves behind that wall (it is in the far room, and may
        /// reach sideways past the opening's outline there) until its centre is back on the room side.
        /// </summary>
        public static ReachState Update(ref ReachLatch latch, in PortalPair pair, IPaintMask maskA, IPaintMask maskB,
            Vec3 centre, float footprint, float reachFront, float reachBehind, bool playerCrossing, PortalSettings settings)
        {
            if (latch.Through)
            {
                PortalEnd end = latch.FromA ? pair.A : pair.B;
                float d = end.Wall.SignedDistance(centre);
                if (d < 0f && d >= -reachBehind)
                {
                    return new ReachState { AtPortal = true, FromA = latch.FromA, Depth = d, UV = end.Wall.ToUV(centre) };
                }
                latch.Through = false;
            }

            ReachState state = Find(pair, maskA, maskB, centre, footprint, reachFront, reachBehind, playerCrossing, settings);
            if (state.Through)
            {
                latch.Through = true;
                latch.FromA = state.FromA;
            }
            return state;
        }

        /// <summary>
        /// The player's head has just been carried through (the whole play space moved, hands included).
        /// A hand that trails behind the head is now behind the EXIT wall: it is still in the room the player
        /// left, reaching back through. A hand that led the way is now simply in the new room.
        /// </summary>
        /// <param name="playerWentInThroughA">The end the head went into.</param>
        /// <param name="centreAfter">The hand's centre after the play space was moved, world metres.</param>
        public static void PlayerCrossed(ref ReachLatch latch, in PortalPair pair, bool playerWentInThroughA,
            Vec3 centreAfter, float reachBehind)
        {
            PortalEnd exit = playerWentInThroughA ? pair.B : pair.A;
            float d = exit.Wall.SignedDistance(centreAfter);
            latch.Through = d < 0f && d >= -reachBehind;
            latch.FromA = !playerWentInThroughA;
        }

        /// <summary>
        /// A ball is let go by a hand. If its centre is through an opening, the launch is carried to the far
        /// side exactly as a ball in flight is carried: position by the pair's full crossing, velocity and
        /// spin turned by it, speed unchanged. No slide along the exit wall: the ball starts where its twin
        /// was being drawn.
        /// </summary>
        /// <param name="latch">The hand's latch (a copy: a let-go changes nothing about the hand).</param>
        /// <param name="position">Ball centre at the let-go, world metres.</param>
        /// <param name="radius">Ball radius, metres.</param>
        /// <param name="clearance">A ball whose skin is within this of an opening's plane counts as in its mouth, metres.</param>
        public static HeldLaunch Launch(ReachLatch latch, in PortalPair pair, IPaintMask maskA, IPaintMask maskB,
            Vec3 position, Vec3 velocity, Vec3 spin, float radius, float clearance, float reachBehind, PortalSettings settings)
        {
            ReachState state = Update(ref latch, pair, maskA, maskB, position, radius, radius + clearance, reachBehind, false, settings);
            var launch = new HeldLaunch
            {
                AtPortal = state.AtPortal,
                FromA = state.FromA,
                Depth = state.Depth,
                Position = position,
                Velocity = velocity,
                Spin = spin,
            };
            if (!state.Through) return launch;

            RigidTransform crossing = pair.Crossing(state.FromA);
            launch.Carried = true;
            launch.Position = crossing.TransformPoint(position);
            launch.Velocity = crossing.TransformDirection(velocity);
            launch.Spin = crossing.TransformDirection(spin);
            return launch;
        }

        /// <summary>Is a let-go ball still at an opening (so the physics engine must not have it yet)?</summary>
        /// <param name="clearance">The physics engine reports a touch from this far away, metres.</param>
        public static MouthOutcome Where(in MouthBall ball, in PortalPair pair, IPaintMask maskA, IPaintMask maskB,
            float radius, float clearance, PortalSettings settings)
        {
            MouthOutcome result = MouthOutcome.Clear;
            for (int k = 0; k < 2; k++)
            {
                PortalEnd end = k == 0 ? pair.A : pair.B;
                IPaintMask mask = k == 0 ? maskA : maskB;
                float d = end.Wall.SignedDistance(ball.Position);
                if (d < 0f || d >= radius + clearance) continue;

                Vec2 uv = end.Wall.ToUV(ball.Position);
                bool onPaint = mask != null && end.Wall.Contains(uv) && mask.IsPainted(end.Color, uv);
                if (!onPaint)
                {
                    // Beside the opening. Cutting the wall there = it has hit the wall; merely near it = an ordinary ball.
                    if (d < radius && end.Wall.Contains(uv)) return MouthOutcome.OffPaint;
                    continue;
                }
                if (!BallTransit.FootprintOnPaint(end, mask, ball.Position, radius, settings)) return MouthOutcome.OffPaint;
                result = MouthOutcome.InMouth;
            }
            return result;
        }

        /// <summary>
        /// One step of free flight for a let-go ball at an opening. If its centre passes an end's wall plane
        /// inside the opening during the step, it is carried across exactly as <see cref="BallTransit.Carry"/>
        /// carries a ball in flight. If it passes the plane anywhere else, the ball is left as it was before
        /// the step and the answer is OffPaint.
        /// </summary>
        public static MouthOutcome Step(ref MouthBall ball, in PortalPair pair, IPaintMask maskA, IPaintMask maskB,
            Vec3 gravity, float dt, float radius, float clearance, PortalSettings settings)
        {
            Vec3 next = BallTransit.At(ball.Position, ball.Velocity, gravity, dt);
            Vec3 velocityNext = ball.Velocity + gravity * dt;

            for (int k = 0; k < 2; k++)
            {
                bool fromA = k == 0;
                PortalEnd end = fromA ? pair.A : pair.B;
                IPaintMask mask = fromA ? maskA : maskB;
                CrossingResult hit = PortalCrossing.Detect(end, mask, ball.Position, next, settings);
                if (hit.Kind != CrossingKind.Entered) continue;
                if (!hit.OnWall) continue;   // past the end of the wall: not this wall's business
                if (!hit.Teleports || !PortalCrossing.Passage(end, mask, hit.UV, radius, settings).Fits) return MouthOutcome.OffPaint;

                PortalCarry carry = BallTransit.Carry(pair, fromA, false, fromA ? maskB : maskA, hit.Point, radius, settings);
                Vec3 velocityAtPlane = ball.Velocity + gravity * (dt * hit.T);
                ball.Crossings++;
                ball.LastFromA = fromA;
                ball.EntryUV = hit.UV;
                ball.PositionBefore = hit.Point;
                ball.VelocityBefore = velocityAtPlane;
                ball.PositionAfter = carry.Transform.TransformPoint(hit.Point);
                ball.VelocityAfter = carry.Transform.TransformDirection(velocityAtPlane);
                ball.ExitMovedBy = carry.MovedBy;

                ball.Position = carry.Transform.TransformPoint(next);
                ball.Velocity = carry.Transform.TransformDirection(velocityNext);
                ball.Spin = carry.Transform.TransformDirection(ball.Spin);
                return Where(ball, pair, maskA, maskB, radius, clearance, settings);
            }

            ball.Position = next;
            ball.Velocity = velocityNext;
            return Where(ball, pair, maskA, maskB, radius, clearance, settings);
        }

        /// <summary>
        /// Fly a let-go ball for a stretch of time in small steps (the catch-up between the moment of the
        /// let-go and the frame that noticed it). Stops early only when the ball meets a wall beside the opening.
        /// </summary>
        /// <param name="maxStep">Longest single step, seconds.</param>
        public static MouthOutcome Fly(ref MouthBall ball, in PortalPair pair, IPaintMask maskA, IPaintMask maskB,
            Vec3 gravity, float seconds, float maxStep, float radius, float clearance, PortalSettings settings)
        {
            MouthOutcome outcome = Where(ball, pair, maskA, maskB, radius, clearance, settings);
            if (outcome == MouthOutcome.OffPaint) return outcome;
            if (!(maxStep > 1e-4f)) maxStep = 1e-4f;

            float left = seconds;
            int guard = 0;
            while (left > 1e-6f && guard++ < 512)
            {
                float dt = left < maxStep ? left : maxStep;
                outcome = Step(ref ball, pair, maskA, maskB, gravity, dt, radius, clearance, settings);
                if (outcome == MouthOutcome.OffPaint) break;
                left -= dt;
            }
            return outcome;
        }
    }
}
