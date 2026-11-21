using System;

namespace Portaballs.Core
{
    /// <summary>
    /// One end of a portal: the paint patch, the wall it is on, and a position and orientation ("frame")
    /// that the crossing maths uses as the doorway.
    ///
    /// The frame's own axes follow the world convention (X forward, Y left, Z up), seen by someone standing
    /// in the doorway looking out into the room:
    ///   X = the wall's Normal (out of the wall into the room),
    ///   Y = the wall's U (which is this person's left, because U is the right of someone facing the wall),
    ///   Z = the wall's V (up the wall).
    /// </summary>
    public readonly struct PortalEnd
    {
        /// <summary>The wall the patch is on.</summary>
        public readonly WallFrame Wall;

        /// <summary>The paint patch this end was built from.</summary>
        public readonly PortalOpening Opening;

        /// <summary>The anchoring policy that was actually used (floor aligned falls back on floors and ceilings).</summary>
        public readonly PortalAnchor AnchorUsed;

        /// <summary>World position of the doorway point. Always on the wall plane.</summary>
        public readonly Vec3 Position;

        /// <summary>Orientation of the frame: turns world X, Y, Z onto Normal, U, V.</summary>
        public readonly Quat Rotation;

        /// <summary>
        /// The same frame with all lean removed: X is the wall normal flattened onto the floor, Z is world up.
        /// Used for the player, who must never be pitched or rolled.
        /// </summary>
        public readonly Quat UprightRotation;

        /// <summary>Turn angle (radians, about world up) of <see cref="UprightRotation"/>.</summary>
        public readonly float UprightYaw;

        private PortalEnd(WallFrame wall, PortalOpening opening, PortalAnchor anchorUsed, Vec3 position,
            Quat rotation, float uprightYaw)
        {
            Wall = wall;
            Opening = opening;
            AnchorUsed = anchorUsed;
            Position = position;
            Rotation = rotation;
            UprightYaw = uprightYaw;
            UprightRotation = Quat.CreateFromAxisAngle(Units.Up, uprightYaw);
        }

        public PaintColor Color
        {
            get { return Opening.Color; }
        }

        /// <summary>Out of the wall into the room.</summary>
        public Vec3 Normal
        {
            get { return Wall.Normal; }
        }

        /// <summary>World position of the centre of the patch's largest circle.</summary>
        public Vec3 PaintCenter
        {
            get { return Wall.ToWorld(Opening.CenterUV); }
        }

        /// <summary>
        /// Builds a portal end.
        /// </summary>
        /// <param name="wall">The wall the patch is on.</param>
        /// <param name="opening">The patch (its centre and size).</param>
        /// <param name="anchor">Paint centre (the default policy, as in Portal) or floor aligned (optional).</param>
        /// <param name="floorHeight">World Z of the floor in this wall's room. Only used when floor aligned.</param>
        /// <param name="minUpness">See <see cref="PortalSettings.FloorAlignMinUpness"/>.</param>
        public static PortalEnd Create(in WallFrame wall, in PortalOpening opening, PortalAnchor anchor,
            float floorHeight, float minUpness = 0.5f)
        {
            Vec3 center = wall.ToWorld(opening.CenterUV);
            Vec3 position = center;
            PortalAnchor used = PortalAnchor.PaintCenter;

            if (anchor == PortalAnchor.FloorAligned && wall.V.Z >= minUpness && wall.V.Z > 1e-4f)
            {
                // Slide down the wall (along V, so we stay on the wall) until we reach floor height.
                float drop = (center.Z - floorHeight) / wall.V.Z;
                position = center - wall.V * drop;
                used = PortalAnchor.FloorAligned;
            }

            Quat rotation = FromAxes(wall.Normal, wall.U, wall.V);
            return new PortalEnd(wall, opening, used, position, rotation, UprightYawOf(wall));
        }

        /// <summary>Same as the other Create, reading anchor and the lean limit from settings.</summary>
        public static PortalEnd Create(in WallFrame wall, in PortalOpening opening, float floorHeight,
            PortalSettings settings)
        {
            return Create(wall, opening, settings.Anchor, floorHeight, settings.FloorAlignMinUpness);
        }

        /// <summary>
        /// Quaternion that turns world X, Y, Z onto the given three unit axes (which must be at right angles
        /// and right-handed: x cross y = z).
        /// </summary>
        public static Quat FromAxes(Vec3 x, Vec3 y, Vec3 z)
        {
            // System.Numerics uses row vectors (v * M), so the rows of the matrix are where each axis goes.
            var m = new Mat4(
                x.X, x.Y, x.Z, 0f,
                y.X, y.Y, y.Z, 0f,
                z.X, z.Y, z.Z, 0f,
                0f, 0f, 0f, 1f);
            return Quat.Normalize(Quat.CreateFromRotationMatrix(m));
        }

        /// <summary>
        /// Which way the wall faces on the floor map, as an angle about world up (0 = facing +X).
        /// For a floor or ceiling, which faces straight up or down, the "up the wall" direction is used instead.
        /// </summary>
        private static float UprightYawOf(in WallFrame wall)
        {
            Vec3 n = wall.Normal;
            float hx = n.X;
            float hy = n.Y;
            if (hx * hx + hy * hy < 1e-8f)
            {
                // Floor or ceiling: there is no "out of the wall" on the floor map. Use the direction
                // up the wall, which for a floor patch is the way its top edge points.
                hx = wall.V.X;
                hy = wall.V.Y;
            }
            return MathF.Atan2(hy, hx);
        }
    }
}
