using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// The one component to put in the scene. Put it on a single object in an otherwise empty scene,
/// at position 0,0,0 with no rotation and scale 1. When the game starts it builds everything:
/// the test room, then either the VR player (game started with -vr) or the desk camera (no VR).
/// </summary>
public sealed class PortaballsGame : Component
{
	/// <summary>Normally the right hand throws blue and the left throws orange. Tick to swap.</summary>
	[Property] public bool SwapHandColours { get; set; }

	/// <summary>
	/// The engine's default gravity is 850 inches/s^2, more than twice real gravity, which makes a
	/// real-arm throw drop like a stone. With this ticked the world uses real gravity instead.
	/// </summary>
	[Property] public bool UseRealGravity { get; set; } = true;

	/// <summary>Gravity when UseRealGravity is ticked, m/s^2.</summary>
	[Property] public float GravityMetres { get; set; } = 9.81f;

	/// <summary>Desk mode only: throw one ball of each colour by itself after AutoThrowDelay, for screenshot tests.</summary>
	[Property] public bool AutoThrow { get; set; }

	/// <summary>Which scripted throw AutoThrow runs: the two-ball test, or the 12 second paint tour.</summary>
	[Property] public ThrowScript AutoThrowScript { get; set; } = ThrowScript.TwoThrows;

	/// <summary>Seconds after start before the auto throw.</summary>
	[Property] public float AutoThrowDelay { get; set; } = 3f;

	/// <summary>Desk mode only: camera height above the floor, metres.</summary>
	[Property] public float DeskEyeHeight { get; set; } = 1.6f;

	/// <summary>
	/// Look for the helper program (helper\dist\PortaballsHelper.exe) and throw from its 500 Hz
	/// samples and eye gaze while it is running. With it off, or the helper not running, throws use
	/// the engine's own once-per-frame poses exactly as before.
	/// </summary>
	[Property] public bool UseHelper { get; set; } = true;

	/// <summary>
	/// Ports on this PC to look for the helper on, in order. s&box only lets a game connect to this
	/// PC on 80, 443, 8080 or 8443, so the helper is started with --port 8443 (see HelperLink).
	/// </summary>
	[Property] public string HelperPorts { get; set; } = "8443,28461";

	/// <summary>
	/// Desk mode only: build a stand-in player whose hands are driven by helper samples, to test the
	/// whole helper path with no headset. Run the helper with --fake --port 8443. See HelperDeskRig.
	/// </summary>
	[Property] public bool HelperDeskTest { get; set; }

	/// <summary>
	/// With HelperDeskTest: also test the lining-up of the helper's space with the play area, which
	/// otherwise needs a headset. A pretend engine reports the helper's own poses guessed 30 ms ahead,
	/// turned 20 degrees, shifted, and with the hands' grip point moved; the game has to work all that
	/// out before it will throw from helper samples. See HelperLink.PretendEngine.
	/// </summary>
	[Property] public bool HelperDeskAlignTest { get; set; }

	/// <summary>Show the readout board (in VR, and in the helper desk test).</summary>
	[Property] public bool ShowReadout { get; set; } = true;

	/// <summary>
	/// Desk mode only: run a scripted portal test by itself a moment after the scene starts (see PortalProof).
	/// None = no test; the test component is still there, so a script can start one later.
	/// </summary>
	[Property] public PortalProofScript PortalProof { get; set; } = PortalProofScript.None;

	/// <summary>
	/// How the inside of an open portal is drawn when the game starts: A = real view with no engine patch,
	/// B = real view worked out per eye by the patched engine, C = dark surface. Switchable while playing
	/// (see PortalView.OnUpdate for the buttons).
	/// </summary>
	[Property] public PortalViewMode PortalViewMode { get; set; } = PortalViewMode.A;

	/// <summary>
	/// The same in a headset, when the engine carries patch P2 (each eye its own picture). B since 2026-11-19: in
	/// mode A both eyes are shown one picture, which the tester felt as crossed eyes in the Steam Frame.
	/// </summary>
	[Property] public PortalViewMode PortalViewModeVr { get; set; } = PortalViewMode.B;

	/// <summary>Flat screen: size of the portal's picture against the screen. 1 = full, 0.5 = half each way.</summary>
	[Property] public float PortalResolutionScale { get; set; } = 1f;

	/// <summary>
	/// Headset: the same. 0.75 until a headset session has measured it: on a flat screen at one eye's
	/// size it takes a third off the portal's cost (see engine\smoke\seethrough\measurements.txt).
	/// The X button steps through 1 / 0.75 / 0.5 while playing.
	/// </summary>
	[Property] public float PortalResolutionScaleVr { get; set; } = 0.75f;

	/// <summary>Desk mode: also put up the portal readout board (it is always there in VR when ShowReadout is on).</summary>
	[Property] public bool ShowPortalReadoutOnDesk { get; set; }

	/// <summary>The board that shows the portal view mode and what the engine drew.</summary>
	public PortalReadout PortalBoard { get; private set; }

	/// <summary>The scripted portal tests (desk mode).</summary>
	public PortalProof Proof { get; private set; }

	public TestRoom Room { get; private set; }
	public VrRig Rig { get; private set; }
	public FlatThrower Desk { get; private set; }
	public HelperLink Link { get; private set; }
	public HelperDeskRig HelperDesk { get; private set; }
	public HelperReadout Readout { get; private set; }

	protected override void OnStart()
	{
		if ( UseRealGravity )
		{
			Scene.PhysicsWorld.Gravity = new Vector3( 0f, 0f, -SboxConvert.LengthToInches( GravityMetres ) );
		}

		// The room is built around this object: its floor centre is this object's position.
		Room = GameObject.AddComponent<TestRoom>();
		Room.Build();

		bool vr = Game.IsRunningInVR;
		Room.View.Mode = vr && PortalView.PerViewCompiledIn ? PortalViewModeVr : PortalViewMode;
		Room.View.ResolutionScale = vr ? PortalResolutionScaleVr : PortalResolutionScale;

		bool deskTest = !vr && HelperDeskTest;
		if ( ( vr && ShowReadout ) || ( !vr && ShowPortalReadoutOnDesk ) ) BuildPortalReadout();

		if ( UseHelper || deskTest )
		{
			Link = GameObject.AddComponent<HelperLink>();
			Link.Ports = HelperPorts;
			Link.IdentityAlignment = deskTest && !HelperDeskAlignTest;
			if ( deskTest && HelperDeskAlignTest )
			{
				Link.PretendEngine = true;
				Link.PretendAlignment = new RigidTransform( AlignmentMath.YawRotation( 20f * System.MathF.PI / 180f ), new Vec3( 0.25f, -0.15f, 0.02f ) );
				Link.PretendGripOffset = new RigidTransform(
					Quat.CreateFromAxisAngle( Vec3.Normalize( new Vec3( 0.1f, 1f, 0.05f ) ), 35f * System.MathF.PI / 180f ), new Vec3( -0.040f, 0.010f, -0.030f ) );
				Log.Info( "[Portaballs] Helper desk ALIGN test on: the truth to be found is a 20.0 deg turn, a shift of (0.250, -0.150, 0.020) m, and a grip point (-4.0, 1.0, -3.0) cm from the raw pose turned 35 deg." );
			}
		}

		if ( vr )
		{
			// The player's play space sits on the floor in the middle of the room.
			GameObject rigObject = new GameObject( GameObject, true, "VR Rig" );
			Rig = rigObject.AddComponent<VrRig>();
			Rig.Room = Room;
			Rig.Link = Link;
			Rig.SwapHandColours = SwapHandColours;
			Rig.Build();
			if ( ShowReadout ) BuildReadout( Rig.LeftHand, Rig.RightHand );
			Log.Info( $"[Portaballs] Started in VR. Helper link: {( Link.IsValid() ? "on, looking on port(s) " + HelperPorts : "off" )}." );
		}
		else
		{
			if ( deskTest )
			{
				// The stand-in player stands a metre behind the middle of the room, facing the +X wall. The
				// helper's made-up throw looks at a point five metres ahead, which from here is on that wall.
				Vec3 floor = SboxConvert.ToMetres( WorldPosition );
				GameObject rigObject = new GameObject( GameObject, true, "Helper Desk Rig" );
				rigObject.WorldPosition = SboxConvert.ToInches( floor + new Vec3( -1f, 0f, 0f ) );
				HelperDesk = rigObject.AddComponent<HelperDeskRig>();
				HelperDesk.Room = Room;
				HelperDesk.Link = Link;
				HelperDesk.SwapHandColours = SwapHandColours;
				HelperDesk.Build();
				if ( ShowReadout ) BuildReadout( HelperDesk.LeftHand, HelperDesk.RightHand );
			}

			// Desk camera: near the back wall, at eye height, facing the +X wall.
			Vec3 floorCentre = SboxConvert.ToMetres( WorldPosition );
			Vec3 eye = floorCentre + new Vec3( -Room.Length * 0.5f + 1.5f, 0f, DeskEyeHeight );

			GameObject deskObject = new GameObject( GameObject, true, "Desk Camera" );
			deskObject.WorldPosition = SboxConvert.ToInches( eye );
			Desk = deskObject.AddComponent<FlatThrower>();
			Desk.Room = Room;
			Desk.AutoThrow = AutoThrow;
			Desk.Script = AutoThrowScript;
			Desk.AutoThrowDelay = AutoThrowDelay;
			Desk.EyeHeight = DeskEyeHeight;
			Desk.Build();

			Proof = GameObject.AddComponent<PortalProof>();
			Proof.Room = Room;
			Proof.Desk = Desk;
			Proof.Script = PortalProof;
			Log.Info( $"[Portaballs] Started without VR (desk mode). Auto throw on: {AutoThrow} ({AutoThrowScript})." );
			if ( deskTest )
			{
				// Stand back and to the left, so the stand-in player, the far wall and the readout are all in view.
				Desk.LookAt( floorCentre + new Vec3( -3.4f, 1.2f, 1.9f ), floorCentre + new Vec3( 2.5f, 0.2f, 1.3f ) );
				Desk.LockView = false;
				Log.Info( "[Portaballs] Helper desk test on: a stand-in player throws from helper samples. Start the helper with:  PortaballsHelper.exe --fake --port 8443" );
			}
		}
	}

	/// <summary>
	/// The portal readout board: to the player's front-right, mirroring the helper's board, turned to face
	/// the middle of the room. Safe to call more than once (a script can put it up in desk mode).
	/// </summary>
	public PortalReadout BuildPortalReadout()
	{
		if ( PortalBoard.IsValid() ) return PortalBoard;
		Vec3 floor = SboxConvert.ToMetres( WorldPosition );
		Vec3 centre = floor + new Vec3( 1.9f, -1.75f, 1.55f );
		Vec3 viewer = floor + new Vec3( -0.3f, 0f, 1.55f );
		Vec3 facing = Vec3.Normalize( viewer - centre );

		GameObject boardObject = new GameObject( GameObject, true, "Portal Readout" );
		boardObject.WorldPosition = SboxConvert.ToInches( centre );
		boardObject.WorldRotation = Rotation.LookAt( SboxConvert.ToEngine( facing ), Vector3.Up );
		PortalBoard = boardObject.AddComponent<PortalReadout>();
		PortalBoard.View = Room.View;
		PortalBoard.Build();
		return PortalBoard;
	}

	/// <summary>
	/// The readout board: to the player's front-left, 2.6 m from the middle of the room, turned to face it.
	/// It is fixed to the room, not to the player.
	/// </summary>
	private void BuildReadout( ThrowHand left, ThrowHand right )
	{
		Vec3 floor = SboxConvert.ToMetres( WorldPosition );
		Vec3 centre = floor + new Vec3( 1.9f, 1.75f, 1.45f );
		Vec3 viewer = floor + new Vec3( -0.3f, 0f, 1.45f );
		Vec3 facing = Vec3.Normalize( viewer - centre );

		GameObject boardObject = new GameObject( GameObject, true, "Helper Readout" );
		boardObject.WorldPosition = SboxConvert.ToInches( centre );
		boardObject.WorldRotation = Rotation.LookAt( SboxConvert.ToEngine( facing ), Vector3.Up );

		Readout = boardObject.AddComponent<HelperReadout>();
		Readout.Link = Link;
		if ( left.IsValid() ) Readout.Hands.Add( left );
		if ( right.IsValid() ) Readout.Hands.Add( right );
		Readout.Build();
	}
}
