using System.Collections.Generic;
using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// THE ONE PLACE that knows whether the orange patch and the blue patch are linked as a portal, and
/// holds the linked pair. One per room, on the room's object (<see cref="TestRoom.Portals"/>).
///
/// It watches the room's two patches (<see cref="PortalPatches"/>). When both have a valid opening it
/// builds the pair with the core (PortalLinker / PortalPair, paint-centre anchoring: exactly like Portal)
/// and rebuilds it whenever either opening's shape changes. When either goes, the pair is unlinked.
///
/// WHAT OTHER CODE READS (balls, the player, and next the see-through view):
///   <see cref="HasPair"/>        true while linked
///   <see cref="Pair"/>           core PortalPair: A = orange end, B = blue end (PortalEnd: Wall, Opening,
///                                Position = the paint centre on the wall, Rotation, Normal), and the
///                                crossings AToB / BToA (balls) and PlayerAToB / PlayerBToA (the player and
///                                the view: upright between two walls; the full rigid crossing, the same
///                                as AToB, when either end is on the floor or the ceiling)
///   <see cref="PairRevision"/>   the change counter: goes up by one whenever the pair appears, disappears,
///                                moves to another patch or one of its patches changes shape
///   <see cref="WallOf"/>         the PaintWall holding one end (its Panel is what draws the opening); it can
///                                be the floor or the ceiling (ask its Frame.IsFloor / IsCeiling / IsFlat)
///   <see cref="MaskOf"/>         the paint on that wall (core IPaintMask): what "inside the opening" means
///
/// Call <see cref="Sync"/> before reading if you run before this component in a frame: it is cheap (two
/// number comparisons when nothing changed) and makes the answer current.
/// </summary>
public sealed class PortalLink : Component
{
	/// <summary>One thing that went through the portal, kept for the readouts and the scripted tests.</summary>
	public struct CrossingRecord
	{
		/// <summary>Counts up with every crossing in this room (balls and player together).</summary>
		public int Number;
		public bool IsPlayer;
		/// <summary>For a ball: its own number (Portaball.Seed).</summary>
		public uint BallSeed;
		/// <summary>True = went in through orange and came out of blue.</summary>
		public bool FromOrange;
		public int PairRevision;
		public double Time;
		public Vec2 EntryUV;

		/// <summary>Centre position and velocity at the moment of crossing, on the entry side. World metres, m/s.</summary>
		public Vec3 PositionBefore;
		public Vec3 VelocityBefore;

		/// <summary>The same moment on the exit side: what the game actually used.</summary>
		public Vec3 PositionAfter;
		public Vec3 VelocityAfter;

		/// <summary>What the core's plain rigid crossing (PortalPair.AToB / BToA, or the player version) gives for the "before" values.</summary>
		public Vec3 PositionPredicted;
		public Vec3 VelocityPredicted;

		/// <summary>How far the exit was slid along the exit wall because the rigid crossing landed on bare wall. 0 = not slid.</summary>
		public float ExitMovedBy;
	}

	/// <summary>The room whose patches are watched. Set by TestRoom.</summary>
	public TestRoom Room { get; set; }

	/// <summary>
	/// The core's portal dials. Anchor stays at its default, PaintCenter: the crossing is a rigid mapping
	/// centred on the paint, as in Portal. (FloorAligned exists as a setting only; changing it here and
	/// calling <see cref="Rebuild"/> would switch.)
	/// </summary>
	public PortalSettings Settings { get; } = new PortalSettings();

	/// <summary>True while an orange opening and a blue opening are linked.</summary>
	public bool HasPair => Linker.HasPair;

	/// <summary>The linked pair: A is orange, B is blue. Only meaningful while <see cref="HasPair"/>.</summary>
	public PortalPair Pair => Linker.Pair;

	/// <summary>Goes up by one every time the pair appears, disappears, moves or changes shape.</summary>
	public int PairRevision => Linker.PairRevision;

	/// <summary>World Z of the room's floor, metres.</summary>
	public float FloorHeight { get; private set; }

	/// <summary>How many things have gone through since the room was built.</summary>
	public int CrossingCount { get; private set; }

	/// <summary>The most recent crossings, oldest first (the last 64 are kept).</summary>
	public IReadOnlyList<CrossingRecord> Crossings => _crossings;

	private PortalLinker _linker;
	private readonly int[] _seenPatchRevision = { -1, -1 };
	private readonly PaintWall[] _endWall = new PaintWall[2];
	private readonly List<CrossingRecord> _crossings = new List<CrossingRecord>();
	private bool _wasLinked;
	private int _flagsForRevision = -1;

	// Made on first use, so it does not matter when the engine first wakes this component.
	private PortalLinker Linker
	{
		get
		{
			if ( _linker == null ) _linker = new PortalLinker( Settings );
			return _linker;
		}
	}

	/// <summary>The wall holding that colour's end of the pair (null when not linked).</summary>
	public PaintWall WallOf( PaintColor color )
	{
		return HasPair ? _endWall[(int)color] : null;
	}

	/// <summary>The paint on the wall holding that colour's end (null when not linked).</summary>
	public IPaintMask MaskOf( PaintColor color )
	{
		PaintWall wall = WallOf( color );
		return wall.IsValid() ? wall.Surface : null;
	}

	/// <summary>The end of that colour. Only meaningful while <see cref="HasPair"/>.</summary>
	public PortalEnd EndOf( PaintColor color )
	{
		return color == PaintColor.Orange ? Linker.Pair.A : Linker.Pair.B;
	}

	/// <summary>Build the pair again after changing <see cref="Settings"/>.</summary>
	public void Rebuild()
	{
		Linker.RebuildPair();
	}

	protected override void OnUpdate()
	{
		Sync();
	}

	protected override void OnFixedUpdate()
	{
		Sync();
	}

	/// <summary>
	/// Bring the pair up to date with the room's patches. Does nothing when neither patch has changed
	/// since the last call.
	/// </summary>
	public void Sync()
	{
		if ( !Room.IsValid() || !Room.Patches.IsValid() ) return;

		FloorHeight = SboxConvert.ToMetres( Room.WorldPosition ).Z;

		bool anyChange = false;
		for ( int c = 0; c < 2; c++ )
		{
			PortalPatches.Patch patch = Room.Patches.Get( (PaintColor)c );
			if ( patch.Revision == _seenPatchRevision[c] ) continue;
			_seenPatchRevision[c] = patch.Revision;
			anyChange = true;

			// Tell the core about this colour on every wall: the wall that holds the patch reports its
			// opening, every other wall reports "nothing here", so a replaced patch stops being an end at once.
			bool open = patch.Exists && patch.Wall.IsValid() && patch.Opening.Valid;
			for ( int i = 0; i < Room.Walls.Count; i++ )
			{
				PaintWall wall = Room.Walls[i];
				PortalOpening report = new PortalOpening { Color = (PaintColor)c };
				if ( open && wall == patch.Wall ) report = patch.Opening;
				Linker.Report( i, wall.Frame, FloorHeight, report );
			}
			_endWall[c] = open ? patch.Wall : null;
		}
		if ( !anyChange ) return;

		if ( HasPair != _wasLinked )
		{
			_wasLinked = HasPair;
			if ( HasPair ) Log.Info( $"[PortalLink] LINKED (pair revision {PairRevision}): {Describe( PaintColor.Orange )}; {Describe( PaintColor.Blue )}. Anchor {Pair.A.AnchorUsed}." );
			else Log.Info( $"[PortalLink] UNLINKED (pair revision {PairRevision}): {DescribePatch( PaintColor.Orange )}; {DescribePatch( PaintColor.Blue )}." );
		}

		UpdateWallFlags();
	}

	// "orange on Wall +X, centre (4.00, 0.12, 1.58) m (u=2.88 v=1.58), fits a circle of radius 0.36 m, patch 0.95 x 1.71 m"
	private string Describe( PaintColor color )
	{
		PortalEnd end = EndOf( color );
		PaintWall wall = _endWall[(int)color];
		Vec3 centre = end.PaintCenter;
		Vec2 size = end.Opening.BoundsMax - end.Opening.BoundsMin;
		return $"{color} on {( wall.IsValid() ? wall.WallName : "?" )}, centre ({centre.X:0.00}, {centre.Y:0.00}, {centre.Z:0.00}) m (u={end.Opening.CenterUV.X:0.00} v={end.Opening.CenterUV.Y:0.00}), fits a circle of radius {end.Opening.InscribedRadius:0.00} m, patch {size.X:0.00} x {size.Y:0.00} m";
	}

	private string DescribePatch( PaintColor color )
	{
		PortalPatches.Patch patch = Room.Patches.Get( color );
		if ( !patch.Exists || !patch.Wall.IsValid() ) return $"{color} has no patch";
		if ( !patch.Opening.Valid ) return $"{color} on {patch.Wall.WallName} has no opening (largest circle {patch.Opening.InscribedRadius:0.00} m)";
		Vec3 centre = patch.Wall.Frame.ToWorld( patch.Opening.CenterUV );
		Vec2 size = patch.Opening.BoundsMax - patch.Opening.BoundsMin;
		return $"{color} on {patch.Wall.WallName}, centre ({centre.X:0.00}, {centre.Y:0.00}, {centre.Z:0.00}) m, circle radius {patch.Opening.InscribedRadius:0.00} m, patch {size.X:0.00} x {size.Y:0.00} m";
	}

	/// <summary>
	/// Tell each wall's paint shader which colours on it are open portals right now. This is the per-wall
	/// flag the shader's "elsewhere" surface is driven from (PortalOpenOrange / PortalOpenBlue, 0 or 1).
	/// </summary>
	private void UpdateWallFlags()
	{
		if ( _flagsForRevision == PairRevision ) return;
		_flagsForRevision = PairRevision;
		for ( int i = 0; i < Room.Walls.Count; i++ )
		{
			PaintWall wall = Room.Walls[i];
			if ( !wall.IsValid() || wall.Panel == null ) continue;
			wall.Panel.SetLook( "PortalOpenOrange", HasPair && _endWall[0] == wall ? 1f : 0f );
			wall.Panel.SetLook( "PortalOpenBlue", HasPair && _endWall[1] == wall ? 1f : 0f );
		}
	}

	/// <summary>Called by whatever carried something through, so readouts and tests can see it.</summary>
	public CrossingRecord Record( CrossingRecord record )
	{
		CrossingCount++;
		record.Number = CrossingCount;
		record.PairRevision = PairRevision;
		record.Time = Time.NowDouble;
		_crossings.Add( record );
		if ( _crossings.Count > 64 ) _crossings.RemoveAt( 0 );
		return record;
	}
}
