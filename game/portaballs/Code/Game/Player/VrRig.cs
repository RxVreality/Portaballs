using Sandbox;
using Sandbox.VR;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// Builds the VR player in code: the play-space anchor, the head camera and two tracked hands.
///
/// Layout (all made here, nothing needs to be in the scene file):
///   this object      = the play-space anchor. Moving THIS object moves the player.
///     Head           = follows the headset; holds the camera that draws both eyes.
///     Left Hand      = follows the left controller; has a ThrowHand.
///     Right Hand     = follows the right controller; has a ThrowHand.
///
/// VR is only on when the game was started with -vr. With VR off this builds nothing.
/// </summary>
public sealed class VrRig : Component
{
	/// <summary>Normally the right hand throws blue and the left throws orange. Tick to swap.</summary>
	[Property] public bool SwapHandColours { get; set; }

	/// <summary>The room the hands aim at. Set by PortaballsGame.</summary>
	public TestRoom Room { get; set; }

	/// <summary>The link to the helper program, if there is one. Set by PortaballsGame before Build.</summary>
	public HelperLink Link { get; set; }

	// ---- getting around: the room is bigger than a play space ----

	/// <summary>Left stick walks, relative to where the head faces. Untick to stand still.</summary>
	[Property] public bool StickMovement { get; set; } = true;

	/// <summary>Walking speed with the stick fully pushed, m/s.</summary>
	[Property] public float MoveSpeed { get; set; } = 2f;

	/// <summary>Stick deflection (0 to 1) below which the walking stick is ignored.</summary>
	[Property] public float StickDeadZone { get; set; } = 0.15f;

	/// <summary>Right stick turns the player, about the head. Untick for no turning.</summary>
	[Property] public bool StickTurning { get; set; } = true;

	/// <summary>
	/// Ticked (the default; the tester, 2026-11-19: "I would prefer smooth turning") = the stick turns smoothly, faster
	/// the further it is pushed. Unticked = snap turns, one each time the stick is flicked.
	/// </summary>
	[Property] public bool SmoothTurn { get; set; } = true;

	/// <summary>Smooth turning: speed with the stick fully over, degrees a second.</summary>
	[Property] public float SmoothTurnDegreesPerSecond { get; set; } = 120f;

	/// <summary>Smooth turning: stick deflection (0 to 1) below which the turning stick is ignored.</summary>
	[Property] public float TurnDeadZone { get; set; } = 0.2f;

	/// <summary>Snap turning: size of one turn, degrees.</summary>
	[Property] public float SnapTurnDegrees { get; set; } = 30f;

	/// <summary>Snap turning: how far the right stick must be pushed sideways (0 to 1) to turn.</summary>
	[Property] public float SnapTurnThreshold { get; set; } = 0.6f;

	/// <summary>Swap the sticks: right walks, left turns.</summary>
	[Property] public bool SwapSticks { get; set; }

	// ---- walls and portals ----

	/// <summary>Radius of the head for "does it fit through the patch", metres.</summary>
	[Property] public float HeadRadius { get; set; } = 0.10f;

	/// <summary>How far solid wall keeps the head away when walking with the stick, metres.</summary>
	[Property] public float WallClearance { get; set; } = 0.20f;

	/// <summary>Top speed of the fall after coming out of a high patch, m/s.</summary>
	[Property] public float MaxFallSpeed { get; set; } = 6f;

	/// <summary>How fast the play space is raised after coming out below the floor, m/s. 0 = at once.</summary>
	[Property] public float LiftSpeed { get; set; } = 3f;

	// ---- portals in the floor and the ceiling (all of it is the core's PlaySpaceWalker; these are its dials) ----

	/// <summary>
	/// Radius of the body, metres. A linked patch in the floor is a hole for the player when a circle this big
	/// under the head is all its paint: then there is no floor there and the player falls in.
	/// </summary>
	[Property] public float BodyRadius { get; set; } = 0.20f;

	/// <summary>
	/// Coming out of a floor or ceiling portal the play space is tipped over (as in Portal) and is swung upright
	/// about the head. This is how long that takes, seconds. 0 = at once, in the frame of the crossing.
	/// </summary>
	[Property] public float RightingSeconds { get; set; } = 0.25f;

	/// <summary>
	/// Coming UP out of a floor portal the player is thrown at least fast enough for the feet to rise this far
	/// above the floor, metres, so they can steer off the hole with the walking stick. Less than 0 = no minimum.
	/// </summary>
	[Property] public float FloorExitRise { get; set; } = 0.4f;

	/// <summary>How much of the walking speed the walking stick gives while in the air (0 to 1).</summary>
	[Property] public float AirSteer { get; set; } = 1f;

	/// <summary>Landing with sideways speed the player slides to a stop, losing this much speed a second, m/s^2. 0 = stops dead.</summary>
	[Property] public float SlideFriction { get; set; } = 15f;

	public GameObject Head { get; private set; }
	public ThrowHand LeftHand { get; private set; }
	public ThrowHand RightHand { get; private set; }

	/// <summary>How many times the player has gone through a portal.</summary>
	public int PortalCrossings { get; private set; }

	/// <summary>
	/// All the moving of the play space (sticks, walls, portal, falling) is the core's PlaySpaceWalker.
	/// This component only reads the headset and sticks, and puts the anchor where the walker says.
	/// </summary>
	public PlaySpaceWalker Walker { get; } = new PlaySpaceWalker();

	private bool _built;
	private bool _vrBuilt;

	protected override void OnStart()
	{
		Build();
	}

	/// <summary>Make the rig now. Safe to call more than once; only the first call does anything.</summary>
	public void Build()
	{
		if ( _built ) return;
		_built = true;

		if ( !Game.IsRunningInVR )
		{
			Log.Info( "[VrRig] VR is off (the game was not started with -vr), so no VR rig was built." );
			return;
		}

		// The anchor: the engine places the whole tracked play space at this object's transform.
		GameObject.AddComponent<VRAnchor>();

		// Head: a tracked object that follows the headset, with the camera on it. The engine works out
		// each eye's view from this camera's position, so the camera MUST follow the head.
		Head = new GameObject( GameObject, false, "Head" );
		VRTrackedObject headTracker = Head.AddComponent<VRTrackedObject>();
		headTracker.PoseSource = VRTrackedObject.PoseSources.Head;

		CameraComponent camera = Head.AddComponent<CameraComponent>();
		camera.TargetEye = StereoTargetEye.Both;
		// Inches. The default (10) would cut off a ball held near the face; half an inch (1.3 cm) also
		// lets the head come right up to an open patch without the wall being cut away early.
		camera.ZNear = 0.5f;
		camera.ZFar = 10000f;
		Head.Enabled = true;

		PaintColor rightPaint = SwapHandColours ? PaintColor.Orange : PaintColor.Blue;
		PaintColor leftPaint = SwapHandColours ? PaintColor.Blue : PaintColor.Orange;

		LeftHand = BuildHand( "Left Hand", ThrowHand.HandSide.Left, VRTrackedObject.PoseSources.LeftHand, leftPaint );
		RightHand = BuildHand( "Right Hand", ThrowHand.HandSide.Right, VRTrackedObject.PoseSources.RightHand, rightPaint );

		_vrBuilt = true;
		string turning = !StickTurning ? "does not turn" : SmoothTurn ? $"turns smoothly at up to {SmoothTurnDegreesPerSecond:0} degrees a second" : $"snap-turns {SnapTurnDegrees:0} degrees";
		Log.Info( $"[VrRig] Built. Left hand throws {leftPaint}, right hand throws {rightPaint}. {( SwapSticks ? "Right" : "Left" )} stick walks at {MoveSpeed:0.0} m/s, {( SwapSticks ? "left" : "right" )} stick {turning}. Floor and ceiling portals: body radius {BodyRadius:0.00} m, righting {RightingSeconds:0.00} s, pop out of a floor {FloorExitRise:0.00} m." );
	}

	protected override void OnUpdate()
	{
		if ( !_vrBuilt || !Game.IsRunningInVR || Input.VR is null ) return;

		// Make sure the engine's idea of the play space is this object's place right now, then read the
		// head in the play space's own coordinates (metres).
		Input.VR.Anchor = WorldTransform;
		SboxConvert.ToCorePose( WorldTransform.ToLocal( Input.VR.Head ), out Vec3 headLocal, out Quat headLocalRotation );

		VRController walkHand = SwapSticks ? Input.VR.RightHand : Input.VR.LeftHand;
		VRController turnHand = SwapSticks ? Input.VR.LeftHand : Input.VR.RightHand;
		Vector2 walk = StickMovement && walkHand is not null ? walkHand.Joystick.Value : Vector2.Zero;
		Vector2 turn = StickTurning && turnHand is not null ? turnHand.Joystick.Value : Vector2.Zero;

		Walker.MoveSpeed = MoveSpeed;
		Walker.DeadZone = StickDeadZone;
		Walker.SmoothTurn = SmoothTurn;
		Walker.SmoothTurnDegreesPerSecond = SmoothTurnDegreesPerSecond;
		Walker.TurnDeadZone = TurnDeadZone;
		Walker.BodyRadius = BodyRadius;
		Walker.RightingSeconds = RightingSeconds;
		Walker.FloorExitRise = FloorExitRise;
		Walker.AirSteer = AirSteer;
		Walker.SlideFriction = SlideFriction;
		Walker.SnapTurnDegrees = SnapTurnDegrees;
		Walker.TurnLatch.Threshold = SnapTurnThreshold;
		Walker.TurnLatch.Release = SnapTurnThreshold * 0.5f;
		Walker.HeadRadius = HeadRadius;
		Walker.WallClearance = WallClearance;
		Walker.Fall.MaxFallSpeed = MaxFallSpeed;
		Walker.Fall.LiftSpeed = LiftSpeed;
		Walker.Fall.Gravity = SboxConvert.ToMetres( Scene.PhysicsWorld.Gravity ).Length();
		Walker.Anchor = SboxConvert.ToRigid( WorldTransform );

		PortalLink portals = Room.IsValid() ? Room.Portals : null;
		bool linked = false;
		PortalPair pair = default;
		IPaintMask maskA = null, maskB = null;
		PortalSettings settings = FallbackSettings;
		float floor = Room.IsValid() ? SboxConvert.ToMetres( Room.WorldPosition ).Z : 0f;
		if ( portals.IsValid() )
		{
			portals.Sync();
			settings = portals.Settings;
			linked = portals.HasPair;
			if ( linked )
			{
				pair = portals.Pair;
				maskA = portals.MaskOf( PaintColor.Orange );
				maskB = portals.MaskOf( PaintColor.Blue );
			}
		}

		RigidTransform anchorBefore = Walker.Anchor;
		PlaySpaceFrame frame = Walker.Update( headLocal, headLocalRotation, new Vec2( walk.x, walk.y ), turn.x, Time.Delta, floor,
			linked, pair, maskA, maskB, Room.IsValid() ? Room.WallFrames : null, Room.IsValid() ? Room.WallCount : 0, settings );

		// Put the play space where the walker says. The head, the hands, a held ball and the throw
		// history are all kept relative to it, so they come along and nothing sees a jump.
		WorldPosition = SboxConvert.ToInches( Walker.Anchor.Translation );
		WorldRotation = SboxConvert.ToEngine( Walker.Anchor.Rotation );
		Input.VR.Anchor = WorldTransform;

		if ( frame.Crossed )
		{
			PortalCrossings++;
			PlayerStep step = frame.Crossing;
			RigidTransform plain = pair.PlayerCrossing( step.FromA );
			PortalLink.CrossingRecord record = new PortalLink.CrossingRecord();
			record.IsPlayer = true;
			record.FromOrange = step.FromA;
			record.EntryUV = step.EntryUV;
			record.PositionBefore = frame.HeadBeforeCrossing;
			record.PositionAfter = step.Head;
			record.PositionPredicted = plain.TransformPoint( frame.HeadBeforeCrossing );
			record.VelocityBefore = anchorBefore.TransformDirection( new Vec3( 1f, 0f, 0f ) );
			record.VelocityAfter = Walker.Anchor.TransformDirection( new Vec3( 1f, 0f, 0f ) );
			record.VelocityPredicted = plain.TransformDirection( record.VelocityBefore );
			record.ExitMovedBy = step.ExitMovedBy;
			record = portals.Record( record );
			// The feet: straight below the head by the head's own height in the play space (the walker's body).
			PlayerArrival arrival = PlayerArrival.Check( step.Head - new Vec3( 0f, 0f, headLocal.Z ), floor );
			Vec3 vIn = frame.VelocityBeforeCrossing, vOut = frame.VelocityAfterCrossing;
			string tipped = pair.PlayerKeepsUpright ? "upright" : $"tipped {PlaySpace.TiltOf( step.Crossing.Rotation ) * 57.29578f:0} deg, righted {( RightingSeconds > 0f ? "over " + RightingSeconds.ToString( "0.00" ) + " s" : "at once" )}";
			Log.Info( $"[VrRig] THROUGH crossing {record.Number}: head went into {( step.FromA ? "ORANGE" : "BLUE" )} at u={step.EntryUV.X:0.000} v={step.EntryUV.Y:0.000} ({( frame.CrossedFlying ? "falling or flying" : "walking or leaning" )}). Head ({record.PositionBefore.X:0.000}, {record.PositionBefore.Y:0.000}, {record.PositionBefore.Z:0.000}) -> ({record.PositionAfter.X:0.000}, {record.PositionAfter.Y:0.000}, {record.PositionAfter.Z:0.000}) m; core predicts ({record.PositionPredicted.X:0.000}, {record.PositionPredicted.Y:0.000}, {record.PositionPredicted.Z:0.000}); velocity ({vIn.X:0.00}, {vIn.Y:0.00}, {vIn.Z:0.00}) -> ({vOut.X:0.00}, {vOut.Y:0.00}, {vOut.Z:0.00}) m/s; {tipped}; exit slid {step.ExitMovedBy * 1000f:0.0} mm; feet arrive {arrival.HeightAboveFloor:0.000} m from the room floor (minus = under it)." );
		}
	}

	private static readonly PortalSettings FallbackSettings = new PortalSettings();

	private ThrowHand BuildHand( string handName, ThrowHand.HandSide side, VRTrackedObject.PoseSources source, PaintColor paint )
	{
		GameObject hand = new GameObject( GameObject, false, handName );

		// Follows the controller's grip pose (centred on the palm), which is where a held ball belongs.
		VRTrackedObject tracker = hand.AddComponent<VRTrackedObject>();
		tracker.PoseSource = source;
		tracker.PoseType = VRTrackedObject.PoseTypes.Grip;

		ThrowHand thrower = hand.AddComponent<ThrowHand>();
		thrower.Side = side;
		thrower.Paint = paint;
		thrower.Room = Room;
		thrower.Link = Link;
		thrower.AnchorObject = GameObject;

		// Something to see where the hand is: a small block about the size of a controller grip,
		// faintly tinted with the hand's paint colour. (The engine's own controller model is just a
		// 50 inch cube, and no hand model ships with the engine, so this is a plain stand-in.)
		// It is a separate child so its scale does not squash the held ball.
		GameObject block = new GameObject( hand, false, "Hand Block" );
		block.LocalScale = SboxConvert.BoxScaleFor( new Vec3( 0.10f, 0.035f, 0.05f ) );
		ModelRenderer renderer = block.AddComponent<ModelRenderer>();
		renderer.Model = Model.Cube;
		Color paintTint = Portaball.TintFor( paint );
		renderer.Tint = new Color( 0.5f + 0.5f * paintTint.r, 0.5f + 0.5f * paintTint.g, 0.5f + 0.5f * paintTint.b );
		block.Enabled = true;

		// Shown at both ends of a portal while the hand is in an opening (PortalBallTwins). These hands ride the play space.
		PortalTwinSource.Register( hand, block ).FollowsPlayer = true;

		hand.Enabled = true;
		return thrower;
	}
}
