using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.Rendering;
using Portaballs.Core;

namespace Portaballs;

/// <summary>How the inside of an open portal is drawn. Switchable while the game runs.</summary>
public enum PortalViewMode
{
	/// <summary>
	/// A: the real view, by the route that needs NO engine change. The far side is drawn from a virtual
	/// eye worked out on the game thread from the camera (in VR: the middle of the head), once per frame.
	/// Correct on a flat screen. In VR both eyes get the same picture.
	/// </summary>
	A = 0,

	/// <summary>
	/// B: the real view, worked out separately for each view as it is drawn (each VR eye gets its own).
	/// Needs engine patch P1 (CommandList.DrawPortal) and the project symbol PORTABALLS_P1.
	/// </summary>
	B = 1,

	/// <summary>C: no view through the portal: the dark surface with the paint rim. The safe fallback.</summary>
	C = 2,
}

/// <summary>
/// Draws the real view through an open portal. One per room (TestRoom adds it).
///
/// HOW. A linked patch is drawn by its wall's paint panel (PaintPanel + portaballs_paint.shader). Just
/// before the engine draws that panel in a view, it plays a short list of render commands that this
/// component writes every frame (Renderer.ExecuteBefore: the mechanism the engine's water uses for
/// refraction). The list draws the scene again from the VIRTUAL eye (the real eye carried through the
/// player's crossing, so the picture matches where you arrive) into a temporary picture, cut off at the
/// exit wall with the core's oblique projection, and hands that picture to the shader, which pastes it
/// by screen position inside the opening.
///
/// The three modes (<see cref="Mode"/>) differ only in who works out the virtual eye; see PortalViewMode.
///
/// CLOSE UP. The paint panel is a flat sheet 3 mm off the wall. An eye closer to it than the camera's
/// near distance sees through it for a moment before the crossing (the wall or the void would flash).
/// Inside that distance (core EyeGuard) the opening is drawn instead on a sheet fixed in front of the
/// camera ("guard sheet"), which works out per pixel what the panel would have shown there.
/// In a headset each eye is a view of its own, up to 35 mm from the middle of the head: one eye can be
/// through the wall while the head is not, and still behind the exit wall after the head has been
/// carried on. So with the per-view pictures (mode B) the panel and the sheet each decide in the shader,
/// from the place of the eye BEING DRAWN, who draws the opening (<see cref="GuardPerView"/>); this
/// component only switches the sheet on near an opening (<see cref="ArmDistance"/>) and says where the
/// boundary is. An eye past the wall inside the opening sees the far side wherever it looks deeper into
/// the wall, and the room it is in wherever it looks back out, before and after the crossing alike.
///
/// A PORTAL SEEN THROUGH A PORTAL. The engine refuses a view drawn from inside a view. With engine
/// patch P4 each view keeps its far-side pictures from frame to frame, and an opening seen inside a
/// far-side picture shows the picture kept from the frame before, looked up in world space
/// (<see cref="Feedback"/>): each frame adds one level, so two facing portals fill in as an endless
/// tunnel within a few frames, for one texture read per pixel. Without P4 (or with Feedback off) such
/// an opening is the dark surface, and the end the player has just stepped into opens over a quarter of
/// a second (<see cref="NestFadeSeconds"/>) instead of popping.
///
/// HALF RATE. With P4 a picture need not be drawn every frame (<see cref="RateMode"/>): on the frames
/// between, the kept one is shown re-projected to where that eye is now, by the same world-space
/// look-up. It follows the head exactly for the room's walls, floor and ceiling; things standing in the
/// room, and anything that moves, are a frame old on those frames.
///
/// WHERE THE OPENING IS. Inside the patch, a rim's width in from the paint's edge, and only where the
/// far patch has paint at the place that point comes out (<see cref="MatchExitShape"/>): the same rule
/// the core uses for whether the player can go through there.
///
/// COST CONTROLS: <see cref="ResolutionScale"/>, <see cref="MinPixels"/> (skip tiny), off-screen skip.
/// Measured numbers: engine\smoke\seethrough\measurements.txt.
///
/// What the engine turned out to do, and the traps: engine\notes\rendering_findings.md (first section).
/// </summary>
public sealed class PortalView : Component
{
	/// <summary>The room whose portal is drawn. Set by TestRoom.</summary>
	public TestRoom Room { get; set; }

	/// <summary>A, B or C. See <see cref="PortalViewMode"/>. B falls back to A when the engine patch is missing.</summary>
	[Property] public PortalViewMode Mode { get; set; } = PortalViewMode.A;

	/// <summary>
	/// Size of the portal's picture against the view it is pasted into: 1 = same size, 0.5 = half as wide
	/// and half as tall (a quarter of the pixels). Halves, thirds and quarters are sized by the engine
	/// for whichever view is being drawn; other values are sized from the last view seen.
	/// </summary>
	[Property] public float ResolutionScale { get; set; } = 1f;

	/// <summary>Do not draw the far side at all when the patch is smaller on screen than this many pixels, across or up.</summary>
	[Property] public float MinPixels { get; set; } = 10f;

	/// <summary>Do not draw the far side when the patch is off screen.</summary>
	[Property] public bool SkipOffScreen { get; set; } = true;

	/// <summary>
	/// How far in front of the exit wall the far side's picture starts, metres. It must be more than the
	/// paint panel's own stand-off (3 mm), or the back of the exit wall's paint would block the view.
	/// </summary>
	[Property] public float ClipOffsetMetres { get; set; } = 0.005f;

	/// <summary>
	/// Start the far side exactly where the opening's surface (the paint panel, 3 mm off the entry wall) comes out:
	/// 3 mm BEHIND the exit wall, with the exit wall's own slab left out of the picture (all of it lies on or behind
	/// that wall's plane, and its paint draws nothing seen from behind). Something pushed through the opening, a
	/// hand and its twin at the far end, is then drawn without a gap at the surface. Off = the cut 5 mm in front of
	/// the exit wall (<see cref="ClipOffsetMetres"/>), which left 8 mm of a straddling thing drawn by neither view.
	/// Used only for an end whose exit PaintWall sits on its own solid slab (the room's walls); otherwise the old cut.
	/// </summary>
	[Property] public bool SeamlessCut { get; set; } = true;

	/// <summary>
	/// Draw every far-side picture fresh this frame, whatever <see cref="RateMode"/> says: for frames in which
	/// something straddles a portal (a hand reaching through, a ball half through), whose far half would otherwise
	/// lag its near half on the re-used frames. Set it every frame it is wanted; it is not cleared by itself.
	/// The view also draws fresh by itself whenever <see cref="SomethingStraddles"/>.
	/// </summary>
	public bool ForceFresh { get; set; }

	/// <summary>
	/// A hand is reaching into an opening, or a hand or ball is shown at both ends (room.Twins, last frame's count:
	/// the twins are placed after this component's pre-render). Re-used pictures would show the far half a frame late.
	/// </summary>
	public bool SomethingStraddles => Room.IsValid() && Room.Twins.IsValid() && ( Room.Twins.HandsAtPortal > 0 || Room.Twins.ActiveTwins > 0 );

	/// <summary>
	/// When the virtual eye is closer than this behind the exit wall (metres), the far side is drawn with
	/// an ordinary projection instead of one cut off at the wall. So close, the only thing between the eye
	/// and the wall is the wall's own back, which is not drawn anyway, and the cut-off projection has too
	/// little depth precision left for things far away (paint flickers against its wall across the room).
	/// (Seen in the engine: cut at 6 mm the far wall's paint broke up; at 26 mm it was whole.) The engine
	/// does the same for its water, at twice the near distance. 0 = always cut.
	/// </summary>
	[Property] public float PlainWithinMetres { get; set; } = 0.05f;

	/// <summary>For testing only: hand the engine a plain projection matrix instead of letting it make its own.</summary>
	public bool DebugPlainWithMatrix { get; set; }

	/// <summary>
	/// Only show the far side where the far patch has paint too. The crossing carries every point of one
	/// patch to a point of the other wall; where that is bare wall the player cannot go through (the
	/// core's rule), so the opening is not drawn there either: it stays paint. Without this, a patch that
	/// hangs lower than its partner shows the space under the far room's floor along its bottom edge.
	/// </summary>
	[Property] public bool MatchExitShape { get; set; } = true;

	/// <summary>Draw the opening on the camera's guard sheet when the eye is this close to it (see class comment). Off = old behaviour.</summary>
	[Property] public bool GuardSheet { get; set; } = true;

	/// <summary>Extra metres added to the guard distance. (Only when <see cref="GuardPerView"/> is off: in VR a further 4 cm is added for the eyes being apart.)</summary>
	[Property] public float GuardMargin { get; set; } = 0.01f;

	/// <summary>
	/// The close-up hand-over as rebuilt after the tester's first walk-through (2026-11-19: "a flash of wall in gray"):
	/// the sheet hangs twice the near distance from the camera and is drawn with no depth test (so rounding can
	/// never put it in front of the near cut, and a wall cannot hide it), it follows the player to the exit wall
	/// in the very frame of the crossing, and with per-view pictures each view decides for its own eye.
	/// Off = the first version, kept only so the old fault can be shown again: the sheet 0.02% beyond the near
	/// distance, decided once per frame from the middle of the head, two frames late after a crossing.
	/// </summary>
	[Property] public bool GuardPerView { get; set; } = true;

	/// <summary>
	/// How far in front of the eye the guard sheet hangs with <see cref="GuardPerView"/>, as a multiple of the camera
	/// near distance. It writes its own depth per pixel (shader combo D_GUARD_SHEET), so where it hangs only has
	/// to be safely beyond the eye pass's real near cut: 4 x half an inch = 5 cm, against a near cut of 1.27 cm, or
	/// 2.54 cm if the headset's eye projection is the engine's VRSystem.CreateProjection (which puts the near cut
	/// at about one inch whatever ZNear says; see rendering_findings.md).
	/// </summary>
	public const float GuardSheetDistancePerView = 4f;

	/// <summary>
	/// The near distance the close-up guard allows for in a headset, inches: the larger of the camera's ZNear and
	/// the one inch at which the engine's own eye projection (VRSystem.CreateProjection) really cuts.
	/// </summary>
	public const float VrNearCutInches = 1f;

	// ---- kept pictures (engine patch P4) ----

	/// <summary>True when this build of the game was compiled against an engine with patch P4 (project symbol PORTABALLS_P4).</summary>
#if PORTABALLS_P4
	public const bool KeptCompiledIn = true;
#else
	public const bool KeptCompiledIn = false;
#endif

	/// <summary>Use the kept pictures of patch P4 for mode B when they are compiled in. Off = patch P2's pictures, made new every frame.</summary>
	[Property] public bool UseKept { get; set; } = true;

	/// <summary>
	/// A portal seen through a portal shows last frame's picture of it, one level further in (see the class
	/// comment). Off = the dark surface there, as before.
	/// </summary>
	[Property] public bool Feedback { get; set; } = true;

	/// <summary>
	/// How often each far-side picture is drawn afresh; between times it is shown re-projected. Flip it while
	/// playing with a click of the RIGHT stick (desk: key 7).
	/// </summary>
	[Property] public PortalRateMode RateMode { get; set; } = PortalRateMode.EveryFrame;

	/// <summary>What <see cref="RateMode"/> starts as in a headset.</summary>
	[Property] public PortalRateMode RateModeInHeadset { get; set; } = PortalRateMode.EveryFrame;

	/// <summary>Draw only the part of a far-side picture that its opening covers on screen.</summary>
	[Property] public bool CropToOpening { get; set; } = true;

	/// <summary>
	/// Extra drawn round that part, as a share of the view's half-width (0.08 = 4% of the view each side, about
	/// 4 degrees in the headset). It is what a re-projected picture has to spare when the head turns.
	/// </summary>
	[Property] public float CropPad { get; set; } = 0.08f;

	/// <summary>
	/// What an opening seen through an opening shows in the one frame before there is any kept picture for it
	/// (linear colour). About the room's own grey, so it is never a black hole.
	/// </summary>
	[Property] public Vector3 NoPictureColor { get; set; } = new Vector3( 0.30f, 0.30f, 0.32f );

	/// <summary>For testing: show even a fresh picture through the world-space look-up (it must then look exactly the same).</summary>
	public bool ForceLookup { get; set; }

	/// <summary>For testing: the frame number the half-rate turns are counted by. Below 0 = the engine's own.</summary>
	public int TickOverride { get; set; } = -1;

	/// <summary>True in the latest frame when mode B used the kept pictures of patch P4.</summary>
	public bool KeptInUse { get; private set; }

	/// <summary>The guard distance used in the latest frame, metres, and the head speed it allowed for, m/s.</summary>
	public float GuardDistanceNow { get; private set; }
	public float HeadSpeed { get; private set; }

	/// <summary>The wall whose openings the guard sheet is switched on for in the latest frame (null = none).</summary>
	public PaintWall ArmedWall { get; private set; }

	// ---- a head that is not the camera (the one-eye test rig) ----

	/// <summary>
	/// For testing the headset case on a flat screen: the "head" every once-per-frame decision is made for is
	/// here, while the camera that draws is somewhere else (one EYE of that head). See PortalEyeRig.
	/// </summary>
	public bool HasTestHead { get; private set; }
	public Vec3 TestHeadPosition { get; private set; }
	public Quat TestHeadRotation { get; private set; }

	public void SetTestHead( Vec3 position, Quat rotation )
	{
		HasTestHead = true;
		TestHeadPosition = position;
		TestHeadRotation = rotation;
	}

	public void ClearTestHead()
	{
		HasTestHead = false;
		TestFrameSeconds = 0f;
	}

	/// <summary>For the test rig with the clock stopped: how long a "frame" (one step of the head) stands for, seconds. 0 = the real frame time.</summary>
	public float TestFrameSeconds { get; set; }

	/// <summary>
	/// After the player steps through, the end they went INTO (which they may now see from the far side)
	/// takes this long to open from the dark surface to its picture. Seen through the portal a frame
	/// earlier it could only be the dark surface, so without this it would pop. 0 = no fade.
	/// </summary>
	[Property] public float NestFadeSeconds { get; set; } = 0.25f;

	/// <summary>
	/// For pictures taken at another shape than the window (the capture scripts render 1280 x 720 whatever
	/// the window is): width over height to use in mode A. 0 = the screen's own.
	/// </summary>
	public float AspectOverride { get; set; }

	/// <summary>
	/// The window test (experiment E1): nothing is carried anywhere. Looking into a patch shows what is
	/// straight behind that wall, with the room's own walls left out, as if the patch were a hole in it.
	/// </summary>
	public bool WindowTest { get; set; }

	// ---- what happened last frame, for the readout board and the tests ----

	/// <summary>The mode actually in use (B becomes A without the engine patch).</summary>
	public PortalViewMode ModeInUse { get; private set; }

	/// <summary>True when this build of the game was compiled against the patched engine.</summary>
#if PORTABALLS_P1
	public const bool PatchCompiledIn = true;
#else
	public const bool PatchCompiledIn = false;
#endif

	/// <summary>Far-side views asked for in the latest frame (0, 1 or 2).</summary>
	public int ViewsAsked { get; private set; }

	/// <summary>Ends left out in the latest frame because their patch was off screen.</summary>
	public int SkippedOffScreen { get; private set; }

	/// <summary>Ends left out in the latest frame because their patch was tiny on screen.</summary>
	public int SkippedTiny { get; private set; }

	/// <summary>True while the guard sheet is drawing an opening (the eye is nearly touching it).</summary>
	public bool GuardActive { get; private set; }

	/// <summary>True while the guard sheet is switched on near an opening but drawing nothing yet.</summary>
	public bool GuardStandingBy { get; private set; }

	/// <summary>
	/// The guard sheet is switched on (standing by) when the head is this close to an open patch's wall, metres.
	/// The engine takes two frames to start drawing a sheet that has just been switched on, so this must be
	/// more than the head can travel in that time (a fall at 6 m/s is 0.27 m in two frames at 45 a second).
	/// </summary>
	[Property] public float ArmDistance { get; set; } = 1.0f;

	/// <summary>How much of the screen each end's patch covered in the latest frame, 0..1 (index = colour).</summary>
	public float[] ScreenShare { get; } = new float[2];

	/// <summary>The virtual eye used for each end in the latest frame, metres (mode A; index = colour).</summary>
	public Vec3[] VirtualEye { get; } = new Vec3[2];
	public Quat[] VirtualRotation { get; } = new Quat[2];
	public bool[] VirtualValid { get; } = new bool[2];

	/// <summary>The render hook that reports what each drawn view saw (experiment E2).</summary>
	public PortalViewProbe Probe { get; private set; }

	public const string GuardTag = "portaballs_guard";
	public const string RoomShellTag = "portaballs_shell";

	private const int MaxWalls = 8;
	private readonly CommandList[][] _wallLists = new CommandList[MaxWalls][];
	private readonly int[] _wallFlip = new int[MaxWalls];
	private readonly CommandList[] _guardLists = new CommandList[2];
	private int _guardFlip;

	private TagSet _excludeAlways;
	private readonly TagSet[] _excludeEnd = new TagSet[2];
	private readonly bool[] _seamlessEnd = new bool[2];
	private int _excludeForRevision = -1;
	private bool _excludeForSeamless;
	private int _wallsTagged;
	public const string WallTagPrefix = "portaballs_wall_";
	private TagSet _excludeWindow;

	private GameObject _guardObject;
	private ModelRenderer _guardRenderer;
	private float _guardBuiltNear = -1f;
	private PaintWall _guardWall;
	private PaintWall _armedWall;
	private int _armedFrames;
	private int _idleFrames;
	private float _guardModeSent = -1f;
	private CommandList _guardOffList;
	/// <summary>How far in front of the eye the guard sheet hangs, as a multiple of the camera near distance.</summary>
	public const float GuardSheetDistance = 1.0002f;

	private static readonly string[] GuardTargetName = { "Portaballs portal orange (guard)", "Portaballs portal blue (guard)" };
	private readonly float[] _holesByGuard = new float[MaxWalls];
	private readonly float[] _fade = { 1f, 1f };
	private readonly float[] _fadeSent = { -1f, -1f };
	private readonly PaintWall[] _fadeSentWall = new PaintWall[2];
	private int _crossingsSeen = -1;
	private static readonly string[] FadeName = { "PortalFadeOrange", "PortalFadeBlue" };

	/// <summary>How far each end's picture has faded in after the player's last crossing, 0..1 (index = colour).</summary>
	public float FadeOf( PaintColor colour ) => _fade[(int)colour];

	private static readonly string[] HasName = { "HasPortalOrange", "HasPortalBlue" };
	private static readonly string[] TexName = { "PortalTexOrange", "PortalTexBlue" };
	private static readonly string[] RectName = { "PortalRectOrange", "PortalRectBlue" };
	private static readonly string[] TargetName = { "Portaballs portal orange", "Portaballs portal blue" };

	protected override void OnStart()
	{
		EnsureBuilt();
	}

	private void EnsureBuilt()
	{
		if ( _excludeAlways != null ) return;
		_excludeAlways = new TagSet();
		_excludeAlways.Add( GuardTag );
		_excludeWindow = new TagSet();
		_excludeWindow.Add( GuardTag );
		_excludeWindow.Add( RoomShellTag );
		for ( int i = 0; i < MaxWalls; i++ )
		{
			_wallLists[i] = new[] { new CommandList( "Portaballs portal wall " + i + " a" ), new CommandList( "Portaballs portal wall " + i + " b" ) };
			_holesByGuard[i] = -1f;
		}
		_guardLists[0] = new CommandList( "Portaballs portal guard a" );
		_guardLists[1] = new CommandList( "Portaballs portal guard b" );

		Probe = GameObject.GetComponent<PortalViewProbe>();
		if ( !Probe.IsValid() ) Probe = GameObject.AddComponent<PortalViewProbe>();
	}

	protected override void OnDisabled()
	{
		if ( Room.IsValid() )
		{
			for ( int i = 0; i < Room.Walls.Count; i++ )
			{
				if ( Room.Walls[i].IsValid() && Room.Walls[i].Panel != null ) Room.Walls[i].Panel.Renderer.ExecuteBefore = null;
			}
		}
		if ( _guardObject.IsValid() ) _guardObject.Enabled = false;
		ClearPerView();
	}

	protected override void OnPreRender()
	{
		Rebuild();
	}

	private static readonly float[] ScaleSteps = { 1f, 0.75f, 0.5f };
	private bool _heldLeftB, _heldRightB, _heldLeftA, _heldRightA;

	/// <summary>
	/// Switching while the game runs. Headset: B or Y (the upper face button, either hand) = next mode,
	/// X (left lower button) = next picture scale, A (right lower button) = the other lens source for mode B,
	/// a click of the left stick = paint the next pair of test portals.
	/// Desk: keys 1, 2, 3 = mode A, B, C; 4 = next scale; 5 = lens source; 6 = test portals.
	/// </summary>
	protected override void OnUpdate()
	{
		_frameNumber++;
		if ( Game.IsRunningInVR && Input.VR is not null )
		{
			if ( !_headsetDefaultsApplied )
			{
				_headsetDefaultsApplied = true;
				RateMode = RateModeInHeadset;
			}
			// Edges are worked out here from the held state, so it does not matter how the engine reports them.
			var left = Input.VR.LeftHand;
			var right = Input.VR.RightHand;
			bool rightStick = right is not null && right.JoystickPress.IsPressed;
			if ( rightStick && !_heldRightStick ) ToggleRate();
			_heldRightStick = rightStick;
			bool leftB = left is not null && left.ButtonB.IsPressed;
			bool rightB = right is not null && right.ButtonB.IsPressed;
			bool leftA = left is not null && left.ButtonA.IsPressed;
			bool rightA = right is not null && right.ButtonA.IsPressed;
			if ( ( leftB && !_heldLeftB ) || ( rightB && !_heldRightB ) ) NextMode();
			if ( leftA && !_heldLeftA ) NextScale();
			bool stick = left is not null && left.JoystickPress.IsPressed;
			if ( stick && !_heldStick ) NextTestLayout();
			_heldStick = stick;
			if ( rightA && !_heldRightA ) ToggleProjectionSource();
			_heldLeftB = leftB;
			_heldRightB = rightB;
			_heldLeftA = leftA;
			_heldRightA = rightA;
			return;
		}
		if ( Input.Pressed( "Slot1" ) ) SetMode( PortalViewMode.A );
		if ( Input.Pressed( "Slot2" ) ) SetMode( PortalViewMode.B );
		if ( Input.Pressed( "Slot3" ) ) SetMode( PortalViewMode.C );
		if ( Input.Pressed( "Slot4" ) ) NextScale();
		if ( Input.Pressed( "Slot5" ) ) ToggleProjectionSource();
		if ( Input.Pressed( "Slot6" ) ) NextTestLayout();
		if ( Input.Pressed( "Slot7" ) ) ToggleRate();
	}

	private bool _heldRightStick;

	/// <summary>
	/// Flip the far side between "drawn every frame" and "drawn every other frame, re-projected in between".
	/// Headset: click the RIGHT stick. Desk: key 7. Does nothing visible without engine patch P4.
	/// </summary>
	public void ToggleRate()
	{
		RateMode = RateMode == PortalRateMode.EveryFrame ? PortalRateMode.HalfRate : PortalRateMode.EveryFrame;
		Log.Info( $"[PortalView] FAR SIDE RATE {DescribeRate( RateMode )}{( KeptCompiledIn ? "" : " (needs engine patch P4, which this build was compiled without: no effect)" )}" );
	}

	public static string DescribeRate( PortalRateMode mode )
	{
		switch ( mode )
		{
			case PortalRateMode.HalfRate: return "HALF: each picture drawn every other frame, re-projected in between";
			case PortalRateMode.OnePerFrame: return "ONE A FRAME: never more than one picture drawn in a frame, the rest re-projected";
			default: return "FULL: every picture drawn every frame";
		}
	}

	private static readonly string[] TestLayouts = { "rightangle", "facing", "samewall", "high", "floor" };
	private int _testLayout = -1;
	private bool _heldStick;

	/// <summary>The test layout painted last by <see cref="NextTestLayout"/> ("" = none yet).</summary>
	public string TestLayoutName => _testLayout < 0 ? "" : TestLayouts[_testLayout];

	/// <summary>
	/// Paint the next ready-made pair of portals (and stand some coloured boxes in the room the first time),
	/// so the view can be checked without having to throw well: walls at right angles, facing walls, both
	/// on one wall, one high up, one in the floor. Headset: click the left stick. Desk: key 6.
	/// </summary>
	public void NextTestLayout()
	{
		if ( !Room.IsValid() ) return;
		_testLayout = ( _testLayout + 1 ) % TestLayouts.Length;
		SeeThroughProof.Furnish( Room );
		string what = SeeThroughProof.Layout( Room, TestLayouts[_testLayout] );
		Log.Info( $"[PortalView] TEST PORTALS {what}" );
	}

	/// <summary>The next picture scale in the order 1, 0.75, 0.5, 1.</summary>
	public void NextScale()
	{
		int at = 0;
		for ( int i = 0; i < ScaleSteps.Length; i++ )
		{
			if ( MathF.Abs( ScaleSteps[i] - ResolutionScale ) < 0.01f ) at = i;
		}
		ResolutionScale = ScaleSteps[( at + 1 ) % ScaleSteps.Length];
		Log.Info( $"[PortalView] SCALE {ResolutionScale:0.00}" );
	}

	public void ToggleProjectionSource()
	{
		ProjectionSource = ProjectionSource == 0 ? 1 : 0;
		Log.Info( $"[PortalView] LENS SOURCE for mode B: {( ProjectionSource == 0 ? "the view's side planes" : "the engine's own matrix for the view" )}" );
	}

	/// <summary>
	/// Write this frame's render commands now. Called by the engine before every frame; a script that moves
	/// the camera and takes a picture in the same call must call it in between, or the picture uses the
	/// previous camera position.
	/// </summary>
	public void Rebuild()
	{
		if ( !Room.IsValid() || Room.WallCount == 0 ) return;
		EnsureBuilt();

		CameraComponent camera = Scene.Camera;
		PortalLink portals = Room.Portals;
		if ( !camera.IsValid() || !portals.IsValid() ) return;
		portals.Sync();

		PortalViewMode mode = Mode;
		if ( mode == PortalViewMode.B && !PatchCompiledIn ) mode = PortalViewMode.A;
		ModeInUse = mode;
		// Mode B with patch P2: the engine draws each end once per view (per eye) and hands it to that view. No
		// command lists on the panels at all then: see PerViewCompiledIn.
		bool perView = mode == PortalViewMode.B && PerViewCompiledIn && !WindowTest && !UseP1ForModeB;
		PerViewInUse = perView;

		ReadCamera( camera, out Vec3 eye, out Quat eyeRotation, out LensTangents drawLens, out LensTangents cullLens, out float viewWidth, out float viewHeight, out bool vr );

		float nearMetres = camera.ZNear * SboxConvert.MetresPerInch;

		// How fast the head is moving (a jump of more than a metre is a crossing, not a speed).
		bool testStep = HasTestHead && TestFrameSeconds > 0f;
		float dt = testStep ? TestFrameSeconds : Time.Delta;
		bool newFrame = testStep ? ( eye - _lastHead ).LengthSquared() > 1e-10f : _lastHeadFrame != _frameNumber;
		if ( _hasLastHead && dt > 0f && newFrame )
		{
			float moved = ( eye - _lastHead ).Length();
			if ( moved < 1f ) HeadSpeed = moved / dt;
		}
		if ( newFrame || !_hasLastHead )
		{
			_lastHead = eye;
			_hasLastHead = true;
			_lastHeadFrame = _frameNumber;
		}

		// With per-view pictures and the rebuilt guard, each view decides in the shader from its own eye, so the
		// boundary only has to cover the near cut for ONE eye (and a frame's travel); the first version decided
		// once from the middle of the head and added 4 cm for the eyes being apart.
		bool keptWanted = perView && KeptCompiledIn && UseKept;
		bool shaderDecides = perView && GuardPerView;
		// In a headset the eye pass may cut at about an inch whatever ZNear says (VrNearCutInches); allow for it.
		float guardNear = vr ? MathF.Max( nearMetres, VrNearCutInches * SboxConvert.MetresPerInch ) : nearMetres;
		float guardDistance = GuardPerView
			? EyeGuard.Distance( guardNear, cullLens, GuardMargin, PaintWall.PanelOffsetMetres, HeadSpeed, dt )
			: PortalViewMath.GuardDistance( nearMetres, cullLens, GuardMargin + ( vr ? 0.04f : 0f ), PaintWall.PanelOffsetMetres );
		GuardDistanceNow = guardDistance;
		KeptInUse = keptWanted;

		ViewsAsked = 0;
		SkippedOffScreen = 0;
		SkippedTiny = 0;
		ScreenShare[0] = ScreenShare[1] = 0f;
		VirtualValid[0] = VirtualValid[1] = false;

		bool linked = portals.HasPair;
		PortalPair pair = linked ? portals.Pair : default;
		bool feedbackOn = keptWanted && Feedback;
		UpdateFades( portals, linked, feedbackOn );
		UpdateExitMaps( portals, linked, pair );
		UpdateLookupConstants( portals, linked, pair, feedbackOn && mode != PortalViewMode.C );
		UpdateEndExcludes( portals, linked );

		// ---- which wall, if any, is the eye near at an opening ----
		// Two distances. Within ArmDistance the guard sheet is switched on but draws nothing (standing by).
		// Within the guard distance, and once the sheet has been standing by for two frames, it takes the
		// openings over from the wall's panel. Both the take-over and the hand-back are then nothing but
		// two shader values changing in the same frame, so there is never a frame with the opening drawn
		// by neither (or by a sheet that the engine has not finished adding or removing).
		PaintWall nearWall = null;
		float nearDistance = float.MaxValue;
		if ( GuardSheet && linked )
		{
			float armDistance = GuardPerView ? MathF.Max( ArmDistance, guardDistance + 0.1f ) : MathF.Min( ArmDistance, 0.5f );
			float behindLimit = GuardPerView ? -0.25f : -0.10f;
			for ( int c = 0; c < 2; c++ )
			{
				PaintWall wall = portals.WallOf( (PaintColor)c );
				if ( !wall.IsValid() ) continue;
				PortalOpening opening = portals.EndOf( (PaintColor)c ).Opening;
				float d = wall.Frame.SignedDistance( eye );
				if ( d > armDistance || d < behindLimit ) continue;
				// Only when the eye is over the patch (or within a hand's width of it, more when further out).
				Vec2 uv = wall.Frame.ToUV( eye );
				float reach = ( GuardPerView ? 0.25f : 0.15f ) + MathF.Max( 0f, d );
				if ( uv.X < opening.BoundsMin.X - reach || uv.X > opening.BoundsMax.X + reach || uv.Y < opening.BoundsMin.Y - reach || uv.Y > opening.BoundsMax.Y + reach ) continue;
				if ( d < nearDistance ) { nearDistance = d; nearWall = wall; }
			}
		}
		PaintWall armedWall, guardWall;
		if ( GuardPerView )
		{
			// The sheet is one object that serves whichever wall the head is at: when the head is carried through,
			// it is pointed at the exit wall in the same frame (shader values on an object that already exists
			// change at once). Only a sheet that has just been switched on has to wait: the engine starts drawing
			// it two frames later.
			bool sheetReady = _guardObject.IsValid() && _guardObject.Enabled && _frameNumber - _guardEnabledAtFrame >= 2;
			armedWall = nearWall.IsValid() && sheetReady ? nearWall : null;
			guardWall = armedWall.IsValid() && nearDistance < guardDistance ? armedWall : null;
		}
		else
		{
			if ( nearWall.IsValid() && nearWall == _armedWall ) _armedFrames++;
			else _armedFrames = 0;
			guardWall = nearWall.IsValid() && _armedFrames >= 2 && nearDistance <= guardDistance ? nearWall : null;
			armedWall = guardWall;
		}
		_armedWall = nearWall;
		ArmedWall = armedWall;
		// For the board: the sheet is (or, per view, may be) drawing an opening.
		GuardActive = shaderDecides ? armedWall.IsValid() && nearDistance < guardDistance + 0.05f : guardWall.IsValid();
		GuardStandingBy = nearWall.IsValid() && !GuardActive;
		// Who leaves the openings out: decided per view in the shader (the flag only says the sheet is there),
		// or decided here once for the one view there is.
		PaintWall holesWall = shaderDecides ? armedWall : guardWall;
		float guardInches = shaderDecides ? SboxConvert.LengthToInches( guardDistance ) : 1000000f;

		// ---- one list per wall ----
		int walls = Math.Min( Room.WallCount, MaxWalls );
		for ( int i = 0; i < walls; i++ )
		{
			PaintWall wall = Room.Walls[i];
			if ( !wall.IsValid() || wall.Panel == null ) continue;

			bool byGuard = guardWall == wall;
			float flag = holesWall == wall ? 1f : 0f;
			if ( _holesByGuard[i] != flag )
			{
				_holesByGuard[i] = flag;
				wall.Panel.SetLook( "PortalHolesByGuard", flag );
			}
			// Where the sheet's camera is: only views drawn from there leave the openings to the sheet.
			if ( flag > 0.5f )
			{
				wall.Panel.Renderer.Attributes.Set( "GuardEyeWs", SboxConvert.ToInches( eye ) );
				wall.Panel.Renderer.Attributes.Set( "GuardDistance", guardInches );
			}

			if ( perView )
			{
				// The pictures come with each view (PortalViews). A list here would only get in their way.
				wall.Panel.Renderer.ExecuteBefore = null;
				continue;
			}

			CommandList list = _wallLists[i][_wallFlip[i] ^= 1];
			list.Reset();
			// Always say "no picture" first: a wall with no open end must not inherit another wall's picture.
			list.Attributes.Set( HasName[0], false );
			list.Attributes.Set( HasName[1], false );

			if ( linked && !byGuard && mode != PortalViewMode.C )
			{
				for ( int c = 0; c < 2; c++ )
				{
					if ( portals.WallOf( (PaintColor)c ) != wall ) continue;
					AddEnd( list, camera, pair, c, wall, eye, eyeRotation, drawLens, cullLens, nearMetres, viewWidth, viewHeight, mode, false, vr );
				}
			}
			wall.Panel.Renderer.ExecuteBefore = list;
		}

		// ---- patch P2: tell the engine which ends to draw in every view ----
		// An end at the sheet's wall is wanted while ANY eye may be in the guard distance (eyes are within 4 cm of the head).
		PaintWall wantedForSheet = shaderDecides ? ( armedWall.IsValid() && nearDistance < guardDistance + 0.06f ? armedWall : null ) : guardWall;
		if ( perView ) SetPerViewEnds( camera, portals, pair, linked, wantedForSheet, eye, eyeRotation, cullLens, nearMetres, viewWidth, viewHeight, vr, keptWanted, guardDistance );
		else ClearPerView();

		// ---- the guard sheet ----
		UpdateGuard( camera, nearWall, shaderDecides ? armedWall.IsValid() : guardWall.IsValid(), pair, linked, eye, eyeRotation, drawLens, cullLens, nearMetres, viewWidth, viewHeight, mode, vr, perView, guardInches );
	}

	private Vec3 _lastHead;
	private bool _hasLastHead;
	private int _lastHeadFrame = -1;
	private int _frameNumber;
	private int _guardEnabledAtFrame;
	private float _guardBuiltDistance = -1f;
	private bool _headsetDefaultsApplied;

	/// <summary>
	/// True when the engine carries patch P2 (PortalViews: each end drawn once per view, so each VR eye gets its
	/// own picture). Set by the project symbol PORTABALLS_P2. Mode B then uses it instead of patch P1, whose command
	/// list the engine was seen to run in the left eye's pass only (the tester's headset, 2026-11-19).
	/// </summary>
#if PORTABALLS_P2
	public const bool PerViewCompiledIn = true;
#else
	public const bool PerViewCompiledIn = false;
#endif

	/// <summary>For comparison only: mode B through patch P1's command list (left eye only in a headset) even when P2 is there.</summary>
	public bool UseP1ForModeB { get; set; }

	/// <summary>True in the latest frame when the pictures were left to the engine's per-view hook (patch P2).</summary>
	public bool PerViewInUse { get; private set; }

#if PORTABALLS_P2
	private bool _perViewSet;
#endif

	private void ClearPerView()
	{
#if PORTABALLS_P2
		if ( _perViewSet ) PortalViews.Clear();
		_perViewSet = false;
#endif
	}

	// Patch P2: which ends to draw, and how. The engine works out the eye and the lens for each view itself.
	private void SetPerViewEnds( CameraComponent camera, PortalLink portals, in PortalPair pair, bool linked, PaintWall guardWall, Vec3 eye, Quat eyeRotation,
		LensTangents cullLens, float nearMetres, float viewWidth, float viewHeight, bool vr, bool kept, float guardDistance )
	{
#if PORTABALLS_P2
		var ends = new PortalViews.End[2];
		for ( int c = 0; c < 2; c++ )
		{
			ends[c].HasAttribute = HasName[c];
			ends[c].TextureAttribute = TexName[c];
			ends[c].RectAttribute = RectName[c];
#if PORTABALLS_P4
			if ( kept )
			{
				ends[c].ModeAttribute = ModeName[c];
				ends[c].Vp0Attribute = Vp0Name[c];
				ends[c].Vp1Attribute = Vp1Name[c];
				ends[c].Vp3Attribute = Vp3Name[c];
				ends[c].ValidAttribute = ValidName[c];
			}
#endif
			if ( !linked ) continue;
			PaintWall wall = portals.WallOf( (PaintColor)c );
			if ( !wall.IsValid() || wall.Panel == null ) continue;
			// The guard sheet draws this end from the same picture, so it is wanted whenever the sheet is on this wall.
			if ( guardWall != wall && !EndWorthDrawing( pair, c, wall, eye, eyeRotation, cullLens, nearMetres, viewWidth, viewHeight, vr ) ) continue;

			bool throughA = c == 0;
			PortalEnd entry = throughA ? pair.A : pair.B;
			PortalEnd exit = throughA ? pair.B : pair.A;
			RigidTransform crossing = pair.PlayerCrossing( throughA );
			Plane3 keep = PortalCamera.ClipPlaneWorld( exit, ClipFor( c ) );
			ends[c].Active = true;
			ends[c].Crossing = new Transform( SboxConvert.ToInches( crossing.Translation ), SboxConvert.ToEngine( crossing.Rotation ) );
			ends[c].Keep = new Plane( SboxConvert.ToEngine( keep.Normal ), -keep.D * SboxConvert.InchesPerMetre );
			ends[c].PlainWithin = SboxConvert.LengthToInches( PlainWithinMetres );
			ends[c].ProjectionSource = ProjectionSource;
			ends[c].ExcludeTags = ExcludeFor( c );
#if PORTABALLS_P4
			if ( kept )
			{
				// So the engine can judge each view by its own eye: the opening's box on the wall (only that part
				// of the view is drawn), and the wall itself (a view that looks away from it gets no picture).
				WallFrame frame = wall.Frame;
				Vec2 pad = new Vec2( 0.06f, 0.06f );
				Vec2 lo = entry.Opening.BoundsMin - pad, hi = entry.Opening.BoundsMax + pad;
				ends[c].HasCorners = true;
				ends[c].Corner0 = SboxConvert.ToInches( frame.ToWorld( new Vec2( lo.X, lo.Y ), PaintWall.PanelOffsetMetres ) );
				ends[c].Corner1 = SboxConvert.ToInches( frame.ToWorld( new Vec2( hi.X, lo.Y ), PaintWall.PanelOffsetMetres ) );
				ends[c].Corner2 = SboxConvert.ToInches( frame.ToWorld( new Vec2( hi.X, hi.Y ), PaintWall.PanelOffsetMetres ) );
				ends[c].Corner3 = SboxConvert.ToInches( frame.ToWorld( new Vec2( lo.X, hi.Y ), PaintWall.PanelOffsetMetres ) );
				ends[c].HasEntry = true;
				ends[c].Entry = new Plane( SboxConvert.ToEngine( frame.Normal ), Vec3.Dot( frame.Normal, frame.Origin ) * SboxConvert.InchesPerMetre );
				ends[c].FullWithin = SboxConvert.LengthToInches( guardDistance + 0.05f );
			}
#endif
			ViewsAsked++;
		}
#if PORTABALLS_P4
		if ( kept )
		{
			// Everything is drawn afresh when the player has been carried through or a patch has moved.
			if ( _generationRevision != portals.PairRevision || _generationCrossings != _playerCrossingsSeen )
			{
				_generationRevision = portals.PairRevision;
				_generationCrossings = _playerCrossingsSeen;
				_generation++;
			}
			var options = new PortalViews.Options
			{
				Scale = ResolutionScale,
				Crop = CropToOpening,
				CropPad = CropPad,
				RateMode = ForceFresh || SomethingStraddles ? 0 : (int)RateMode,
				Eyes = Game.IsRunningInVR ? 2 : 1,
				Feedback = Feedback,
				Generation = _generation,
				Tick = TickOverride,
				ForceLookup = ForceLookup,
			};
			PortalViews.Set( camera, ends[0], ends[1], options );
			_perViewSet = true;
			return;
		}
#endif
		PortalViews.Set( camera, ends[0], ends[1], ResolutionScale );
		_perViewSet = true;
#endif
	}

	private static readonly string[] ModeName = { "PortalModeOrange", "PortalModeBlue" };
	private static readonly string[] Vp0Name = { "PortalVp0Orange", "PortalVp0Blue" };
	private static readonly string[] Vp1Name = { "PortalVp1Orange", "PortalVp1Blue" };
	private static readonly string[] Vp3Name = { "PortalVp3Orange", "PortalVp3Blue" };
	private static readonly string[] ValidName = { "PortalValidOrange", "PortalValidBlue" };
	private static readonly string[][] CrossName =
	{
		new[] { "PortalCrossOrange0", "PortalCrossOrange1", "PortalCrossOrange2" },
		new[] { "PortalCrossBlue0", "PortalCrossBlue1", "PortalCrossBlue2" },
	};
	private int _generation = 1;
#if PORTABALLS_P4
	private int _generationRevision = -1;
	private int _generationCrossings = -1;
#endif
	private int _playerCrossingsSeen;
	private int _lookupForRevision = -1;
	private float _noPictureSent = -1f;
	private readonly Vector4[][] _cross = { new Vector4[3], new Vector4[3] };
	private Vector3 _roomMin, _roomMax;

	/// <summary>Where the far side of an end starts, metres in front of its EXIT wall (negative = behind it). See <see cref="SeamlessCut"/>.</summary>
	public float ClipFor( int colour )
	{
		return _seamlessEnd[colour & 1] ? -PaintWall.PanelOffsetMetres : ClipOffsetMetres;
	}

	/// <summary>Tags left out of an end's far-side picture: the guard sheet, and with <see cref="SeamlessCut"/> the exit wall's slab.</summary>
	public TagSet ExcludeFor( int colour )
	{
		EnsureBuilt();
		return _excludeEnd[colour & 1] ?? _excludeAlways;
	}

	// Each wall's slab gets a tag of its own, so a far-side picture can leave its exit wall out. New sets every time
	// they change: the engine is handed them and they must not change under it.
	private void UpdateEndExcludes( PortalLink portals, bool linked )
	{
		if ( _wallsTagged != Room.WallCount )
		{
			for ( int i = 0; i < Room.WallCount; i++ )
			{
				PaintWall w = Room.Walls[i];
				if ( w.IsValid() ) w.GameObject.Tags.Add( WallTagPrefix + i );
			}
			_wallsTagged = Room.WallCount;
			_excludeForRevision = -1;
		}
		if ( !linked ) { _excludeEnd[0] = _excludeEnd[1] = null; _seamlessEnd[0] = _seamlessEnd[1] = false; _excludeForRevision = -1; return; }
		if ( _excludeForRevision == portals.PairRevision && _excludeForSeamless == SeamlessCut ) return;
		_excludeForRevision = portals.PairRevision;
		_excludeForSeamless = SeamlessCut;
		for ( int c = 0; c < 2; c++ )
		{
			PaintWall exitWall = portals.WallOf( (PaintColor)( 1 - c ) );
			int index = -1;
			for ( int i = 0; i < Room.WallCount; i++ ) if ( Room.Walls[i] == exitWall ) index = i;
			// Only when the exit wall is a slab of its own that the tag really leaves out (the room's walls are).
			bool slab = exitWall.IsValid() && index >= 0 && exitWall.GameObject.GetComponent<ModelRenderer>().IsValid();
			_seamlessEnd[c] = SeamlessCut && slab;
			var set = new TagSet();
			set.Add( GuardTag );
			if ( _seamlessEnd[c] ) set.Add( WallTagPrefix + index );
			_excludeEnd[c] = set;
		}
	}

	/// <summary>How many times the far-side pictures have all been made new at once (a crossing, or a patch moved).</summary>
	public int Generation => _generation;

	/// <summary>
	/// The engine's running counts for the kept pictures (patch P4), all since the engine started: pictures drawn
	/// afresh, views handed a kept picture to re-project, far-side pictures whose own openings were fed a kept
	/// picture, pictures skipped for a view looking away / with the opening out of view, pixels drawn and what
	/// they would have been uncut, and how many views have pictures kept for them. All 0 without the patch.
	/// </summary>
	public static void EngineCounts( out int fresh, out int kept, out int nested, out int lookingAway, out int outOfView, out long pixels, out long pixelsUncut, out int sets )
	{
#if PORTABALLS_P4
		fresh = PortalViews.FreshDrawn;
		kept = PortalViews.KeptShown;
		nested = PortalViews.NestedViewsFed;
		lookingAway = PortalViews.SkippedLookingAway;
		outOfView = PortalViews.SkippedOutOfView;
		pixels = PortalViews.PixelsDrawn;
		pixelsUncut = PortalViews.PixelsUncut;
		sets = PortalViews.Slots;
#else
		fresh = kept = nested = lookingAway = outOfView = sets = 0;
		pixels = pixelsUncut = 0;
#endif
	}

	/// <summary>Forget every kept picture (they are made again as needed). For tests.</summary>
	public static void DropKeptPictures()
	{
#if PORTABALLS_P4
		PortalViews.DropKept();
#endif
	}

	// What the paint shader needs to look a KEPT picture up in world space: each end's crossing (so a line of
	// sight can be carried through it) and the room's box (which stands in for the depth of what is seen).
	private void UpdateLookupConstants( PortalLink portals, bool linked, in PortalPair pair, bool noPictureIsPlausible )
	{
		if ( !linked ) { _lookupForRevision = -1; return; }
		float noPicture = noPictureIsPlausible ? 1f : 0f;
		if ( _lookupForRevision == portals.PairRevision && _noPictureSent == noPicture ) return;
		_lookupForRevision = portals.PairRevision;
		_noPictureSent = noPicture;

		Vec3 floor = SboxConvert.ToMetres( Room.WorldPosition );
		_roomMin = SboxConvert.ToInches( floor + new Vec3( -Room.Length * 0.5f, -Room.Width * 0.5f, 0f ) );
		_roomMax = SboxConvert.ToInches( floor + new Vec3( Room.Length * 0.5f, Room.Width * 0.5f, Room.Height ) );
		Vector4 none = new Vector4( NoPictureColor.x, NoPictureColor.y, NoPictureColor.z, noPicture );

		for ( int c = 0; c < 2; c++ )
		{
			RigidTransform crossing = pair.PlayerCrossing( c == 0 );
			// p' = turn * p + move. Mat4 is row-vector, so row i of the turn (for a column vector) is its column i.
			Mat4 turn = Mat4.CreateFromQuaternion( crossing.Rotation );
			Vec3 move = crossing.Translation * SboxConvert.InchesPerMetre;
			_cross[c][0] = new Vector4( turn.M11, turn.M21, turn.M31, move.X );
			_cross[c][1] = new Vector4( turn.M12, turn.M22, turn.M32, move.Y );
			_cross[c][2] = new Vector4( turn.M13, turn.M23, turn.M33, move.Z );
		}
		for ( int c = 0; c < 2; c++ )
		{
			PaintWall wall = portals.WallOf( (PaintColor)c );
			if ( !wall.IsValid() || wall.Panel == null ) continue;
			SetLookupConstants( wall.Panel.Renderer.Attributes, none );
		}
		_guardWall = null;   // the guard sheet copies these the next time it is set up
	}

	private void SetLookupConstants( RenderAttributes a, Vector4 none )
	{
		// Both ends' crossings on every such wall: the shader also reads the OTHER end's kept picture when its own misses.
		for ( int c = 0; c < 2; c++ )
		{
			for ( int r = 0; r < 3; r++ ) a.Set( CrossName[c][r], _cross[c][r] );
		}
		a.Set( "PortalRoomMin", _roomMin );
		a.Set( "PortalRoomMax", _roomMax );
		a.Set( "PortalNoPicture", none );
	}

	// Is the patch on screen, and big enough to be worth a second look at the room? (Also counts the skips.)
	private bool EndWorthDrawing( in PortalPair pair, int colour, PaintWall wall, Vec3 eye, Quat eyeRotation, LensTangents cullLens, float nearMetres,
		float viewWidth, float viewHeight, bool vr )
	{
		PortalEnd entry = colour == 0 ? pair.A : pair.B;
		Vec2 pad = new Vec2( vr ? 0.10f : 0.02f, vr ? 0.10f : 0.02f );
		ScreenCover cover = PortalViewMath.CoverOf( wall.Frame, entry.Opening.BoundsMin - pad, entry.Opening.BoundsMax + pad, PaintWall.PanelOffsetMetres, eye, eyeRotation, cullLens, nearMetres );
		ScreenShare[colour] = cover.WidthFraction * cover.HeightFraction;
		if ( SkipOffScreen && !cover.Visible )
		{
			SkippedOffScreen++;
			return false;
		}
		if ( cover.Visible && MinPixels > 0f && ( cover.WidthFraction * viewWidth < MinPixels || cover.HeightFraction * viewHeight < MinPixels ) )
		{
			SkippedTiny++;
			return false;
		}
		return true;
	}

	/// <summary>
	/// The eye this frame's decisions are made for, and its lens: the camera on a flat screen, the middle
	/// of the head in a headset (the engine keeps each eye's own pose to itself until it draws).
	/// </summary>
	/// <param name="drawLens">The lens mode A draws with.</param>
	/// <param name="cullLens">The lens "is it on screen" is asked with (wider in VR: two eyes, guessed lens).</param>
	public void ReadCamera( CameraComponent camera, out Vec3 eye, out Quat eyeRotation, out LensTangents drawLens, out LensTangents cullLens,
		out float viewWidth, out float viewHeight, out bool vr )
	{
		EnsureBuilt();
		vr = Game.IsRunningInVR && Input.VR is not null;
		Transform eyeEngine = vr ? Input.VR.Head : camera.WorldTransform;
		SboxConvert.ToCorePose( eyeEngine, out eye, out eyeRotation );

		float aspect = AspectOverride > 0f ? AspectOverride : ScreenAspect();
		if ( HasTestHead && !vr )
		{
			// The one-eye test rig: decisions are made for a head that is not the camera, as in a headset, and
			// "is it on screen" is asked with a wide lens because the eye that draws is off to one side of it.
			eye = TestHeadPosition;
			eyeRotation = TestHeadRotation;
			drawLens = LensTangents.Symmetric( camera.FieldOfView, aspect );
			float wide = MathF.Max( drawLens.MaxHorizontal, drawLens.MaxVertical ) * 1.25f;
			cullLens = new LensTangents( -wide, wide, -wide, wide );
			viewHeight = MathF.Max( 1f, Screen.Height );
			viewWidth = viewHeight * aspect;
			vr = true;
			return;
		}
		if ( vr )
		{
			// Mode A can only draw one picture for both eyes. The first eye's own lens angles (measured by
			// the probe from the views the engine really drew) make it line up in that eye at least.
			bool measured = Probe.IsValid() && Probe.EyeCount > 0 && Probe.Eye( 0 ).Lens.IsValid;
			drawLens = measured ? Probe.Eye( 0 ).Lens : LensTangents.Symmetric( 100f, 1f );
			float wide = measured ? MathF.Max( drawLens.MaxHorizontal, drawLens.MaxVertical ) * 1.25f : 1.8f;
			cullLens = new LensTangents( -wide, wide, -wide, wide );
			viewWidth = measured ? Probe.Eye( 0 ).ViewportWidth : 2000f;
			viewHeight = measured ? Probe.Eye( 0 ).ViewportHeight : 2000f;
		}
		else
		{
			drawLens = LensTangents.Symmetric( camera.FieldOfView, aspect );
			cullLens = drawLens;
			viewHeight = MathF.Max( 1f, Screen.Height );
			viewWidth = viewHeight * aspect;
		}
	}

	/// <summary>Tags left out of every far-side picture (the camera's guard sheet).</summary>
	public TagSet ExcludeAlways { get { EnsureBuilt(); return _excludeAlways; } }

	private static readonly string[] ExitMaskName = { "PortalExitMaskOrange", "PortalExitMaskBlue" };
	private static readonly string[] ExitMapAName = { "PortalExitMapOrangeA", "PortalExitMapBlueA" };
	private static readonly string[] ExitMapBName = { "PortalExitMapOrangeB", "PortalExitMapBlueB" };
	private readonly Vector4[] _exitMapA = new Vector4[2];
	private readonly Vector4[] _exitMapB = new Vector4[2];
	private readonly Texture[] _exitMask = new Texture[2];
	private int _exitMapsForRevision = -1;
	private float _matchSent = -1f;

	// Tell each end's wall how its own 0..1 position maps onto the other wall, and give it that wall's
	// paint, so its shader can ask "is there paint where this point comes out?" (MatchExitShape).
	private void UpdateExitMaps( PortalLink portals, bool linked, in PortalPair pair )
	{
		float match = MatchExitShape && linked ? 1f : 0f;
		if ( !linked ) { _exitMapsForRevision = -1; return; }
		if ( _exitMapsForRevision == portals.PairRevision && _matchSent == match ) return;
		_exitMapsForRevision = portals.PairRevision;
		_matchSent = match;

		for ( int c = 0; c < 2; c++ )
		{
			bool throughA = c == 0;
			PaintWall entryWall = portals.WallOf( (PaintColor)c );
			PaintWall exitWall = portals.WallOf( (PaintColor)( 1 - c ) );
			if ( !entryWall.IsValid() || !exitWall.IsValid() || entryWall.Panel == null || exitWall.Panel == null ) continue;
			WallFrame e = entryWall.Frame, x = exitWall.Frame;
			RigidTransform crossing = pair.PlayerCrossing( throughA );

			// The whole map is three numbers pairs (both walls are flat, the crossing is rigid): core PortalViewMath.
			PortalViewMath.FarWallMap( e, x, crossing, out Vec2 origin, out Vec2 alongU, out Vec2 alongV );
			_exitMapA[c] = new Vector4( origin.X, origin.Y, alongU.X, alongU.Y );
			_exitMapB[c] = new Vector4( alongV.X, alongV.Y, exitWall.Surface.Width, exitWall.Surface.Height );
			_exitMask[c] = exitWall.Panel.Mask;

			RenderAttributes a = entryWall.Panel.Renderer.Attributes;
			a.Set( ExitMaskName[c], _exitMask[c] );
			a.Set( ExitMapAName[c], _exitMapA[c] );
			a.Set( ExitMapBName[c], _exitMapB[c] );
			a.Set( "PortalMatchExit", match );
		}
		_guardWall = null;   // the guard sheet copies these the next time it is set up
	}

	// The end the player has just gone into starts from the dark surface again and opens over a moment.
	private void UpdateFades( PortalLink portals, bool linked, bool feedbackOn )
	{
		int count = portals.CrossingCount;
		if ( _crossingsSeen < 0 ) _crossingsSeen = count;
		if ( count != _crossingsSeen )
		{
			IReadOnlyList<PortalLink.CrossingRecord> list = portals.Crossings;
			int fresh = Math.Min( count - _crossingsSeen, list.Count );
			for ( int i = list.Count - fresh; i < list.Count; i++ )
			{
				if ( i < 0 || !list[i].IsPlayer ) continue;
				_playerCrossingsSeen++;
				// With kept pictures the end seen through the portal a frame earlier already had a picture in it,
				// so there is nothing to fade in from: no dark patch after stepping through.
				if ( NestFadeSeconds > 0f && !feedbackOn ) _fade[list[i].FromOrange ? 0 : 1] = 0f;
			}
			_crossingsSeen = count;
		}
		if ( feedbackOn ) _fade[0] = _fade[1] = 1f;
		for ( int c = 0; c < 2; c++ )
		{
			if ( _fade[c] < 1f )
			{
				// Game time: a stopped clock (a picture being taken) holds the fade where it is.
				float step = NestFadeSeconds > 0f ? Time.Delta / NestFadeSeconds : 1f;
				// The frame of the crossing itself shows it fully dark.
				if ( _fade[c] > 0f || _fadeSent[c] == 0f ) _fade[c] = MathF.Min( 1f, _fade[c] + step );
			}
			PaintWall wall = linked ? portals.WallOf( (PaintColor)c ) : null;
			if ( !wall.IsValid() || wall.Panel == null ) continue;
			if ( _fadeSent[c] != _fade[c] || _fadeSentWall[c] != wall )
			{
				_fadeSent[c] = _fade[c];
				_fadeSentWall[c] = wall;
				wall.Panel.SetLook( FadeName[c], _fade[c] );
			}
		}
	}

	private static float ScreenAspect()
	{
		float h = MathF.Max( 1f, Screen.Height );
		return MathF.Max( 0.1f, Screen.Width / h );
	}

	/// <summary>Add the far-side view of one end to a list, unless its patch is off screen or tiny.</summary>
	private void AddEnd( CommandList list, CameraComponent camera, in PortalPair pair, int colour, PaintWall wall, Vec3 eye, Quat eyeRotation,
		LensTangents drawLens, LensTangents cullLens, float nearMetres, float viewWidth, float viewHeight, PortalViewMode mode, bool forGuard, bool vr )
	{
		bool throughA = colour == 0;
		PortalEnd entry = throughA ? pair.A : pair.B;
		PortalEnd exit = throughA ? pair.B : pair.A;

		if ( !forGuard )
		{
			// Is the patch on screen, and big enough to be worth a second look at the room?
			Vec2 pad = new Vec2( vr ? 0.10f : 0.02f, vr ? 0.10f : 0.02f );
			ScreenCover cover = PortalViewMath.CoverOf( wall.Frame, entry.Opening.BoundsMin - pad, entry.Opening.BoundsMax + pad, PaintWall.PanelOffsetMetres, eye, eyeRotation, cullLens, nearMetres );
			ScreenShare[colour] = cover.WidthFraction * cover.HeightFraction;
			if ( SkipOffScreen && !cover.Visible )
			{
				SkippedOffScreen++;
				return;
			}
			if ( cover.Visible && MinPixels > 0f && ( cover.WidthFraction * viewWidth < MinPixels || cover.HeightFraction * viewHeight < MinPixels ) )
			{
				SkippedTiny++;
				return;
			}
		}
		else
		{
			ScreenShare[colour] = 1f;
		}

		RigidTransform crossing;
		Plane3 keep;
		TagSet exclude;
		if ( WindowTest )
		{
			// Nothing is carried: look straight on through the wall, keep what is behind the panel.
			crossing = RigidTransform.Identity;
			Vec3 n = -entry.Wall.Normal;
			keep = new Plane3( n, -Vec3.Dot( n, entry.Wall.Origin ) - 0.002f );
			exclude = _excludeWindow;
		}
		else
		{
			crossing = pair.PlayerCrossing( throughA );
			keep = PortalCamera.ClipPlaneWorld( exit, ClipFor( colour ) );
			exclude = ExcludeFor( colour );
		}

		AddView( list, camera, colour, crossing, keep, exclude, eye, eyeRotation, drawLens, viewWidth, viewHeight, mode, forGuard ? 2 : 1 );
	}

	/// <summary>
	/// Write the commands that draw one far-side picture and hand it to the shader, into a list. Used for
	/// the two portal ends here and by the free-standing window test.
	/// </summary>
	/// <param name="slot">0 = the shader's "orange" picture, 1 = its "blue" picture.</param>
	/// <param name="crossing">Carries the real eye to the virtual eye (metres).</param>
	/// <param name="keep">World plane (metres): only what is on its positive side is drawn.</param>
	public void AddView( CommandList list, CameraComponent camera, int slot, in RigidTransform crossing, Plane3 keep, TagSet exclude, Vec3 eye,
		Quat eyeRotation, LensTangents drawLens, float viewWidth, float viewHeight, PortalViewMode mode, int hashSalt )
	{
		int colour = slot;
		// The guard sheet's pictures have names of their own: the engine keeps one picture per name.
		RenderTargetHandle target = GetTarget( list, hashSalt == 2 ? GuardTargetName[colour] : TargetName[colour], viewWidth, viewHeight );
		ViewSetup setup = new ViewSetup
		{
			EnablePostprocessing = false,
			EnableUI = false,
			EnableDebugOverlays = false,
			ViewHash = HashCode.Combine( GameObject.Id, colour, hashSalt ),
			ExcludeTags = exclude,
		};

#if PORTABALLS_P1
		if ( mode == PortalViewMode.B )
		{
			// The engine works out the virtual eye and the cut for whichever view (eye) it is drawing.
			Transform crossingEngine = new Transform( SboxConvert.ToInches( crossing.Translation ), SboxConvert.ToEngine( crossing.Rotation ) );
			Plane keepEngine = new Plane( SboxConvert.ToEngine( keep.Normal ), -keep.D * SboxConvert.InchesPerMetre );
			ViewHandle portalView = list.DrawPortal( camera, crossingEngine, keepEngine, target, new RefractionSetup { ViewSetup = setup, RenderBehind = true }, ProjectionSource, SboxConvert.LengthToInches( PlainWithinMetres ) );
			ViewsAsked++;
			BindView( list, colour, target, portalView );
			return;
		}
#endif
		// Mode A: the virtual eye and its cut-off projection, worked out here from the game thread's camera.
		Vec3 virtualPosition;
		Quat virtualRotation;
		Mat4 projection;
		bool cut = PortalViewMath.Build( crossing, keep, eye, eyeRotation, drawLens, camera.ZNear, camera.ZFar, SboxConvert.InchesPerMetre, out PortalEyeView eyeView );
		bool plain = !cut || eyeView.BehindExit < PlainWithinMetres;
		if ( cut && !plain )
		{
			virtualPosition = eyeView.Position;
			virtualRotation = eyeView.Rotation;
			projection = eyeView.Projection;
		}
		else
		{
			// The eye is already past the entry wall's plane (a VR eye can be, a moment before the head is
			// carried through): its carried position is in the exit room, where nothing needs cutting.
			PortalCamera.VirtualEye( crossing, eye, eyeRotation, out virtualPosition, out virtualRotation );
			projection = PortalViewMath.Projection( drawLens, camera.ZNear, camera.ZFar );
		}
		VirtualEye[colour] = virtualPosition;
		VirtualRotation[colour] = virtualRotation;
		VirtualValid[colour] = true;

		setup.Transform = new Transform( SboxConvert.ToInches( virtualPosition ), SboxConvert.ToEngine( virtualRotation ) );
		setup.ZNear = camera.ZNear;
		setup.ZFar = camera.ZFar;
		if ( plain && !DebugPlainWithMatrix && !Game.IsRunningInVR )
		{
			// Nothing to cut: let the engine make its own projection for this view (same lens, same shape).
			setup.FieldOfView = camera.FieldOfView;
		}
		else
		{
			// The engine's projection matrices are the transpose of the core's (see CameraConventions).
			setup.ProjectionMatrix = Mat4.Transpose( projection );
		}
		ViewHandle view = list.DrawView( camera, target, setup );
		ViewsAsked++;
		BindView( list, colour, target, view );
	}

	/// <summary>Mode B only: 0 = rebuild the eye's projection from its side planes, 1 = take the engine's own matrix for the view.</summary>
	public int ProjectionSource { get; set; }

	private static void BindView( CommandList list, int colour, RenderTargetHandle target, ViewHandle view )
	{
		list.Attributes.Set( TexName[colour], target.ColorTexture );
		list.Attributes.Set( RectName[colour], view.Viewport );
		list.Attributes.Set( HasName[colour], view.IsValid );
	}

	// Float colour with no tone curve on it: the main view applies the tone curve once, to everything.
	private RenderTargetHandle GetTarget( CommandList list, string name, float viewWidth, float viewHeight )
	{
		float scale = Math.Clamp( ResolutionScale, 0.1f, 1f );
		float divisor = 1f / scale;
		int whole = (int)MathF.Round( divisor );
		if ( MathF.Abs( divisor - whole ) < 0.02f )
		{
			// Sized by the engine from whatever view is being drawn when the list plays.
			return list.GetRenderTarget( name, whole, ImageFormat.RGBA16161616F, ImageFormat.None );
		}
		int w = Math.Max( 16, (int)MathF.Round( viewWidth * scale ) );
		int h = Math.Max( 16, (int)MathF.Round( viewHeight * scale ) );
		return list.GetRenderTarget( name, w, h, ImageFormat.RGBA16161616F, ImageFormat.None );
	}

	// ------------------------------------------------------------------ the guard sheet

	private void UpdateGuard( CameraComponent camera, PaintWall wall, bool active, in PortalPair pair, bool linked, Vec3 eye, Quat eyeRotation,
		LensTangents drawLens, LensTangents cullLens, float nearMetres, float viewWidth, float viewHeight, PortalViewMode mode, bool vr, bool perView, float guardInches )
	{
		if ( !wall.IsValid() || !linked )
		{
			// Not near any opening. The sheet is taken away only after a few idle frames, and it has been
			// drawing nothing since the frame the eye left (see below), so its removal cannot show.
			if ( _guardObject.IsValid() && _guardObject.Enabled )
			{
				SetGuardMode( 2f );
				_guardRenderer.ExecuteBefore = perView ? null : _guardOffList;
				if ( ++_idleFrames > 4 ) _guardObject.Enabled = false;
			}
			_guardWall = null;
			return;
		}
		_idleFrames = 0;

		// The sheet hangs beyond the camera's near distance, so the near cut can never touch it.
		// First version: 0.02% beyond it, because anything further out left a sliver of screen where a wall that
		// tilts across the view was nearer than the sheet and hid it. At half an inch that is 2.5 thousandths of a
		// millimetre, which is no more than the rounding of a position a few metres from the world's origin: the
		// sheet could be cut away, in whole or in part, from one head position to the next. Now it hangs at twice
		// the near distance and is drawn with no depth test (shader combo D_GUARD_SHEET), so nothing can hide it.
		float sheetDistance = GuardPerView ? GuardSheetDistancePerView : GuardSheetDistance;
		if ( !_guardObject.IsValid() || _guardObject.Parent != camera.GameObject || _guardBuiltNear != camera.ZNear || _guardBuiltDistance != sheetDistance )
		{
			if ( _guardObject.IsValid() ) _guardObject.Destroy();
			_guardObject = new GameObject( camera.GameObject, false, "Portal guard sheet" );
			_guardObject.Tags.Add( GuardTag );
			_guardRenderer = _guardObject.AddComponent<ModelRenderer>();
			_guardRenderer.Model = BuildGuardModel( camera.ZNear * sheetDistance, 20f * MathF.Max( 1f, sheetDistance ) );
			_guardRenderer.RenderType = ModelRenderer.ShadowRenderType.Off;
			_guardBuiltNear = camera.ZNear;
			_guardBuiltDistance = sheetDistance;
			_guardWall = null;
			_guardModeSent = -1f;
			_guardObject.Enabled = true;
			_guardEnabledAtFrame = _frameNumber;
		}
		if ( !_guardObject.Enabled )
		{
			_guardObject.Enabled = true;
			_guardWall = null;       // a re-made scene object starts with no values
			_guardModeSent = -1f;
			_guardEnabledAtFrame = _frameNumber;
		}
		if ( _guardOffList == null )
		{
			_guardOffList = new CommandList( "Portaballs portal guard off" );
			_guardOffList.Attributes.Set( HasName[0], false );
			_guardOffList.Attributes.Set( HasName[1], false );
		}

		PortalLink portals = Room.Portals;
		RenderAttributes a = _guardRenderer.Attributes;
		if ( _guardWall != wall )
		{
			_guardWall = wall;
			WallFrame frame = wall.Frame;
			PaintSurface surface = wall.Surface;
			a.Set( "PaintMask", wall.Panel.Mask );
			a.Set( "GhostMask", wall.Panel.Mask );
			a.Set( "PaintMaskSize", new Vector4( surface.Width, surface.Height, surface.Width / frame.Width, surface.Height / frame.Height ) );
			a.Set( "WallU", SboxConvert.ToEngine( frame.U ) );
			a.Set( "WallV", SboxConvert.ToEngine( frame.V ) );
			a.Set( "WallN", SboxConvert.ToEngine( frame.Normal ) );
			a.Set( "WallOrigin", SboxConvert.ToInches( frame.Origin ) );
			a.Set( "WallSize", new Vector3( SboxConvert.LengthToInches( frame.Width ), SboxConvert.LengthToInches( frame.Height ), SboxConvert.LengthToInches( PaintWall.PanelOffsetMetres ) ) );
			a.Set( "OrangeColor", PaintPanel.LinearColour( PaintColor.Orange ) );
			a.Set( "BlueColor", PaintPanel.LinearColour( PaintColor.Blue ) );
			a.Set( "GhostAlpha", 0f );
			a.Set( "GhostIsBlue", 0f );
			a.Set( "PortalHolesByGuard", 0f );
			a.Set( "PortalMatchExit", MatchExitShape ? 1f : 0f );
			a.SetCombo( "D_GUARD_SHEET", GuardPerView ? 1 : 0 );
			for ( int c = 0; c < 2; c++ )
			{
				if ( portals.WallOf( (PaintColor)c ) != wall || _exitMask[c] == null ) continue;
				a.Set( ExitMaskName[c], _exitMask[c] );
				a.Set( ExitMapAName[c], _exitMapA[c] );
				a.Set( ExitMapBName[c], _exitMapB[c] );
			}
			SetLookupConstants( a, new Vector4( NoPictureColor.x, NoPictureColor.y, NoPictureColor.z, _noPictureSent > 0.5f ? 1f : 0f ) );
		}
		a.Set( "PortalOpenOrange", portals.WallOf( PaintColor.Orange ) == wall ? 1f : 0f );
		a.Set( "PortalOpenBlue", portals.WallOf( PaintColor.Blue ) == wall ? 1f : 0f );
		// The same boundary the wall's panel was given: inside it this view's openings are the sheet's.
		a.Set( "GuardDistance", guardInches );
		a.Set( FadeName[0], _fade[0] );
		a.Set( FadeName[1], _fade[1] );

		if ( !active )
		{
			// Standing by: on, drawing nothing.
			SetGuardMode( 2f );
			_guardRenderer.ExecuteBefore = perView ? null : _guardOffList;
			return;
		}
		SetGuardMode( 1f );

		if ( perView )
		{
			// Patch P2: the sheet shows the same per-view pictures as the walls (SetPerViewEnds asked for them).
			_guardRenderer.ExecuteBefore = null;
			return;
		}

		CommandList list = _guardLists[_guardFlip ^= 1];
		list.Reset();
		list.Attributes.Set( HasName[0], false );
		list.Attributes.Set( HasName[1], false );

		// Does any part of the screen look toward the wall at all? Facing away from it (as just after
		// coming out of it) the sheet has nothing to show, and the far side need not be drawn.
		Vec3 forward = Vec3.Transform( new Vec3( 1f, 0f, 0f ), eyeRotation );
		Vec3 left = Vec3.Transform( new Vec3( 0f, 1f, 0f ), eyeRotation );
		Vec3 up = Vec3.Transform( new Vec3( 0f, 0f, 1f ), eyeRotation );
		Vec3 n = wall.Frame.Normal;
		float towardWall = -Vec3.Dot( forward, n ) + cullLens.MaxHorizontal * MathF.Abs( Vec3.Dot( left, n ) ) + cullLens.MaxVertical * MathF.Abs( Vec3.Dot( up, n ) );
		if ( mode != PortalViewMode.C && ( towardWall > 0f || vr ) )
		{
			for ( int c = 0; c < 2; c++ )
			{
				if ( portals.WallOf( (PaintColor)c ) != wall ) continue;
				AddEnd( list, camera, pair, c, wall, eye, eyeRotation, drawLens, cullLens, nearMetres, viewWidth, viewHeight, mode, true, vr );
			}
		}
		_guardRenderer.ExecuteBefore = list;
	}

	private void SetGuardMode( float mode )
	{
		if ( _guardModeSent == mode ) return;
		_guardModeSent = mode;
		_guardRenderer.Attributes.Set( "GuardMode", mode );
	}

	// A big square facing the camera, in the camera object's own space (X forward, inches).
	private static Model BuildGuardModel( float distance, float halfSize )
	{
		Material material = Material.FromShader( PaintPanel.ShaderPath );
		Vector3 normal = new Vector3( -1f, 0f, 0f );
		Vector3 tangent = new Vector3( 0f, -1f, 0f );
		Vertex[] v =
		{
			new Vertex( new Vector3( distance, halfSize, -halfSize ), normal, tangent, new Vector4( 0f, 0f, 0f, 0f ) ),
			new Vertex( new Vector3( distance, -halfSize, -halfSize ), normal, tangent, new Vector4( 1f, 0f, 0f, 0f ) ),
			new Vertex( new Vector3( distance, -halfSize, halfSize ), normal, tangent, new Vector4( 1f, 1f, 0f, 0f ) ),
			new Vertex( new Vector3( distance, halfSize, halfSize ), normal, tangent, new Vector4( 0f, 1f, 0f, 0f ) ),
		};
		int[] indices = { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 };
		Mesh mesh = new Mesh( material );
		mesh.CreateVertexBuffer<Vertex>( v.Length, v );
		mesh.CreateIndexBuffer( indices.Length, indices );
		mesh.Bounds = new BBox( new Vector3( distance - 0.01f, -halfSize, -halfSize ), new Vector3( distance + 0.01f, halfSize, halfSize ) );
		return Model.Builder.AddMesh( mesh ).Create();
	}

	// ------------------------------------------------------------------ switching

	/// <summary>The next mode in the order A, B, C, A. B is skipped when the engine patch is not compiled in.</summary>
	public void NextMode()
	{
		PortalViewMode next = (PortalViewMode)( ( (int)Mode + 1 ) % 3 );
		if ( next == PortalViewMode.B && !PatchCompiledIn ) next = PortalViewMode.C;
		SetMode( next );
	}

	public void SetMode( PortalViewMode mode )
	{
		if ( Mode == mode ) return;
		Mode = mode;
		Log.Info( $"[PortalView] MODE {mode}: {Describe( mode )}" );
	}

	public static string Describe( PortalViewMode mode )
	{
		switch ( mode )
		{
			case PortalViewMode.A: return "real view, one picture per frame from the game thread's camera (no engine patch)";
			case PortalViewMode.B: return PatchCompiledIn ? "real view, worked out per drawn view by the patched engine (each VR eye its own)" : "needs engine patch P1, which this build was compiled without: mode A is used";
			default: return "no view: dark surface with the paint rim";
		}
	}
}
