using System;

namespace Portaballs.Core
{
    /// <summary>What a flying ball is about to do at one portal end. Answer from <see cref="BallTransit.Approach"/>.</summary>
    public struct BallApproach
    {
        /// <summary>The ball's skin reaches the wall plane within the look-ahead, moving into the wall from the room side.</summary>
        public bool Touches;

        /// <summary>It touches, and its whole footprint on the wall, from first touch until it is through, lies on this end's paint.</summary>
        public bool Enters;

        /// <summary>Seconds from now until the ball's skin first reaches the wall plane (0 when it already overlaps it).</summary>
        public float TouchTime;

        /// <summary>Seconds from now until the ball's centre is on the wall plane.</summary>
        public float CrossTime;

        /// <summary>Seconds from now until the whole ball is behind the wall plane.</summary>
        public float ClearTime;

        /// <summary>Wall coordinates (metres) under the ball's centre at those three moments.</summary>
        public Vec2 TouchUV;
        public Vec2 CrossUV;
        public Vec2 ClearUV;

        /// <summary>The paint under the ball's centre at first touch and when it is fully through.</summary>
        public bool TouchPainted;
        public bool ClearPainted;

        /// <summary>The full-size footprint test at the moment the centre is on the plane.</summary>
        public PassageResult Footprint;
    }

    /// <summary>Answer from <see cref="BallTransit.Carry"/>: how to carry something through, exit placement included.</summary>
    public struct PortalCarry
    {
        /// <summary>
        /// The crossing to apply to positions, velocities, spins and orientations. It is the pair's own rigid
        /// crossing, plus a sideways slide along the exit wall when the exit patch has no paint where the
        /// rigid crossing lands (see <see cref="MovedBy"/>).
        /// </summary>
        public RigidTransform Transform;

        /// <summary>How far (metres) the exit point was slid along the exit wall. 0 = the plain rigid crossing.</summary>
        public float MovedBy;

        /// <summary>False when the object is wider than the exit patch's largest circle.</summary>
        public bool Fits;

        /// <summary>True when the rigid crossing landed on the exit patch's paint with room for the whole object.</summary>
        public bool LandedOnPaint;
    }

    /// <summary>
    /// A paint ball meeting a linked portal: will it go in, is it still inside the opening while it passes,
    /// and where and how does it come out. Uses <see cref="PortalCrossing"/> for the paint tests. Nothing here
    /// allocates memory.
    ///
    /// THE RULE for "enters": the ball goes through only when its whole footprint on the wall is on the
    /// linked paint, from the moment its skin first touches the wall plane until it is completely through.
    /// That is three checks along its path: the point under its centre at first touch, the full-size circle
    /// (centre plus outline samples) when its centre is on the plane, and the point under its centre when
    /// its skin leaves the plane. A ball that clips the edge of the patch fails this and hits the wall.
    /// </summary>
    public static class BallTransit
    {
        /// <summary>
        /// Look ahead along the ball's free flight (position + velocity * t + gravity * t * t / 2) and say
        /// whether it reaches this end's wall within <paramref name="lookAhead"/> seconds and whether it
        /// would go through the opening.
        /// </summary>
        /// <param name="end">The portal end to test.</param>
        /// <param name="mask">The paint on that end's wall.</param>
        /// <param name="position">Ball centre now, world metres.</param>
        /// <param name="velocity">Ball velocity now, m/s.</param>
        /// <param name="gravity">World gravity, m/s^2.</param>
        /// <param name="radius">Ball radius, metres.</param>
        /// <param name="lookAhead">Only a touch within this many seconds counts.</param>
        /// <param name="minCenterHeight">
        /// The ball's centre cannot be lower than this world Z (floor height plus the ball's radius): free
        /// flight would sink a ball that is rolling or resting on the floor into it. Leave out for none.
        /// </param>
        /// <param name="nearMargin">
        /// Measure the look-ahead to the moment the ball's skin comes within this many metres of the wall,
        /// instead of to the touch itself. For a physics engine that reports a touch a little before the
        /// real one (a slow ball would otherwise be "touching" before the look-ahead sees it coming).
        /// The touch, crossing and footprint that are reported are still the real ones. 0 = none.
        /// </param>
        public static BallApproach Approach(in PortalEnd end, IPaintMask mask, Vec3 position, Vec3 velocity,
            Vec3 gravity, float radius, float lookAhead, PortalSettings settings,
            float minCenterHeight = float.NegativeInfinity, float nearMargin = 0f)
        {
            var result = new BallApproach();
            WallFrame wall = end.Wall;
            float d0 = wall.SignedDistance(position);
            if (d0 < 0f) return result;                      // already behind the wall: not coming in from the room

            float vn = Vec3.Dot(velocity, wall.Normal);
            float gn = Vec3.Dot(gravity, wall.Normal);

            float tCross = TimeToDistance(d0, vn, gn, 0f);
            if (float.IsInfinity(tCross)) return result;     // never reaches the plane

            // Already overlapping the plane and only drifting in later: it is touching now.
            float tTouch = TimeToDistance(d0, vn, gn, radius);
            if (float.IsInfinity(tTouch) || tTouch > tCross) tTouch = 0f;

            // When its skin comes within nearMargin of the wall: never later than the touch. Already that near = now.
            float tNear = tTouch;
            if (nearMargin > 0f)
            {
                tNear = TimeToDistance(d0, vn, gn, radius + nearMargin);
                if (float.IsInfinity(tNear) || tNear > tTouch) tNear = 0f;
            }
            if (tNear > lookAhead) return result;

            float tClear = TimeToDistance(d0, vn, gn, -radius);

            result.Touches = true;
            result.TouchTime = tTouch;
            result.CrossTime = tCross;
            result.ClearTime = tClear;
            result.TouchUV = wall.ToUV(AtAbove(position, velocity, gravity, tTouch, minCenterHeight));
            result.CrossUV = wall.ToUV(AtAbove(position, velocity, gravity, tCross, minCenterHeight));
            result.TouchPainted = OnPaint(end, mask, result.TouchUV);
            result.Footprint = PortalCrossing.Passage(end, mask, result.CrossUV, radius, settings);

            if (float.IsInfinity(tClear))
            {
                // Gravity turns it round before it is fully through (only possible on a floor or a leaning wall).
                result.ClearUV = result.CrossUV;
                result.ClearPainted = false;
            }
            else
            {
                result.ClearUV = wall.ToUV(AtAbove(position, velocity, gravity, tClear, minCenterHeight));
                result.ClearPainted = OnPaint(end, mask, result.ClearUV);
            }

            result.Enters = result.TouchPainted && result.Footprint.Fits && result.ClearPainted;
            return result;
        }

        /// <summary>
        /// While a ball is part-way through the wall plane: is the circle where it cuts the plane still all
        /// on paint? True when the ball does not reach the plane at all.
        /// </summary>
        public static bool FootprintOnPaint(in PortalEnd end, IPaintMask mask, Vec3 center, float radius,
            PortalSettings settings)
        {
            float d = end.Wall.SignedDistance(center);
            float cut = CutRadius(radius, d);
            if (cut <= 0f) return true;
            return PortalCrossing.Passage(end, mask, end.Wall.ToUV(center), cut, settings).Fits;
        }

        /// <summary>Radius of the circle where a ball of this radius cuts a plane this far from its centre. 0 when it does not reach.</summary>
        public static float CutRadius(float radius, float distance)
        {
            float sq = radius * radius - distance * distance;
            return sq > 0f ? MathF.Sqrt(sq) : 0f;
        }

        /// <summary>
        /// Work out the crossing for something going in through one end at <paramref name="entryPoint"/>.
        ///
        /// The crossing is the pair's rigid mapping centred on the paint. Only when that lands the object on
        /// bare wall at the exit (the two patches are different shapes) is it slid along the exit wall into
        /// the exit patch's largest circle, by <see cref="PortalCrossing.PlaceExit"/>. Direction and speed
        /// are never changed by the slide.
        /// </summary>
        /// <param name="fromA">True when going in through A (orange).</param>
        /// <param name="player">True to use the upright player crossing (a turn about world up only).</param>
        /// <param name="exitMask">The paint on the exit end's wall.</param>
        /// <param name="entryPoint">Where the object's centre meets the entry wall plane, world metres.</param>
        /// <param name="radius">The object's radius, metres.</param>
        public static PortalCarry Carry(in PortalPair pair, bool fromA, bool player, IPaintMask exitMask,
            Vec3 entryPoint, float radius, PortalSettings settings)
        {
            RigidTransform crossing = player ? pair.PlayerCrossing(fromA) : pair.Crossing(fromA);
            PortalEnd exit = fromA ? pair.B : pair.A;

            var result = new PortalCarry { Transform = crossing, MovedBy = 0f, Fits = true, LandedOnPaint = true };
            Vec3 mapped = crossing.TransformPoint(entryPoint);
            Vec2 uv = exit.Wall.ToUV(mapped);
            if (PortalCrossing.Passage(exit, exitMask, uv, radius, settings).Fits) return result;

            result.LandedOnPaint = false;
            ExitPlacement placed = PortalCrossing.PlaceExit(exit, mapped, radius, settings);
            result.Fits = placed.Fits;
            result.MovedBy = placed.MovedBy;
            if (placed.MovedBy > 0f)
            {
                Vec3 slide = placed.Position - mapped;
                result.Transform = new RigidTransform(crossing.Rotation, crossing.Translation + slide);
            }
            return result;
        }

        /// <summary>Free-flight position after t seconds.</summary>
        public static Vec3 At(Vec3 position, Vec3 velocity, Vec3 gravity, float t)
        {
            return position + velocity * t + gravity * (0.5f * t * t);
        }

        /// <summary>Free-flight position after t seconds, but never lower than <paramref name="minHeight"/> (world Z).</summary>
        public static Vec3 AtAbove(Vec3 position, Vec3 velocity, Vec3 gravity, float t, float minHeight)
        {
            Vec3 p = At(position, velocity, gravity, t);
            if (p.Z < minHeight) p.Z = minHeight;
            return p;
        }

        /// <summary>
        /// Earliest time t >= 0 at which d0 + vn*t + gn*t*t/2 comes down to <paramref name="target"/> while
        /// still falling. Infinity when it never does. If it is already at or below the target, 0 when it is
        /// moving down, otherwise infinity.
        /// </summary>
        public static float TimeToDistance(float d0, float vn, float gn, float target)
        {
            float c = d0 - target;
            if (c <= 0f) return vn < 0f || (vn == 0f && gn < 0f) ? 0f : float.PositiveInfinity;

            if (MathF.Abs(gn) < 1e-6f)
            {
                return vn < -1e-9f ? c / -vn : float.PositiveInfinity;
            }

            // gn/2 t^2 + vn t + c = 0
            float disc = vn * vn - 2f * gn * c;
            if (disc < 0f) return float.PositiveInfinity;
            float root = MathF.Sqrt(disc);
            // The crossing on the way down is where the slope vn + gn t is negative: t = (-vn - root) / gn.
            float t = (-vn - root) / gn;
            return t >= 0f ? t : float.PositiveInfinity;
        }

        private static bool OnPaint(in PortalEnd end, IPaintMask mask, Vec2 uv)
        {
            return mask != null && end.Wall.Contains(uv) && mask.IsPainted(end.Color, uv);
        }
    }
}
