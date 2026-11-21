using System.Collections.Generic;
using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>Which scripted throw runs when AutoThrow is on.</summary>
public enum ThrowScript
{
	/// <summary>The original test: one orange and one blue ball at the wall in front, once.</summary>
	TwoThrows,

	/// <summary>
	/// About 12 seconds that show the paint: orange high on the far wall, blue on the right-hand wall,
	/// a second blue overlapping the first (joins it), then orange on the right-hand wall (replaces the first orange).
	/// </summary>
	PaintTour,
}

/// <summary>
/// The no-headset way to test, at a desk: a first-person camera you walk with WASD and turn with
/// the mouse. Left mouse button throws orange, right mouse button throws blue, straight along the view.
///
/// It also has an "auto throw" switch that throws by itself with nobody touching anything, for
/// screenshot tests. <see cref="Script"/> picks which: the two-throw test or the paint tour.
///
/// Keys come from the engine's built-in default actions (Forward/Backward/Left/Right = WASD,
/// Attack1 = left mouse, Attack2 = right mouse), which every project gets unless it defines its own.
/// </summary>
public sealed class FlatThrower : Component
{
	/// <summary>Speed of a mouse-button throw, m/s.</summary>
	[Property] public float ThrowSpeed { get; set; } = 8f;

	/// <summary>Walking speed, m/s.</summary>
	[Property] public float WalkSpeed { get; set; } = 2.5f;

	/// <summary>
	/// Size of the splat a thrown ball makes, metres. 0.42 is the size the paint was tuned and pictured
	/// with: big enough that one hit can run down into a doorway (an opening needs a 0.30 m circle).
	/// </summary>
	[Property] public float SplatRadius { get; set; } = 0.42f;

	/// <summary>Throw by itself after AutoThrowDelay, following <see cref="Script"/>.</summary>
	[Property] public bool AutoThrow { get; set; }

	/// <summary>Which scripted throw AutoThrow runs.</summary>
	[Property] public ThrowScript Script { get; set; } = ThrowScript.TwoThrows;

	/// <summary>Seconds after start before the auto throw.</summary>
	[Property] public float AutoThrowDelay { get; set; } = 3f;

	/// <summary>Speed of the auto throws, m/s.</summary>
	[Property] public float AutoThrowSpeed { get; set; } = 9f;

	/// <summary>Speed of the paint tour's throws, m/s (they cross the whole room).</summary>
	[Property] public float TourThrowSpeed { get; set; } = 11f;

	/// <summary>The room, for finding the wall in front. Set by PortaballsGame.</summary>
	public TestRoom Room { get; set; }

	/// <summary>When true the mouse and keys do not move the camera or throw (scripted runs set this).</summary>
	public bool LockView { get; set; }

	// ---- the camera as a player: walls, portals, a body, falling, being turned upright ----
	// All of it is the core's PlaySpaceWalker, the same code that moves the VR play space: the camera is a
	// play space whose head sits EyeHeight above its floor. So what a scripted walk proves here is the code
	// the headset runs.

	/// <summary>Radius of the camera's "head" for "does it fit through the patch", metres.</summary>
	[Property] public float HeadRadius { get; set; } = 0.10f;

	/// <summary>How far solid wall keeps the camera away, metres.</summary>
	[Property] public float WallClearance { get; set; } = 0.20f;

	/// <summary>Top speed of a fall, m/s.</summary>
	[Property] public float MaxFallSpeed { get; set; } = 6f;

	/// <summary>How fast the camera is raised after coming out below the floor, m/s. 0 = at once.</summary>
	[Property] public float LiftSpeed { get; set; } = 3f;

	/// <summary>Radius of the body under the camera, metres: a linked patch in the floor this big under it is a hole it falls into.</summary>
	[Property] public float BodyRadius { get; set; } = 0.20f;

	/// <summary>How long the view takes to swing upright after a floor or ceiling crossing tipped it over, seconds. 0 = at once.</summary>
	[Property] public float RightingSeconds { get; set; } = 0.25f;

	/// <summary>Coming up out of a floor portal: thrown at least high enough for the feet to rise this far above the floor, metres.</summary>
	[Property] public float FloorExitRise { get; set; } = 0.4f;

	/// <summary>How much of the walking speed the keys (or a scripted walk) give while in the air (0 to 1).</summary>
	[Property] public float AirSteer { get; set; } = 1f;

	/// <summary>Landing with sideways speed the camera slides to a stop, losing this much speed a second, m/s^2. 0 = stops dead.</summary>
	[Property] public float SlideFriction { get; set; } = 15f;

	/// <summary>
	/// How high the camera stands above the floor, metres: the height of its body. Walking keeps it there;
	/// after a portal crossing leaves it higher or lower, it falls or is lifted back to this. Placing the
	/// camera with SetView makes wherever it was put the new standing height.
	/// </summary>
	public float EyeHeight { get; set; } = 1.6f;

	/// <summary>How many times the camera has gone through a portal.</summary>
	public int PortalCrossings { get; private set; }

	/// <summary>The latest of those: eye position before and after, and the core's prediction. The "velocity" fields hold the view direction.</summary>
	public PortalLink.CrossingRecord LastCrossing { get; private set; }

	/// <summary>What the walker did in that crossing's frame: the real velocity before and after, the tilt, how it crossed.</summary>
	public PlaySpaceFrame LastCrossingFrame { get; private set; }

	/// <summary>What the walker did in the latest frame the camera moved as a player.</summary>
	public PlaySpaceFrame LastFrame { get; private set; }

	/// <summary>True while the camera is in the air (falling, flying, rising) or being lifted to its standing height.</summary>
	public bool IsFalling => _walker.Fall.Falling || _walker.Fall.Lifting;

	/// <summary>True while the camera is still doing anything by itself: in the air, sliding, or being turned upright.</summary>
	public bool IsSettling => _settling;

	/// <summary>How far the view is tipped over right now, degrees. 0 = level.</summary>
	public float TiltDegrees => _walker.Tilt * 57.29578f;

	/// <summary>The camera's own velocity (falling, flung out of a portal), m/s. Walking is not in it.</summary>
	public Vec3 VelocityMetres => _walker.Velocity;

	/// <summary>True when, in the latest frame, there was no floor under the camera's body (it was over or in a floor portal).</summary>
	public bool OverHole { get; private set; }

	/// <summary>The core walker that moves the camera (its Fall holds gravity and the fall state).</summary>
	public PlaySpaceWalker Walker => _walker;

	/// <summary>True while a scripted walk (see <see cref="Walk"/>) is still going.</summary>
	public bool IsWalking => _walkLeft > 0f;

	/// <summary>True on frames where a wall held the camera back.</summary>
	public bool BlockedByWall { get; private set; }

	private readonly PlaySpaceWalker _walker = new PlaySpaceWalker();
	private static readonly PortalSettings FallbackSettings = new PortalSettings();
	private float _walkLeft;     // metres of scripted walk still to go
	private float _walkSpeed;
	private bool _settling;      // a scripted camera still has a fall, a slide or a righting to finish
	private float _pitch;        // the camera's own pitch, degrees (engine Angles: positive looks down)

	private float _sinceStart;
	private bool _autoThrown;
	private bool _built;

	protected override void OnStart()
	{
		Build();
	}

	/// <summary>Add the camera to this object. Safe to call more than once.</summary>
	public void Build()
	{
		if ( _built ) return;
		_built = true;

		CameraComponent camera = GameObject.AddComponent<CameraComponent>();
		camera.FieldOfView = 80f;
		// Inches. Half an inch (1.3 cm), because the camera walks right up to and through an open
		// patch: with a bigger value the wall is cut away while the eye is still in front of it and
		// the void behind the room shows for a moment before the crossing.
		camera.ZNear = 0.5f;
		camera.ZFar = 10000f;

		// Level, facing +X, standing wherever the object was put.
		_pitch = 0f;
		PlaceWalker( SboxConvert.ToMetres( WorldPosition ), 0f );
		ApplyPose();
	}

	private Vec3 HeadLocal => new Vec3( 0f, 0f, EyeHeight );

	private Quat HeadLocalRotation => SboxConvert.ToCore( new Angles( _pitch, 0f, 0f ).ToRotation() );

	// Stand the walker so that its head is at this eye point, facing this yaw, level, still.
	private void PlaceWalker( Vec3 eyeMetres, float yawDegrees )
	{
		Quat q = Quat.CreateFromAxisAngle( Units.Up, yawDegrees * ( System.MathF.PI / 180f ) );
		_walker.Anchor = new RigidTransform( q, eyeMetres - new Vec3( 0f, 0f, EyeHeight ) );
		_walker.Reset();
	}

	// Put the camera where the walker has the head, looking the way the walker's play space is turned.
	private void ApplyPose()
	{
		WorldPosition = SboxConvert.ToInches( _walker.Anchor.TransformPoint( HeadLocal ) );
		WorldRotation = SboxConvert.ToEngine( _walker.Anchor.TransformRotation( HeadLocalRotation ) );
	}

	protected override void OnUpdate()
	{
		if ( Game.IsRunningInVR ) return;   // the headset drives the view instead

		if ( !LockView )
		{
			// Look. AnalogLook is the mouse movement already turned into degrees by the engine. Turning is the
			// walker's (about the head, positive yaw is to the left); looking up and down is the camera's own.
			Angles look = Input.AnalogLook;
			_walker.Turn( HeadLocal, -look.yaw * ( System.MathF.PI / 180f ) );
			_pitch = System.Math.Clamp( _pitch + look.pitch, -89f, 89f );

			// Walk. AnalogMove is WASD as a vector (x = forward, y = left). Turn it by the yaw only, so
			// looking up or down does not lift the camera off the floor.
			Vector3 wish = Input.AnalogMove;
			Vector3 step = Rotation.FromYaw( YawDegrees ) * wish * ( WalkSpeed * Time.Delta );   // metres
			MoveAsPlayer( SboxConvert.ToCore( step ), WalkSpeed * Time.Delta );
			ApplyPose();

			if ( Input.Pressed( "Attack1" ) ) ThrowAlongView( PaintColor.Orange );
			if ( Input.Pressed( "Attack2" ) ) ThrowAlongView( PaintColor.Blue );
		}
		else if ( _walkLeft > 0f || _settling )
		{
			// A scripted walk: straight ahead on the floor, under the same rules as a person at the keys.
			// (A camera a script has only placed stays where it was put: it does not fall until it walks.)
			float distance = System.MathF.Min( _walkLeft, _walkSpeed * Time.Delta );
			_walkLeft -= distance;
			Vector3 step = Rotation.FromYaw( YawDegrees ) * new Vector3( distance, 0f, 0f );
			MoveAsPlayer( SboxConvert.ToCore( step ), System.MathF.Max( _walkSpeed, WalkSpeed ) * Time.Delta );
			ApplyPose();
		}
		else
		{
			ApplyPose();
		}

		_sinceStart += Time.Delta;
		if ( AutoThrow && !_autoThrown && _sinceStart >= AutoThrowDelay )
		{
			_autoThrown = true;
			if ( Script == ThrowScript.PaintTour ) StartTour( false );
			else RunAutoThrow();
		}

		UpdateTour();
		UpdateBurst();
	}

	// ------------------------------------------------------------------ the camera as a player

	/// <summary>
	/// Walk straight ahead (the way the camera faces, on the floor) for this many metres at this speed,
	/// with nobody at the keys. Walls stop it, a linked patch carries it through (the walk then goes on
	/// in the new direction), and it falls or is lifted afterwards, exactly as for a person walking.
	/// </summary>
	public void Walk( float metres, float speed )
	{
		LockView = true;
		_walkLeft = System.MathF.Max( 0f, metres );
		_walkSpeed = System.MathF.Max( 0.01f, speed );
	}

	/// <summary>Stop a scripted walk where it is.</summary>
	public void StopWalk()
	{
		_walkLeft = 0f;
	}

	/// <summary>
	/// Move the camera by <paramref name="wish"/> (metres, world) the way a player moves: solid walls hold
	/// it back, a linked patch carries it through to the other patch, a linked patch in the floor under its
	/// body is a hole it falls into, and afterwards it flies, falls (or is lifted) to its standing height and
	/// is turned upright. All the deciding is the core's PlaySpaceWalker; this only feeds it and writes down
	/// what happened.
	/// </summary>
	private void MoveAsPlayer( Vec3 wish, float maxPushOut )
	{
		float dt = Time.Delta;
		if ( !Room.IsValid() )
		{
			_walker.Anchor = _walker.Anchor.Then( PlaySpace.Shift( wish ) );
			return;
		}

		PortalLink portals = Room.Portals;
		float floor = SboxConvert.ToMetres( Room.WorldPosition ).Z;

		bool linked = false;
		PortalPair pair = default;
		IPaintMask maskA = null, maskB = null;
		PortalSettings settings = FallbackSettings;
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

		_walker.HeadRadius = HeadRadius;
		_walker.WallClearance = WallClearance;
		_walker.BodyRadius = BodyRadius;
		_walker.RightingSeconds = RightingSeconds;
		_walker.FloorExitRise = FloorExitRise;
		_walker.AirSteer = AirSteer;
		_walker.SlideFriction = SlideFriction;
		_walker.Fall.MaxFallSpeed = MaxFallSpeed;
		_walker.Fall.LiftSpeed = LiftSpeed;
		_walker.Fall.Gravity = SboxConvert.ToMetres( Scene.PhysicsWorld.Gravity ).Length();

		Quat headRotation = HeadLocalRotation;
		Quat viewBefore = _walker.Anchor.TransformRotation( headRotation );
		bool wasInAir = _walker.InAir;

		PlaySpaceFrame frame = _walker.Move( HeadLocal, headRotation, wish, maxPushOut, dt, floor, linked, pair, maskA, maskB,
			Room.WallFrames, Room.WallCount, settings );
		LastFrame = frame;
		BlockedByWall = frame.Blocked;
		OverHole = frame.OverHole;

		if ( frame.Crossed )
		{
			PlayerStep step = frame.Crossing;
			Vec3 eyeNow = step.Head;
			RigidTransform plain = pair.PlayerCrossing( step.FromA );
			Vec3 forward = new Vec3( 1f, 0f, 0f );

			// The view straight after the crossing. With the righting spread over time nothing has been turned
			// upright yet in this frame, so this is what the walker really did; with righting "at once" the walker
			// has already levelled the view, and the crossing itself is the step's own transform.
			Quat viewAfter = frame.Righted > 0f ? step.Crossing.TransformRotation( viewBefore ) : _walker.Anchor.TransformRotation( headRotation );
			Angles before = SboxConvert.ToEngine( viewBefore ).Angles();
			Angles after = SboxConvert.ToEngine( viewAfter ).Angles();

			PortalLink.CrossingRecord record = new PortalLink.CrossingRecord();
			record.IsPlayer = true;
			record.FromOrange = step.FromA;
			record.EntryUV = step.EntryUV;
			record.PositionBefore = frame.HeadBeforeCrossing;
			record.VelocityBefore = Vec3.Transform( forward, viewBefore );
			record.PositionAfter = eyeNow;
			record.VelocityAfter = Vec3.Transform( forward, viewAfter );
			record.PositionPredicted = plain.TransformPoint( frame.HeadBeforeCrossing );
			record.VelocityPredicted = plain.TransformDirection( record.VelocityBefore );
			record.ExitMovedBy = step.ExitMovedBy;
			record = portals.Record( record );
			LastCrossing = record;
			LastCrossingFrame = frame;
			PortalCrossings++;

			PaintWall inWall = portals.WallOf( step.FromA ? PaintColor.Orange : PaintColor.Blue );
			PaintWall outWall = portals.WallOf( step.FromA ? PaintColor.Blue : PaintColor.Orange );
			string inName = inWall.IsValid() ? inWall.WallName : "?";
			string outName = outWall.IsValid() ? outWall.WallName : "?";
			PlayerArrival arrival = PlayerArrival.Check( eyeNow - new Vec3( 0f, 0f, EyeHeight ), floor );
			float facingError = MathUtil.AngleBetween( record.VelocityAfter, record.VelocityPredicted ) * 57.29578f;
			float yawPredicted = PlaySpace.YawOf( plain.TransformRotation( viewBefore ) ) * 57.29578f;
			float positionError = ( eyeNow - record.PositionPredicted ).Length() * 1000f;
			string arrivalText = arrival.FallDistance > 0.001f ? "falls " + arrival.FallDistance.ToString( "0.000" ) + " m"
				: arrival.LiftNeeded > 0.001f ? "lifted " + arrival.LiftNeeded.ToString( "0.000" ) + " m" : "on the floor";
			Vec3 vIn = frame.VelocityBeforeCrossing, vOut = frame.VelocityAfterCrossing;
			float tipped = PlaySpace.TiltOf( step.Crossing.Rotation ) * 57.29578f;
			Log.Info( $"[FlatThrower] THROUGH crossing {record.Number}: the desk camera went into {( step.FromA ? "ORANGE" : "BLUE" )} on {inName} at u={step.EntryUV.X:0.000} v={step.EntryUV.Y:0.000} and out of {( step.FromA ? "BLUE" : "ORANGE" )} on {outName} (pair revision {record.PairRevision}), {( frame.CrossedFlying ? "falling or flying" : "walking" )}." );
			Log.Info( $"[FlatThrower]   BEFORE {record.Number}: eye ({record.PositionBefore.X:0.0000}, {record.PositionBefore.Y:0.0000}, {record.PositionBefore.Z:0.0000}) m, facing yaw {before.yaw:0.000} deg, pitch {before.pitch:0.000} deg, velocity ({vIn.X:0.000}, {vIn.Y:0.000}, {vIn.Z:0.000}) m/s" );
			Log.Info( $"[FlatThrower]   AFTER  {record.Number}: eye ({eyeNow.X:0.0000}, {eyeNow.Y:0.0000}, {eyeNow.Z:0.0000}) m, facing yaw {after.yaw:0.000} deg, pitch {after.pitch:0.000} deg, roll {after.roll:0.000} deg, velocity ({vOut.X:0.000}, {vOut.Y:0.000}, {vOut.Z:0.000}) m/s" );
			Log.Info( $"[FlatThrower]   CORE   {record.Number}: eye ({record.PositionPredicted.X:0.0000}, {record.PositionPredicted.Y:0.0000}, {record.PositionPredicted.Z:0.0000}) m, facing yaw {yawPredicted:0.000} deg. Difference: position {positionError:0.000} mm, facing {facingError:0.0000} deg, exit slid {step.ExitMovedBy * 1000f:0.0} mm." );
			Log.Info( $"[FlatThrower]   ARRIVAL {record.Number}: feet {arrival.HeightAboveFloor:0.000} m from the floor (minus = under it): {arrivalText}. The crossing tips the view {tipped:0.0} deg{( tipped > 0.01f ? ( RightingSeconds > 0f ? $": swung upright over {RightingSeconds:0.00} s" : ": levelled at once" ) : "" )}." );
		}

		_settling = frame.InAir || frame.Tilt > 0f || frame.Velocity != Vec3.Zero;
		if ( wasInAir && !_walker.InAir )
		{
			Vec3 eye = frame.Head;
			Log.Info( $"[FlatThrower] The desk camera is back on the floor: eye ({eye.X:0.000}, {eye.Y:0.000}, {eye.Z:0.000}) m." );
		}
	}

	private void ThrowAlongView( PaintColor paint )
	{
		SboxConvert.ToCorePose( WorldTransform, out Vec3 eye, out Quat rotation );
		Vec3 forward = Vec3.Transform( new Vec3( 1f, 0f, 0f ), rotation );   // +X is forward

		// Start a little in front of the camera so the ball is not born inside the near clip distance.
		Portaball.Spawn( Scene, paint, eye + forward * 0.3f, forward * ThrowSpeed, new Vec3( 0f, 0f, 0f ), SplatRadius );
	}

	// ------------------------------------------------------------------ the two-throw test

	private void RunAutoThrow()
	{
		SboxConvert.ToCorePose( WorldTransform, out Vec3 eye, out Quat rotation );
		Vec3 forward = Vec3.Transform( new Vec3( 1f, 0f, 0f ), rotation );
		Vec3 left = Vec3.Transform( new Vec3( 0f, 1f, 0f ), rotation );          // +Y is left

		if ( !Room.IsValid() || !AimAssist.FindAimPoint( eye, forward, Room.WallFrames, Room.WallCount, 100f, out _, out int wallIndex, out _ ) )
		{
			Log.Warning( "[FlatThrower] AUTO THROW: no wall in front of the camera, nothing thrown." );
			return;
		}

		WallFrame wall = Room.WallFrames[wallIndex];
		Vec3 gravity = SboxConvert.ToMetres( Scene.PhysicsWorld.Gravity );

		// Two fixed points on that wall: 30% and 70% of the way across, a little below half height.
		Vec3 orangeTarget = wall.ToWorld( new Vec2( wall.Width * 0.3f, wall.Height * 0.45f ) );
		Vec3 blueTarget = wall.ToWorld( new Vec2( wall.Width * 0.7f, wall.Height * 0.45f ) );

		// The balls start a hand's width either side of the camera so they do not bump each other.
		AutoThrowAt( PaintColor.Orange, eye + forward * 0.4f + left * 0.25f, orangeTarget, gravity, AutoThrowSpeed, SplatRadius );
		AutoThrowAt( PaintColor.Blue, eye + forward * 0.4f - left * 0.25f, blueTarget, gravity, AutoThrowSpeed, SplatRadius );
	}

	private void AutoThrowAt( PaintColor paint, Vec3 from, Vec3 target, Vec3 gravity, float speed, float splatRadius )
	{
		// Ask the core how to throw to land exactly on the target (the flatter of the two possible arcs).
		// If the set speed cannot reach, use the slowest throw that does.
		LaunchSolution solution = Ballistics.SolveLaunch( from, target, speed, gravity );
		Vec3 velocity = solution.Reachable ? solution.LowArcVelocity : solution.MinSpeedVelocity;

		Portaball.Spawn( Scene, paint, from, velocity, new Vec3( 0f, 0f, 0f ), splatRadius );
		Log.Info( $"[FlatThrower] AUTO THROW: {paint} from ({from.X:0.00}, {from.Y:0.00}, {from.Z:0.00}) m toward ({target.X:0.00}, {target.Y:0.00}, {target.Z:0.00}) m at {velocity.Length():0.0} m/s" );
	}

	// ------------------------------------------------------------------ the paint tour

	private struct TourThrow
	{
		public float At;          // seconds into the tour
		public PaintColor Paint;
		public int Wall;          // index into Room.Walls: 0 = +X (far), 1 = -X, 2 = +Y (left), 3 = -Y (right)
		public float U, V;        // target in wall metres
		public string Note;
	}

	// The whole show. Wall 3 (-Y, on your right) has u = 0 at the far corner and counts back toward the camera.
	private static readonly TourThrow[] TourThrows =
	{
		new TourThrow { At = 0.0f, Paint = PaintColor.Orange, Wall = 0, U = 2.4f, V = 2.55f, Note = "orange, high on the far wall" },
		new TourThrow { At = 2.5f, Paint = PaintColor.Blue, Wall = 3, U = 2.0f, V = 1.90f, Note = "blue on the right-hand wall" },
		new TourThrow { At = 5.0f, Paint = PaintColor.Blue, Wall = 3, U = 2.4f, V = 1.75f, Note = "second blue, overlapping the first: joins it" },
		new TourThrow { At = 8.5f, Paint = PaintColor.Orange, Wall = 3, U = 5.0f, V = 2.00f, Note = "orange on the right-hand wall: replaces the first orange" },
	};

	/// <summary>How long the tour's throwing part lasts, seconds. The last paint settles a few seconds later.</summary>
	public const float TourLength = 12f;

	private struct PictureStop
	{
		public int AfterHit;      // 1 = first ball to land
		public float Delay;       // seconds after that hit
		public string Label;
		public bool Done;
	}

	// Moments worth a picture. Only used when the tour was started with pauseForPictures.
	private static readonly PictureStop[] DefaultPictureStops =
	{
		new PictureStop { AfterHit = 1, Delay = 0.06f, Label = "01_orange_just_hit" },
		new PictureStop { AfterHit = 1, Delay = 1.50f, Label = "02_orange_mid_drip" },
		new PictureStop { AfterHit = 2, Delay = 0.06f, Label = "03_blue_just_hit" },
		new PictureStop { AfterHit = 3, Delay = 0.06f, Label = "04_blue_second_just_hit" },
		new PictureStop { AfterHit = 3, Delay = 1.50f, Label = "05_blue_joined_mid_drip" },
		new PictureStop { AfterHit = 4, Delay = 0.10f, Label = "06_orange_replaced_old_fading" },
		new PictureStop { AfterHit = 4, Delay = 1.50f, Label = "07_orange_second_mid_drip" },
		new PictureStop { AfterHit = 4, Delay = 9.00f, Label = "08_settled" },
	};

	/// <summary>True from StartTour until the last picture stop (or TourLength) has passed.</summary>
	public bool TourRunning { get; private set; }

	/// <summary>Seconds since the tour's first throw. Stands still while paused.</summary>
	public float TourTime { get; private set; }

	/// <summary>When the tour has stopped the clock for a picture: which one. Empty otherwise.</summary>
	public string PausedAt { get; private set; } = "";

	private int _tourNext;
	private bool _tourPauses;
	private PictureStop[] _stops = new PictureStop[0];
	private readonly List<float> _hitTimes = new List<float>();
	private int _hitsSeen;

	/// <summary>
	/// Wipe the walls and run the paint tour from the start. With <paramref name="pauseForPictures"/> the
	/// game's clock stops at each picture moment (see <see cref="PausedAt"/>) until <see cref="ResumeTour"/>.
	/// The first throw comes <paramref name="leadSeconds"/> after this call; a timing run started by a script
	/// asks for a couple of seconds, so the stall the script's own call causes is over before anything is timed.
	/// </summary>
	public void StartTour( bool pauseForPictures, float leadSeconds = 0.25f )
	{
		if ( !Room.IsValid() ) { Log.Warning( "[FlatThrower] TOUR: no room." ); return; }

		Scene.TimeScale = 1f;
		Room.ClearAllPaint();
		Room.Patches.ResetTimings();
		Room.Patches.LogWorstPerSecond = true;

		TourView();

		TourRunning = true;
		TourTime = -leadSeconds;
		PausedAt = "";
		_tourNext = 0;
		_tourPauses = pauseForPictures;
		_stops = new PictureStop[DefaultPictureStops.Length];
		for ( int i = 0; i < _stops.Length; i++ ) _stops[i] = DefaultPictureStops[i];
		_hitTimes.Clear();
		_hitsSeen = 0;
		Log.Info( $"[FlatThrower] TOUR started (pause for pictures: {pauseForPictures})." );
	}

	/// <summary>The tour's camera: back-left corner, turned so the far wall and the right-hand wall are both in view.</summary>
	public void TourView()
	{
		Vec3 floor = SboxConvert.ToMetres( Room.WorldPosition );
		Vec3 eye = floor + new Vec3( -Room.Length * 0.5f + 0.6f, Room.Width * 0.5f - 0.6f, 1.75f );
		SetView( eye, new Angles( 0f, -35f, 0f ) );
	}

	/// <summary>Let the clock run again after a picture stop.</summary>
	public void ResumeTour()
	{
		PausedAt = "";
		Scene.TimeScale = 1f;
	}

	private void UpdateTour()
	{
		if ( !TourRunning ) return;
		TourTime += Time.Delta;

		// Throws.
		while ( _tourNext < TourThrows.Length && TourTime >= TourThrows[_tourNext].At )
		{
			// Count the paint's cost from the first throw, so wiping the walls for the tour is not in it.
			if ( _tourNext == 0 ) Room.Patches.ResetTimings();
			TourThrow t = TourThrows[_tourNext++];
			WallFrame wall = Room.WallFrames[t.Wall];
			SboxConvert.ToCorePose( WorldTransform, out Vec3 eye, out Quat rotation );
			Vec3 forward = Vec3.Transform( new Vec3( 1f, 0f, 0f ), rotation );
			Vec3 gravity = SboxConvert.ToMetres( Scene.PhysicsWorld.Gravity );
			Log.Info( $"[FlatThrower] TOUR t={TourTime:0.00}: {t.Note}" );
			AutoThrowAt( t.Paint, eye + forward * 0.4f - new Vec3( 0f, 0f, 0.25f ), wall.ToWorld( new Vec2( t.U, t.V ) ), gravity, TourThrowSpeed, SplatRadius );
		}

		// Notice paint landing (a few frames after its ball hit).
		int hits = 0;
		for ( int i = 0; i < Room.Walls.Count; i++ ) hits += Room.Walls[i].LandedCount;
		while ( _hitsSeen < hits ) { _hitTimes.Add( TourTime ); _hitsSeen++; }

		// Picture stops.
		bool anyLeft = false;
		for ( int i = 0; i < _stops.Length; i++ )
		{
			if ( _stops[i].Done ) continue;
			anyLeft = true;
			int k = _stops[i].AfterHit - 1;
			if ( k >= _hitTimes.Count || TourTime < _hitTimes[k] + _stops[i].Delay ) continue;
			_stops[i].Done = true;
			if ( _tourPauses )
			{
				PausedAt = _stops[i].Label;
				Scene.TimeScale = 0f;
				Log.Info( $"[FlatThrower] TOUR t={TourTime:0.00}: clock stopped for picture {PausedAt}" );
				return;   // one stop at a time
			}
		}

		// Over when the throwing is done and the last picture moment has passed (or, if a ball never
		// landed, a little after that would have been).
		if ( TourTime >= TourLength && ( !anyLeft || TourTime >= TourLength + 12f ) )
		{
			TourRunning = false;
			Log.Info( $"[FlatThrower] TOUR finished at t={TourTime:0.00}. {Room.Patches.TimingReport()}" );
		}
	}

	// ------------------------------------------------------------------ the burst: many hits on one wall, for timing

	/// <summary>True from StartBurst until its report has been logged.</summary>
	public bool BurstRunning { get; private set; }

	private int _burstWall, _burstCount, _burstNext;
	private float _burstGap, _burstTime;

	/// <summary>
	/// Wipe the walls, then lay <paramref name="count"/> splats on one wall spread evenly over
	/// <paramref name="seconds"/>, with the same call a landing ball makes, and log what the paint cost
	/// four seconds after the last one. Orange and blue take turns: each colour's hits overlap and join
	/// into one growing patch, except the ninth hit, an orange far from the orange patch, which replaces it.
	/// </summary>
	public void StartBurst( int wallIndex = 3, int count = 10, float seconds = 2f, float leadSeconds = 0.25f )
	{
		if ( !Room.IsValid() ) { Log.Warning( "[FlatThrower] BURST: no room." ); return; }
		Scene.TimeScale = 1f;
		TourRunning = false;
		Room.ClearAllPaint();
		TourView();
		_burstWall = wallIndex;
		_burstCount = count;
		_burstGap = seconds / System.Math.Max( 1, count );
		_burstNext = 0;
		_burstTime = -leadSeconds;
		BurstRunning = true;
		Log.Info( $"[FlatThrower] BURST started: {count} hits in {seconds:0.0} s on {Room.Walls[wallIndex].WallName}." );
	}

	private void UpdateBurst()
	{
		if ( !BurstRunning ) return;
		_burstTime += Time.Delta;
		while ( _burstNext < _burstCount && _burstTime >= _burstNext * _burstGap )
		{
			if ( _burstNext == 0 )
			{
				Room.Patches.ResetTimings();
				Room.Patches.LogWorstPerSecond = true;
			}
			int i = _burstNext++;
			int k = i / 2;
			bool orange = i % 2 == 0;
			Vec2 uv = orange ? new Vec2( 1.5f + 0.35f * k, 1.9f ) : new Vec2( 5.0f + 0.35f * k, 1.7f );
			if ( i == 8 ) uv = new Vec2( 3.4f, 2.6f );   // far from the orange patch: replaces it
			Room.Walls[_burstWall].Splat( orange ? PaintColor.Orange : PaintColor.Blue, uv, SplatRadius, new Vec2( 1.5f, -1f ), (uint)( 9000 + i ), 8f );
		}
		if ( _burstNext >= _burstCount && _burstTime >= ( _burstCount - 1 ) * _burstGap + 4f )
		{
			BurstRunning = false;
			Log.Info( $"[FlatThrower] BURST finished at t={_burstTime:0.00}. {Room.Patches.TimingReport()}" );
		}
	}

	// ------------------------------------------------------------------ camera placement for scripts

	/// <summary>Put the camera at a point (metres) with a given pitch and yaw (degrees), and stop the mouse moving it.</summary>
	public void SetView( Vec3 eyeMetres, Angles look )
	{
		LockView = true;

		// Wherever a script puts the camera is where it stands: level, no fall from there (not even over a
		// floor portal, until it walks), no velocity, no walk left over.
		float floor = Room.IsValid() ? SboxConvert.ToMetres( Room.WorldPosition ).Z : 0f;
		EyeHeight = eyeMetres.Z - floor;
		_pitch = look.pitch;
		PlaceWalker( eyeMetres, look.yaw );
		_settling = false;
		_walkLeft = 0f;
		OverHole = false;
		BlockedByWall = false;
		ApplyPose();
	}

	/// <summary>Where the camera is and which way it faces, for scripts: eye in metres, yaw and pitch in degrees.</summary>
	public Vec3 EyeMetres => _walker.Anchor.TransformPoint( HeadLocal );
	public float YawDegrees => PlaySpace.YawOf( _walker.Anchor.Rotation ) * 57.29578f;
	public float PitchDegrees => _pitch;

	/// <summary>Turn the camera to face this yaw (degrees, 0 = +X, positive to the left), keeping its place. Levels it too.</summary>
	public void Face( float yawDegrees, float pitchDegrees = 0f )
	{
		Vec3 eye = EyeMetres;
		_pitch = pitchDegrees;
		Quat q = Quat.CreateFromAxisAngle( Units.Up, yawDegrees * ( System.MathF.PI / 180f ) );
		_walker.Anchor = new RigidTransform( q, eye - new Vec3( 0f, 0f, EyeHeight ) );
		ApplyPose();
	}

	/// <summary>Put the camera at a point (metres), looking at another point (metres).</summary>
	public void LookAt( Vec3 eyeMetres, Vec3 targetMetres )
	{
		Vec3 dir = Vec3.Normalize( targetMetres - eyeMetres );
		Rotation r = Rotation.LookAt( SboxConvert.ToEngine( dir ), Vector3.Up );
		Angles a = r.Angles();
		a.roll = 0f;
		SetView( eyeMetres, a );
	}

	/// <summary>
	/// Look at a point on a wall from close up. <paramref name="distance"/> is how far the camera is from
	/// that point, metres. <paramref name="sideDegrees"/> swings the camera round to the viewer's right
	/// (negative: left) and <paramref name="upDegrees"/> lifts it, so 0, 0 is square-on.
	/// </summary>
	public void ViewWallPoint( int wallIndex, float u, float v, float distance, float sideDegrees = 0f, float upDegrees = 0f )
	{
		WallFrame wall = Room.WallFrames[wallIndex];
		Vec3 target = wall.ToWorld( new Vec2( u, v ) );
		float side = sideDegrees * ( System.MathF.PI / 180f );
		float up = upDegrees * ( System.MathF.PI / 180f );
		Vec3 offset = wall.Normal * ( System.MathF.Cos( side ) * System.MathF.Cos( up ) )
			+ wall.U * ( System.MathF.Sin( side ) * System.MathF.Cos( up ) )
			+ wall.V * System.MathF.Sin( up );
		LookAt( target + offset * distance, target );
	}
}
