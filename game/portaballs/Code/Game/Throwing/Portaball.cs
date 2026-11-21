using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// A flying paint ball: a small sphere with real physics.
///
/// Two things can happen at a paint surface (a wall, the floor or the ceiling: each is a <see cref="PaintWall"/>):
///   - It is heading into a LINKED patch (an open portal) and its whole footprint is on the paint: it goes
///     through. It does not splat and does not bounce; it comes out of the other patch with its velocity
///     and spin carried over by the core's crossing, and flies on. Dropped into a patch on the floor it
///     flies out of a patch on a wall sideways, at the speed it fell in with.
///   - Anything else (bare surface, paint that is not a linked opening, the rim of an opening): the first
///     time it touches a <see cref="PaintWall"/> it tells that surface where and how hard it hit, then
///     removes itself.
/// Anything solid that is NOT a paint surface (a test box) bursts it with no paint: see
/// <see cref="BurstOnAnythingSolid"/>. If it never reaches anything it removes itself after a few seconds.
///
/// HOW GOING THROUGH WORKS. Just before each physics step the ball asks the core (BallTransit.Approach)
/// whether its free flight reaches a linked wall within the next two steps and fits the opening. If so
/// it takes itself off the physics engine for a moment (so the wall cannot stop it), flies its own
/// free-flight path through the wall plane, is carried across by the crossing at the step in which its
/// centre passes the plane, and hands itself back to the physics engine as soon as it is clear of the
/// exit wall. Time is not skipped: the ball is always where free flight says it should be.
/// </summary>
public sealed class Portaball : Component, Component.ICollisionListener
{
	/// <summary>Ball radius in metres (7 cm across).</summary>
	public const float RadiusMetres = 0.035f;

	/// <summary>Balls that never hit a paint surface are removed after this long, seconds.</summary>
	public const float MaxLifeSeconds = 10f;

	/// <summary>
	/// The physics engine calls a ball "touching" a wall while its skin is still about 2 cm short of it, even
	/// when the ball is moving away. Measured 2026-11-19 with balls set down moving away at 1.5 m/s: skin
	/// 18 mm clear was reported as a touch, 22 mm was not (engine\smoke\ball_exit\measurements.txt). Anything
	/// nearer a wall than this is treated as "the physics engine may call it a hit", metres.
	/// </summary>
	public const float EngineTouchMarginMetres = 0.03f;

	/// <summary>After coming out of a portal the ball is back on the physics engine once its skin is this far clear of the exit wall, metres.</summary>
	public const float ExitClearanceMetres = EngineTouchMarginMetres;

	/// <summary>A ball moving away from a wall faster than this is not hitting it, whatever the physics engine reports, m/s.</summary>
	public const float MovingAwaySpeed = 0.05f;

	/// <summary>
	/// The walls, the floor and the ceiling take paint (they are PaintWalls). With this on, a ball that hits
	/// anything else solid (a box put in the room for a test) bursts there and leaves nothing. Off = it
	/// bounces and rolls off such things, as balls did off everything but walls until 2026-11-19.
	/// </summary>
	public static bool BurstOnAnythingSolid { get; set; } = true;

	public PaintColor Paint { get; private set; }

	/// <summary>Size of the splat this ball makes, metres.</summary>
	public float SplatRadiusMetres { get; private set; } = 0.25f;

	/// <summary>Each ball gets its own number so its splat shape is repeatable.</summary>
	public uint Seed { get; private set; }

	/// <summary>The room's portal pair. Found when the ball is made; none = every wall is just a wall.</summary>
	public PortalLink Portals { get; set; }

	/// <summary>How many times this ball has gone through a portal.</summary>
	public int Crossings { get; private set; }

	/// <summary>True while the ball is passing through a portal (off the physics engine, flying its own path).</summary>
	public bool InPortal => _inPortal;

	/// <summary>True once the ball has burst on a wall or timed out. It is removed at the start of the next frame.</summary>
	public bool Spent => _spent;

	/// <summary>
	/// Set one physics step after the ball was handed back to the physics engine on the far side: how far
	/// it then was (metres, and m/s) from plain free flight carried through the core's crossing.
	/// </summary>
	public bool HandBackChecked { get; private set; }
	public float HandBackPositionError { get; private set; }
	public float HandBackVelocityError { get; private set; }

	/// <summary>Centre and velocity right now, metres and m/s, whether the physics engine or the portal code is moving it.</summary>
	public Vec3 PositionMetres => _inPortal ? _p : SboxConvert.ToMetres( WorldPosition );
	public Vec3 VelocityMetres => _inPortal ? _v : ( _body.IsValid() ? SboxConvert.ToMetres( _body.Velocity ) : _velocityBeforeStep );

	private Rigidbody _body;
	private Vec3 _velocityBeforeStep;   // m/s, as it was going INTO the latest physics step
	private float _age;
	private bool _spent;

	// ---- while passing through a portal ----
	private bool _inPortal;
	private bool _crossed;              // the centre has been carried to the exit side
	private bool _fromOrange;
	private PaintWall _entryWall;
	private PaintWall _exitWall;
	private Vec3 _p, _v, _spin;         // the ball's own free-flight state, metres, m/s, rad/s
	private Vec3 _startP, _startV;      // physics state when it left the physics engine
	private float _sinceStart;          // seconds of its own flight since then
	private float _crossedAt;           // _sinceStart at the end of the step in which it was carried across
	private PortalCarry _carry;
	private bool _checkAfterHandBack;
	private int _recordNumber;

	private static uint _nextSeed = 1;

	/// <summary>The on-screen colour for each paint.</summary>
	public static Color TintFor( PaintColor paint )
	{
		return paint == PaintColor.Orange ? new Color( 1.0f, 0.45f, 0.05f ) : new Color( 0.10f, 0.45f, 1.0f );
	}

	/// <summary>
	/// Put a new ball into the world, already moving.
	/// All inputs are in core units: metres, metres per second, radians per second.
	/// </summary>
	public static Portaball Spawn( Scene scene, PaintColor paint, Vec3 positionMetres, Vec3 velocityMetres, Vec3 spinRadians, float splatRadiusMetres )
	{
		// Built switched off, so every part is in place before physics first sees it.
		GameObject go = new GameObject( scene, false, paint == PaintColor.Orange ? "Portaball (orange)" : "Portaball (blue)" );
		go.WorldPosition = SboxConvert.ToInches( positionMetres );
		go.LocalScale = SboxConvert.SphereScaleFor( RadiusMetres );

		ModelRenderer renderer = go.AddComponent<ModelRenderer>();
		renderer.Model = Model.Sphere;
		PaintLook.Glow( renderer, paint );   // the tint, and the glowing neon material

		// The collider's radius is that of the unscaled sphere model; the object's scale shrinks both together.
		SphereCollider collider = go.AddComponent<SphereCollider>();
		collider.Radius = SboxConvert.DevSphereRadius;

		Rigidbody body = go.AddComponent<Rigidbody>();
		body.Gravity = true;
		body.EnhancedCcd = true;                                        // a thrown ball is fast and small: never let it skip through a wall
		body.RigidbodyFlags = RigidbodyFlags.DisableCollisionSounds;    // test runs on this PC must be silent

		Portaball ball = go.AddComponent<Portaball>();
		ball.Paint = paint;
		ball.SplatRadiusMetres = splatRadiusMetres;
		ball.Seed = _nextSeed++;
		ball._body = body;
		ball._velocityBeforeStep = velocityMetres;
		foreach ( PortalLink link in scene.GetAllComponents<PortalLink>() )
		{
			ball.Portals = link;
			break;
		}

		go.Enabled = true;

		body.Velocity = SboxConvert.ToInches( velocityMetres );
		body.AngularVelocity = SboxConvert.SpinToEngine( spinRadians );
		return ball;
	}

	protected override void OnFixedUpdate()
	{
		if ( _spent || !_body.IsValid() ) return;

		if ( _inPortal )
		{
			FlyThroughPortal( Time.Delta );
			return;
		}

		// Runs just before each physics step. By the time a collision is reported the ball has already
		// bounced, so its velocity then is the wrong one; this is the velocity it arrived with.
		_velocityBeforeStep = SboxConvert.ToMetres( _body.Velocity );

		if ( _checkAfterHandBack )
		{
			_checkAfterHandBack = false;
			LogFlyingOn();
		}

		if ( TryEnterPortal( Time.Delta ) )
		{
			FlyThroughPortal( Time.Delta );
		}
	}

	protected override void OnUpdate()
	{
		_age += Time.Delta;
		// (A ball part-way through a portal is left to finish; twice the time is the limit whatever it is doing.)
		if ( _age > MaxLifeSeconds && !_spent && ( !_inPortal || _age > MaxLifeSeconds * 2f ) )
		{
			_spent = true;
			GameObject.Destroy();
		}
	}

	/// <summary>Called by the engine when this ball starts touching something.</summary>
	public void OnCollisionStart( Collision collision )
	{
		Touched( collision );
	}

	/// <summary>Called by the engine every physics step while the ball goes on touching something.</summary>
	public void OnCollisionUpdate( Collision collision )
	{
		Touched( collision );
	}

	private void Touched( Collision collision )
	{
		if ( _spent || _inPortal ) return;

		GameObject other = collision.Other.GameObject;
		if ( !other.IsValid() ) return;

		PaintWall wall = other.GetComponent<PaintWall>();
		if ( !wall.IsValid() )
		{
			if ( !BurstOnAnythingSolid ) return;                        // a box or the like: bounce and roll
			if ( other.GetComponent<Portaball>().IsValid() ) return;    // two balls just bounce off each other
			_spent = true;
			Log.Info( $"[Portaball] #{Seed} {Paint}: burst on {other.Name} at {V( SboxConvert.ToMetres( WorldPosition ) )} m. No paint: it is not a paint surface." );
			GameObject.Destroy();
			return;
		}

		// The engine reports a touch from about 2 cm away (see EngineTouchMarginMetres), so a ball that has
		// just come out of a portal in this wall, or was let go right beside it, can be "touching" a wall it
		// is flying away from. That is not a hit. If it turns back while still that near, the per-step
		// update above catches it.
		if ( Vec3.Dot( _velocityBeforeStep, wall.Frame.Normal ) > MovingAwaySpeed ) return;

		// Where: the contact point. How: the velocity it arrived with.
		BurstOn( wall, SboxConvert.ToMetres( collision.Contact.Point ), _velocityBeforeStep );
	}

	/// <summary>
	/// Burst on a wall: tell it where and how hard, throw a few droplets off it, then remove the ball.
	/// Most of the paint sticks (the splat); 3 to 6 small droplets bounce off and fly on (PaintDroplets, numbers in
	/// core SplatFeel; the tester's request, 2026-11-19). They never touch the portal patches.
	/// </summary>
	private void BurstOn( PaintWall wall, Vec3 hitPoint, Vec3 v )
	{
		WallFrame frame = wall.Frame;

		// Where, in the wall's own coordinates (metres from its lower-left corner).
		Vec2 uv = frame.ToUV( hitPoint );

		// How: split the arrival velocity into the part sliding along the wall and the part going into it.
		Vec2 alongWall = new Vec2( Vec3.Dot( v, frame.U ), Vec3.Dot( v, frame.V ) );
		float intoWall = -Vec3.Dot( v, frame.Normal );   // Normal points into the room, so "into the wall" is against it
		if ( !( intoWall > 0f ) ) intoWall = -1f;         // not a believable reading: let the core use its default

		_spent = true;
		_inPortal = false;
		wall.Splat( Paint, uv, SplatRadiusMetres, alongWall, Seed, intoWall );
		PaintDroplets.Burst( Scene, wall, Paint, frame.ToWorld( uv ), v, SplatRadiusMetres, Seed );
		GameObject.Destroy();
	}

	// ------------------------------------------------------------------ going through a portal

	private Vec3 Gravity()
	{
		return SboxConvert.ToMetres( Scene.PhysicsWorld.Gravity );
	}

	/// <summary>
	/// Is the ball about to reach a linked opening it fits through? If so it leaves the physics engine
	/// and starts flying its own path. Looks two physics steps ahead of the moment its skin would be
	/// within the engine's touch margin of the wall, so the physics engine never gets to touch the wall first.
	/// </summary>
	private bool TryEnterPortal( float dt )
	{
		if ( !Portals.IsValid() ) return false;
		Portals.Sync();
		if ( !Portals.HasPair ) return false;

		Vec3 p = SboxConvert.ToMetres( WorldPosition );
		Vec3 v = _velocityBeforeStep;
		Vec3 g = Gravity();
		PortalPair pair = Portals.Pair;

		for ( int k = 0; k < 2; k++ )
		{
			bool fromOrange = k == 0;
			PaintColor colour = fromOrange ? PaintColor.Orange : PaintColor.Blue;
			PortalEnd end = fromOrange ? pair.A : pair.B;
			// The look-ahead runs to the moment the skin comes within the engine's touch margin of the wall,
			// so a slow ball leaves the physics engine before the engine can call it a hit on the opening.
			// ("Never lower than the floor" is for a ball rolling into a wall patch; into the floor itself it must not apply.)
			BallApproach approach = BallTransit.Approach( end, Portals.MaskOf( colour ), p, v, g, RadiusMetres, dt * 2f, Portals.Settings, LowestCentre( end.Wall ), EngineTouchMarginMetres );
			if ( !approach.Enters ) continue;

			_inPortal = true;
			_crossed = false;
			_fromOrange = fromOrange;
			_entryWall = Portals.WallOf( colour );
			_exitWall = Portals.WallOf( fromOrange ? PaintColor.Blue : PaintColor.Orange );
			_p = p;
			_v = v;
			_spin = SboxConvert.ToCore( _body.AngularVelocity );
			_startP = p;
			_startV = v;
			_sinceStart = 0f;

			// Off the physics engine: a body that is not simulated passes through the wall slab.
			_body.MotionEnabled = false;
			return true;
		}
		return false;
	}

	/// <summary>
	/// The lowest the ball's centre can be while it flies its own path: resting on the floor, unless the surface it
	/// is passing through IS the floor (then there is no floor there for it).
	/// </summary>
	private float LowestCentre( in WallFrame passing )
	{
		return passing.IsFloor ? float.NegativeInfinity : Portals.FloorHeight + RadiusMetres;
	}

	/// <summary>One physics step's worth of the ball's own flight while it is passing through.</summary>
	private void FlyThroughPortal( float dt )
	{
		Vec3 g = Gravity();
		PortalSettings settings = Portals.Settings;

		if ( _crossed )
		{
			// Out the other side. Once clear of the exit wall, the physics engine takes over again
			// (it moves the ball itself in this step, so nothing is advanced here).
			float clear = _exitWall.IsValid() ? _exitWall.Frame.SignedDistance( _p ) : float.MaxValue;
			if ( clear >= RadiusMetres + ExitClearanceMetres )
			{
				HandBackToPhysics();
				_checkAfterHandBack = true;
				return;
			}

			// Only out of a FLOOR can this happen: it came up too slowly to get clear and gravity is bringing it
			// back down. It goes in again through the end it came out of, like anything falling into that patch.
			PaintColor exitColour = _fromOrange ? PaintColor.Blue : PaintColor.Orange;
			bool fallingBack = _exitWall.IsValid() && Vec3.Dot( _v, _exitWall.Frame.Normal ) < 0f;
			if ( !fallingBack || !Portals.HasPair || Portals.WallOf( exitColour ) != _exitWall )
			{
				Advance( g, dt, _exitWall.IsValid() ? LowestCentre( _exitWall.Frame ) : Portals.FloorHeight + RadiusMetres );
				return;
			}
			_fromOrange = !_fromOrange;
			_entryWall = _exitWall;
			_exitWall = Portals.WallOf( _fromOrange ? PaintColor.Blue : PaintColor.Orange );
			_crossed = false;
			_startP = _p;          // the hand-back check measures from here now
			_startV = _v;
			_sinceStart = 0f;
		}

		// Still on the way in. The opening must still be there: same pair, same wall, paint under the ball.
		Portals.Sync();
		PaintColor colour = _fromOrange ? PaintColor.Orange : PaintColor.Blue;
		bool stillOpen = Portals.HasPair && _entryWall.IsValid() && Portals.WallOf( colour ) == _entryWall;
		if ( !stillOpen )
		{
			GiveUpOnPortal( "the opening closed while the ball was on its way in" );
			return;
		}

		PortalPair pair = Portals.Pair;
		PortalEnd entry = _fromOrange ? pair.A : pair.B;
		IPaintMask entryMask = Portals.MaskOf( colour );
		IPaintMask exitMask = Portals.MaskOf( _fromOrange ? PaintColor.Blue : PaintColor.Orange );

		float lowest = LowestCentre( entry.Wall );
		Vec3 next = BallTransit.AtAbove( _p, _v, g, dt, lowest );
		Vec3 vNext = _v + g * dt;
		if ( next.Z <= lowest && vNext.Z < 0f ) vNext.Z = 0f;   // rolling along the floor

		CrossingResult hit = PortalCrossing.Detect( entry, entryMask, _p, next, settings );
		if ( hit.Kind == CrossingKind.Entered )
		{
			if ( !hit.Teleports || !PortalCrossing.Passage( entry, entryMask, hit.UV, RadiusMetres, settings ).Fits )
			{
				GiveUpOnPortal( "the paint under the ball changed before it was through" );
				return;
			}

			// The centre passes the wall plane in this step: carry the ball across. Exactly one crossing
			// per step, and afterwards the ball is behind the exit plane moving out, which the core never
			// counts as an entry, so it cannot go straight back in.
			_carry = BallTransit.Carry( pair, _fromOrange, false, exitMask, hit.Point, RadiusMetres, settings );
			_exitWall = Portals.WallOf( _fromOrange ? PaintColor.Blue : PaintColor.Orange );

			Vec3 vAtPlane = _v + g * ( dt * hit.T );
			RigidTransform plain = pair.Crossing( _fromOrange );
			PortalLink.CrossingRecord record = new PortalLink.CrossingRecord();
			record.BallSeed = Seed;
			record.FromOrange = _fromOrange;
			record.EntryUV = hit.UV;
			record.PositionBefore = hit.Point;
			record.VelocityBefore = vAtPlane;
			record.PositionAfter = _carry.Transform.TransformPoint( hit.Point );
			record.VelocityAfter = _carry.Transform.TransformDirection( vAtPlane );
			record.PositionPredicted = plain.TransformPoint( hit.Point );
			record.VelocityPredicted = plain.TransformDirection( vAtPlane );
			record.ExitMovedBy = _carry.MovedBy;
			record = Portals.Record( record );
			_recordNumber = record.Number;
			LogThrough( record, entry, _fromOrange ? pair.B : pair.A );

			_p = _carry.Transform.TransformPoint( next );
			_v = _carry.Transform.TransformDirection( vNext );
			_spin = _carry.Transform.TransformDirection( _spin );
			_sinceStart += dt;
			_crossedAt = _sinceStart;
			_crossed = true;
			Crossings++;

			WorldPosition = SboxConvert.ToInches( _p );
			WorldRotation = SboxConvert.ToEngine( _carry.Transform.TransformRotation( SboxConvert.ToCore( WorldRotation ) ) );
			Transform.ClearInterpolation();   // it jumped: do not draw it sliding across the room
			return;
		}

		// Not at the plane yet. If the ball is already cutting the wall, the cut must still be on paint.
		if ( !BallTransit.FootprintOnPaint( entry, entryMask, next, RadiusMetres, settings ) )
		{
			GiveUpOnPortal( "the ball ran off the paint part-way in" );
			return;
		}
		Advance( g, dt, lowest );
	}

	private void Advance( Vec3 g, float dt, float lowest )
	{
		_p = BallTransit.AtAbove( _p, _v, g, dt, lowest );
		_v += g * dt;
		if ( _p.Z <= lowest && _v.Z < 0f ) _v.Z = 0f;   // rolling along the floor, not sinking into it
		_sinceStart += dt;
		WorldPosition = SboxConvert.ToInches( _p );
	}

	private void HandBackToPhysics()
	{
		_inPortal = false;
		_body.MotionEnabled = true;
		_body.Velocity = SboxConvert.ToInches( _v );
		_body.AngularVelocity = SboxConvert.SpinToEngine( _spin );
		_velocityBeforeStep = _v;
	}

	/// <summary>
	/// The opening went away, or the ball left the paint, before its centre was through. If it is already
	/// touching the wall it bursts there like any ball that hits a wall; otherwise the physics engine has it back.
	/// </summary>
	private void GiveUpOnPortal( string why )
	{
		bool touching = _entryWall.IsValid() && _entryWall.Frame.SignedDistance( _p ) <= RadiusMetres + 0.005f;
		Log.Info( $"[Portaball] #{Seed} {Paint}: NOT through, {why}. {( touching ? "It bursts on " + _entryWall.WallName + "." : "Back to normal flight." )}" );
		if ( touching )
		{
			BurstOn( _entryWall, _p, _v );
			return;
		}
		HandBackToPhysics();
	}

	private void LogThrough( PortalLink.CrossingRecord r, PortalEnd entry, PortalEnd exit )
	{
		string inName = _entryWall.IsValid() ? _entryWall.WallName : "?";
		string outName = _exitWall.IsValid() ? _exitWall.WallName : "?";
		float speedBefore = r.VelocityBefore.Length();
		float speedAfter = r.VelocityAfter.Length();
		float positionError = ( r.PositionAfter - r.PositionPredicted ).Length();
		float directionError = MathUtil.AngleBetween( r.VelocityAfter, r.VelocityPredicted ) * 57.29578f;

		// The same thing said with the two walls' own axes, which does not use the crossing transform at
		// all: speed into the entry wall must equal speed out of the exit wall, sideways speed swaps
		// sign (the traveller's right stays their right), speed up the wall stays.
		WallFrame a = entry.Wall, b = exit.Wall;
		float inInto = -Vec3.Dot( r.VelocityBefore, a.Normal ), outOut = Vec3.Dot( r.VelocityAfter, b.Normal );
		float inU = Vec3.Dot( r.VelocityBefore, a.U ), outU = Vec3.Dot( r.VelocityAfter, b.U );
		float inV = Vec3.Dot( r.VelocityBefore, a.V ), outV = Vec3.Dot( r.VelocityAfter, b.V );

		Log.Info( $"[Portaball] THROUGH crossing {r.Number}: #{Seed} {Paint} ball went into {( _fromOrange ? "ORANGE" : "BLUE" )} on {inName} at u={r.EntryUV.X:0.000} v={r.EntryUV.Y:0.000} and out of {( _fromOrange ? "BLUE" : "ORANGE" )} on {outName} (pair revision {r.PairRevision})." );
		Log.Info( $"[Portaball]   BEFORE {r.Number}: pos {V( r.PositionBefore )} m, vel {V( r.VelocityBefore )} m/s, speed {speedBefore:0.0000}" );
		Log.Info( $"[Portaball]   AFTER  {r.Number}: pos {V( r.PositionAfter )} m, vel {V( r.VelocityAfter )} m/s, speed {speedAfter:0.0000}" );
		Log.Info( $"[Portaball]   CORE   {r.Number}: pos {V( r.PositionPredicted )} m, vel {V( r.VelocityPredicted )} m/s. Difference: position {positionError * 1000f:0.000} mm, speed {speedAfter - speedBefore:+0.00000;-0.00000;0.00000} m/s, direction {directionError:0.0000} deg, exit slid {r.ExitMovedBy * 1000f:0.0} mm." );
		Log.Info( $"[Portaball]   WALLS  {r.Number}: into entry wall {inInto:0.000} -> out of exit wall {outOut:0.000} m/s; along wall (to the right) {inU:0.000} -> {outU:0.000} (sign must swap); up the wall {inV:0.000} -> {outV:0.000}." );
	}

	/// <summary>
	/// One real physics step after the hand-back: compare where the physics engine has the ball with
	/// where plain free flight says it should be: from before the wall up to the step it was carried across,
	/// carried by the core's crossing, then on under the world's gravity. (Between two upright walls the crossing
	/// leaves gravity as it is, so that is the same as carrying the whole flight across. Through a floor or a
	/// ceiling it is not: gravity pulls down on both sides of a portal, as in Portal.)
	/// </summary>
	private void LogFlyingOn()
	{
		Vec3 g = Gravity();
		float t = _sinceStart + Time.Delta;
		float before = System.MathF.Min( _crossedAt, t );
		Vec3 pCross = _carry.Transform.TransformPoint( BallTransit.At( _startP, _startV, g, before ) );
		Vec3 vCross = _carry.Transform.TransformDirection( _startV + g * before );
		Vec3 expectedP = BallTransit.At( pCross, vCross, g, t - before );
		Vec3 expectedV = vCross + g * ( t - before );
		Vec3 p = SboxConvert.ToMetres( WorldPosition );
		Vec3 v = SboxConvert.ToMetres( _body.Velocity );
		HandBackChecked = true;
		HandBackPositionError = ( p - expectedP ).Length();
		HandBackVelocityError = ( v - expectedV ).Length();
		Log.Info( $"[Portaball]   PHYSICS {_recordNumber}: #{Seed} one physics step after hand-back, {t * 1000f:0} ms after leaving the physics engine: pos {V( p )} m, vel {V( v )} m/s, speed {v.Length():0.0000}. Free flight from before the wall, carried across: pos {V( expectedP )} m, vel {V( expectedV )} m/s. Difference: position {( p - expectedP ).Length() * 1000f:0.00} mm, velocity {( v - expectedV ).Length():0.0000} m/s." );
	}

	private static string V( Vec3 v )
	{
		return $"({v.X:0.0000}, {v.Y:0.0000}, {v.Z:0.0000})";
	}
}
