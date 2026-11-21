namespace Portaballs.Core
{
    /// <summary>The two paint colours. The numeric values index per-colour arrays.</summary>
    public enum PaintColor
    {
        Orange = 0,
        Blue = 1,
    }

    /// <summary>
    /// Read-only view of the paint on one wall. Paint code implements it; portal code only needs this,
    /// so the two can be built and tested apart.
    /// </summary>
    public interface IPaintMask
    {
        /// <summary>The wall this paint lives on.</summary>
        WallFrame Wall { get; }

        /// <summary>True when the point at wall coordinates uv (metres) is covered by solid paint of that colour.</summary>
        bool IsPainted(PaintColor color, Vec2 uv);
    }

    /// <summary>
    /// A patch of one colour that is big enough to be a portal opening.
    /// Paint code produces it; portal code links an orange one with a blue one.
    /// </summary>
    public struct PortalOpening
    {
        /// <summary>False when there is no patch, or the patch is too small to step through.</summary>
        public bool Valid;

        public PaintColor Color;

        /// <summary>Wall coordinates (metres) of the centre of the largest circle that fits inside the paint.</summary>
        public Vec2 CenterUV;

        /// <summary>Radius in metres of that largest circle.</summary>
        public float InscribedRadius;

        /// <summary>Lowest and highest wall coordinates (metres) of the patch's bounding box.</summary>
        public Vec2 BoundsMin;
        public Vec2 BoundsMax;

        /// <summary>Painted area of the patch in square metres.</summary>
        public float Area;

        /// <summary>Goes up by one every time the patch's shape changes, so users can tell when to refresh.</summary>
        public int Revision;
    }
}
