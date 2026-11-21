using System;

namespace Portaballs.Core
{
    /// <summary>What a point did relative to a portal's wall during one frame.</summary>
    public enum CrossingKind
    {
        /// <summary>Stayed on one side, or slid along the wall without going through.</summary>
        None = 0,

        /// <summary>Went from the room side to behind the wall: this is going into the portal.</summary>
        Entered = 1,

        /// <summary>Came from behind the wall out to the room side (for example backing out). Not a portal entry.</summary>
        Backward = 2,
    }

    /// <summary>Answer from <see cref="PortalCrossing.Detect"/>.</summary>
    public struct CrossingResult
    {
        public CrossingKind Kind;

        /// <summary>Fraction (0 to 1) of the way from the previous to the current position where it met the wall.</summary>
        public float T;

        /// <summary>Wall coordinates (metres) of the meeting point.</summary>
        public Vec2 UV;

        /// <summary>World position of the meeting point.</summary>
        public Vec3 Point;

        /// <summary>True when the meeting point lies on the wall's rectangle.</summary>
        public bool OnWall;

        /// <summary>True when the meeting point is covered by this portal's colour of paint.</summary>
        public bool Painted;

        /// <summary>Entered through paint: the thing should be carried to the other portal.</summary>
        public bool Teleports
        {
            get { return Kind == CrossingKind.Entered && Painted; }
        }
    }

    /// <summary>Answer from <see cref="PortalCrossing.Passage"/>.</summary>
    public struct PassageResult
    {
        /// <summary>True when every sample was painted: the object may go through here.</summary>
        public bool Fits;

        /// <summary>How many samples landed on paint.</summary>
        public int PaintedSamples;

        /// <summary>How many samples were checked (outline samples + 1 for the centre).</summary>
        public int TotalSamples;

        /// <summary>Painted share, 0 to 1. Close to 1 but not 1 means "nearly fits".</summary>
        public float Fraction
        {
            get { return TotalSamples > 0 ? (float)PaintedSamples / TotalSamples : 0f; }
        }
    }

    /// <summary>Answer from <see cref="PortalCrossing.PlaceExit"/>.</summary>
    public struct ExitPlacement
    {
        /// <summary>Where the object should appear (world).</summary>
        public Vec3 Position;

        /// <summary>How far (metres) it was moved sideways along the exit wall to fit. 0 = not moved.</summary>
        public float MovedBy;

        /// <summary>False when the object is wider than the exit patch's largest circle (it was put at the centre).</summary>
        public bool Fits;
    }

    /// <summary>
    /// Questions about things meeting a portal: did it go through this frame, may an object of this size
    /// pass here, and where should it appear on the far side. Nothing here allocates memory.
    /// </summary>
    public static class PortalCrossing
    {
        /// <summary>
        /// Did a point go through the portal's wall between last frame and this frame?
        /// Works for any speed (it checks the whole path, not just the two positions), for points that start
        /// exactly on the wall (they count as on the room side), and for points sliding along the wall
        /// (no crossing). The mask is asked about this portal's colour.
        /// </summary>
        public static CrossingResult Detect(in PortalEnd end, IPaintMask mask, Vec3 previous, Vec3 current,
            float planeEpsilon = 1e-5f)
        {
            var result = new CrossingResult();
            WallFrame wall = end.Wall;
            float dPrev = wall.SignedDistance(previous);
            float dCurr = wall.SignedDistance(current);
            int sidePrev = Side(dPrev, planeEpsilon);
            int sideCurr = Side(dCurr, planeEpsilon);

            if (sidePrev >= 0 && sideCurr < 0)
                result.Kind = CrossingKind.Entered;
            else if (sidePrev < 0 && sideCurr >= 0)
                result.Kind = CrossingKind.Backward;
            else
                return result;

            // Treat "within epsilon" as exactly on the wall so a point resting on it crosses at t = 0.
            float a = sidePrev == 0 ? 0f : dPrev;
            float b = sideCurr == 0 ? 0f : dCurr;
            float denom = a - b;
            float t = MathF.Abs(denom) < 1e-12f ? 0f : a / denom;
            t = MathUtil.Saturate(t);

            Vec3 hit = previous + (current - previous) * t;
            Vec2 uv = wall.ToUV(hit);
            result.T = t;
            result.Point = wall.ToWorld(uv);
            result.UV = uv;
            result.OnWall = wall.Contains(uv);
            result.Painted = result.OnWall && mask != null && mask.IsPainted(end.Color, uv);
            return result;
        }

        /// <summary>Same, reading the on-wall tolerance from settings.</summary>
        public static CrossingResult Detect(in PortalEnd end, IPaintMask mask, Vec3 previous, Vec3 current,
            PortalSettings settings)
        {
            return Detect(end, mask, previous, current, settings.PlaneEpsilon);
        }

        private static int Side(float d, float eps)
        {
            if (d > eps) return 1;
            if (d < -eps) return -1;
            return 0;
        }

        /// <summary>
        /// May a round object pass through the portal at this wall point? Its footprint on the wall is a
        /// circle of the given radius around uv. The centre and <paramref name="outlineSamples"/> evenly
        /// spaced points on the circle (the first one straight along U) are checked against the paint.
        /// All painted means it fits; otherwise the wall is solid for it.
        /// </summary>
        public static PassageResult Passage(IPaintMask mask, PaintColor color, in WallFrame wall, Vec2 uv,
            float radius, int outlineSamples = 16)
        {
            if (outlineSamples < 0) outlineSamples = 0;
            int painted = 0;
            if (IsPaintedOnWall(mask, color, wall, uv)) painted++;

            if (radius > 0f && outlineSamples > 0)
            {
                float step = 2f * MathF.PI / outlineSamples;
                for (int i = 0; i < outlineSamples; i++)
                {
                    float angle = step * i;
                    Vec2 p = new Vec2(uv.X + radius * MathF.Cos(angle), uv.Y + radius * MathF.Sin(angle));
                    if (IsPaintedOnWall(mask, color, wall, p)) painted++;
                }
            }
            else
            {
                // A point-sized object: the outline is the centre itself.
                outlineSamples = 0;
            }

            int total = outlineSamples + 1;
            return new PassageResult { Fits = painted == total, PaintedSamples = painted, TotalSamples = total };
        }

        /// <summary>Passage test against this portal's own wall and colour, sample count from settings.</summary>
        public static PassageResult Passage(in PortalEnd end, IPaintMask mask, Vec2 uv, float radius,
            PortalSettings settings)
        {
            return Passage(mask, end.Color, end.Wall, uv, radius, settings.PassageOutlineSamples);
        }

        private static bool IsPaintedOnWall(IPaintMask mask, PaintColor color, in WallFrame wall, Vec2 uv)
        {
            return mask != null && wall.Contains(uv) && mask.IsPainted(color, uv);
        }

        /// <summary>
        /// The exit patch may be a different shape, so a carried-over point can land on bare wall. This pulls
        /// the point sideways (along the exit wall, keeping its distance from the wall) until the whole object
        /// fits inside the exit patch's largest circle. Objects already inside are not moved.
        /// </summary>
        /// <param name="exit">The portal the object comes out of.</param>
        /// <param name="mappedPoint">The object's centre after the crossing transform.</param>
        /// <param name="radius">The object's radius in metres.</param>
        /// <param name="clamp">False switches the pull off (the point is returned unchanged).</param>
        public static ExitPlacement PlaceExit(in PortalEnd exit, Vec3 mappedPoint, float radius, bool clamp = true)
        {
            var result = new ExitPlacement { Position = mappedPoint, MovedBy = 0f, Fits = true };
            float room = exit.Opening.InscribedRadius - MathF.Max(radius, 0f);
            if (room < 0f)
            {
                result.Fits = false;
                room = 0f;
            }
            if (!clamp) return result;

            WallFrame wall = exit.Wall;
            float distance = wall.SignedDistance(mappedPoint);
            Vec2 uv = wall.ToUV(mappedPoint);
            Vec2 offset = uv - exit.Opening.CenterUV;
            float len = offset.Length();
            if (len <= room) return result;

            Vec2 pulled = len > 1e-9f ? exit.Opening.CenterUV + offset * (room / len) : exit.Opening.CenterUV;
            result.Position = wall.ToWorld(pulled, distance);
            result.MovedBy = len - room;
            return result;
        }

        /// <summary>Exit placement with the on/off switch from settings.</summary>
        public static ExitPlacement PlaceExit(in PortalEnd exit, Vec3 mappedPoint, float radius,
            PortalSettings settings)
        {
            return PlaceExit(exit, mappedPoint, radius, settings.ClampExitToOpening);
        }
    }
}
