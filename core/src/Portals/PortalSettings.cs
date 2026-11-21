namespace Portaballs.Core
{
    /// <summary>Where on a paint patch the portal's "doorway point" sits.</summary>
    public enum PortalAnchor
    {
        /// <summary>
        /// The default, as in the game Portal. At the centre of the paint patch. A ball thrown into the middle
        /// of one patch comes out of the middle of the other, even if the two patches are at different heights.
        /// </summary>
        PaintCenter = 0,

        /// <summary>
        /// Optional, not the default. On the wall straight below the patch centre, at the room's floor height.
        /// The two rooms' floors line up like a doorway, so a player walking through never lands inside the
        /// floor or in mid air. Unlike Portal: where you go in on the patch no longer matches where you come out.
        /// </summary>
        FloorAligned = 1,
    }

    /// <summary>
    /// Every tunable number for the portal maths in one place. Make one, change what you need, and pass it
    /// in. Nothing here changes while the game runs unless you change it.
    /// </summary>
    public sealed class PortalSettings
    {
        /// <summary>
        /// Which anchoring policy portal frames use, for everything including the player. Default: paint
        /// centre, which behaves exactly like the game Portal: the middle of one patch maps onto the middle of
        /// the other, so a player coming out of a higher patch drops to the floor and one coming out of a lower
        /// patch must be lifted out of it (see <see cref="PlayerArrival"/>). Floor aligned is the optional
        /// doorway-style alternative.
        /// </summary>
        public PortalAnchor Anchor = PortalAnchor.PaintCenter;

        /// <summary>
        /// Floor alignment only makes sense on walls that stand up. When the wall's "up the wall" direction
        /// points upward by less than this much (1 = straight up, 0.5 = leaning 60 degrees), the frame falls
        /// back to the paint centre. Floors and ceilings always fall back.
        /// </summary>
        public float FloorAlignMinUpness = 0.5f;

        /// <summary>
        /// Points closer to a wall than this (metres) count as lying exactly on it. Stops float noise from
        /// turning an object that slides along the wall into a crossing.
        /// </summary>
        public float PlaneEpsilon = 1e-5f;

        /// <summary>
        /// How many points around the outline of an object's footprint the passage test checks (the centre
        /// is always checked too). More is stricter and slower.
        /// </summary>
        public int PassageOutlineSamples = 16;

        /// <summary>
        /// When true, an object that would come out of the exit portal on bare wall is pulled back inside the
        /// exit patch's largest circle, with room for its own radius.
        /// </summary>
        public bool ClampExitToOpening = true;

        /// <summary>Extra safety distance (metres) added by the head-clip guard on top of the exact amount.</summary>
        public float HeadClipMargin = 0.01f;

        /// <summary>
        /// Moves the see-through clip plane this far (metres) into the exit room. Positive values hide a thin
        /// sliver in front of the exit wall, negative values let a sliver behind it show. 0 = exactly on the wall.
        /// </summary>
        public float ClipPlaneOffset = 0f;
    }
}
