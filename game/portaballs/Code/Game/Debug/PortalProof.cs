using System;
using System.Collections.Generic;
using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>Which scripted portal test runs.</summary>
public enum PortalProofScript
{
	None,

	/// <summary>Every desk test below, one after the other (about six minutes).</summary>
	All,

	/// <summary>Patches on the two facing end walls, made by thrown balls. A ball through each way.</summary>
	Facing,

	/// <summary>Patches on two walls at right angles. A ball through each way.</summary>
	RightAngle,

	/// <summary>Both patches on the same wall. A ball through each way.</summary>
	SameWall,

	/// <summary>A low patch and a high one: the ball, then the desk camera, come out high and fall. Then a low exit: the camera is lifted.</summary>
	HighPatch,

	/// <summary>The desk camera: stopped by bare wall, walks through and back, stopped at the rim.</summary>
	WalkThrough,

	/// <summary>A ball at the rim, a ball at unlinked paint, balls thrown while a patch is replaced.</summary>
	EdgeCases,

	/// <summary>The VR play-space code (PlaySpaceWalker) driven by made-up sticks and a made-up head, shown through the desk camera.</summary>
	VrDryRun,

	/// <summary>For the helper desk scene: wait for a helper-thrown ball to make a patch, link it, and watch a helper-thrown ball go through.</summary>
	HelperThrow,

	/// <summary>A ball thrown at the floor, and one at the ceiling, leave paint and a patch; looking down into a floor portal, two layouts.</summary>
	FloorPaint,

	/// <summary>Balls through floor and ceiling portals: floor to wall and back, floor to floor, ceiling to floor and back, and one dropped in.</summary>
	FloorBalls,

	/// <summary>The desk camera as the player: onto a floor portal and out of a wall, into a wall and up out of the floor, floor to floor, and round a ceiling-over-floor loop.</summary>
	FloorWalk,
}

/// <summary>
/// Scripted portal tests that run with nobody at the keyboard, log numbers, and can stop the clock for
/// pictures. Desk mode only.
///
/// Start one from a scene (PortaballsGame.PortalProof) or from a script:  proof.Run( PortalProofScript.All, true ).
/// With pictures on, the clock stops at each picture moment and <see cref="PausedAt"/> names it; take the
/// picture and call <see cref="Resume"/> (engine\portal_proof_capture.ps1 does this).
///
/// Every check writes one line starting "[PortalProof] PASS" or "[PortalProof] FAIL". The balls and the
/// camera write their own before / after / core-prediction lines ("[Portaball] THROUGH", "[FlatThrower] THROUGH").
/// </summary>
public sealed class PortalProof : Component
{
	[Property] public PortalProofScript Script { get; set; } = PortalProofScript.None;

	/// <summary>Seconds after the scene starts before <see cref="Script"/> begins.</summary>
	[Property] public float StartDelay { get; set; } = 2f;

	/// <summary>Stop the clock at each picture moment and wait for <see cref="Resume"/>.</summary>
	[Property] public bool PauseForPictures { get; set; }

	public TestRoom Room { get; set; }
	public FlatThrower Desk { get; set; }

	/// <summary>When the clock is stopped for a picture: which one. Empty otherwise.</summary>
	public string PausedAt { get; private set; } = "";

	public bool Running { get; private set; }
	public bool Finished { get; private set; }
	public int Passes { get; private set; }
	public int Failures { get; private set; }

	private readonly Stack<IEnumerator<object>> _stack = new Stack<IEnumerator<object>>();
	private float _sinceStart;
	private bool _autoStarted;
	private bool _timedOut;
	private uint _splatSeed = 9000;

	private const float Splat = 0.42f;
	private const PaintColor O = PaintColor.Orange;
	private const PaintColor B = PaintColor.Blue;

	// ------------------------------------------------------------------ running

	/// <summary>Start a script now (stops one that is running).</summary>
	public void Run( PortalProofScript script, bool pauseForPictures )
	{
		_stack.Clear();
		PausedAt = "";
		Scene.TimeScale = 1f;
		PauseForPictures = pauseForPictures;
		Passes = 0;
		Failures = 0;
		Finished = false;
		Running = true;
		_tracked.Clear();
		_stack.Push( Main( script ) );
		Log.Info( $"[PortalProof] START {script} (pictures: {pauseForPictures})." );
	}

	/// <summary>Let the clock run again after a picture stop.</summary>
	public void Resume()
	{
		PausedAt = "";
	}

	protected override void OnUpdate()
	{
		if ( !_autoStarted && Script != PortalProofScript.None )
		{
			_sinceStart += Time.Delta;
			if ( _sinceStart >= StartDelay )
			{
				_autoStarted = true;
				Run( Script, PauseForPictures );
			}
		}

		// Step the script: run until it asks to wait a frame.
		int guard = 0;
		while ( _stack.Count > 0 && guard++ < 64 )
		{
			IEnumerator<object> top = _stack.Peek();
			bool more;
			try
			{
				more = top.MoveNext();
			}
			catch ( Exception e )
			{
				Log.Warning( $"[PortalProof] FAIL the script stopped with an error: {e.Message}" );
				Failures++;
				_stack.Clear();
				break;
			}
			if ( !more ) { _stack.Pop(); continue; }
			if ( top.Current is IEnumerator<object> sub ) { _stack.Push( sub ); continue; }
			break;
		}

		if ( Running && _stack.Count == 0 )
		{
			Running = false;
			Finished = true;
			Scene.TimeScale = 1f;
			PausedAt = "";
			Log.Info( $"[PortalProof] FINISHED: {Passes} checks passed, {Failures} failed." );
		}
	}

	protected override void OnFixedUpdate()
	{
		WatchBalls();

		// The "replace a patch while a ball is inside the opening" test acts on the physics clock.
		if ( _replaceWhenInside.IsValid() && _replaceWhenInside.InPortal && _replaceWhenInside.Crossings == _replaceCrossingsAtThrow )
		{
			_replaceWhenInside = null;
			_replaceDone = true;
			Log.Info( $"[PortalProof] the ball is inside the opening now: replacing the {_replaceColour} patch with a splat on {Room.Walls[_replaceWall].WallName}." );
			Room.Walls[_replaceWall].Splat( _replaceColour, _replaceUV, Splat, Vec2.Zero, _splatSeed++, 8f );
		}
	}

	private IEnumerator<object> Main( PortalProofScript script )
	{
		bool all = script == PortalProofScript.All;
		if ( !Room.IsValid() || !Desk.IsValid() )
		{
			Check( "desk mode", false, "no room or no desk camera" );
			yield break;
		}

		if ( all || script == PortalProofScript.Facing ) yield return Facing();
		if ( all || script == PortalProofScript.RightAngle ) yield return RightAngle();
		if ( all || script == PortalProofScript.SameWall ) yield return SameWall();
		if ( all || script == PortalProofScript.HighPatch ) yield return HighPatch();
		if ( all || script == PortalProofScript.WalkThrough ) yield return WalkThrough();
		if ( all || script == PortalProofScript.EdgeCases ) yield return EdgeCases();
		if ( all || script == PortalProofScript.VrDryRun ) yield return VrDryRun();
		if ( all || script == PortalProofScript.FloorPaint ) yield return FloorPaint();
		if ( all || script == PortalProofScript.FloorBalls ) yield return FloorBalls();
		if ( all || script == PortalProofScript.FloorWalk ) yield return FloorWalk();
		if ( script == PortalProofScript.HelperThrow ) yield return HelperThrow();

		yield return Wait( 0.5f );
		Bookkeeping();
	}

	// ------------------------------------------------------------------ small tools

	private void Check( string name, bool ok, string detail )
	{
		if ( ok ) Passes++; else Failures++;
		if ( ok ) Log.Info( $"[PortalProof] PASS {name}: {detail}" );
		else Log.Warning( $"[PortalProof] FAIL {name}: {detail}" );
	}

	private void Say( string text )
	{
		Log.Info( $"[PortalProof] {text}" );
	}

	private static string V( Vec3 v )
	{
		return $"({v.X:0.000}, {v.Y:0.000}, {v.Z:0.000})";
	}

	/// <summary>Wait this many seconds of game time.</summary>
	private IEnumerator<object> Wait( float seconds )
	{
		float t = 0f;
		while ( t < seconds )
		{
			yield return null;
			t += Time.Delta;
		}
	}

	/// <summary>Wait until the condition is true, or give up after the time-out (game seconds). Sets _timedOut.</summary>
	private IEnumerator<object> Until( Func<bool> condition, float timeout )
	{
		_timedOut = false;
		float t = 0f;
		while ( !condition() )
		{
			if ( t >= timeout ) { _timedOut = true; yield break; }
			yield return null;
			t += Time.Delta;
		}
	}

	/// <summary>Stop the clock for a picture and wait to be resumed (or one real minute).</summary>
	private IEnumerator<object> Picture( string label )
	{
		if ( !PauseForPictures )
		{
			yield break;
		}
		PausedAt = label;
		Scene.TimeScale = 0f;
		Say( $"clock stopped for picture {label}" );
		double started = RealTime.Now;
		while ( PausedAt != "" && RealTime.Now - started < 60.0 ) yield return null;
		PausedAt = "";
		Scene.TimeScale = 1f;
		yield return null;
	}

	private IEnumerator<object> ClearRoom( string title )
	{
		Say( $"======== {title} ========" );
		foreach ( Portaball ball in Scene.GetAllComponents<Portaball>() ) ball.GameObject.Destroy();
		Room.ClearAllPaint();
		Desk.TourView();
		yield return Wait( 0.3f );
	}

	/// <summary>Lay a splat straight onto a wall: the same call a landing ball makes.</summary>
	private void Paint( PaintColor colour, int wall, float u, float v )
	{
		Room.Walls[wall].Splat( colour, new Vec2( u, v ), Splat, Vec2.Zero, _splatSeed++, 8f );
	}

	/// <summary>Wait for the pair to link and for the paint to stop moving, then say where the two ends are.</summary>
	private IEnumerator<object> LinkedAndSettled( string name )
	{
		PortalLink portals = Room.Portals;
		yield return Until( () => portals.HasPair, 6f );
		Check( name + ": the two patches link", portals.HasPair, portals.HasPair ? $"pair revision {portals.PairRevision}" : "no pair after 6 s" );
		if ( !portals.HasPair ) yield break;

		yield return Until( () => !AnyPaintMoving(), 16f );
		yield return Wait( 0.3f );
		portals.Sync();
		if ( !portals.HasPair )
		{
			Check( name + ": still linked once the paint settled", false, "the pair came apart while settling" );
			yield break;
		}
		PortalPair pair = portals.Pair;
		Say( $"{name}: settled{( _timedOut ? " (gave up waiting for the last drips)" : "" )}. ORANGE on {portals.WallOf( O ).WallName} centre {V( pair.A.PaintCenter )} m, circle {pair.A.Opening.InscribedRadius:0.000} m. BLUE on {portals.WallOf( B ).WallName} centre {V( pair.B.PaintCenter )} m, circle {pair.B.Opening.InscribedRadius:0.000} m. Pair revision {portals.PairRevision}." );
	}

	private bool AnyPaintMoving()
	{
		for ( int i = 0; i < Room.Walls.Count; i++ )
		{
			if ( Room.Walls[i].IsPaintMoving ) return true;
		}
		return false;
	}

	private int WallIndexOf( PaintWall wall )
	{
		for ( int i = 0; i < Room.Walls.Count; i++ )
		{
			if ( Room.Walls[i] == wall ) return i;
		}
		return -1;
	}

	private int HitsWithSeed( uint seed, out string where )
	{
		int n = 0;
		where = "";
		for ( int i = 0; i < Room.Walls.Count; i++ )
		{
			IReadOnlyList<PaintWall.SplatHit> hits = Room.Walls[i].Hits;
			for ( int k = 0; k < hits.Count; k++ )
			{
				if ( hits[k].Seed != seed ) continue;
				n++;
				// The patch rule is applied a frame or more after the hit: until then there is no decision to show.
				where = $"{Room.Walls[i].WallName} at u={hits[k].UV.X:0.00} v={hits[k].UV.Y:0.00} ({( hits[k].Decided ? hits[k].Decision.ToString() : NotDecidedYet )})";
			}
		}
		return n;
	}

	private const string NotDecidedYet = "not decided yet";

	private int CrossingsWithSeed( uint seed )
	{
		int n = 0;
		IReadOnlyList<PortalLink.CrossingRecord> list = Room.Portals.Crossings;
		for ( int i = 0; i < list.Count; i++ )
		{
			if ( !list[i].IsPlayer && list[i].BallSeed == seed ) n++;
		}
		return n;
	}

	private bool FindCrossing( uint seed, out PortalLink.CrossingRecord record )
	{
		IReadOnlyList<PortalLink.CrossingRecord> list = Room.Portals.Crossings;
		for ( int i = list.Count - 1; i >= 0; i-- )
		{
			if ( !list[i].IsPlayer && list[i].BallSeed == seed ) { record = list[i]; return true; }
		}
		record = default;
		return false;
	}

	/// <summary>How far the paint of this colour reaches from a point along a direction on the wall, metres (2 mm steps).</summary>
	private static float PaintReach( PaintWall wall, PaintColor colour, Vec2 from, Vec2 direction )
	{
		float d = 0f;
		while ( d < 3f && wall.Surface.IsPainted( colour, from + direction * ( d + 0.002f ) ) ) d += 0.002f;
		return d;
	}

	// ------------------------------------------------------------------ balls: throwing and watching

	private sealed class Tracked
	{
		public Portaball Ball;
		public uint Seed;
		public string Name;
		public bool Escaped;
		public Vec3 EscapedAt;
		public bool RemovedByScript;

		// Written down at the moment the ball ends (a later test wipes the walls and their hit lists).
		public bool Ended;
		public int Hits;
		public string HitWhere = "";
		public int Crossings;
	}

	private readonly List<Tracked> _tracked = new List<Tracked>();

	private Portaball _replaceWhenInside;
	private int _replaceCrossingsAtThrow;
	private bool _replaceDone;
	private PaintColor _replaceColour;
	private int _replaceWall;
	private Vec2 _replaceUV;

	/// <summary>Throw a ball so that its centre arrives exactly at a point (the flatter of the two arcs).</summary>
	private Portaball ThrowAt( string name, PaintColor paint, Vec3 from, Vec3 target, float speed )
	{
		Vec3 gravity = SboxConvert.ToMetres( Scene.PhysicsWorld.Gravity );
		LaunchSolution solution = Ballistics.SolveLaunch( from, target, speed, gravity );
		Vec3 velocity = solution.Reachable ? solution.LowArcVelocity : solution.MinSpeedVelocity;
		Portaball ball = Portaball.Spawn( Scene, paint, from, velocity, new Vec3( 0f, 3f, 0f ), Splat );
		_tracked.Add( new Tracked { Ball = ball, Seed = ball.Seed, Name = name } );
		Say( $"throw '{name}': #{ball.Seed} {paint} from {V( from )} m toward {V( target )} m at {velocity.Length():0.00} m/s" );
		return ball;
	}

	/// <summary>Every physics step: a ball in free flight must be inside the room. Anywhere else it is stuck in a wall or lost.</summary>
	private void WatchBalls()
	{
		if ( !Room.IsValid() || _tracked.Count == 0 ) return;
		Vec3 floor = SboxConvert.ToMetres( Room.WorldPosition );
		float slack = 0.02f;
		float r = Portaball.RadiusMetres - slack;
		for ( int i = 0; i < _tracked.Count; i++ )
		{
			Tracked t = _tracked[i];
			if ( t.Ended )
			{
				// Written down at the hit, before the patch rule had its turn: fill in what it decided.
				if ( t.Hits == 1 && t.HitWhere.Contains( NotDecidedYet ) && HitsWithSeed( t.Seed, out string later ) == 1 ) t.HitWhere = later;
				continue;
			}
			if ( !t.Ball.IsValid() || t.Ball.Spent )
			{
				t.Ended = true;
				t.Hits = HitsWithSeed( t.Seed, out t.HitWhere );
				t.Crossings = CrossingsWithSeed( t.Seed );
				continue;
			}
			if ( t.Ball.InPortal || t.Escaped ) continue;
			Vec3 p = t.Ball.PositionMetres;
			bool inside = p.X >= floor.X - Room.Length * 0.5f + r && p.X <= floor.X + Room.Length * 0.5f - r
				&& p.Y >= floor.Y - Room.Width * 0.5f + r && p.Y <= floor.Y + Room.Width * 0.5f - r
				&& p.Z >= floor.Z + r && p.Z <= floor.Z + Room.Height - r;
			if ( !inside )
			{
				t.Escaped = true;
				t.EscapedAt = p;
			}
		}
	}

	/// <summary>At the end: every ball the script threw is accounted for exactly once.</summary>
	private void Bookkeeping()
	{
		if ( _tracked.Count == 0 ) return;
		int bad = 0;
		Dictionary<uint, int> alive = new Dictionary<uint, int>();
		foreach ( Portaball ball in Scene.GetAllComponents<Portaball>() )
		{
			if ( ball.Spent ) continue;
			alive[ball.Seed] = alive.TryGetValue( ball.Seed, out int n ) ? n + 1 : 1;
		}
		for ( int i = 0; i < _tracked.Count; i++ )
		{
			Tracked t = _tracked[i];
			string where = t.HitWhere;
			int hits = t.Ended ? t.Hits : HitsWithSeed( t.Seed, out where );
			int crossings = t.Ended ? t.Crossings : CrossingsWithSeed( t.Seed );
			alive.TryGetValue( t.Seed, out int living );
			string end = t.RemovedByScript ? "removed by the script after its measurement"
				: hits == 1 ? "burst on " + where
				: living == 1 ? "still in the room"
				: hits == 0 ? "timed out in the room (never reached a wall again)" : "?";
			bool ok = !t.Escaped && hits <= 1 && living <= 1 && !( hits == 1 && living == 1 );
			if ( !ok ) bad++;
			Say( $"ball '{t.Name}' #{t.Seed}: went through {crossings} time(s), {hits} splat(s), {living} still flying, outside the room {( t.Escaped ? "YES at " + V( t.EscapedAt ) : "never" )}: {end}{( ok ? "" : "  <-- WRONG" )}" );
		}
		Check( "no ball lost, stuck in a wall or doubled", bad == 0, $"{_tracked.Count} balls thrown by the script, {bad} wrong" );
	}

	/// <summary>
	/// Throw a ball into the middle of one end and measure what comes out of the other: the ball's own
	/// record (before, after, core prediction) is checked here against limits.
	/// </summary>
	/// <param name="viewIn">Places the camera for the "going in" picture (null: leave it).</param>
	/// <param name="viewOut">Places the camera for the "coming out" picture (null: leave it).</param>
	private IEnumerator<object> BallThrough( string name, PaintColor into, float speed, Action viewIn, Action viewOut, bool pictures )
	{
		PortalLink portals = Room.Portals;
		portals.Sync();
		if ( !portals.HasPair )
		{
			Check( name, false, "no linked pair to throw into" );
			yield break;
		}
		PortalEnd entry = portals.EndOf( into );
		PortalEnd exit = portals.EndOf( into == O ? B : O );
		PaintWall exitWall = portals.WallOf( into == O ? B : O );
		Vec3 target = entry.PaintCenter;
		// From 3 m out in front of the patch, a little to one side and a little low, so the ball comes in at an angle.
		Vec3 from = target + entry.Normal * 3.0f + entry.Wall.U * 0.5f - new Vec3( 0f, 0f, 0.25f );
		if ( viewIn != null ) viewIn();
		yield return Wait( 0.2f );

		Portaball ball = ThrowAt( name, into == O ? B : O, from, target, speed );
		Tracked tracked = _tracked[_tracked.Count - 1];
		uint seed = ball.Seed;

		yield return Until( () => !ball.IsValid() || ball.Spent || ball.InPortal || ball.Crossings > 0, 3f );
		if ( pictures && ball.IsValid() && ball.InPortal ) yield return Picture( name + "_1_going_in" );

		yield return Until( () => !ball.IsValid() || ball.Spent || ( ball.Crossings > 0 && !ball.InPortal ), 3f );
		bool through = ball.IsValid() && !ball.Spent && ball.Crossings == 1;
		string splatWhere = "";
		int splats = through ? 0 : HitsWithSeed( seed, out splatWhere );
		Check( name + ": the ball came out of the other patch", through, through ? $"#{seed} is flying on at {V( ball.PositionMetres )} m" : $"it did not go through (splats with its number: {splats} {splatWhere})" );
		if ( !through ) yield break;

		if ( viewOut != null ) viewOut();
		if ( pictures ) yield return Picture( name + "_2_coming_out" );

		// Let the physics engine take one step with it, so its own check line is written.
		yield return Until( () => !ball.IsValid() || ball.HandBackChecked, 1f );

		if ( FindCrossing( seed, out PortalLink.CrossingRecord r ) )
		{
			float speedIn = r.VelocityBefore.Length(), speedOut = r.VelocityAfter.Length();
			float positionError = ( r.PositionAfter - r.PositionPredicted ).Length() * 1000f;
			float directionError = MathUtil.AngleBetween( r.VelocityAfter, r.VelocityPredicted ) * 57.29578f;
			Check( name + ": speed kept", MathF.Abs( speedOut - speedIn ) < 0.0005f, $"{speedIn:0.0000} m/s in, {speedOut:0.0000} m/s out" );
			Check( name + ": exit direction is the core's", directionError < 0.01f, $"{directionError:0.0000} deg from PortalPair's crossing of the entry velocity" );
			if ( r.ExitMovedBy <= 0f )
				Check( name + ": exit position is the core's", positionError < 0.5f, $"{positionError:0.000} mm from PortalPair's crossing of the entry point" );
			else
				Say( $"{name}: the exit was slid {r.ExitMovedBy * 1000f:0.0} mm along the wall to stay on the exit patch (the patches are different shapes there), so position is not compared with the plain crossing." );

			// The same check using only the two walls' own axes (no crossing transform involved).
			float inInto = -Vec3.Dot( r.VelocityBefore, entry.Wall.Normal ), outOut = Vec3.Dot( r.VelocityAfter, exit.Wall.Normal );
			float inU = Vec3.Dot( r.VelocityBefore, entry.Wall.U ), outU = Vec3.Dot( r.VelocityAfter, exit.Wall.U );
			float inV = Vec3.Dot( r.VelocityBefore, entry.Wall.V ), outV = Vec3.Dot( r.VelocityAfter, exit.Wall.V );
			bool wallsOk = MathF.Abs( inInto - outOut ) < 0.002f && MathF.Abs( inU + outU ) < 0.002f && MathF.Abs( inV - outV ) < 0.002f && outOut > 0f;
			Check( name + ": comes out of the wall the way it went in", wallsOk, $"into {inInto:0.000} -> out {outOut:0.000} m/s, sideways {inU:0.000} -> {outU:0.000} (mirrored), up {inV:0.000} -> {outV:0.000}" );
		}
		else
		{
			Check( name + ": crossing was recorded", false, "no record for this ball" );
		}

		if ( ball.IsValid() && ball.HandBackChecked )
		{
			Check( name + ": the physics engine carries on from the right place", ball.HandBackPositionError < 0.005f && ball.HandBackVelocityError < 0.05f,
				$"one step after hand-back it is {ball.HandBackPositionError * 1000f:0.00} mm and {ball.HandBackVelocityError:0.0000} m/s from free flight carried through the core's crossing" );
		}

		// A moment of flight on the far side, then take the test ball away so it does not repaint a patch.
		yield return Wait( 0.15f );
		if ( ball.IsValid() && !ball.Spent )
		{
			Vec3 p = ball.PositionMetres;
			Check( name + ": flying on in the room, clear of the exit wall", exitWall.Frame.SignedDistance( p ) > Portaball.RadiusMetres && !tracked.Escaped,
				$"{exitWall.Frame.SignedDistance( p ):0.000} m out from {exitWall.WallName} at {V( p )} m, velocity {V( ball.VelocityMetres )} m/s" );
			tracked.RemovedByScript = true;
			ball.GameObject.Destroy();
		}
		yield return Wait( 0.2f );
	}

	// ------------------------------------------------------------------ the tests

	private IEnumerator<object> Facing()
	{
		yield return ClearRoom( "FACING WALLS (patches made by thrown balls)" );
		Vec3 floor = SboxConvert.ToMetres( Room.WorldPosition );
		Vec3 gravity = SboxConvert.ToMetres( Scene.PhysicsWorld.Gravity );
		Vec3 centre = floor + new Vec3( 0f, 0f, 1.5f );
		// Two real balls from the middle of the room, one at each end wall.
		Vec3 orangeTarget = Room.WallFrames[0].ToWorld( new Vec2( 3.0f, 1.75f ) );
		Vec3 blueTarget = Room.WallFrames[1].ToWorld( new Vec2( 3.0f, 1.75f ) );
		Portaball.Spawn( Scene, O, centre + new Vec3( 0.4f, 0f, 0f ), Ballistics.SolveLaunch( centre, orangeTarget, 11f, gravity ).LowArcVelocity, Vec3.Zero, Splat );
		Portaball.Spawn( Scene, B, centre - new Vec3( 0.4f, 0f, 0f ), Ballistics.SolveLaunch( centre, blueTarget, 11f, gravity ).LowArcVelocity, Vec3.Zero, Splat );
		Say( "threw an orange ball at Wall +X and a blue ball at Wall -X from the middle of the room" );

		yield return LinkedAndSettled( "facing" );
		PortalLink portals = Room.Portals;
		if ( !portals.HasPair ) yield break;
		Vec2 oc = portals.Pair.A.Opening.CenterUV, bc = portals.Pair.B.Opening.CenterUV;

		Desk.ViewWallPoint( 0, oc.X, oc.Y, 2.6f, 20f, 5f );
		yield return Picture( "facing_0_orange_end_linked" );
		Desk.ViewWallPoint( 1, bc.X, bc.Y, 2.6f, 20f, 5f );
		yield return Picture( "facing_0_blue_end_linked" );

		yield return BallThrough( "facing_orange_to_blue", O, 8f,
			() => Desk.ViewWallPoint( 0, oc.X, oc.Y, 1.6f, 55f, 10f ), () => Desk.ViewWallPoint( 1, bc.X, bc.Y, 1.6f, 55f, 10f ), true );
		yield return BallThrough( "facing_blue_to_orange", B, 8f,
			() => Desk.ViewWallPoint( 1, bc.X, bc.Y, 1.6f, 55f, 10f ), () => Desk.ViewWallPoint( 0, oc.X, oc.Y, 1.6f, 55f, 10f ), false );
	}

	private IEnumerator<object> RightAngle()
	{
		yield return ClearRoom( "WALLS AT RIGHT ANGLES" );
		Paint( O, 0, 3.0f, 1.75f );
		Paint( B, 3, 4.0f, 1.75f );
		yield return LinkedAndSettled( "right angle" );
		if ( !Room.Portals.HasPair ) yield break;

		Desk.TourView();
		yield return Picture( "rightangle_0_linked_room" );
		Vec2 oc = Room.Portals.Pair.A.Opening.CenterUV;
		Desk.ViewWallPoint( 0, oc.X, oc.Y - 0.2f, 1.3f, 25f, 5f );
		yield return Picture( "rightangle_0_orange_close" );
		Desk.TourView();

		yield return BallThrough( "rightangle_orange_to_blue", O, 8f, null, null, true );
		yield return BallThrough( "rightangle_blue_to_orange", B, 8f, null, null, true );
	}

	private IEnumerator<object> SameWall()
	{
		yield return ClearRoom( "BOTH PATCHES ON THE SAME WALL" );
		Paint( O, 0, 1.4f, 1.75f );
		Paint( B, 0, 4.6f, 1.75f );
		yield return LinkedAndSettled( "same wall" );
		if ( !Room.Portals.HasPair ) yield break;

		Action view = () => Desk.ViewWallPoint( 0, 3.0f, 1.5f, 3.6f, 0f, 8f );
		view();
		yield return Picture( "samewall_0_linked" );
		yield return BallThrough( "samewall_orange_to_blue", O, 8f, view, view, true );
		yield return BallThrough( "samewall_blue_to_orange", B, 8f, view, view, false );
	}

	private IEnumerator<object> HighPatch()
	{
		yield return ClearRoom( "A PATCH HIGH ON A WALL" );
		Paint( O, 0, 3.0f, 1.55f );
		Paint( B, 3, 4.0f, 2.75f );
		yield return LinkedAndSettled( "high patch" );
		PortalLink portals = Room.Portals;
		if ( !portals.HasPair ) yield break;
		float floor = portals.FloorHeight;
		PortalPair pair = portals.Pair;
		float rise = pair.B.PaintCenter.Z - pair.A.PaintCenter.Z;
		Say( $"high patch: blue's centre is {rise:0.000} m higher than orange's ({pair.B.PaintCenter.Z - floor:0.000} m against {pair.A.PaintCenter.Z - floor:0.000} m above the floor)." );

		Desk.TourView();
		yield return Picture( "high_0_linked_room" );

		// 1. The ball: into the low orange patch, out of the high blue one, then it falls.
		Vec3 target = pair.A.PaintCenter;
		Vec3 from = target + pair.A.Normal * 3.0f - new Vec3( 0f, 0f, 0.1f );
		Portaball ball = ThrowAt( "high_ball_into_low_orange", B, from, target, 6f );
		Tracked tracked = _tracked[_tracked.Count - 1];
		uint seed = ball.Seed;
		yield return Until( () => !ball.IsValid() || ball.Spent || ( ball.Crossings > 0 && !ball.InPortal ), 3f );
		bool through = ball.IsValid() && !ball.Spent && ball.Crossings == 1;
		Check( "high patch: the ball went through", through, through ? "out of blue" : "it did not" );
		if ( through )
		{
			yield return Picture( "high_1_ball_out_high" );
			FindCrossing( seed, out PortalLink.CrossingRecord r );
			float outHeight = r.PositionAfter.Z - floor;
			Check( "high patch: the ball comes out high", MathF.Abs( ( r.PositionAfter.Z - r.PositionBefore.Z ) - rise ) < 0.002f && r.ExitMovedBy <= 0f,
				$"went in {r.PositionBefore.Z - floor:0.000} m above the floor, came out {outHeight:0.000} m above it: {r.PositionAfter.Z - r.PositionBefore.Z:0.000} m higher, the patches' centres differ by {rise:0.000} m (exit slid {r.ExitMovedBy * 1000f:0.0} mm)" );
			float z0 = ball.PositionMetres.Z;
			float t = 0f;
			float nextNote = 0f;
			string trace = "";
			while ( ball.IsValid() && !ball.Spent && t < 0.36f )
			{
				if ( t >= nextNote )
				{
					trace += $" t={t:0.00}s z={ball.PositionMetres.Z - floor:0.000}";
					nextNote += 0.09f;
				}
				yield return null;
				t += Time.Delta;
			}
			if ( ball.IsValid() && !ball.Spent )
			{
				Vec3 p = ball.PositionMetres;
				Vec3 v = ball.VelocityMetres;
				Check( "high patch: the ball then falls", p.Z < z0 - 0.2f && v.Z < -1f, $"height above the floor:{trace}; now {p.Z - floor:0.000} m, falling at {-v.Z:0.00} m/s" );
				tracked.RemovedByScript = true;
				ball.GameObject.Destroy();
			}
		}
		yield return Wait( 0.3f );

		// 2. The desk camera: walks into the low patch, comes out of the high one, falls to the floor.
		yield return CameraThrough( "high", O, 1.6f, true );

		// 3. Different shapes. Where orange has paint and blue (as carried across) has none, a ball would,
		//    carried rigidly, come out on bare wall. It must be slid along the exit wall onto the exit paint,
		//    direction and speed untouched.
		Say( "======== DIFFERENT SHAPES: A SECOND ORANGE SPLAT JOINS THE FIRST, SO ORANGE IS WIDER THAN BLUE ========" );
		portals.Sync();
		Vec2 firstCentre = portals.Pair.A.Opening.CenterUV;
		Paint( O, 0, firstCentre.X + 0.45f, 1.55f );
		yield return Wait( 0.5f );
		yield return LinkedAndSettled( "different shapes" );
		if ( !portals.HasPair ) yield break;
		Desk.TourView();
		yield return Picture( "shapes_0_orange_wider_than_blue_room" );
		pair = portals.Pair;
		Vec2 lowCentre = pair.A.Opening.CenterUV;
		// Look for a spot in orange with room for the ball (and half as much again) whose rigid crossing
		// lands where blue has no room for it. Lowest spots first: that is where the two differ most.
		Vec2 lowUV = lowCentre;
		bool found = false;
		for ( float dv = -1.6f; dv <= 0.5f && !found; dv += 0.05f )
		{
			for ( float du = -1.0f; du <= 1.0f && !found; du += 0.05f )
			{
				Vec2 candidate = lowCentre + new Vec2( du, dv );
				if ( candidate.Y < 0.12f ) continue;
				if ( !PortalCrossing.Passage( pair.A, portals.MaskOf( O ), candidate, Portaball.RadiusMetres * 1.5f, portals.Settings ).Fits ) continue;
				Vec2 lands = pair.B.Wall.ToUV( pair.AToB.TransformPoint( pair.A.Wall.ToWorld( candidate ) ) );
				if ( PortalCrossing.Passage( pair.B, portals.MaskOf( B ), lands, Portaball.RadiusMetres, portals.Settings ).PaintedSamples > 0 ) continue;
				lowUV = candidate;
				found = true;
			}
		}
		if ( !found )
		{
			Say( "different shapes: blue has paint everywhere orange does (as carried across), so there is nothing to slide in this layout. Skipped." );
		}
		Vec3 lowTarget = pair.A.Wall.ToWorld( lowUV );
		Vec3 plainExit = pair.AToB.TransformPoint( lowTarget );
		bool plainOnPaint = PortalCrossing.Passage( pair.B, portals.MaskOf( B ), pair.B.Wall.ToUV( plainExit ), Portaball.RadiusMetres, portals.Settings ).Fits;
		if ( found ) Say( $"different shapes: aiming at u={lowUV.X:0.000} v={lowUV.Y:0.000} on orange, ({lowUV.X - lowCentre.X:0.000}, {lowUV.Y - lowCentre.Y:0.000}) m from its centre. Carried rigidly that is {V( plainExit )} m on Wall -Y, which is bare wall: blue has no paint there." );
		if ( found )
		{
		Portaball lowBall = ThrowAt( "shapes_ball_where_blue_has_no_paint", B, lowTarget + pair.A.Normal * 2.5f + new Vec3( 0f, 0f, 0.15f ), lowTarget, 7f );
		Tracked lowTracked = _tracked[_tracked.Count - 1];
		uint lowSeed = lowBall.Seed;
		yield return Until( () => !lowBall.IsValid() || lowBall.Spent || ( lowBall.Crossings > 0 && !lowBall.InPortal ), 3f );
		bool lowThrough = lowBall.IsValid() && !lowBall.Spent && lowBall.Crossings == 1 && FindCrossing( lowSeed, out PortalLink.CrossingRecord low );
		if ( lowThrough )
		{
			FindCrossing( lowSeed, out low );
			yield return Picture( "shapes_1_ball_slid_onto_the_exit_patch" );
			bool exitOnPaint = PortalCrossing.Passage( pair.B, portals.MaskOf( B ), pair.B.Wall.ToUV( low.PositionAfter ), Portaball.RadiusMetres, portals.Settings ).Fits;
			float inCircle = Vec2.Distance( pair.B.Wall.ToUV( low.PositionAfter ), pair.B.Opening.CenterUV );
			float directionError = MathUtil.AngleBetween( low.VelocityAfter, low.VelocityPredicted ) * 57.29578f;
			Check( "different shapes: the exit is slid onto the exit patch", !plainOnPaint && low.ExitMovedBy > 0.01f && exitOnPaint,
				$"plain crossing {V( low.PositionPredicted )} m; came out at {V( low.PositionAfter )} m, slid {low.ExitMovedBy:0.000} m along the wall, {inCircle:0.000} m from blue's centre (its circle is {pair.B.Opening.InscribedRadius:0.000} m), on blue's paint: {exitOnPaint}" );
			Check( "different shapes: the slide does not change speed or direction", MathF.Abs( low.VelocityAfter.Length() - low.VelocityBefore.Length() ) < 0.0005f && directionError < 0.01f,
				$"speed {low.VelocityBefore.Length():0.0000} -> {low.VelocityAfter.Length():0.0000} m/s, direction {directionError:0.0000} deg from the core's crossing" );
			yield return Wait( 0.15f );
			if ( lowBall.IsValid() && !lowBall.Spent )
			{
				lowTracked.RemovedByScript = true;
				lowBall.GameObject.Destroy();
			}
		}
		else
		{
			string lowWhere = "";
			int lowHits = HitsWithSeed( lowSeed, out lowWhere );
			Check( "different shapes: the ball went through", false, $"it did not ({lowHits} splat: {lowWhere})" );
		}
		}
		yield return Wait( 0.3f );


		// 4. The other way about: a low exit. The camera comes out with its feet under the floor and is lifted.
		yield return ClearRoom( "A LOW EXIT: THE CAMERA IS LIFTED" );
		Paint( O, 0, 3.0f, 2.05f );
		Paint( B, 3, 4.0f, 1.15f );
		yield return LinkedAndSettled( "low exit" );
		if ( !portals.HasPair ) yield break;
		Desk.TourView();
		yield return Picture( "lowexit_0_linked_room" );
		yield return CameraThrough( "lowexit", O, 1.6f, true );
	}

	/// <summary>
	/// Walk the desk camera into one end from 1.5 m away. Pictures 3 cm before the wall, on the first frame
	/// after the crossing, and once it has settled on the floor. Checks the crossing against the core.
	/// </summary>
	private IEnumerator<object> CameraThrough( string name, PaintColor into, float wantedEyeHeight, bool pictures )
	{
		PortalLink portals = Room.Portals;
		portals.Sync();
		if ( !portals.HasPair ) { Check( name + ": camera walk", false, "no linked pair" ); yield break; }
		PortalPair pair = portals.Pair;
		PortalEnd entry = portals.EndOf( into );
		PortalEnd exit = portals.EndOf( into == O ? B : O );
		float floor = portals.FloorHeight;

		// Stand in front of the patch's centre, at normal eye height if the patch is open there, else at the centre's height.
		Vec2 centre = entry.Opening.CenterUV;
		float eyeV = wantedEyeHeight;
		bool openAtEyeHeight = PlayerPassage.IsOpenAt( pair, portals.MaskOf( O ), portals.MaskOf( B ), entry.Wall, new Vec2( centre.X, eyeV ), Desk.HeadRadius, portals.Settings );
		if ( !openAtEyeHeight ) eyeV = centre.Y;
		Vec3 start = entry.Wall.ToWorld( new Vec2( centre.X, eyeV ), 1.5f );
		float yaw = MathF.Atan2( -entry.Normal.Y, -entry.Normal.X ) * 57.29578f;
		Desk.SetView( start, new Angles( 0f, yaw, 0f ) );
		Say( $"{name}: camera placed {V( start )} m, facing the {into} patch from 1.5 m, eye {eyeV:0.00} m above the floor{( openAtEyeHeight ? "" : " (the patch's centre height: it is not open at 1.6 m)" )}." );
		yield return Wait( 0.2f );
		if ( pictures ) yield return Picture( name + "_2_camera_start" );

		int before = Desk.PortalCrossings;
		Desk.Walk( 1.5f - 0.03f, 2f );
		yield return Until( () => !Desk.IsWalking, 3f );
		Check( name + ": the wall lets the camera right up to an open patch", MathF.Abs( entry.Wall.SignedDistance( Desk.EyeMetres ) - 0.03f ) < 0.004f && Desk.PortalCrossings == before,
			$"3 cm from the wall was asked for, it is {entry.Wall.SignedDistance( Desk.EyeMetres ) * 100f:0.00} cm away" );
		if ( pictures ) yield return Picture( name + "_3_camera_3cm_before" );

		Desk.Walk( 0.06f, 2f );
		yield return Until( () => Desk.PortalCrossings > before || !Desk.IsWalking, 2f );
		bool crossed = Desk.PortalCrossings == before + 1;
		Check( name + ": the camera went through", crossed, crossed ? "one crossing" : $"{Desk.PortalCrossings - before} crossings" );
		if ( !crossed ) yield break;
		Desk.StopWalk();
		if ( pictures ) yield return Picture( name + "_4_camera_just_out" );

		PortalLink.CrossingRecord r = Desk.LastCrossing;
		float positionError = ( r.PositionAfter - r.PositionPredicted ).Length() * 1000f;
		float facingError = MathUtil.AngleBetween( r.VelocityAfter, r.VelocityPredicted ) * 57.29578f;
		float outOfWall = Vec3.Dot( r.VelocityAfter, exit.Normal );
		if ( r.ExitMovedBy <= 0f )
			Check( name + ": camera position is the core's", positionError < 0.5f, $"{positionError:0.000} mm from PortalPair.Player crossing of the eye point" );
		else
			Say( $"{name}: the camera's exit was slid {r.ExitMovedBy * 1000f:0.0} mm to stay on the exit patch." );
		Check( name + ": camera faces the mapped way", facingError < 0.01f && outOfWall > 0.99f, $"{facingError:0.0000} deg from the core; looking {outOfWall:0.000} along the exit wall's outward normal" );

		// Falling or lifting to the floor.
		float arriveHeight = r.PositionAfter.Z - floor - Desk.EyeHeight;
		float expected = ( exit.PaintCenter.Z - entry.PaintCenter.Z );
		if ( r.ExitMovedBy <= 0f )
			Check( name + ": arrives at the mapped height", MathF.Abs( ( r.PositionAfter.Z - r.PositionBefore.Z ) - expected ) < 0.002f,
				$"eye went from {r.PositionBefore.Z - floor:0.000} m to {r.PositionAfter.Z - floor:0.000} m above the floor; the patches' centres differ by {expected:0.000} m" );
		float t = 0f;
		string trace = "";
		float worstStep = 0f;
		float last = Desk.EyeMetres.Z;
		int frames = 0;
		while ( Desk.IsFalling && t < 4f )
		{
			if ( frames % 12 == 0 ) trace += $" t={t:0.00}s {Desk.EyeMetres.Z - floor:0.000}";
			yield return null;
			t += Time.Delta;
			frames++;
			float step = MathF.Abs( Desk.EyeMetres.Z - last );
			if ( Time.Delta > 0f && step / Time.Delta > worstStep ) worstStep = step / Time.Delta;
			last = Desk.EyeMetres.Z;
		}
		float restHeight = Desk.EyeMetres.Z - floor;
		string what = arriveHeight > 0.001f ? "fell" : arriveHeight < -0.001f ? "was lifted" : "stayed";
		Check( name + ": the camera ends standing on the floor", MathF.Abs( restHeight - Desk.EyeHeight ) < 0.002f && !Desk.IsFalling,
			$"it {what} {MathF.Abs( arriveHeight ):0.000} m in {t:0.00} s (fastest {worstStep:0.00} m/s); eye height above the floor:{trace}; at rest {restHeight:0.000} m (standing height {Desk.EyeHeight:0.000} m)" );
		if ( pictures ) yield return Picture( name + "_5_camera_on_the_floor" );

		// Walk on out into the room.
		Desk.Walk( 1.0f, 2f );
		yield return Until( () => !Desk.IsWalking, 2f );
		Check( name + ": walks on out of the exit patch", exit.Wall.SignedDistance( Desk.EyeMetres ) > 0.9f && Desk.PortalCrossings == before + 1,
			$"{exit.Wall.SignedDistance( Desk.EyeMetres ):0.000} m out from the exit wall, still one crossing" );
	}

	private IEnumerator<object> WalkThrough()
	{
		yield return ClearRoom( "THE DESK CAMERA WALKS THROUGH" );
		Paint( O, 0, 3.0f, 1.9f );
		Paint( B, 3, 4.0f, 1.9f );
		yield return LinkedAndSettled( "walk through" );
		PortalLink portals = Room.Portals;
		if ( !portals.HasPair ) yield break;
		PortalPair pair = portals.Pair;
		Vec3 floor = SboxConvert.ToMetres( Room.WorldPosition );

		// 1. Bare wall is solid.
		Desk.SetView( floor + new Vec3( 1.5f, 2.0f, 1.6f ), new Angles( 0f, 0f, 0f ) );
		yield return Wait( 0.1f );
		int crossingsAtWall = Desk.PortalCrossings;
		Desk.Walk( 4.0f, 2f );
		yield return Until( () => !Desk.IsWalking, 4f );
		float gap = Room.WallFrames[0].SignedDistance( Desk.EyeMetres );
		Check( "walk: bare wall stops the camera", MathF.Abs( gap - Desk.WallClearance ) < 0.002f && Desk.PortalCrossings == crossingsAtWall,
			$"asked to walk 4 m into Wall +X from 2.5 m away; stopped {gap:0.000} m from it (clearance {Desk.WallClearance:0.00} m), crossings during the walk {Desk.PortalCrossings - crossingsAtWall}" );
		yield return Picture( "walk_1_stopped_by_bare_wall" );

		// 2. Through orange, out of blue.
		int crossingsBefore = Desk.PortalCrossings;
		yield return CameraThrough( "walk", O, 1.6f, true );
		if ( Desk.PortalCrossings != crossingsBefore + 1 ) yield break;

		// 3. Turn round and walk back: into blue, out of orange.
		Desk.Face( Desk.YawDegrees + 180f );
		yield return Wait( 0.1f );
		Say( $"walk: turned round at {V( Desk.EyeMetres )} m, now facing yaw {Desk.YawDegrees:0.0} deg; walking back into the blue patch." );
		Desk.Walk( 1.2f, 2f );
		yield return Until( () => Desk.PortalCrossings > crossingsBefore + 1 || !Desk.IsWalking, 3f );
		bool back = Desk.PortalCrossings == crossingsBefore + 2 && !Desk.LastCrossing.FromOrange;
		Check( "walk: back the other way, into blue and out of orange", back, back ? "one more crossing, entered blue" : "no crossing on the way back" );
		if ( back )
		{
			PortalLink.CrossingRecord r = Desk.LastCrossing;
			float positionError = ( r.PositionAfter - r.PositionPredicted ).Length() * 1000f;
			float facingError = MathUtil.AngleBetween( r.VelocityAfter, r.VelocityPredicted ) * 57.29578f;
			Check( "walk: the way back matches the core too", ( r.ExitMovedBy > 0f || positionError < 0.5f ) && facingError < 0.01f,
				$"position {positionError:0.000} mm, facing {facingError:0.0000} deg from PortalPair.PlayerBToA (exit slid {r.ExitMovedBy * 1000f:0.0} mm)" );
			yield return Until( () => !Desk.IsWalking && !Desk.IsFalling, 3f );
			yield return Picture( "walk_6_back_out_of_orange" );
		}

		// 4. The rim: centre on paint, but no room for the whole head. Solid.
		portals.Sync();
		pair = portals.Pair;
		PaintWall orangeWall = portals.WallOf( O );
		Vec2 centre = pair.A.Opening.CenterUV;
		float reach = PaintReach( orangeWall, O, centre, new Vec2( 1f, 0f ) );
		Vec2 rimUV = centre + new Vec2( reach - 0.03f, 0f );
		Vec3 rimStart = pair.A.Wall.ToWorld( rimUV, 1.0f );
		Desk.SetView( rimStart, new Angles( 0f, MathF.Atan2( -pair.A.Normal.Y, -pair.A.Normal.X ) * 57.29578f, 0f ) );
		int beforeRim = Desk.PortalCrossings;
		yield return Wait( 0.1f );
		Desk.Walk( 1.5f, 2f );
		yield return Until( () => !Desk.IsWalking, 3f );
		yield return Wait( 0.3f );
		float rimGap = pair.A.Wall.SignedDistance( Desk.EyeMetres );
		Check( "walk: the rim of the patch is solid", Desk.PortalCrossings == beforeRim && rimGap > Desk.WallClearance - 0.005f,
			$"walked at a point 3 cm inside the paint's edge ({reach:0.000} m right of centre): no crossing, held {rimGap:0.000} m from the wall" );
		yield return Picture( "walk_7_stopped_at_the_rim" );
	}

	private IEnumerator<object> EdgeCases()
	{
		yield return ClearRoom( "EDGE CASES: UNLINKED PAINT, THE RIM, A PATCH REPLACED IN MID-FLIGHT" );
		PortalLink portals = Room.Portals;

		// (b) Unlinked paint: only orange exists, so nothing is linked. A ball at its middle must splat.
		Paint( O, 0, 3.0f, 1.75f );
		yield return Until( () => Room.Patches.TryGetOpening( O, out _, out _ ), 4f );
		yield return Wait( 1.0f );
		Room.Patches.TryGetOpening( O, out PaintWall loneWall, out PortalOpening lone );
		Check( "edge: one patch alone is not a portal", lone.Valid && !portals.HasPair, $"orange opening valid {lone.Valid}, linked {portals.HasPair}" );
		Vec3 loneTarget = loneWall.Frame.ToWorld( lone.CenterUV );
		Portaball unlinked = ThrowAt( "edge_unlinked_paint", O, loneTarget + loneWall.Frame.Normal * 3f, loneTarget, 8f );
		uint unlinkedSeed = unlinked.Seed;
		yield return Until( () => !unlinked.IsValid() || unlinked.Spent, 3f );
		yield return Wait( 0.1f );
		int unlinkedHits = HitsWithSeed( unlinkedSeed, out string unlinkedWhere );
		Check( "edge: a ball at unlinked paint splats", unlinkedHits == 1 && CrossingsWithSeed( unlinkedSeed ) == 0, $"{unlinkedHits} splat: {unlinkedWhere}; went through {CrossingsWithSeed( unlinkedSeed )} times" );
		Desk.ViewWallPoint( 0, lone.CenterUV.X, lone.CenterUV.Y - 0.2f, 1.6f, 20f, 5f );
		yield return Picture( "edge_1_unlinked_paint_looks_as_before" );

		// Now link it.
		Paint( B, 3, 4.0f, 1.75f );
		yield return LinkedAndSettled( "edge" );
		if ( !portals.HasPair ) yield break;
		Desk.ViewWallPoint( 0, lone.CenterUV.X, lone.CenterUV.Y - 0.2f, 1.6f, 20f, 5f );
		yield return Picture( "edge_2_same_patch_now_linked" );
		Desk.TourView();

		// (a) The rim: the ball's centre is on the paint, 1.5 cm inside its edge, so part of the ball is over bare wall.
		PortalPair pair = portals.Pair;
		PaintWall orangeWall = portals.WallOf( O );
		Vec2 centre = pair.A.Opening.CenterUV;
		float reach = PaintReach( orangeWall, O, centre, new Vec2( 1f, 0f ) );
		Vec2 rimUV = centre + new Vec2( reach - 0.015f, 0f );
		Vec3 rimTarget = pair.A.Wall.ToWorld( rimUV );
		int revisionBefore = Room.Patches.Get( O ).Opening.Revision;
		Portaball rim = ThrowAt( "edge_rim", O, rimTarget + pair.A.Normal * 3f, rimTarget, 8f );
		uint rimSeed = rim.Seed;
		yield return Until( () => !rim.IsValid() || rim.Spent, 3f );
		yield return Wait( 0.3f );
		int rimHits = HitsWithSeed( rimSeed, out string rimWhere );
		Check( "edge: a ball that clips the rim splats", rimHits == 1 && CrossingsWithSeed( rimSeed ) == 0,
			$"aimed 1.5 cm inside the paint's edge ({reach:0.000} m right of centre): {rimHits} splat: {rimWhere}; went through {CrossingsWithSeed( rimSeed )} times" );
		yield return Until( () => !AnyPaintMoving(), 14f );
		portals.Sync();
		Check( "edge: the rim splat joined the patch and the portal stayed linked", portals.HasPair && Room.Patches.Get( O ).Opening.Revision != revisionBefore,
			$"linked {portals.HasPair}; orange opening revision {revisionBefore} -> {Room.Patches.Get( O ).Opening.Revision}, circle {Room.Patches.Get( O ).Opening.InscribedRadius:0.000} m" );
		Desk.ViewWallPoint( 0, centre.X + 0.2f, centre.Y - 0.2f, 1.8f, 20f, 5f );
		yield return Picture( "edge_3_rim_ball_joined_the_patch" );
		Desk.TourView();

		// (c1) The entry patch is replaced while the ball is still in the air.
		portals.Sync();
		pair = portals.Pair;
		Vec3 target = pair.A.PaintCenter;
		Portaball early = ThrowAt( "edge_replaced_before_arrival", B, target + pair.A.Normal * 3.5f, target, 7f );
		uint earlySeed = early.Seed;
		yield return Wait( 0.25f );
		Say( $"the ball is {pair.A.Wall.SignedDistance( early.IsValid() ? early.PositionMetres : target ):0.00} m from Wall +X: replacing the orange patch with a splat on Wall +Y now." );
		Paint( O, 2, 4.0f, 1.75f );
		portals.Sync();
		bool unlinkedAtOnce = !portals.HasPair;
		yield return Until( () => !early.IsValid() || early.Spent, 3f );
		yield return Wait( 0.1f );
		int earlyHits = HitsWithSeed( earlySeed, out string earlyWhere );
		Check( "edge: replacing a patch unlinks at once", unlinkedAtOnce, $"linked straight after the replacing splat: {!unlinkedAtOnce}" );
		Check( "edge: a ball in the air when its patch is replaced splats on the wall", earlyHits == 1 && CrossingsWithSeed( earlySeed ) == 0,
			$"{earlyHits} splat: {earlyWhere}; went through {CrossingsWithSeed( earlySeed )} times" );

		// That left orange on Wall +Y and (from the ball) blue on Wall +X. Let them link.
		yield return LinkedAndSettled( "edge, after the replacement" );
		if ( !portals.HasPair ) yield break;

		// (c2) The ENTRY patch is replaced while the ball is already inside the opening (between first touch and its centre crossing).
		yield return ReplaceWhileInside( "edge_entry_replaced_while_inside", O, true );
		yield return LinkedAndSettled( "edge, after the second replacement" );
		if ( !portals.HasPair ) yield break;

		// (c3) The EXIT patch is replaced while the ball is inside the opening.
		yield return ReplaceWhileInside( "edge_exit_replaced_while_inside", O, false );
		yield return Until( () => !AnyPaintMoving(), 14f );
	}

	/// <summary>
	/// A slow ball into one end; at the physics step in which it is inside the opening (taken off the
	/// physics engine, centre not yet across), one of the two patches is replaced by a splat elsewhere.
	/// </summary>
	private IEnumerator<object> ReplaceWhileInside( string name, PaintColor into, bool replaceEntry )
	{
		PortalLink portals = Room.Portals;
		portals.Sync();
		PortalEnd entry = portals.EndOf( into );
		PaintWall entryWall = portals.WallOf( into );
		PaintColor victim = replaceEntry ? into : ( into == O ? B : O );
		PaintWall victimWall = portals.WallOf( victim );

		// Put the replacement on a wall that holds neither end.
		int spare = -1;
		for ( int i = 0; i < Room.Walls.Count; i++ )
		{
			if ( Room.Walls[i] != portals.WallOf( O ) && Room.Walls[i] != portals.WallOf( B ) ) { spare = i; break; }
		}
		_replaceColour = victim;
		_replaceWall = spare;
		_replaceUV = new Vec2( Room.WallFrames[spare].Width * 0.5f, 1.75f );
		_replaceDone = false;

		Vec3 target = entry.PaintCenter;
		// Slow (3 m/s into the wall), so it spends several physics steps inside the opening.
		Portaball ball = ThrowAt( name, into == O ? B : O, target + entry.Normal * 1.2f + new Vec3( 0f, 0f, 0.1f ), target, 3.2f );
		uint seed = ball.Seed;
		_replaceCrossingsAtThrow = 0;
		_replaceWhenInside = ball;

		yield return Until( () => !ball.IsValid() || ball.Spent || ball.Crossings > 0, 3f );
		yield return Wait( 0.15f );
		_replaceWhenInside = null;
		int hits = HitsWithSeed( seed, out string where );
		int crossings = CrossingsWithSeed( seed );
		bool burstOnEntryWall = hits == 1 && where.StartsWith( entryWall.WallName ) && crossings == 0;
		// When it is the EXIT that moved, the opening the ball is heading into can be linked again, to the
		// new exit, before the ball reaches the wall. A ball that was not yet touching the wall when the link
		// broke is back in normal flight by then, and rightly goes through: once, out of the new exit.
		bool throughToNewExit = !replaceEntry && hits == 0 && crossings == 1 && ball.IsValid() && !ball.Spent && !ball.InPortal
			&& portals.HasPair && portals.WallOf( victim ) == Room.Walls[spare];
		Check( name + ": the patch was replaced while the ball was inside the opening", _replaceDone, _replaceDone ? $"{victim} (on {victimWall.WallName}) replaced by a splat on {Room.Walls[spare].WallName}" : "the ball was through before the script could act" );
		Check( name + ": the ball is not lost: it bursts on the wall it was entering" + ( replaceEntry ? "" : ", or goes through to the new exit" ), burstOnEntryWall || throughToNewExit,
			$"{hits} splat: {where}; went through {crossings} times" + ( throughToNewExit ? $" (out of the new {victim} patch on {Room.Walls[spare].WallName})" : "" ) );
		int entryIndex = WallIndexOf( entryWall );
		Vec2 at = entryWall.Frame.ToUV( target );
		Desk.ViewWallPoint( entryIndex, at.X, at.Y - 0.2f, 2.0f, 20f, 5f );
		yield return Wait( 0.4f );
		yield return Picture( name + "_burst_on_the_entry_wall" );
		Desk.TourView();
	}

	private IEnumerator<object> VrDryRun()
	{
		yield return ClearRoom( "VR PLAY-SPACE CODE, DRIVEN BY MADE-UP STICKS (no headset)" );
		Paint( O, 0, 3.0f, 1.9f );
		Paint( B, 3, 4.0f, 1.9f );
		yield return LinkedAndSettled( "vr dry run" );
		PortalLink portals = Room.Portals;
		if ( !portals.HasPair ) yield break;
		float floor = portals.FloorHeight;
		PortalPair pair = portals.Pair;

		// The pretend person stands 0.3 m forward and 0.1 m left of the middle of their play space, head 1.6 m up,
		// with a hand held out. The play space starts so that the head is 1.5 m in front of orange's centre.
		Vec3 headLocal = new Vec3( 0.3f, 0.1f, 1.6f );
		Vec3 handLocal = new Vec3( 0.6f, -0.2f, 1.25f );
		PlaySpaceWalker walker = new PlaySpaceWalker();
		Vec2 centre = pair.A.Opening.CenterUV;
		bool openAtHead = PlayerPassage.IsOpenAt( pair, portals.MaskOf( O ), portals.MaskOf( B ), pair.A.Wall, new Vec2( centre.X, 1.6f ), walker.HeadRadius, portals.Settings );
		Vec3 headStart = pair.A.Wall.ToWorld( new Vec2( centre.X, openAtHead ? 1.6f : centre.Y ), 1.5f );
		float yaw = MathF.Atan2( -pair.A.Normal.Y, -pair.A.Normal.X );
		Quat facing = Quat.CreateFromAxisAngle( Units.Up, yaw );
		walker.Anchor = new RigidTransform( facing, new Vec3( headStart.X, headStart.Y, floor ) - Vec3.Transform( new Vec3( headLocal.X, headLocal.Y, 0f ), facing ) );
		if ( !openAtHead ) headLocal.Z = centre.Y - 0f;
		Say( $"vr dry run: play space at {V( walker.Anchor.Translation )} m facing yaw {yaw * 57.29578f:0.0} deg; head {V( walker.Anchor.TransformPoint( headLocal ) )} m; hand {V( walker.Anchor.TransformPoint( handLocal ) )} m." );

		Func<Vec2, float, PlaySpaceFrame> tick = ( stick, turn ) =>
		{
			portals.Sync();
			PlaySpaceFrame f = walker.Update( headLocal, Quat.Identity, stick, turn, Time.Delta, floor, portals.HasPair, portals.Pair,
				portals.MaskOf( O ), portals.MaskOf( B ), Room.WallFrames, Room.WallCount, portals.Settings );
			float headYaw = PlaySpace.YawOf( walker.Anchor.Rotation ) * 57.29578f;
			Desk.SetView( f.Head, new Angles( 0f, headYaw, 0f ) );
			return f;
		};

		tick( Vec2.Zero, 0f );
		yield return null;
		yield return Picture( "vr_1_start" );

		// Left stick fully forward until the head is through.
		RigidTransform anchorBefore = walker.Anchor;
		PlaySpaceFrame crossing = default;
		bool crossed = false;
		float walked = 0f;
		Vec3 headAtStart = walker.Anchor.TransformPoint( headLocal );
		float time = 0f;
		while ( !crossed && time < 2f )
		{
			anchorBefore = walker.Anchor;
			PlaySpaceFrame f = tick( new Vec2( 0f, 1f ), 0f );
			if ( f.Crossed ) { crossing = f; crossed = true; }
			else walked = ( f.Head - headAtStart ).Length();
			time += Time.Delta;
			yield return null;
		}
		Check( "vr dry run: full stick walks at the set speed", MathF.Abs( walked / MathF.Max( time, 0.001f ) - walker.MoveSpeed ) < 0.15f, $"{walked:0.000} m in {time:0.000} s = {walked / MathF.Max( time, 0.001f ):0.00} m/s (set: {walker.MoveSpeed:0.0})" );
		Check( "vr dry run: the head going through carries the play space through", crossed, crossed ? "crossed" : "no crossing in 2 s" );
		if ( !crossed ) yield break;
		yield return Picture( "vr_2_just_through" );

		RigidTransform plain = pair.PlayerCrossing( true );
		Vec3 headPredicted = plain.TransformPoint( crossing.HeadBeforeCrossing );
		Vec3 headGot = crossing.Head - new Vec3( 0f, 0f, crossing.Dropped );
		Vec3 anchorPredicted = plain.TransformPoint( anchorBefore.Translation + crossing.Moved );
		Vec3 anchorGot = walker.Anchor.Translation - new Vec3( 0f, 0f, crossing.Dropped );
		Vec3 handOffsetBefore = Vec3.Transform( handLocal - headLocal, anchorBefore.Rotation );
		Vec3 handOffsetAfter = walker.Anchor.TransformPoint( handLocal ) - walker.Anchor.TransformPoint( headLocal );
		float handError = ( handOffsetAfter - plain.TransformDirection( handOffsetBefore ) ).Length() * 1000f;
		Say( $"vr dry run: BEFORE head {V( crossing.HeadBeforeCrossing )} m, play space {V( anchorBefore.Translation + crossing.Moved )} m. AFTER head {V( headGot )} m, play space {V( anchorGot )} m. CORE head {V( headPredicted )} m, play space {V( anchorPredicted )} m. Exit slid {crossing.Crossing.ExitMovedBy * 1000f:0.0} mm." );
		if ( crossing.Crossing.ExitMovedBy <= 0f )
			Check( "vr dry run: head and play space land where the core's player crossing says", ( headGot - headPredicted ).Length() < 0.0005f && ( anchorGot - anchorPredicted ).Length() < 0.0005f,
				$"head {( headGot - headPredicted ).Length() * 1000f:0.000} mm, play space {( anchorGot - anchorPredicted ).Length() * 1000f:0.000} mm from the prediction" );
		Check( "vr dry run: the hand stays where it was on the player", handError < 0.5f, $"hand-from-head offset differs by {handError:0.000} mm from the offset before, turned by the crossing; its length {handOffsetBefore.Length():0.0000} -> {handOffsetAfter.Length():0.0000} m" );

		// Let it settle on the floor, then walk out and snap-turn.
		float t2 = 0f;
		while ( ( walker.Fall.Falling || walker.Fall.Lifting ) && t2 < 3f ) { tick( Vec2.Zero, 0f ); t2 += Time.Delta; yield return null; }
		Check( "vr dry run: the play space ends on the floor", MathF.Abs( walker.Anchor.Translation.Z - floor ) < 0.001f, $"play-space floor {walker.Anchor.Translation.Z - floor:0.0000} m from the room floor after {t2:0.00} s" );
		for ( float t = 0f; t < 0.5f; t += Time.Delta ) { tick( new Vec2( 0f, 1f ), 0f ); yield return null; }

		// Snap turning is a setting now (smooth is the default): switch it on for this check.
		walker.SmoothTurn = false;
		Vec3 headBeforeTurn = walker.Anchor.TransformPoint( headLocal );
		float yawBeforeTurn = PlaySpace.YawOf( walker.Anchor.Rotation ) * 57.29578f;
		PlaySpaceFrame turnFrame = tick( Vec2.Zero, 1f );
		yield return null;
		PlaySpaceFrame held = tick( Vec2.Zero, 1f );
		yield return null;
		float yawAfterTurn = PlaySpace.YawOf( walker.Anchor.Rotation ) * 57.29578f;
		float turnedBy = MathF.IEEERemainder( yawAfterTurn - yawBeforeTurn, 360f );
		Check( "vr dry run: right stick snap-turns once, on the spot", turnFrame.Turned == 1 && held.Turned == 0 && MathF.Abs( turnedBy + walker.SnapTurnDegrees ) < 0.01f && ( walker.Anchor.TransformPoint( headLocal ) - headBeforeTurn ).Length() < 0.0005f,
			$"turned {turnedBy:0.000} deg (set: {walker.SnapTurnDegrees:0} to the right), head moved {( walker.Anchor.TransformPoint( headLocal ) - headBeforeTurn ).Length() * 1000f:0.000} mm, holding the stick over turned {held.Turned} more times" );
		tick( Vec2.Zero, 0f );
		yield return Picture( "vr_3_walked_out_and_snap_turned" );

		// Smooth turning, the default: the stick fully over turns at the set speed, on the spot, for as long as it is held.
		walker.SmoothTurn = true;
		Vec3 headBeforeSmooth = walker.Anchor.TransformPoint( headLocal );
		float yawBeforeSmooth = PlaySpace.YawOf( walker.Anchor.Rotation ) * 57.29578f;
		float smoothTime = 0f, smoothTurned = 0f, lastYaw = yawBeforeSmooth, headDrift = 0f;
		while ( smoothTime < 0.5f )
		{
			tick( Vec2.Zero, 1f );
			smoothTime += Time.Delta;
			float y = PlaySpace.YawOf( walker.Anchor.Rotation ) * 57.29578f;
			smoothTurned += MathF.IEEERemainder( y - lastYaw, 360f );
			lastYaw = y;
			headDrift = MathF.Max( headDrift, ( walker.Anchor.TransformPoint( headLocal ) - headBeforeSmooth ).Length() );
			yield return null;
		}
		float smoothRate = -smoothTurned / MathF.Max( smoothTime, 0.001f );
		Check( "vr dry run: right stick held over turns smoothly at the set speed, on the spot", MathF.Abs( smoothRate - walker.SmoothTurnDegreesPerSecond ) < 2f && headDrift < 0.0005f,
			$"turned {-smoothTurned:0.00} deg to the right in {smoothTime:0.000} s = {smoothRate:0.0} deg/s (set: {walker.SmoothTurnDegreesPerSecond:0}), head moved at most {headDrift * 1000f:0.000} mm" );
		float halfBefore = PlaySpace.YawOf( walker.Anchor.Rotation ) * 57.29578f;
		float halfTime = 0f, halfTurned = 0f;
		lastYaw = halfBefore;
		float halfStick = -( walker.TurnDeadZone + ( 1f - walker.TurnDeadZone ) * 0.5f );     // half way between the dead zone and full, to the LEFT
		while ( halfTime < 0.5f )
		{
			tick( Vec2.Zero, halfStick );
			halfTime += Time.Delta;
			float y = PlaySpace.YawOf( walker.Anchor.Rotation ) * 57.29578f;
			halfTurned += MathF.IEEERemainder( y - lastYaw, 360f );
			lastYaw = y;
			yield return null;
		}
		PlaySpaceFrame inDeadZone = tick( Vec2.Zero, walker.TurnDeadZone * 0.9f );
		Check( "vr dry run: half the push is half the speed, the other way, and the dead zone does nothing", MathF.Abs( halfTurned / MathF.Max( halfTime, 0.001f ) - walker.SmoothTurnDegreesPerSecond * 0.5f ) < 2f && inDeadZone.Turned == 0,
			$"stick {halfStick:0.00}: {halfTurned / MathF.Max( halfTime, 0.001f ):0.0} deg/s to the left (half of {walker.SmoothTurnDegreesPerSecond:0}); stick {walker.TurnDeadZone * 0.9f:0.00} (inside the dead zone of {walker.TurnDeadZone:0.00}) turned {inDeadZone.TurnedDegrees:0.000} deg" );
		tick( Vec2.Zero, 0f );
		yield return Picture( "vr_3b_smooth_turned" );

		// Stick into a bare wall: held at the clearance.
		Vec3 wallHead = Room.WallFrames[2].ToWorld( new Vec2( 1.5f, 1.6f ), 0.8f );
		Quat faceWall = Quat.CreateFromAxisAngle( Units.Up, MathF.PI / 2f );   // Wall +Y is at +Y
		walker.Anchor = new RigidTransform( faceWall, new Vec3( wallHead.X, wallHead.Y, floor ) - Vec3.Transform( new Vec3( headLocal.X, headLocal.Y, 0f ), faceWall ) );
		walker.ForgetHead();
		PlaySpaceFrame last = default;
		for ( float t = 0f; t < 1.0f; t += Time.Delta ) { last = tick( new Vec2( 0f, 1f ), 0f ); yield return null; }
		float wallGap = Room.WallFrames[2].SignedDistance( last.Head );
		Check( "vr dry run: stick walking is stopped by bare wall", MathF.Abs( wallGap - walker.WallClearance ) < 0.002f && last.Blocked && !last.Crossed, $"one second of full stick from 0.8 m away: head held {wallGap:0.000} m from Wall +Y" );
		yield return Picture( "vr_4_stick_into_bare_wall" );
	}

	// ------------------------------------------------------------------ the floor and the ceiling

	private const int FloorWall = TestRoom.FloorIndex;
	private const int CeilingWall = TestRoom.CeilingIndex;

	/// <summary>World point on the floor (or ceiling) to that surface's own coordinates.</summary>
	private Vec2 FlatUV( int wall, float x, float y )
	{
		Vec3 floor = SboxConvert.ToMetres( Room.WorldPosition );
		return Room.WallFrames[wall].ToUV( new Vec3( floor.X + x, floor.Y + y, floor.Z ) );
	}

	private void PaintAt( PaintColor colour, int wall, Vec2 uv )
	{
		Room.Walls[wall].Splat( colour, uv, Splat, Vec2.Zero, _splatSeed++, 8f );
	}

	/// <summary>Stand a little way off and look down at a point on the floor (or up at one on the ceiling).</summary>
	private void LookAtFlat( Vec3 point, float back, float side, float eyeHeight )
	{
		Vec3 floor = SboxConvert.ToMetres( Room.WorldPosition );
		Desk.LookAt( new Vec3( point.X - back, point.Y - side, floor.Z + eyeHeight ), point );
	}

	private IEnumerator<object> FloorPaint()
	{
		yield return ClearRoom( "THE FLOOR AND THE CEILING TAKE PAINT" );
		Vec3 floor = SboxConvert.ToMetres( Room.WorldPosition );
		PaintWall floorWall = Room.Floor, ceilingWall = Room.Ceiling;
		Check( "floor paint: the room has the floor and the ceiling as paint surfaces", Room.WallCount == 6 && floorWall.IsValid() && ceilingWall.IsValid() && floorWall.Frame.IsFloor && ceilingWall.Frame.IsCeiling,
			$"{Room.WallCount} paint surfaces; floor normal {V( floorWall.Frame.Normal )}, ceiling normal {V( ceilingWall.Frame.Normal )}" );

		// 1. A real ball thrown at the floor.
		Vec3 floorTarget = floor + new Vec3( 1.5f, 0f, 0f );
		LookAtFlat( floorTarget, 2.2f, 1.2f, 1.7f );
		yield return Wait( 0.2f );
		Portaball floorBall = ThrowAt( "floor_paint_ball_at_the_floor", O, floor + new Vec3( -1.2f, 0.3f, 1.5f ), floorTarget, 7f );
		uint floorSeed = floorBall.Seed;
		yield return Until( () => !floorBall.IsValid() || floorBall.Spent, 3f );
		yield return Wait( 0.15f );
		int floorHits = HitsWithSeed( floorSeed, out string floorWhere );
		Check( "floor paint: a ball thrown at the floor bursts there and leaves paint", floorHits == 1 && floorWhere.StartsWith( "Floor" ), $"{floorHits} splat: {floorWhere}" );
		yield return Until( () => Room.Patches.TryGetOpening( O, out _, out _ ), 5f );
		bool floorOpen = Room.Patches.TryGetOpening( O, out PaintWall orangeOn, out PortalOpening orangeOpening );
		Check( "floor paint: the paint on the floor is a patch with an opening", floorOpen && orangeOn == floorWall,
			floorOpen ? $"orange opening on {orangeOn.WallName} at u={orangeOpening.CenterUV.X:0.00} v={orangeOpening.CenterUV.Y:0.00}, fits a circle of radius {orangeOpening.InscribedRadius:0.00} m" : "no orange opening after 5 s" );
		yield return Wait( 0.6f );
		yield return Picture( "floorpaint_1_ball_thrown_at_the_floor_left_paint" );

		// 2. And one at the ceiling.
		Vec3 ceilingTarget = floor + new Vec3( -1.0f, 0.5f, Room.Height );
		Desk.LookAt( floor + new Vec3( 1.6f, -1.4f, 1.3f ), ceilingTarget );
		yield return Wait( 0.2f );
		Portaball ceilingBall = ThrowAt( "floor_paint_ball_at_the_ceiling", B, floor + new Vec3( 0.2f, 0.2f, 1.2f ), ceilingTarget, 10f );
		uint ceilingSeed = ceilingBall.Seed;
		yield return Until( () => !ceilingBall.IsValid() || ceilingBall.Spent, 3f );
		yield return Wait( 0.15f );
		int ceilingHits = HitsWithSeed( ceilingSeed, out string ceilingWhere );
		Check( "floor paint: a ball thrown at the ceiling bursts there and leaves paint", ceilingHits == 1 && ceilingWhere.StartsWith( "Ceiling" ), $"{ceilingHits} splat: {ceilingWhere}" );
		yield return Wait( 0.6f );
		yield return Picture( "floorpaint_2_ball_thrown_at_the_ceiling_left_paint" );

		// 3. Orange on the floor and blue on the ceiling link like any two patches. Look down into the floor one.
		yield return LinkedAndSettled( "floor and ceiling" );
		PortalLink portals = Room.Portals;
		if ( !portals.HasPair ) yield break;
		PortalPair pair = portals.Pair;
		Check( "floor paint: a floor patch and a ceiling patch link as a portal", portals.WallOf( O ) == floorWall && portals.WallOf( B ) == ceilingWall && pair.PlayerKeepsUpright,
			$"orange on {portals.WallOf( O ).WallName}, blue on {portals.WallOf( B ).WallName}; the crossing turns nothing (a plain move of {V( pair.PlayerAToB.Translation )} m)" );
		LookAtFlat( pair.A.PaintCenter, 0.9f, 0.5f, 1.6f );
		yield return Wait( 0.3f );
		yield return Picture( "floorpaint_3_looking_down_into_the_floor_portal_ceiling_layout" );
		Desk.LookAt( floor + new Vec3( pair.B.PaintCenter.X - floor.X + 1.0f, pair.B.PaintCenter.Y - floor.Y - 0.6f, 1.5f ), pair.B.PaintCenter );
		yield return Wait( 0.3f );
		yield return Picture( "floorpaint_4_looking_up_into_the_ceiling_portal" );

		// 4. Second layout: the blue end moves to a wall. Looking down into the floor now looks out of that wall.
		Paint( B, 0, 3.0f, 1.9f );
		yield return Wait( 0.5f );
		yield return LinkedAndSettled( "floor and wall" );
		if ( !portals.HasPair ) yield break;
		pair = portals.Pair;
		Check( "floor paint: the floor patch links to a wall patch too", portals.WallOf( O ) == floorWall && portals.WallOf( B ) == Room.Walls[0] && !pair.PlayerKeepsUpright,
			$"orange on {portals.WallOf( O ).WallName}, blue on {portals.WallOf( B ).WallName}; this crossing tips the player {PlaySpace.TiltOf( pair.PlayerAToB.Rotation ) * 57.29578f:0} deg" );
		LookAtFlat( pair.A.PaintCenter, 0.9f, 0.5f, 1.6f );
		yield return Wait( 0.3f );
		yield return Picture( "floorpaint_5_looking_down_into_the_floor_portal_wall_layout" );
		Desk.TourView();
		yield return Wait( 0.2f );
		yield return Picture( "floorpaint_6_floor_and_wall_linked_room" );
	}

	private IEnumerator<object> FloorBalls()
	{
		PortalLink portals = Room.Portals;
		Vec3 floor = SboxConvert.ToMetres( Room.WorldPosition );

		// ---- floor to wall, and back ----
		yield return ClearRoom( "BALLS: A FLOOR PATCH AND A WALL PATCH" );
		PaintAt( O, FloorWall, FlatUV( FloorWall, 1.5f, 0f ) );
		Paint( B, 0, 3.0f, 1.75f );
		yield return LinkedAndSettled( "ball floor to wall" );
		if ( !portals.HasPair ) yield break;
		Action roomView = () => Desk.LookAt( floor + new Vec3( -1.6f, -2.2f, 2.3f ), floor + new Vec3( 2.6f, 0f, 0.9f ) );
		roomView();
		yield return Picture( "floorballs_0_floor_and_wall_linked" );
		yield return BallThrough( "floorballs_floor_to_wall", O, 8f, roomView, roomView, true );
		yield return BallThrough( "floorballs_wall_to_floor", B, 8f, roomView, roomView, true );

		// A ball let go above the floor patch: it falls in and flies out of the wall sideways.
		portals.Sync();
		if ( portals.HasPair )
		{
			PortalPair pair = portals.Pair;
			Vec3 above = pair.A.PaintCenter + new Vec3( 0f, 0f, 2.0f );
			Portaball dropped = Portaball.Spawn( Scene, B, above, Vec3.Zero, Vec3.Zero, Splat );
			_tracked.Add( new Tracked { Ball = dropped, Seed = dropped.Seed, Name = "floorballs_dropped_into_the_floor" } );
			Tracked droppedTracked = _tracked[_tracked.Count - 1];
			uint droppedSeed = dropped.Seed;
			Say( $"dropped #{droppedSeed} from {V( above )} m, 2 m above the middle of the floor patch, with no speed at all" );
			yield return Until( () => !dropped.IsValid() || dropped.Spent || ( dropped.Crossings > 0 && !dropped.InPortal ), 3f );
			bool through = dropped.IsValid() && !dropped.Spent && dropped.Crossings == 1 && FindCrossing( droppedSeed, out _ );
			Check( "floorballs_dropped: a ball dropped into the floor patch comes out of the wall patch", through, through ? "out of blue on Wall +X" : "it did not go through" );
			if ( through )
			{
				FindCrossing( droppedSeed, out PortalLink.CrossingRecord r );
				yield return Picture( "floorballs_dropped_flying_out_of_the_wall_sideways" );
				float speedIn = r.VelocityBefore.Length(), speedOut = r.VelocityAfter.Length();
				float outward = Vec3.Dot( r.VelocityAfter, pair.B.Normal ) / MathF.Max( speedOut, 0.001f );
				float expected = MathF.Sqrt( 2f * SboxConvert.ToMetres( Scene.PhysicsWorld.Gravity ).Length() * 2.0f );
				Check( "floorballs_dropped: it flies out sideways at the speed it fell in with", MathF.Abs( speedOut - speedIn ) < 0.0005f && outward > 0.999f && MathF.Abs( r.VelocityAfter.Z ) < 0.05f && MathF.Abs( speedIn - expected ) < 0.25f,
					$"fell in at {speedIn:0.0000} m/s straight down (a 2 m drop gives {expected:0.00}); came out at {speedOut:0.0000} m/s, {outward:0.0000} of it straight out of the wall, {r.VelocityAfter.Z:0.000} m/s up" );
				yield return Wait( 0.1f );
				if ( dropped.IsValid() && !dropped.Spent ) { droppedTracked.RemovedByScript = true; dropped.GameObject.Destroy(); }
			}
			yield return Wait( 0.2f );
		}

		// ---- floor to floor ----
		yield return ClearRoom( "BALLS: TWO PATCHES ON THE FLOOR" );
		PaintAt( O, FloorWall, FlatUV( FloorWall, 1.5f, 0f ) );
		PaintAt( B, FloorWall, FlatUV( FloorWall, -1.5f, 0f ) );
		yield return LinkedAndSettled( "ball floor to floor" );
		if ( !portals.HasPair ) yield break;
		Action floorView = () => Desk.LookAt( floor + new Vec3( 0f, -2.6f, 2.4f ), floor + new Vec3( 0f, 0f, 0.4f ) );
		floorView();
		yield return Picture( "floorballs_1_two_floor_patches_linked" );
		yield return BallThrough( "floorballs_floor_to_floor", O, 8f, floorView, floorView, true );

		// ---- ceiling to floor, and back ----
		yield return ClearRoom( "BALLS: A CEILING PATCH AND A FLOOR PATCH" );
		PaintAt( O, CeilingWall, FlatUV( CeilingWall, -1.5f, 0.5f ) );
		PaintAt( B, FloorWall, FlatUV( FloorWall, 1.5f, 0f ) );
		yield return LinkedAndSettled( "ball ceiling to floor" );
		if ( !portals.HasPair ) yield break;
		Action tallView = () => Desk.LookAt( floor + new Vec3( 0.4f, -2.7f, 1.2f ), floor + new Vec3( 0f, 0f, 1.9f ) );
		tallView();
		yield return Picture( "floorballs_2_ceiling_and_floor_linked" );
		yield return BallThrough( "floorballs_ceiling_to_floor", O, 10f, tallView, tallView, true );
		yield return BallThrough( "floorballs_floor_to_ceiling", B, 8f, tallView, tallView, false );
	}

	/// <summary>What the desk camera did between two moments, for the floor walks.</summary>
	private struct FlightNotes
	{
		public float Seconds;
		public float RightedAfter;        // seconds from the start until the view was level again (-1: it never was tipped)
		public float TopFeet;             // highest the feet got above the floor, metres
		public float LowestEye;           // lowest the eye got above the floor, metres
		public float FastestSpeed;        // m/s
		public float WorstTilt;           // degrees
		public float HeadJumpWhileRighting;   // the most the eye moved in a frame beyond what its velocity explains, metres
		public int Crossings;
		public bool EverOverHoleWithFeetOut;  // was over a hole while the feet were above the floor
		public string Trace;
	}

	/// <summary>Let the desk camera carry on until it has settled (or the time is up, or the condition says stop), writing down what it does.</summary>
	private IEnumerator<object> Fly( float maxSeconds, Func<bool> stop, Action<FlightNotes> done )
	{
		float floor = Room.Portals.FloorHeight;
		FlightNotes n = new FlightNotes { RightedAfter = -1f, LowestEye = float.MaxValue, Trace = "" };
		int crossingsAtStart = Desk.PortalCrossings;
		bool tipped = Desk.TiltDegrees > 0f;
		Vec3 last = Desk.EyeMetres;
		float lastSpeed = Desk.VelocityMetres.Length();
		int frames = 0;
		while ( n.Seconds < maxSeconds && ( stop == null ? Desk.IsSettling || Desk.IsWalking : !stop() ) )
		{
			if ( frames % 9 == 0 && n.Trace.Length < 600 ) n.Trace += $" t={n.Seconds:0.00}s eye z {Desk.EyeMetres.Z - floor:0.00} tilt {Desk.TiltDegrees:0}";
			yield return null;
			float dt = Time.Delta;
			if ( dt <= 0f ) continue;
			n.Seconds += dt;
			frames++;
			Vec3 eye = Desk.EyeMetres;
			float feet = eye.Z - floor - Desk.EyeHeight;
			n.TopFeet = MathF.Max( n.TopFeet, feet );
			n.LowestEye = MathF.Min( n.LowestEye, eye.Z - floor );
			n.FastestSpeed = MathF.Max( n.FastestSpeed, Desk.VelocityMetres.Length() );
			n.WorstTilt = MathF.Max( n.WorstTilt, Desk.TiltDegrees );
			if ( Desk.OverHole && feet > 0.01f ) n.EverOverHoleWithFeetOut = true;
			bool crossedNow = Desk.PortalCrossings != crossingsAtStart + n.Crossings;
			if ( crossedNow ) n.Crossings = Desk.PortalCrossings - crossingsAtStart;
			if ( tipped && !crossedNow )
			{
				// While the view swings upright the eye must only move as its own velocity (and the walk) say. A swing
				// about anything but the eye would throw it several centimetres a frame.
				float moved = ( eye - last ).Length();
				float speed = MathF.Max( lastSpeed, Desk.VelocityMetres.Length() );
				float allowed = ( speed + ( Desk.IsWalking ? 2.05f : 0f ) ) * dt + Desk.Walker.Fall.Gravity * dt * dt + 0.002f;
				n.HeadJumpWhileRighting = MathF.Max( n.HeadJumpWhileRighting, moved - allowed );
			}
			lastSpeed = Desk.VelocityMetres.Length();
			if ( Desk.TiltDegrees > 0f ) tipped = true;
			else if ( tipped ) { tipped = false; if ( n.RightedAfter < 0f ) n.RightedAfter = n.Seconds; }
			last = eye;
		}
		done( n );
	}

	/// <summary>The checks every player crossing gets: where the eye went, which way it faces, and what became of the velocity.</summary>
	private void CheckPlayerCrossing( string name, bool flying, PortalEnd entry, PortalEnd exit, PortalPair pair )
	{
		PortalLink.CrossingRecord r = Desk.LastCrossing;
		PlaySpaceFrame f = Desk.LastCrossingFrame;
		RigidTransform plain = pair.PlayerCrossing( r.FromOrange );
		float positionError = ( r.PositionAfter - r.PositionPredicted ).Length() * 1000f;
		float facingError = MathUtil.AngleBetween( r.VelocityAfter, r.VelocityPredicted ) * 57.29578f;
		Check( name + ": it went through " + ( flying ? "by falling" : "by walking" ), f.Crossed && f.CrossedFlying == flying, $"crossed {f.Crossed}, by falling or flying {f.CrossedFlying}; into {( r.FromOrange ? "orange" : "blue" )}" );
		if ( r.ExitMovedBy <= 0f )
			Check( name + ": the eye is carried rigidly, as the core says", positionError < 0.5f, $"{positionError:0.000} mm from PortalPair's player crossing of the eye point {V( r.PositionBefore )} -> {V( r.PositionAfter )} m" );
		else
			Say( $"{name}: the exit was slid {r.ExitMovedBy * 1000f:0.0} mm to stay on the exit patch." );
		Check( name + ": the view is carried rigidly too", facingError < 0.01f, $"{facingError:0.0000} deg from the core's crossing of the view direction" );

		Vec3 vIn = f.VelocityBeforeCrossing, vOut = f.VelocityAfterCrossing;
		Vec3 mapped = plain.TransformDirection( vIn );
		if ( exit.Wall.IsFloor )
		{
			float pop = Desk.Walker.FloorExitSpeed( Desk.EyeHeight );
			float up = Vec3.Dot( vOut, exit.Normal );
			float wantUp = MathF.Max( pop, Vec3.Dot( mapped, exit.Normal ) );
			Check( name + ": coming up out of the floor it is thrown at least at the pop-out speed", MathF.Abs( up - wantUp ) < 0.01f && up >= pop - 0.001f,
				$"velocity {V( vIn )} -> {V( vOut )} m/s; upward {up:0.000} m/s, the pop-out speed for a {Desk.EyeHeight:0.00} m body and a {Desk.FloorExitRise:0.00} m rise is {pop:0.000} m/s (the mapped speed was {Vec3.Dot( mapped, exit.Normal ):0.000})" );
		}
		else
		{
			float speedIn = vIn.Length(), speedOut = vOut.Length();
			bool along = speedIn < 0.001f || Vec3.Dot( vOut, exit.Normal ) / MathF.Max( speedOut, 0.001f ) > 0.999f;
			Check( name + ": the speed it went in with is the speed it comes out with, in the mapped direction", ( vOut - mapped ).Length() < 0.001f && MathF.Abs( speedOut - speedIn ) < 0.001f && along && speedOut <= Desk.MaxFallSpeed + 0.001f,
				$"velocity {V( vIn )} ({speedIn:0.000} m/s) -> {V( vOut )} ({speedOut:0.000} m/s); the core's mapping gives {V( mapped )}; top speed {Desk.MaxFallSpeed:0.0}" );
		}

		float tilt = PlaySpace.TiltOf( plain.Rotation ) * 57.29578f;
		float tiltNow = f.Tilt * 57.29578f;
		if ( pair.PlayerKeepsUpright )
			Check( name + ": this crossing tips nothing", tiltNow == 0f && f.Righted == 0f, $"tilt after the crossing {tiltNow:0.000} deg" );
		else if ( Desk.RightingSeconds > 0f )
			Check( name + ": the crossing frame shows the rigid mapping, not yet righted", MathF.Abs( tiltNow - tilt ) < 0.05f && f.Righted == 0f, $"the crossing tips the view {tilt:0.0} deg; in the crossing frame it is tipped {tiltNow:0.00} deg and nothing has been turned back yet" );
		else
			Check( name + ": righting at once levels the view in the crossing frame", tiltNow == 0f && MathF.Abs( f.Righted * 57.29578f - tilt ) < 0.05f, $"the crossing tips the view {tilt:0.0} deg; turned back {f.Righted * 57.29578f:0.00} deg in the same frame" );
	}

	private void CheckStanding( string name, PortalPair pair, int crossingsWanted, int crossingsBefore )
	{
		PortalLink portals = Room.Portals;
		float floor = portals.FloorHeight;
		Vec3 eye = Desk.EyeMetres;
		bool overHole = PlayerPassage.FloorIsOpenUnder( portals.HasPair, pair, portals.MaskOf( O ), portals.MaskOf( B ), eye, floor, Desk.BodyRadius, portals.Settings );
		Quat q = Desk.Walker.Anchor.Rotation;
		Check( name + ": ends standing on solid floor, level and still", MathF.Abs( eye.Z - floor - Desk.EyeHeight ) < 0.002f && !Desk.IsFalling && !Desk.IsSettling && !overHole && q.X == 0f && q.Y == 0f && Desk.VelocityMetres == Vec3.Zero && Desk.PortalCrossings == crossingsBefore + crossingsWanted,
			$"eye {V( eye )} m ({eye.Z - floor:0.000} m above the floor, standing height {Desk.EyeHeight:0.000}); over a hole {overHole}; play-space quaternion x {q.X:0.000000} y {q.Y:0.000000} (exactly level = 0); velocity {V( Desk.VelocityMetres )}; crossings {Desk.PortalCrossings - crossingsBefore} (wanted {crossingsWanted})" );
	}

	private IEnumerator<object> FloorWalk()
	{
		PortalLink portals = Room.Portals;
		Vec3 floorCentre = SboxConvert.ToMetres( Room.WorldPosition );
		float floor = floorCentre.Z;
		const float eyeHeight = 1.6f;
		FlightNotes notes = default;

		// ================= (a) onto a floor portal, out of a wall =================
		yield return ClearRoom( "THE PLAYER: WALK ONTO A FLOOR PORTAL, COME OUT OF A WALL" );
		PaintAt( O, FloorWall, FlatUV( FloorWall, 1.5f, 0f ) );
		Paint( B, 0, 1.6f, 2.05f );      // on Wall +X at y = +1.4, so the way out of it does not run over the floor patch
		yield return LinkedAndSettled( "floor walk a" );
		if ( !portals.HasPair ) yield break;
		PortalPair pair = portals.Pair;
		Vec3 hole = pair.A.PaintCenter;
		bool bodyFits = PlayerPassage.FloorIsOpenUnder( true, pair, portals.MaskOf( O ), portals.MaskOf( B ), hole, floor, Desk.BodyRadius, portals.Settings );
		Check( "floor walk a: the floor patch is big enough for the body", bodyFits, $"a body of radius {Desk.BodyRadius:0.00} m at the patch's centre {V( hole )}; the patch fits a circle of {pair.A.Opening.InscribedRadius:0.000} m" );

		// Stand 1.5 m short of the hole, facing +Y (the floor's own "up"), looking a little down. Walk to its middle.
		Desk.SetView( new Vec3( hole.X, hole.Y - 1.5f, floor + eyeHeight ), new Angles( 20f, 90f, 0f ) );
		Say( $"floor walk a: camera at {V( Desk.EyeMetres )} m, 1.5 m short of the floor patch (centre {V( hole )}), facing +Y; the blue patch is on Wall +X, centre {V( pair.B.PaintCenter )} m." );
		yield return Wait( 0.2f );
		yield return Picture( "floorwalk_a_1_start" );
		int before = Desk.PortalCrossings;
		Desk.Walk( 1.5f, 2f );
		yield return Until( () => Desk.OverHole || !Desk.IsWalking, 3f );
		float sinkStartedAt = new Vec2( Desk.EyeMetres.X - hole.X, Desk.EyeMetres.Y - hole.Y ).Length();
		Check( "floor walk a: solid floor up to the hole, then nothing under the feet", Desk.OverHole && MathF.Abs( Desk.EyeMetres.Z - floor - eyeHeight ) < 0.03f,
			$"walked on the floor until {sinkStartedAt:0.000} m from the patch's centre, where the body ({Desk.BodyRadius:0.00} m) is all over paint; eye still {Desk.EyeMetres.Z - floor:0.000} m up" );
		yield return Until( () => Desk.EyeMetres.Z - floor < 0.85f || Desk.PortalCrossings > before, 2f );
		yield return Picture( "floorwalk_a_2_sinking_into_the_floor" );
		float sunkEye = Desk.EyeMetres.Z - floor;
		yield return Until( () => Desk.PortalCrossings > before, 2f );
		bool crossedA = Desk.PortalCrossings == before + 1;
		Check( "floor walk a: the body sinks into the hole and the head passing the floor carries it across", crossedA && sunkEye < 0.9f,
			crossedA ? $"eye was {sunkEye:0.00} m above the floor on the way down; one crossing" : $"{Desk.PortalCrossings - before} crossings" );
		if ( !crossedA ) yield break;
		Desk.StopWalk();
		yield return Picture( "floorwalk_a_3_just_out_of_the_wall_lying_on_its_side" );
		CheckPlayerCrossing( "floor walk a", true, pair.A, pair.B, pair );
		float speedInA = Desk.LastCrossingFrame.VelocityBeforeCrossing.Length();
		float fallSpeed = MathF.Sqrt( 2f * Desk.Walker.Fall.Gravity * eyeHeight );
		Check( "floor walk a: it fell in as fast as a 1.6 m drop makes it", MathF.Abs( speedInA - MathF.Min( fallSpeed, Desk.MaxFallSpeed ) ) < 0.35f, $"{speedInA:0.000} m/s at the floor plane; sqrt(2 g 1.6) = {fallSpeed:0.000}" );

		yield return Fly( 0.12f, () => false, n => notes = n );
		float firstPart = notes.Seconds;
		float tiltMidWay = Desk.TiltDegrees;
		yield return Picture( "floorwalk_a_4_flying_out_being_turned_upright" );
		yield return Fly( 4f, null, n => notes = n );
		float rightedIn = notes.RightedAfter + firstPart;
		Check( "floor walk a: the view is swung upright about the eye in the set time", notes.HeadJumpWhileRighting <= 0f && Desk.TiltDegrees == 0f && tiltMidWay > 5f && tiltMidWay < 85f && MathF.Abs( rightedIn - Desk.RightingSeconds ) < 0.06f,
			$"tipped 90 deg at the crossing, {tiltMidWay:0.0} deg {firstPart:0.000} s later, level {rightedIn:0.000} s after the crossing (set: {Desk.RightingSeconds:0.00} s); the eye never moved more than its velocity explains (worst excess {MathF.Max( 0f, notes.HeadJumpWhileRighting ) * 1000f:0.0} mm)" );
		CheckStanding( "floor walk a", pair, 1, before );
		float outFromWall = pair.B.Wall.SignedDistance( Desk.EyeMetres );
		float facingOut = Vec3.Dot( Desk.Walker.Anchor.TransformDirection( new Vec3( 1f, 0f, 0f ) ), pair.B.Normal );
		Check( "floor walk a: it flew out of the wall into the room and faces out of it", outFromWall > 0.6f && facingOut > 0.99f,
			$"came to rest {outFromWall:0.000} m out from Wall +X, facing {facingOut:0.000} along its outward normal (it went in facing +Y, the floor frame's V){notes.Trace}" );
		yield return Picture( "floorwalk_a_5_landed_in_the_room" );

		// The same again with righting at once (the other setting of the dial).
		Desk.RightingSeconds = 0f;
		Desk.SetView( new Vec3( hole.X, hole.Y - 1.5f, floor + eyeHeight ), new Angles( 20f, 90f, 0f ) );
		yield return Wait( 0.1f );
		before = Desk.PortalCrossings;
		Desk.Walk( 1.5f, 2f );
		yield return Until( () => Desk.PortalCrossings > before, 4f );
		Desk.StopWalk();
		if ( Desk.PortalCrossings == before + 1 )
		{
			yield return Picture( "floorwalk_a_6_righting_at_once_already_level_in_the_crossing_frame" );
			CheckPlayerCrossing( "floor walk a, righting at once", true, pair.A, pair.B, pair );
			yield return Fly( 4f, null, n => notes = n );
			CheckStanding( "floor walk a, righting at once", pair, 1, before );
		}
		else Check( "floor walk a, righting at once: went through", false, "no crossing" );
		Desk.RightingSeconds = 0.25f;

		// ================= (b) into the wall portal, up out of the floor =================
		Say( "======== THE PLAYER: WALK INTO THE WALL PORTAL, COME UP OUT OF THE FLOOR ========" );
		portals.Sync();
		pair = portals.Pair;
		Vec2 wallCentre = pair.B.Opening.CenterUV;
		bool openAtEye = PlayerPassage.IsOpenAt( pair, portals.MaskOf( O ), portals.MaskOf( B ), pair.B.Wall, new Vec2( wallCentre.X, eyeHeight ), Desk.HeadRadius, portals.Settings );
		float eyeV = openAtEye ? eyeHeight : wallCentre.Y;
		float offCentre = eyeV - wallCentre.Y;
		Vec3 startB = pair.B.Wall.ToWorld( new Vec2( wallCentre.X, eyeV ), 1.5f );
		Desk.SetView( startB, new Angles( 0f, MathF.Atan2( -pair.B.Normal.Y, -pair.B.Normal.X ) * 57.29578f, 0f ) );
		Say( $"floor walk b: camera at {V( startB )} m, 1.5 m in front of the blue patch on Wall +X, eye {eyeV:0.00} m up{( openAtEye ? "" : " (the patch's centre height: it is not open at 1.6 m)" )}." );
		yield return Wait( 0.2f );
		yield return Picture( "floorwalk_b_1_start_facing_the_wall_portal" );
		before = Desk.PortalCrossings;
		Desk.Walk( 4.0f, 2f );      // into the wall, and on: the walk steers in the air afterwards
		yield return Until( () => Desk.PortalCrossings > before || !Desk.IsWalking, 3f );
		bool crossedB = Desk.PortalCrossings == before + 1;
		Check( "floor walk b: walking into the wall patch carries the camera to the floor patch", crossedB, crossedB ? "one crossing" : "no crossing" );
		if ( !crossedB ) yield break;
		yield return Picture( "floorwalk_b_2_just_up_out_of_the_floor" );
		CheckPlayerCrossing( "floor walk b", false, pair.B, pair.A, pair );
		float eyeOut = Desk.LastCrossing.PositionAfter.Z - floor;
		Check( "floor walk b: the eye comes out at the floor, the body still down the hole, where the hole is wide enough for it", eyeOut > -0.001f && eyeOut < 0.15f && Desk.OverHole,
			$"eye {eyeOut:0.000} m above the floor straight after the crossing; over the hole {Desk.OverHole}; it went in {offCentre * 100f:0} cm off the wall patch's centre and the exit was slid {Desk.LastCrossing.ExitMovedBy * 1000f:0} mm to give the body ({Desk.BodyRadius:0.00} m) room" );
		yield return Until( () => Desk.VelocityMetres.Z <= 0f || !Desk.IsFalling, 2f );
		yield return Picture( "floorwalk_b_3_top_of_the_pop" );
		yield return Fly( 4f, null, n => notes = n );
		Desk.StopWalk();
		float topFeetB = MathF.Max( notes.TopFeet, Desk.EyeMetres.Z - floor - Desk.EyeHeight );
		yield return Fly( 1f, null, n => notes = n );
		Check( "floor walk b: the pop throws the feet about 0.4 m clear of the floor", topFeetB > Desk.FloorExitRise - 0.15f && topFeetB < Desk.FloorExitRise + 0.03f, $"feet reached {topFeetB:0.000} m above the floor (set: {Desk.FloorExitRise:0.00} m)" );
		CheckStanding( "floor walk b", pair, 1, before );
		float besideHole = new Vec2( Desk.EyeMetres.X - hole.X, Desk.EyeMetres.Y - hole.Y ).Length();
		Check( "floor walk b: steering in the air lands it beside the hole", besideHole > pair.A.Opening.InscribedRadius && besideHole < 2.5f, $"standing {besideHole:0.000} m from the middle of the floor patch (its circle is {pair.A.Opening.InscribedRadius:0.000} m)" );
		yield return Picture( "floorwalk_b_4_landed_beside_the_hole" );

		// ================= (c) floor to floor =================
		yield return ClearRoom( "THE PLAYER: FLOOR TO FLOOR" );
		PaintAt( O, FloorWall, FlatUV( FloorWall, 1.5f, -0.6f ) );
		PaintAt( B, FloorWall, FlatUV( FloorWall, -1.5f, -0.6f ) );
		yield return LinkedAndSettled( "floor walk c" );
		if ( !portals.HasPair ) yield break;
		pair = portals.Pair;
		hole = pair.A.PaintCenter;
		Vec3 otherHole = pair.B.PaintCenter;
		Desk.SetView( new Vec3( hole.X, hole.Y - 1.5f, floor + eyeHeight ), new Angles( 20f, 90f, 0f ) );
		yield return Wait( 0.2f );
		yield return Picture( "floorwalk_c_1_start" );
		before = Desk.PortalCrossings;
		float yawIn = Desk.YawDegrees;
		Desk.Walk( 4.5f, 2f );      // to the middle of the hole, and on in the air after coming out of the other
		yield return Until( () => Desk.OverHole || !Desk.IsWalking, 3f );
		// Only as far as the middle of the hole for now.
		float toMiddle = new Vec2( Desk.EyeMetres.X - hole.X, Desk.EyeMetres.Y - hole.Y ).Length();
		Desk.Walk( toMiddle, 2f );
		yield return Until( () => Desk.PortalCrossings > before, 3f );
		bool crossedC = Desk.PortalCrossings == before + 1;
		Check( "floor walk c: falls into one floor patch and comes up out of the other", crossedC, crossedC ? $"eye {V( Desk.LastCrossing.PositionBefore )} -> {V( Desk.LastCrossing.PositionAfter )} m" : "no crossing" );
		if ( !crossedC ) yield break;
		yield return Picture( "floorwalk_c_2_just_out_upside_down" );
		CheckPlayerCrossing( "floor walk c", true, pair.A, pair.B, pair );
		Desk.Walk( 2.5f, 2f );      // stick forward again: steers once the feet are out
		yield return Fly( 5f, null, n => notes = n );
		Desk.StopWalk();
		yield return Fly( 1f, null, n => notes = n );
		float yawOut = Desk.YawDegrees;
		Check( "floor walk c: righted, it faces the way it faced going in", MathF.Abs( MathF.IEEERemainder( yawOut - yawIn, 360f ) ) < 0.5f && Desk.TiltDegrees == 0f, $"facing yaw {yawIn:0.00} deg going in, {yawOut:0.00} deg after coming out and being turned the right way up" );
		CheckStanding( "floor walk c", pair, 1, before );
		float besideOther = new Vec2( Desk.EyeMetres.X - otherHole.X, Desk.EyeMetres.Y - otherHole.Y ).Length();
		Check( "floor walk c: lands beside the patch it came out of", besideOther > pair.B.Opening.InscribedRadius && besideOther < 2.5f, $"standing {besideOther:0.000} m from the middle of the blue floor patch" );
		yield return Picture( "floorwalk_c_3_landed_beside_the_other_hole" );

		// ================= (d) the ceiling over the floor: falling for ever =================
		Say( "======== THE PLAYER: A CEILING PATCH STRAIGHT ABOVE A FLOOR PATCH ========" );
		// The two patches' centres must be one above the other (the crossing carries centre to centre). Paint
		// runs a little after it lands, so: paint, see where the centres ended up, and paint again moved by the difference.
		Vec2 floorUV = FlatUV( FloorWall, 1.0f, 0.5f );
		Vec2 ceilingUV = FlatUV( CeilingWall, 1.0f, 0.5f );
		Vec2 apart = Vec2.Zero;
		for ( int attempt = 0; attempt < 3; attempt++ )
		{
			foreach ( Portaball ball in Scene.GetAllComponents<Portaball>() ) ball.GameObject.Destroy();
			Room.ClearAllPaint();
			yield return Wait( 0.3f );
			Room.Walls[FloorWall].Splat( B, floorUV, Splat, Vec2.Zero, 9701, 8f );
			Room.Walls[CeilingWall].Splat( O, ceilingUV, Splat, Vec2.Zero, 9702, 8f );
			yield return LinkedAndSettled( $"floor walk d (try {attempt + 1})" );
			if ( !portals.HasPair ) yield break;
			pair = portals.Pair;
			apart = new Vec2( pair.A.PaintCenter.X - pair.B.PaintCenter.X, pair.A.PaintCenter.Y - pair.B.PaintCenter.Y );
			if ( apart.Length() < 0.015f ) break;
			// The ceiling's U runs along -X and its V along +Y.
			ceilingUV += new Vec2( apart.X, -apart.Y );
		}
		Check( "floor walk d: the ceiling patch is straight above the floor patch", apart.Length() < 0.03f && pair.PlayerKeepsUpright,
			$"orange on the ceiling centre {V( pair.A.PaintCenter )}, blue on the floor centre {V( pair.B.PaintCenter )} m: {apart.Length() * 1000f:0} mm apart on the map; the crossing turns nothing" );
		hole = pair.B.PaintCenter;
		Desk.LookAt( floorCentre + new Vec3( -2.2f, -1.6f, 1.5f ), new Vec3( hole.X, hole.Y, floor + 1.6f ) );
		yield return Wait( 0.2f );
		yield return Picture( "floorwalk_d_0_ceiling_over_floor_room" );
		Desk.SetView( new Vec3( hole.X, hole.Y - 1.5f, floor + eyeHeight ), new Angles( 35f, 90f, 0f ) );
		yield return Wait( 0.2f );
		before = Desk.PortalCrossings;
		yawIn = Desk.YawDegrees;
		Desk.Walk( 1.5f, 2f );
		yield return Until( () => Desk.PortalCrossings > before, 4f );
		bool crossedD = Desk.PortalCrossings == before + 1;
		Check( "floor walk d: falls into the floor and comes out of the ceiling", crossedD, crossedD ? $"eye {V( Desk.LastCrossing.PositionBefore )} -> {V( Desk.LastCrossing.PositionAfter )} m" : "no crossing" );
		if ( !crossedD ) yield break;
		CheckPlayerCrossing( "floor walk d", true, pair.B, pair.A, pair );
		yield return Fly( 0.25f, () => false, n => notes = n );
		yield return Picture( "floorwalk_d_1_falling_from_the_ceiling_toward_the_floor_portal" );
		// Two seconds of it.
		float loopTime = 0f;
		yield return Fly( 2f, () => false, n => notes = n );
		loopTime = notes.Seconds;
		int loops = Desk.PortalCrossings - before;
		float drift = new Vec2( Desk.EyeMetres.X - hole.X, Desk.EyeMetres.Y - hole.Y ).Length();
		Check( "floor walk d: it falls round and round, never faster than the top speed, never tipped, never turned", loops >= 4 && notes.FastestSpeed <= Desk.MaxFallSpeed + 0.001f && notes.WorstTilt == 0f && Desk.IsFalling && drift < 0.15f && MathF.Abs( MathF.IEEERemainder( Desk.YawDegrees - yawIn, 360f ) ) < 0.01f,
			$"{loops} crossings in {loopTime + 0.25f:0.00} s after the first; fastest {notes.FastestSpeed:0.000} m/s (top speed {Desk.MaxFallSpeed:0.0}); worst tilt {notes.WorstTilt:0.000} deg; still falling {Desk.IsFalling}; {drift * 1000f:0} mm from the hole's middle on the map; facing yaw {Desk.YawDegrees:0.00} deg (went in at {yawIn:0.00})" );
		yield return Picture( "floorwalk_d_2_still_falling_two_seconds_on" );
		// Take the paint away: the next time down there is a floor.
		Room.ClearAllPaint();
		before = Desk.PortalCrossings;
		yield return Fly( 3f, null, n => notes = n );
		Check( "floor walk d: with the paint gone the fall ends on the floor", MathF.Abs( Desk.EyeMetres.Z - floor - Desk.EyeHeight ) < 0.002f && !Desk.IsFalling && Desk.PortalCrossings == before,
			$"standing at {V( Desk.EyeMetres )} m, {notes.Seconds:0.00} s after the paint was wiped" );
		yield return Picture( "floorwalk_d_3_landed_once_the_paint_was_gone" );
	}

	private IEnumerator<object> HelperThrow()
	{
		Say( "======== HELPER-THROWN BALL THROUGH A PORTAL (needs PortaballsHelper.exe --fake --port 8443) ========" );
		PortalLink portals = Room.Portals;
		int throwsBefore = 0;

		// A helper-thrown blue ball must land and make an opening.
		yield return Until( () => Room.Patches.TryGetOpening( B, out _, out _ ), 40f );
		bool haveBlue = Room.Patches.TryGetOpening( B, out PaintWall blueWall, out PortalOpening blue );
		Check( "helper: a helper-thrown ball made a blue opening", haveBlue, haveBlue ? $"on {blueWall.WallName} at u={blue.CenterUV.X:0.00} v={blue.CenterUV.Y:0.00}, circle {blue.InscribedRadius:0.00} m" : "no blue opening after 40 s (is the helper running with --fake --port 8443?)" );
		if ( !haveBlue ) yield break;

		// Give it an orange partner on the right-hand wall, where the desk view can see it.
		int orangeWall = WallIndexOf( blueWall ) == 3 ? 2 : 3;
		Paint( O, orangeWall, 4.6f, 1.75f );
		yield return Until( () => portals.HasPair, 6f );
		Check( "helper: linked", portals.HasPair, $"pair revision {portals.PairRevision}" );
		if ( !portals.HasPair ) yield break;
		// Stand in the back-left corner, so the far wall and the right-hand wall are both in the picture.
		Vec3 floorCentre = SboxConvert.ToMetres( Room.WorldPosition );
		Desk.LookAt( floorCentre + new Vec3( -3.3f, 2.3f, 1.9f ), floorCentre + new Vec3( 2.2f, -2.2f, 1.2f ) );
		yield return Wait( 0.1f );
		yield return Picture( "helper_1_linked" );
		throwsBefore = portals.CrossingCount;

		// The stand-in player keeps throwing every 2 s. Watch for one to go through.
		yield return Until( () => portals.CrossingCount > throwsBefore, 30f );
		bool went = portals.CrossingCount > throwsBefore;
		Check( "helper: a helper-thrown ball went through the portal", went, went ? $"crossing {portals.CrossingCount}" : "none in 30 s" );
		if ( !went ) yield break;
		PortalLink.CrossingRecord r = portals.Crossings[portals.Crossings.Count - 1];
		float positionError = ( r.PositionAfter - r.PositionPredicted ).Length() * 1000f;
		float directionError = MathUtil.AngleBetween( r.VelocityAfter, r.VelocityPredicted ) * 57.29578f;
		Check( "helper: it matches the core like any other ball", MathF.Abs( r.VelocityAfter.Length() - r.VelocityBefore.Length() ) < 0.0005f && directionError < 0.01f && ( r.ExitMovedBy > 0f || positionError < 0.5f ),
			$"ball #{r.BallSeed} went into {( r.FromOrange ? "orange" : "blue" )}: speed {r.VelocityBefore.Length():0.0000} -> {r.VelocityAfter.Length():0.0000} m/s, direction {directionError:0.0000} deg, position {positionError:0.000} mm from the core (exit slid {r.ExitMovedBy * 1000f:0.0} mm)" );
		yield return Wait( 0.06f );
		yield return Picture( "helper_2_ball_coming_out" );
		yield return Wait( 2.5f );
	}
}
