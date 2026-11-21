using System;
using System.Collections.Generic;
using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// Small tools for the see-through pictures taken by engine\seethrough_capture.ps1 (desk mode, nobody
/// at the keyboard). Nothing here runs unless a script calls it.
///
/// The main one is the REFERENCE picture. What you see through a portal is supposed to be what a camera
/// standing at the virtual eye would see of the exit room. To check that without using any of the
/// see-through code: <see cref="ReferenceOn"/> hides the exit wall and its paint and moves the ordinary
/// desk camera to the virtual eye, with portals drawn dark. The picture taken then must match, inside the
/// opening, the picture taken through the portal (<see cref="OpeningBox"/> says where that is on screen).
/// </summary>
public static class SeeThroughProof
{
	private static readonly List<GameObject> Hidden = new List<GameObject>();
	private static Vec3 _savedEye;
	private static Angles _savedLook;
	private static PortalViewMode _savedMode;
	private static bool _referenceOn;

	/// <summary>Lay the two patches of a named layout on bare walls (the same call a landing ball makes). Returns what it did.</summary>
	public static string Layout( TestRoom room, string name )
	{
		foreach ( Portaball ball in room.Scene.GetAllComponents<Portaball>() ) ball.GameObject.Destroy();
		room.ClearAllPaint();
		const float splat = 0.42f;
		PaintColor O = PaintColor.Orange, B = PaintColor.Blue;
		switch ( name )
		{
			case "facing":
				room.Walls[0].Splat( O, new Vec2( 3.0f, 1.75f ), splat, Vec2.Zero, 9101, 8f );
				room.Walls[1].Splat( B, new Vec2( 3.0f, 1.75f ), splat, Vec2.Zero, 9102, 8f );
				return "facing: orange on Wall +X, blue on Wall -X";
			case "rightangle":
				room.Walls[0].Splat( O, new Vec2( 3.0f, 1.75f ), splat, Vec2.Zero, 9103, 8f );
				room.Walls[3].Splat( B, new Vec2( 4.0f, 1.75f ), splat, Vec2.Zero, 9104, 8f );
				return "right angle: orange on Wall +X, blue on Wall -Y";
			case "samewall":
				room.Walls[0].Splat( O, new Vec2( 1.4f, 1.75f ), splat, Vec2.Zero, 9105, 8f );
				room.Walls[0].Splat( B, new Vec2( 4.6f, 1.75f ), splat, Vec2.Zero, 9106, 8f );
				return "same wall: orange and blue both on Wall +X";
			case "high":
				room.Walls[0].Splat( O, new Vec2( 3.0f, 1.55f ), splat, Vec2.Zero, 9107, 8f );
				room.Walls[3].Splat( B, new Vec2( 4.0f, 2.75f ), splat, Vec2.Zero, 9108, 8f );
				return "high: orange low on Wall +X, blue high on Wall -Y";
			case "floor":
				// Floor to wall, like Portal: orange in the floor 1.5 m ahead of the middle of the room (where the
				// headset player starts, so they are not standing on it), blue at head height on the side wall.
				// Walk onto the orange and you fall in and fly out of the wall sideways.
				room.Walls[TestRoom.FloorIndex].Splat( O, new Vec2( room.Length * 0.5f + 1.5f, room.Width * 0.5f ), splat, Vec2.Zero, 9111, 8f );
				room.Walls[3].Splat( B, new Vec2( 4.0f, 1.75f ), splat, Vec2.Zero, 9112, 8f );
				return "floor: orange in the floor 1.5 m ahead, blue on Wall -Y";
			case "droplets":
				// A linked pair plus stray paint of the same colour that is not part of the opening.
				room.Walls[0].Splat( O, new Vec2( 3.0f, 1.75f ), splat, Vec2.Zero, 9109, 8f );
				room.Walls[3].Splat( B, new Vec2( 4.0f, 1.75f ), splat, Vec2.Zero, 9110, 8f );
				return "droplets: as right angle (stray paint comes from the splats' own spatter)";
		}
		return "unknown layout " + name;
	}

	/// <summary>True when the pair is linked and no paint is moving any more.</summary>
	public static bool Settled( TestRoom room )
	{
		if ( !room.Portals.HasPair ) return false;
		for ( int i = 0; i < room.Walls.Count; i++ )
		{
			if ( room.Walls[i].IsPaintMoving ) return false;
		}
		return true;
	}

	/// <summary>A few things standing in the room, so there is something to see through a portal besides bare walls.</summary>
	public static string Furnish( TestRoom room )
	{
		GameObject existing = null;
		foreach ( GameObject child in room.GameObject.Children )
		{
			if ( child.Name == "See-through furniture" ) existing = child;
		}
		if ( existing.IsValid() ) return "already furnished";

		Vec3 floor = SboxConvert.ToMetres( room.WorldPosition );
		GameObject root = new GameObject( room.GameObject, true, "See-through furniture" );
		Box( root, "red pillar", floor + new Vec3( 1.2f, 1.4f, 0.9f ), new Vec3( 0.35f, 0.35f, 1.8f ), new Color( 0.85f, 0.15f, 0.15f ) );
		Box( root, "green block", floor + new Vec3( -1.6f, -1.2f, 0.3f ), new Vec3( 0.6f, 0.6f, 0.6f ), new Color( 0.2f, 0.75f, 0.25f ) );
		Box( root, "yellow table", floor + new Vec3( 0.2f, -0.4f, 0.75f ), new Vec3( 0.9f, 0.5f, 0.06f ), new Color( 0.95f, 0.8f, 0.15f ) );
		Box( root, "table leg", floor + new Vec3( 0.2f, -0.4f, 0.36f ), new Vec3( 0.08f, 0.08f, 0.72f ), new Color( 0.5f, 0.4f, 0.1f ) );
		Box( root, "cyan post", floor + new Vec3( -3.3f, -2.4f, 1.1f ), new Vec3( 0.2f, 0.2f, 2.2f ), new Color( 0.15f, 0.8f, 0.85f ) );
		Box( root, "purple step", floor + new Vec3( 2.6f, -1.8f, 0.2f ), new Vec3( 0.8f, 0.5f, 0.4f ), new Color( 0.6f, 0.25f, 0.8f ) );
		Box( root, "white beam", floor + new Vec3( 0f, 2.2f, 2.6f ), new Vec3( 5.0f, 0.12f, 0.12f ), new Color( 0.95f, 0.95f, 0.95f ) );
		return "furnished: 7 boxes";
	}

	private static void Box( GameObject parent, string name, Vec3 centre, Vec3 size, Color tint )
	{
		GameObject go = new GameObject( parent, false, name );
		go.WorldPosition = SboxConvert.ToInches( centre );
		go.LocalScale = SboxConvert.BoxScaleFor( size );
		ModelRenderer r = go.AddComponent<ModelRenderer>();
		r.Model = Model.Cube;
		r.Tint = tint;
		go.Enabled = true;
	}

	private static Portaball _thrown;

	/// <summary>The ball thrown by <see cref="ThrowInto"/>, while it lasts.</summary>
	public static Portaball Thrown => _thrown;

	/// <summary>
	/// Throw one ball of the other colour into the middle of an end from in front of it (2.5 m out unless told otherwise), a little
	/// to one side, so it goes through. Returns what it did. <see cref="BallState"/> then follows it.
	/// </summary>
	public static string ThrowInto( TestRoom room, PaintColor into, float speed = 8f, float distance = 2.5f )
	{
		PortalLink portals = room.Portals;
		if ( !portals.HasPair ) return "no pair";
		PortalEnd end = portals.EndOf( into );
		Vec3 target = end.PaintCenter;
		Vec3 from = target + end.Normal * distance + end.Wall.U * ( 0.14f * distance ) - new Vec3( 0f, 0f, 0.06f * distance );
		Vec3 gravity = SboxConvert.ToMetres( room.Scene.PhysicsWorld.Gravity );
		LaunchSolution solution = Ballistics.SolveLaunch( from, target, speed, gravity );
		Vec3 velocity = solution.Reachable ? solution.LowArcVelocity : solution.MinSpeedVelocity;
		_thrown = Portaball.Spawn( room.Scene, into == PaintColor.Orange ? PaintColor.Blue : PaintColor.Orange, from, velocity, new Vec3( 0f, 3f, 0f ), 0.42f );
		return $"threw #{_thrown.Seed} at {into} from ({from.X:0.00}, {from.Y:0.00}, {from.Z:0.00}) m at {velocity.Length():0.0} m/s";
	}

	/// <summary>
	/// The thrown ball right now: "gone", or "flying|inportal  crossings N  d_orange  d_blue  twins T", where
	/// the two distances are from its centre to each end's wall in millimetres (minus = behind the wall).
	/// </summary>
	public static string BallState( TestRoom room )
	{
		if ( !_thrown.IsValid() || _thrown.Spent ) return "gone";
		PortalLink portals = room.Portals;
		Vec3 p = _thrown.PositionMetres;
		float dO = portals.HasPair ? portals.Pair.A.Wall.SignedDistance( p ) * 1000f : 0f;
		float dB = portals.HasPair ? portals.Pair.B.Wall.SignedDistance( p ) * 1000f : 0f;
		return $"{( _thrown.InPortal ? "inportal" : "flying" )} crossings {_thrown.Crossings} orange {dO:0.0} blue {dB:0.0} twins {room.Twins.ActiveTwins}";
	}

	/// <summary>Take the thrown ball away (so it does not repaint a patch).</summary>
	public static void RemoveBall()
	{
		if ( _thrown.IsValid() ) _thrown.GameObject.Destroy();
		_thrown = null;
	}

	/// <summary>
	/// A ball that just hangs in the air, standing in for one held in a hand (desk mode has no hands). It is
	/// an ordinary scene object like a held ball, so if this shows through a portal, so does a held one.
	/// </summary>
	public static string HeldBall( TestRoom room, Vec3 at, PaintColor colour )
	{
		GameObject go = new GameObject( room.GameObject, false, "Held ball stand-in" );
		go.WorldPosition = SboxConvert.ToInches( at );
		go.LocalScale = SboxConvert.SphereScaleFor( Portaball.RadiusMetres );
		ModelRenderer r = go.AddComponent<ModelRenderer>();
		r.Model = Model.Sphere;
		PaintLook.Glow( r, colour );
		go.Enabled = true;
		return $"held-ball stand-in at ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00}) m";
	}

	/// <summary>
	/// Where on a picture of the given size the middle of an end's opening is, as "x0 y0 x1 y1" in pixels
	/// (y down): a square of <paramref name="share"/> times the opening's largest circle, as seen right now
	/// by the desk camera. Empty when the end is not in front of the camera.
	/// </summary>
	public static string OpeningBox( TestRoom room, FlatThrower desk, PaintColor colour, int width, int height, float share = 0.55f )
	{
		PortalLink portals = room.Portals;
		if ( !portals.HasPair ) return "";
		PortalEnd end = portals.EndOf( colour );
		CameraComponent camera = room.Scene.Camera;
		LensTangents lens = LensTangents.Symmetric( camera.FieldOfView, width / (float)height );
		SboxConvert.ToCorePose( camera.WorldTransform, out Vec3 eye, out Quat rotation );
		float r = end.Opening.InscribedRadius * share;
		float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
		for ( int k = 0; k < 4; k++ )
		{
			Vec2 uv = end.Opening.CenterUV + new Vec2( ( k == 0 || k == 3 ) ? -r : r, k < 2 ? -r : r );
			Vec3 v = CameraConventions.WorldToView( end.Wall.ToWorld( uv ), eye, rotation );
			if ( !( v.Z < -0.001f ) ) return "";
			float x = ( v.X / -v.Z - lens.Left ) / ( lens.Right - lens.Left ) * width;
			float y = ( lens.Up - v.Y / -v.Z ) / ( lens.Up - lens.Down ) * height;
			minX = MathF.Min( minX, x ); maxX = MathF.Max( maxX, x );
			minY = MathF.Min( minY, y ); maxY = MathF.Max( maxY, y );
		}
		// The largest square that is inside all four projected corners is not needed: shrink to the inner half.
		float cx = ( minX + maxX ) * 0.5f, cy = ( minY + maxY ) * 0.5f;
		float hw = ( maxX - minX ) * 0.35f, hh = ( maxY - minY ) * 0.35f;
		int x0 = Math.Clamp( (int)MathF.Ceiling( cx - hw ), 0, width - 1 ), x1 = Math.Clamp( (int)MathF.Floor( cx + hw ), 0, width - 1 );
		int y0 = Math.Clamp( (int)MathF.Ceiling( cy - hh ), 0, height - 1 ), y1 = Math.Clamp( (int)MathF.Floor( cy + hh ), 0, height - 1 );
		if ( x1 <= x0 || y1 <= y0 ) return "";
		return $"{x0} {y0} {x1} {y1}";
	}

	/// <summary>
	/// Set up the reference picture for looking INTO <paramref name="into"/>: hide the exit wall and its
	/// paint, draw portals dark, and stand the desk camera at the virtual eye. Returns where it put it.
	/// </summary>
	public static string ReferenceOn( TestRoom room, FlatThrower desk, PaintColor into )
	{
		if ( _referenceOn ) return "reference is already on";
		PortalLink portals = room.Portals;
		PortalView view = room.View;
		if ( !portals.HasPair ) return "no pair";
		PortalPair pair = portals.Pair;
		bool throughA = into == PaintColor.Orange;
		PaintWall exitWall = portals.WallOf( throughA ? PaintColor.Blue : PaintColor.Orange );

		_savedEye = desk.EyeMetres;
		_savedLook = new Angles( desk.PitchDegrees, desk.YawDegrees, 0f );
		_savedMode = view.Mode;

		SboxConvert.ToCorePose( desk.WorldTransform, out Vec3 eye, out Quat rotation );
		PortalCamera.VirtualEye( pair.PlayerCrossing( throughA ), eye, rotation, out Vec3 virtualEye, out Quat virtualRotation );
		Angles look = SboxConvert.ToEngine( virtualRotation ).Angles();

		Hide( exitWall.GameObject );
		Hide( exitWall.Panel.Object );
		view.Mode = PortalViewMode.C;
		desk.SetView( virtualEye, look );
		_referenceOn = true;
		return $"reference: camera at the virtual eye ({virtualEye.X:0.0000}, {virtualEye.Y:0.0000}, {virtualEye.Z:0.0000}) m, pitch {look.pitch:0.000} yaw {look.yaw:0.000} roll {look.roll:0.000}; {exitWall.WallName} and its paint hidden; it is {-exitWall.Frame.SignedDistance( virtualEye ):0.0000} m behind that wall";
	}

	/// <summary>Undo <see cref="ReferenceOn"/>.</summary>
	public static string ReferenceOff( TestRoom room, FlatThrower desk )
	{
		if ( !_referenceOn ) return "reference was not on";
		for ( int i = 0; i < Hidden.Count; i++ )
		{
			if ( !Hidden[i].IsValid() ) continue;
			foreach ( ModelRenderer r in Hidden[i].GetComponents<ModelRenderer>( true ) ) r.Enabled = true;
		}
		Hidden.Clear();
		room.View.Mode = _savedMode;
		desk.SetView( _savedEye, _savedLook );
		_referenceOn = false;
		return "reference off";
	}

	private static void Hide( GameObject go )
	{
		if ( !go.IsValid() ) return;
		foreach ( ModelRenderer r in go.GetComponents<ModelRenderer>( true ) ) r.Enabled = false;
		Hidden.Add( go );
	}
}

/// <summary>
/// Stops the game clock in the very frame the desk camera goes through a portal, so a picture can be
/// taken of the first frame on the far side (before the camera starts to fall or be lifted to its
/// standing height). A script adds it, arms it, walks the camera, takes the picture and sets
/// Scene.TimeScale back to 1.
/// </summary>
public sealed class SeeThroughFreeze : Component
{
	public FlatThrower Desk { get; set; }

	/// <summary>True from <see cref="Arm"/> until the crossing has been seen.</summary>
	public bool Armed { get; private set; }

	/// <summary>True once the clock has been stopped by a crossing.</summary>
	public bool Frozen { get; private set; }

	/// <summary>Where the eye was in the frame the clock stopped, metres, and which way it faced.</summary>
	public Vec3 EyeAtFreeze { get; private set; }
	public float YawAtFreeze { get; private set; }

	private int _crossings;
	private bool _ballArmed;
	private int _stepsLeft;

	/// <summary>How many physics steps have run since <see cref="ArmBall"/> stopped the clock.</summary>
	public int StepsSinceBallFreeze { get; private set; }

	/// <summary>Stop the clock at the physics step in which the thrown ball comes within half a metre of a portal wall.</summary>
	public void ArmBall()
	{
		_ballArmed = true;
		Frozen = false;
		StepsSinceBallFreeze = 0;
		Scene.TimeScale = 1f;
	}

	/// <summary>With the clock stopped: let exactly this many physics steps run, then stop it again.</summary>
	public void StepPhysics( int steps )
	{
		_stepsLeft = steps;
		Scene.TimeScale = 1f;
	}

	protected override void OnFixedUpdate()
	{
		if ( _stepsLeft > 0 )
		{
			StepsSinceBallFreeze++;
			if ( --_stepsLeft == 0 ) Scene.TimeScale = 0f;
			return;
		}
		if ( !_ballArmed ) return;
		Portaball ball = SeeThroughProof.Thrown;
		if ( !ball.IsValid() || ball.Spent ) { _ballArmed = false; return; }
		// Stop a few steps early: when the ball is within half a metre of either end's wall.
		bool near = ball.InPortal;
		foreach ( TestRoom room in Scene.GetAllComponents<TestRoom>() )
		{
			if ( !room.Portals.IsValid() || !room.Portals.HasPair ) continue;
			Vec3 p = ball.PositionMetres;
			if ( room.Portals.Pair.A.Wall.SignedDistance( p ) < 0.5f || room.Portals.Pair.B.Wall.SignedDistance( p ) < 0.5f ) near = true;
		}
		if ( !near ) return;
		_ballArmed = false;
		Frozen = true;
		Scene.TimeScale = 0f;
	}

	public void Arm()
	{
		_crossings = Desk.PortalCrossings;
		Armed = true;
		Frozen = false;
		Scene.TimeScale = 1f;
	}

	// Late in the frame: after the camera has moved, before the frame is drawn.
	protected override void OnPreRender()
	{
		if ( !Armed || !Desk.IsValid() ) return;
		if ( Desk.PortalCrossings == _crossings ) return;
		Armed = false;
		Frozen = true;
		Desk.StopWalk();
		Scene.TimeScale = 0f;
		EyeAtFreeze = Desk.EyeMetres;
		YawAtFreeze = Desk.YawDegrees;
	}
}

/// <summary>
/// Averages the engine's own frame-time numbers over a stretch of frames, for the cost measurements
/// (engine\seethrough_cost.ps1). A script calls <see cref="Begin"/>, waits, and reads <see cref="Result"/>.
///
/// What is averaged: the whole frame time and the GPU frame time (both from the engine's
/// PerformanceStats), and the main thread's time by category (PerformanceStats.Timings). When a frame-rate
/// cap is on (vsync, fps_max) the frame time is the cap, so the CPU's real work is frame time minus idle.
/// </summary>
public sealed class SeeThroughCost : Component
{
	public bool Running { get; private set; }

	/// <summary>The latest finished measurement, as one line of text. Empty while one is running.</summary>
	public string Result { get; private set; } = "";

	private double _end;
	private int _frames;
	private double _sumFrame, _maxFrame, _sumGpu, _maxGpu;
	private int _gpuSamples;
	private uint _lastGpu;
	private readonly System.Collections.Generic.List<double> _frameTimes = new System.Collections.Generic.List<double>();

	public void Begin( float seconds )
	{
		Running = true;
		Result = "";
		_end = RealTime.Now + seconds;
		_frames = 0;
		_sumFrame = _maxFrame = _sumGpu = _maxGpu = 0.0;
		_gpuSamples = 0;
		_lastGpu = Sandbox.Diagnostics.PerformanceStats.GpuFrameNumber;
		_frameTimes.Clear();
	}

	protected override void OnUpdate()
	{
		if ( !Running ) return;
		double frame = Sandbox.Diagnostics.PerformanceStats.FrameTime * 1000.0;
		_frames++;
		_sumFrame += frame;
		_frameTimes.Add( frame );
		if ( frame > _maxFrame ) _maxFrame = frame;
		uint gpuNumber = Sandbox.Diagnostics.PerformanceStats.GpuFrameNumber;
		if ( gpuNumber != _lastGpu )
		{
			_lastGpu = gpuNumber;
			double gpu = Sandbox.Diagnostics.PerformanceStats.GpuFrametime;
			_sumGpu += gpu;
			if ( gpu > _maxGpu ) _maxGpu = gpu;
			_gpuSamples++;
		}
		if ( RealTime.Now < _end ) return;

		Running = false;
		int n = System.Math.Min( _frames, 250 );
		string categories = "";
		double idle = 0.0;
		foreach ( Sandbox.Diagnostics.PerformanceStats.Timings t in Sandbox.Diagnostics.PerformanceStats.Timings.GetMain() )
		{
			float ms = t.AverageMs( n );
			if ( t.Name == "Idle" ) idle = ms;
			if ( ms >= 0.005f ) categories += $" {t.Name} {ms:0.000}";
		}
		_frameTimes.Sort();
		double p99 = _frameTimes.Count > 0 ? _frameTimes[System.Math.Min( _frameTimes.Count - 1, (int)( _frameTimes.Count * 0.99 ) )] : 0.0;
		double meanFrame = _sumFrame / System.Math.Max( 1, _frames );
		Result = $"frames {_frames}, frame mean {meanFrame:0.000} ms (99th percentile {p99:0.000}, worst {_maxFrame:0.000}), CPU work (frame minus idle) {meanFrame - idle:0.000} ms, GPU mean {( _gpuSamples > 0 ? _sumGpu / _gpuSamples : 0.0 ):0.000} ms (worst {_maxGpu:0.000}, {_gpuSamples} readings); main thread by category, ms:{categories}";
	}
}
