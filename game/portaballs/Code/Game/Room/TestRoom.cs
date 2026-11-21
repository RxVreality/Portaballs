using System.Collections.Generic;
using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// Builds a plain box room when the game starts: floor, ceiling, four walls, one lamp.
/// Everything is made from the engine's built-in cube model, so no assets are needed.
///
/// All six surfaces take paint and can hold a portal (the tester, 2026-11-19: "I should be able to portal through
/// the floor ... and any other surface"). <see cref="Walls"/> lists them: the four walls first, then the
/// floor, then the ceiling.
///
/// The room's floor centre sits at this object's position. Keep this object unrotated and at
/// scale 1: the walls are axis-aligned boxes and are not turned to follow it.
/// </summary>
public sealed class TestRoom : Component
{
	/// <summary>Inside size along X (forward/back), metres.</summary>
	[Property] public float Length { get; set; } = 8f;

	/// <summary>Inside size along Y (left/right), metres.</summary>
	[Property] public float Width { get; set; } = 6f;

	/// <summary>Inside height, metres.</summary>
	[Property] public float Height { get; set; } = 3.5f;

	/// <summary>How thick the slabs are, metres. Thick enough that a fast ball cannot tunnel through.</summary>
	[Property] public float WallThickness { get; set; } = 0.2f;

	[Property] public Color WallTint { get; set; } = new Color( 0.82f, 0.82f, 0.80f );
	[Property] public Color FloorTint { get; set; } = new Color( 0.45f, 0.45f, 0.48f );
	[Property] public Color CeilingTint { get; set; } = new Color( 0.90f, 0.90f, 0.90f );

	/// <summary>
	/// Every surface that takes paint: the four walls in the order +X, -X, +Y, -Y (indexes 0 to 3, as
	/// always), then the floor (<see cref="FloorIndex"/>) and the ceiling (<see cref="CeilingIndex"/>).
	/// </summary>
	public IReadOnlyList<PaintWall> Walls => _walls;

	/// <summary>The same surfaces as plain core frames, ready for AimAssist.FindAimPoint and the player's movement.</summary>
	public WallFrame[] WallFrames { get; private set; } = new WallFrame[0];

	/// <summary>How many paint surfaces there are (six: four walls, floor, ceiling).</summary>
	public int WallCount => _walls.Count;

	/// <summary>Where the floor and the ceiling are in <see cref="Walls"/> and <see cref="WallFrames"/>.</summary>
	public const int FloorIndex = 4;
	public const int CeilingIndex = 5;

	/// <summary>The floor as a paint surface. Its frame: Normal +Z (into the room), U = +X, V = +Y, origin at the -X -Y corner.</summary>
	public PaintWall Floor => _walls.Count > FloorIndex ? _walls[FloorIndex] : null;

	/// <summary>The ceiling as a paint surface. Its frame: Normal -Z (into the room), U = -X, V = +Y, origin at the +X -Y corner.</summary>
	public PaintWall Ceiling => _walls.Count > CeilingIndex ? _walls[CeilingIndex] : null;

	/// <summary>
	/// Where the orange patch and the blue patch are, and their portal openings. The portal job reads this:
	/// <c>Patches.Get( PaintColor.Orange )</c>. See <see cref="PortalPatches"/>.
	/// </summary>
	public PortalPatches Patches { get; private set; }

	/// <summary>
	/// The linked portal pair, when the orange patch and the blue patch both have an opening.
	/// Balls, the player and the see-through view read this. See <see cref="PortalLink"/>.
	/// </summary>
	public PortalLink Portals { get; private set; }

	/// <summary>Draws the real view through the linked pair. See <see cref="PortalView"/>.</summary>
	public PortalView View { get; private set; }

	/// <summary>Shows a ball at both ends while it passes through. See <see cref="PortalBallTwins"/>.</summary>
	public PortalBallTwins Twins { get; private set; }

	private readonly List<PaintWall> _walls = new List<PaintWall>();
	private bool _built;

	protected override void OnStart()
	{
		Build();
	}

	/// <summary>Wipe all paint off every wall and forget both patches.</summary>
	public void ClearAllPaint()
	{
		for ( int i = 0; i < _walls.Count; i++ ) _walls[i].ClearPaint();
		if ( Patches.IsValid() ) Patches.Forget();
	}

	/// <summary>Make the room now. Safe to call more than once; only the first call does anything.</summary>
	public void Build()
	{
		if ( _built ) return;
		_built = true;

		Vec3 floorCentre = SboxConvert.ToMetres( WorldPosition );
		float L = Length, W = Width, H = Height, T = WallThickness;

		Patches = GameObject.AddComponent<PortalPatches>();

		// Run every kind of paint work once now, so the runtime's first-use pause is spent while loading
		// and not in the frame the first ball lands.
		long warmStart = System.Diagnostics.Stopwatch.GetTimestamp();
		PaintPacer.WarmUp();
		double warmMs = ( System.Diagnostics.Stopwatch.GetTimestamp() - warmStart ) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

		// Each wall is described by the direction it FACES (into the room). These four come first, so
		// their indexes (0 to 3) are what they always were.
		AddWall( "Wall +X", floorCentre, new Vec3( -1f, 0f, 0f ), L * 0.5f, W, new Vec3( T, W + 2f * T, H ) );
		AddWall( "Wall -X", floorCentre, new Vec3( 1f, 0f, 0f ), L * 0.5f, W, new Vec3( T, W + 2f * T, H ) );
		AddWall( "Wall +Y", floorCentre, new Vec3( 0f, -1f, 0f ), W * 0.5f, L, new Vec3( L, T, H ) );
		AddWall( "Wall -Y", floorCentre, new Vec3( 0f, 1f, 0f ), W * 0.5f, L, new Vec3( L, T, H ) );

		// The floor and the ceiling are paint surfaces too. Their slabs overhang by one wall thickness so the
		// corners are closed; the paint covers the inside of the room only.
		//
		// Frames (core WallFrame: U and V lie in the surface, Normal = U x V points INTO the room):
		//   floor    Normal +Z:  U = +X, V = +Y   ( X x Y = Z ),   origin at the -X -Y corner
		//   ceiling  Normal -Z:  U = -X, V = +Y   ( -X x Y = -Z ), origin at the +X -Y corner
		// V is level here, not up. U is still "to your right" for someone looking square at the surface with
		// the top of their head toward +Y. The pair of choices is deliberate: a floor patch and a ceiling
		// patch then link with NO turn at all (the crossing is a plain move), so falling for ever through a
		// ceiling over a floor never spins the player.
		Vec3 slabXY = new Vec3( L + 2f * T, W + 2f * T, T );
		AddFlat( "Floor", floorCentre + new Vec3( 0f, 0f, -T * 0.5f ), slabXY, FloorTint,
			new WallFrame( floorCentre + new Vec3( -L * 0.5f, -W * 0.5f, 0f ), new Vec3( 1f, 0f, 0f ), new Vec3( 0f, 1f, 0f ), L, W ), new Vec3( 0f, 0f, 1f ) );
		AddFlat( "Ceiling", floorCentre + new Vec3( 0f, 0f, H + T * 0.5f ), slabXY, CeilingTint,
			new WallFrame( floorCentre + new Vec3( L * 0.5f, -W * 0.5f, H ), new Vec3( -1f, 0f, 0f ), new Vec3( 0f, 1f, 0f ), L, W ), new Vec3( 0f, 0f, -1f ) );

		WallFrames = new WallFrame[_walls.Count];
		for ( int i = 0; i < _walls.Count; i++ )
		{
			WallFrames[i] = _walls[i].Frame;
		}

		// The same for the game's side of a landing (the patch rule, the reactions, the texture upload).
		long warmGameStart = System.Diagnostics.Stopwatch.GetTimestamp();
		Patches.WarmUp();
		double warmGameMs = ( System.Diagnostics.Stopwatch.GetTimestamp() - warmGameStart ) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

		Portals = GameObject.AddComponent<PortalLink>();
		Portals.Room = this;

		View = GameObject.AddComponent<PortalView>();
		View.Room = this;
		Twins = GameObject.AddComponent<PortalBallTwins>();
		Twins.Room = this;
		GameObject.AddComponent<PaintGlowLights>().Room = this;   // the light the paint throws on the room (neon look)

		AddLighting( floorCentre );

		Log.Info( $"[TestRoom] Built {L:0.0} x {W:0.0} x {H:0.0} m room with {_walls.Count} paint surfaces: 4 walls, the floor and the ceiling." );
		Log.Info( $"[TestRoom] Paint warm-up while loading took {warmMs:0.0} ms for the core and {warmGameMs:0.0} ms for the game side." );
	}

	/// <param name="inward">Unit direction the wall faces: out of the wall, into the room.</param>
	/// <param name="distance">How far the wall's inner face is from the room centre, metres.</param>
	/// <param name="width">The wall's width as you look at it, metres.</param>
	/// <param name="slabSize">Size of the solid box along world X, Y, Z, metres.</param>
	private void AddWall( string wallName, Vec3 floorCentre, Vec3 inward, float distance, float width, Vec3 slabSize )
	{
		// WallFrame (core WallFrame.cs) wants, standing in the room and facing the wall:
		//   Origin = lower-left corner, U = to your right, V = up, and U x V = Normal = toward you.
		//
		// Axes are +X forward, +Y left, +Z up, right-handed. "To your right" when the wall faces
		// 'inward' is  U = Up x inward.
		//
		// Check with the +X wall (inward = (-1, 0, 0)): you stand facing +X, so your left is +Y and
		// your right is -Y.
		//   U = Up x inward = (0,0,1) x (-1,0,0) = (0*0 - 1*0, 1*(-1) - 0*0, 0*0 - 0*(-1)) = (0,-1,0)  -> -Y, your right. Correct.
		//   U x V = (0,-1,0) x (0,0,1) = ((-1)*1 - 0*0, 0*0 - 0*1, 0*0 - (-1)*0) = (-1,0,0) = inward. Correct.
		//   Origin = foot of the wall - U * width/2 = moved toward +Y = your left. Correct.
		// The same formula gives U = +Y for the -X wall, +X for the +Y wall and -X for the -Y wall,
		// each of which is "to your right" when you face that wall. In general
		//   (Up x n) x Up = n   for any level n,  so Normal always comes out as 'inward'.
		Vec3 up = Units.Up;
		Vec3 u = Vec3.Cross( up, inward );
		Vec3 foot = floorCentre - inward * distance;          // middle of the wall's bottom edge, on its inner face
		Vec3 origin = foot - u * ( width * 0.5f );            // lower-left corner as seen from inside
		WallFrame frame = new WallFrame( origin, u, up, width, Height );

		// The solid slab sits BEHIND the inner face, so the frame's plane is the surface balls hit.
		Vec3 slabCentre = foot - inward * ( WallThickness * 0.5f ) + up * ( Height * 0.5f );
		GameObject slab = CreateSlab( wallName, slabCentre, slabSize, WallTint );

		PaintWall wall = slab.AddComponent<PaintWall>();
		wall.Setup( wallName, frame );
		Patches.AddWall( wall );   // the room runs the wall's paint and shares one work budget between the walls
		_walls.Add( wall );
		slab.Enabled = true;

		// The same check, done for real every time the room is built: the middle of the room must be
		// on the painted side, and the frame's own centre must be on the wall.
		Vec3 roomMiddle = floorCentre + up * ( Height * 0.5f );
		bool facesRoom = frame.SignedDistance( roomMiddle ) > 0f && Vec3.Dot( frame.Normal, inward ) > 0.99f;
		bool centreOnWall = frame.Contains( frame.ToUV( frame.Center ) );
		if ( !facesRoom || !centreOnWall )
		{
			Log.Warning( $"[TestRoom] {wallName}: wall frame is the wrong way round (facesRoom {facesRoom}, centreOnWall {centreOnWall})." );
		}
	}

	/// <summary>The floor or the ceiling: a slab like a wall, with paint on the face that looks into the room.</summary>
	/// <param name="inward">Unit direction the surface faces: +Z for the floor, -Z for the ceiling.</param>
	private void AddFlat( string surfaceName, Vec3 slabCentre, Vec3 slabSize, Color tint, WallFrame frame, Vec3 inward )
	{
		GameObject slab = CreateSlab( surfaceName, slabCentre, slabSize, tint );
		PaintWall surface = slab.AddComponent<PaintWall>();
		surface.Setup( surfaceName, frame );
		Patches.AddWall( surface );
		_walls.Add( surface );
		slab.Enabled = true;

		// The same check the walls get: the middle of the room is on the painted side, and the frame covers the room.
		Vec3 roomMiddle = SboxConvert.ToMetres( WorldPosition ) + Units.Up * ( Height * 0.5f );
		bool facesRoom = frame.SignedDistance( roomMiddle ) > 0f && Vec3.Dot( frame.Normal, inward ) > 0.99f;
		bool coversRoom = frame.Contains( frame.ToUV( roomMiddle ) ) && System.MathF.Abs( frame.SignedDistance( roomMiddle ) - Height * 0.5f ) < 0.001f;
		if ( !facesRoom || !coversRoom )
		{
			Log.Warning( $"[TestRoom] {surfaceName}: surface frame is the wrong way round (facesRoom {facesRoom}, coversRoom {coversRoom})." );
		}
	}

	/// <summary>A solid, unmoving box. Returned switched off so the caller can add to it before it goes live.</summary>
	private GameObject CreateSlab( string slabName, Vec3 centre, Vec3 size, Color tint )
	{
		GameObject go = new GameObject( GameObject, false, slabName );
		go.WorldPosition = SboxConvert.ToInches( centre );
		go.LocalScale = SboxConvert.BoxScaleFor( size );
		// Lets the portal window test leave the room's own shell out of a picture (PortalView.WindowTest).
		go.Tags.Add( PortalView.RoomShellTag );

		ModelRenderer renderer = go.AddComponent<ModelRenderer>();
		renderer.Model = Model.Cube;
		renderer.Tint = tint;

		// The collider is the size of the unscaled cube model; the object's scale stretches both together.
		BoxCollider collider = go.AddComponent<BoxCollider>();
		collider.Scale = new Vector3( SboxConvert.DevBoxSize, SboxConvert.DevBoxSize, SboxConvert.DevBoxSize );
		collider.Static = true;

		return go;
	}

	private void AddLighting( Vec3 floorCentre )
	{
		// A soft fill so nothing is ever black, plus one lamp under the ceiling for shape.
		AmbientLight ambient = GameObject.AddComponent<AmbientLight>();
		ambient.Color = new Color( 0.35f, 0.35f, 0.38f );

		GameObject lamp = new GameObject( GameObject, false, "Room Lamp" );
		lamp.WorldPosition = SboxConvert.ToInches( floorCentre + new Vec3( 0f, 0f, Height - 0.4f ) );

		PointLight light = lamp.AddComponent<PointLight>();
		light.LightColor = Color.White;
		light.Radius = SboxConvert.LengthToInches( System.MathF.Max( Length, Width ) * 1.5f );
		light.Shadows = false;   // keeps the test room cheap to draw

		lamp.Enabled = true;
	}
}
