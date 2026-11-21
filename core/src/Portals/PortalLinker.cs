using System.Collections.Generic;

namespace Portaballs.Core
{
    /// <summary>
    /// Keeps track of which orange patch and which blue patch are the live portals.
    ///
    /// Feed it every patch report from the paint code (per wall, per colour). Rules:
    ///   - The newest valid splat of each colour, on any wall, is that colour's portal. A newer splat of the
    ///     same colour replaces the older portal; the older patch does not come back unless it is splatted again.
    ///   - Both colours may be on the same wall.
    ///   - If the live patch stops being valid (painted over, too small), that colour has no portal and the
    ///     pair is unlinked.
    ///   - A report counts as a new splat only when its Revision differs from the last report for that
    ///     wall and colour, so repeating the same report every frame changes nothing.
    /// </summary>
    public sealed class PortalLinker
    {
        /// <summary>The last report seen for one wall and colour.</summary>
        private struct Slot
        {
            public int WallId;
            public PaintColor Color;
            public WallFrame Wall;
            public float FloorHeight;
            public PortalOpening Opening;
        }

        // One entry per (wall, colour) ever reported. Grows only when a new wall reports for the first time.
        private readonly List<Slot> _slots = new List<Slot>();

        // Index into _slots of the live portal of each colour, or -1.
        private readonly int[] _live = { -1, -1 };

        private readonly PortalSettings _settings;
        private PortalPair _pair;

        public PortalLinker(PortalSettings settings = null)
        {
            _settings = settings ?? new PortalSettings();
        }

        /// <summary>True when there is a live orange portal and a live blue portal.</summary>
        public bool HasPair { get; private set; }

        /// <summary>
        /// Goes up by one every time the linked pair appears, disappears, moves to another patch, or one of
        /// its patches changes shape. Compare with a remembered value to know when to rebuild visuals.
        /// </summary>
        public int PairRevision { get; private set; }

        /// <summary>The linked pair: A is orange, B is blue. Only meaningful while <see cref="HasPair"/> is true.</summary>
        public PortalPair Pair
        {
            get { return _pair; }
        }

        /// <summary>Wall id of the live portal of this colour, or -1 when there is none.</summary>
        public int LiveWall(PaintColor color)
        {
            int i = _live[(int)color];
            return i < 0 ? -1 : _slots[i].WallId;
        }

        /// <summary>The live patch of this colour (Valid is false when there is none).</summary>
        public PortalOpening LiveOpening(PaintColor color)
        {
            int i = _live[(int)color];
            return i < 0 ? default : _slots[i].Opening;
        }

        /// <summary>
        /// Report the current patch of one colour on one wall. Returns true when the linked pair changed
        /// (appeared, vanished, moved or changed shape).
        /// </summary>
        /// <param name="wallId">Any number that names this wall; the same wall must always use the same id.</param>
        /// <param name="wall">The wall's geometry.</param>
        /// <param name="floorHeight">World Z of the floor in this wall's room, for floor-aligned frames.</param>
        /// <param name="opening">The patch report. Its Color says which colour it is.</param>
        public bool Report(int wallId, in WallFrame wall, float floorHeight, in PortalOpening opening)
        {
            int color = (int)opening.Color;
            int index = FindSlot(wallId, opening.Color);
            bool isNewSplat;
            if (index < 0)
            {
                _slots.Add(new Slot { WallId = wallId, Color = opening.Color });
                index = _slots.Count - 1;
                isNewSplat = opening.Valid;
            }
            else
            {
                Slot old = _slots[index];
                isNewSplat = opening.Valid && (!old.Opening.Valid || old.Opening.Revision != opening.Revision);
            }

            Slot s = _slots[index];
            bool wasLive = _live[color] == index;
            bool liveChanged = false;
            if (wasLive)
            {
                // The live patch itself changed: shape, validity or wall geometry.
                liveChanged = s.Opening.Valid != opening.Valid || s.Opening.Revision != opening.Revision
                              || s.FloorHeight != floorHeight || !SameWall(s.Wall, wall);
            }
            s.Wall = wall;
            s.FloorHeight = floorHeight;
            s.Opening = opening;
            _slots[index] = s;

            if (!opening.Valid)
            {
                if (wasLive) _live[color] = -1;
            }
            else if (isNewSplat && !wasLive)
            {
                // A newer splat of this colour replaces the old portal of this colour.
                _live[color] = index;
                liveChanged = true;
            }

            bool hadPair = HasPair;
            HasPair = _live[0] >= 0 && _live[1] >= 0;
            bool changed = HasPair != hadPair || (HasPair && liveChanged);
            if (changed)
            {
                PairRevision++;
                if (HasPair) RebuildPair();
            }
            return changed;
        }

        /// <summary>Forget everything (new level).</summary>
        public void Clear()
        {
            bool hadPair = HasPair;
            _slots.Clear();
            _live[0] = -1;
            _live[1] = -1;
            HasPair = false;
            if (hadPair) PairRevision++;
        }

        /// <summary>Build the frames again, for example after changing the anchoring setting.</summary>
        public void RebuildPair()
        {
            if (!HasPair) return;
            Slot o = _slots[_live[(int)PaintColor.Orange]];
            Slot b = _slots[_live[(int)PaintColor.Blue]];
            PortalEnd a = PortalEnd.Create(o.Wall, o.Opening, o.FloorHeight, _settings);
            PortalEnd bb = PortalEnd.Create(b.Wall, b.Opening, b.FloorHeight, _settings);
            _pair = new PortalPair(a, bb);
        }

        private int FindSlot(int wallId, PaintColor color)
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].WallId == wallId && _slots[i].Color == color) return i;
            }
            return -1;
        }

        private static bool SameWall(in WallFrame a, in WallFrame b)
        {
            return a.Origin == b.Origin && a.U == b.U && a.V == b.V && a.Width == b.Width && a.Height == b.Height;
        }
    }
}
