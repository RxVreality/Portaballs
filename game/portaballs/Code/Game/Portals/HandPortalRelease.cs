using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// Letting go of a ball while the hand is in a portal's opening or through it.
///
/// ThrowHand asks <see cref="TryRelease"/> in the place where it would spawn the ball. Nowhere near an
/// opening (nearly always) the answer is "not mine" after two plane distances, and the throw goes on
/// exactly as before. At an opening:
///
///   - ball's centre THROUGH the wall: the launch is carried to the far side by the pair's full crossing,
///     the one a ball in flight is carried by (position mapped, velocity and spin turned, speed kept).
///     The ball starts where its twin was being drawn. Nothing touches the wall that was reached into.
///   - ball's centre still on the room side but cutting the opening (or within the physics engine's touch
///     margin of it), or just through and still cutting the exit wall: the physics engine would call the
///     opening a wall and burst the ball on it. So the ball is flown as a picture with no physics
///     (<see cref="PortalMouthBall"/>) until it is clear of the wall on either side, then it becomes a
///     real ball. If it runs off the opening's edge on the way, it becomes a real ball there and bursts
///     like any ball that hits a rim.
///
/// NO AIM HELP for either: the point being looked at is in the room the hand left, so pulling the throw
/// toward it would be wrong on the far side. The ball flies as the hand threw it.
///
/// All the deciding is core PortalReach (tested in PortalReachTests).
/// </summary>
public static class HandPortalRelease
{
	/// <summary>The switch. Off = a ball always starts where the hand really is, as before 2026-11-19.</summary>
	public static bool Enabled { get; set; } = true;

	/// <summary>Longest step used when catching a let-go ball up to "now", seconds.</summary>
	public const float CatchUpStep = 0.004f;

	/// <summary>What the latest let-go at a portal came to, for the tests and the log.</summary>
	public struct Outcome
	{
		/// <summary>Counts up with every let-go handled here.</summary>
		public int Number;
		public HeldLaunch Launch;
		/// <summary>Position and velocity as the hand let go, before any carrying.</summary>
		public Vec3 ReleasePosition;
		public Vec3 ReleaseVelocity;
		/// <summary>Where the ball was put into the world (after the catch-up), or where the picture-only flight began.</summary>
		public Vec3 StartPosition;
		public Vec3 StartVelocity;
		public MouthOutcome AfterCatchUp;
		/// <summary>The real ball, once there is one (straight away unless it began in the mouth).</summary>
		public Portaball Ball;
		public PortalMouthBall Mouth;
	}

	public static Outcome Last { get; private set; }

	/// <summary>
	/// A hand lets go of a ball. Returns false when the ball is not at a portal (the caller spawns it as usual).
	/// </summary>
	/// <param name="hand">The hand's object (its PortalTwinSource, if any, says whether the hand is through).</param>
	/// <param name="releasePosition">Ball centre at the moment of the let-go, world metres.</param>
	/// <param name="releaseVelocity">Its velocity then, m/s, with no aim help.</param>
	/// <param name="flownSeconds">How long ago that moment was: the ball is flown this far before it appears.</param>
	/// <param name="who">For the log ("Left", "Right").</param>
	public static bool TryRelease( GameObject hand, TestRoom room, PaintColor paint, Vec3 releasePosition, Vec3 releaseVelocity, Vec3 spin,
		float flownSeconds, Vec3 gravity, float splatRadius, string who )
	{
		if ( !Enabled || !room.IsValid() ) return false;
		PortalLink portals = room.Portals;
		if ( !portals.IsValid() ) return false;
		portals.Sync();
		if ( !portals.HasPair ) return false;

		PortalPair pair = portals.Pair;
		IPaintMask maskA = portals.MaskOf( PaintColor.Orange );
		IPaintMask maskB = portals.MaskOf( PaintColor.Blue );
		PortalTwinSource source = hand.IsValid() ? hand.GetComponent<PortalTwinSource>( true ) : null;
		ReachLatch latch = source.IsValid() ? source.Latch : default;
		const float r = Portaball.RadiusMetres;
		const float clearance = Portaball.EngineTouchMarginMetres;

		HeldLaunch launch = PortalReach.Launch( latch, pair, maskA, maskB, releasePosition, releaseVelocity, spin, r, clearance, PortalReach.ArmReach, portals.Settings );
		if ( !launch.AtPortal ) return false;

		// Catch up from the let-go to now, on the side the ball is really on, carried across if it goes through meanwhile.
		MouthBall ball = new MouthBall { Position = launch.Position, Velocity = launch.Velocity, Spin = launch.Spin };
		if ( flownSeconds < 0f ) flownSeconds = 0f;
		MouthOutcome after = PortalReach.Fly( ref ball, pair, maskA, maskB, gravity, flownSeconds, CatchUpStep, r, clearance, portals.Settings );

		Outcome outcome = new Outcome();
		outcome.Number = Last.Number + 1;
		outcome.Launch = launch;
		outcome.ReleasePosition = releasePosition;
		outcome.ReleaseVelocity = releaseVelocity;
		outcome.StartPosition = ball.Position;
		outcome.StartVelocity = ball.Velocity;
		outcome.AfterCatchUp = after;

		string endName = launch.FromA ? "ORANGE" : "BLUE";
		string otherName = launch.FromA ? "BLUE" : "ORANGE";
		PaintWall entryWall = portals.WallOf( launch.FromA ? PaintColor.Orange : PaintColor.Blue );
		PaintWall exitWall = portals.WallOf( launch.FromA ? PaintColor.Blue : PaintColor.Orange );
		string depth = launch.Depth < 0f ? $"{-launch.Depth * 100f:0.0} cm THROUGH" : $"{launch.Depth * 100f:0.0} cm in front of";
		Log.Info( $"[HandRelease] {who} let go of {paint} #{outcome.Number} with the ball {depth} {endName} on {( entryWall.IsValid() ? entryWall.WallName : "?" )}. No aim help at a portal." );
		Log.Info( $"[HandRelease]   HAND   {outcome.Number}: pos {V( releasePosition )} m, vel {V( releaseVelocity )} m/s, speed {releaseVelocity.Length():0.0000}" );
		if ( launch.Carried )
		{
			PortalEnd entry = launch.FromA ? pair.A : pair.B;
			PortalEnd exit = launch.FromA ? pair.B : pair.A;
			float inInto = -Vec3.Dot( releaseVelocity, entry.Wall.Normal ), outOut = Vec3.Dot( launch.Velocity, exit.Wall.Normal );
			float inU = Vec3.Dot( releaseVelocity, entry.Wall.U ), outU = Vec3.Dot( launch.Velocity, exit.Wall.U );
			float inV = Vec3.Dot( releaseVelocity, entry.Wall.V ), outV = Vec3.Dot( launch.Velocity, exit.Wall.V );
			Log.Info( $"[HandRelease]   FAR    {outcome.Number}: starts on the far side, out of {otherName} on {( exitWall.IsValid() ? exitWall.WallName : "?" )}: pos {V( launch.Position )} m ({exit.Wall.SignedDistance( launch.Position ) * 100f:0.0} cm in front of that wall), vel {V( launch.Velocity )} m/s, speed {launch.Velocity.Length():0.0000} (change {launch.Velocity.Length() - releaseVelocity.Length():+0.00000;-0.00000;0.00000})" );
			Log.Info( $"[HandRelease]   WALLS  {outcome.Number}: into entry wall {inInto:0.000} -> out of exit wall {outOut:0.000} m/s; along wall (to the right) {inU:0.000} -> {outU:0.000} (sign must swap); up the wall {inV:0.000} -> {outV:0.000}." );
		}

		if ( after == MouthOutcome.InMouth )
		{
			outcome.Mouth = PortalMouthBall.Begin( room, paint, ball, splatRadius, outcome.Number, launch, releasePosition, releaseVelocity );
			Log.Info( $"[HandRelease]   MOUTH  {outcome.Number}: the ball is still in the opening after {flownSeconds * 1000f:0.0} ms of catch-up (pos {V( ball.Position )} m): flown with no physics until it is clear." );
		}
		else
		{
			outcome.Ball = SpawnReal( room, paint, ball, splatRadius, launch.Carried ? 1 : 0, launch, releasePosition, releaseVelocity );
			Log.Info( $"[HandRelease]   BALL   {outcome.Number}: #{outcome.Ball.Seed} put into the world after {flownSeconds * 1000f:0.0} ms of catch-up at {V( ball.Position )} m, vel {V( ball.Velocity )} m/s{( after == MouthOutcome.OffPaint ? " (it met the wall beside the opening: it will burst there)" : "" )}{( ball.Crossings > 0 ? $"; went through {ball.Crossings} more time(s) during the catch-up" : "" )}." );
		}
		Last = outcome;
		return true;
	}

	/// <summary>Make the real ball, and tell the room's portal book-keeping about each time it was carried.</summary>
	internal static Portaball SpawnReal( TestRoom room, PaintColor paint, in MouthBall ball, float splatRadius, int carriedByHand, in HeldLaunch launch,
		Vec3 releasePosition, Vec3 releaseVelocity )
	{
		Portaball real = Portaball.Spawn( room.Scene, paint, ball.Position, ball.Velocity, ball.Spin, splatRadius );
		PortalLink portals = room.Portals;
		if ( !portals.IsValid() || !portals.HasPair ) return real;

		if ( carriedByHand > 0 )
		{
			// Carried in the hand: the "crossing point" is where the ball was when it was let go.
			PortalLink.CrossingRecord record = new PortalLink.CrossingRecord();
			record.BallSeed = real.Seed;
			record.FromOrange = launch.FromA;
			record.EntryUV = ( launch.FromA ? portals.Pair.A : portals.Pair.B ).Wall.ToUV( releasePosition );
			record.PositionBefore = releasePosition;
			record.VelocityBefore = releaseVelocity;
			record.PositionAfter = launch.Position;
			record.VelocityAfter = launch.Velocity;
			record.PositionPredicted = launch.Position;
			record.VelocityPredicted = launch.Velocity;
			portals.Record( record );
		}
		if ( ball.Crossings > 0 )
		{
			// The latest crossing made in the mouth (flown with no physics): same record a ball in flight writes.
			RigidTransform plain = portals.Pair.Crossing( ball.LastFromA );
			PortalLink.CrossingRecord record = new PortalLink.CrossingRecord();
			record.BallSeed = real.Seed;
			record.FromOrange = ball.LastFromA;
			record.EntryUV = ball.EntryUV;
			record.PositionBefore = ball.PositionBefore;
			record.VelocityBefore = ball.VelocityBefore;
			record.PositionAfter = ball.PositionAfter;
			record.VelocityAfter = ball.VelocityAfter;
			record.PositionPredicted = plain.TransformPoint( ball.PositionBefore );
			record.VelocityPredicted = plain.TransformDirection( ball.VelocityBefore );
			record.ExitMovedBy = ball.ExitMovedBy;
			portals.Record( record );
		}
		return real;
	}

	internal static string V( Vec3 v )
	{
		return $"({v.X:0.0000}, {v.Y:0.0000}, {v.Z:0.0000})";
	}
}

/// <summary>
/// A let-go ball that is still in a portal's opening: a picture with no physics, flown along its free
/// flight (core PortalReach.Step, the same carry a ball in flight gets if its centre passes the wall)
/// until it is clear of the wall, when it turns into a real <see cref="Portaball"/> in the same place with
/// the same velocity. It is drawn at both ends meanwhile, like a hand (it carries a PortalTwinSource).
/// Made by <see cref="HandPortalRelease"/>.
/// </summary>
public sealed class PortalMouthBall : Component
{
	/// <summary>If a ball somehow stays in an opening this long, it is handed to the physics engine anyway, seconds.</summary>
	public const float MaxSeconds = 3f;

	public TestRoom Room { get; private set; }
	public PaintColor Paint { get; private set; }
	public float SplatRadiusMetres { get; private set; }

	/// <summary>The let-go this ball came from (HandPortalRelease.Outcome.Number).</summary>
	public int ReleaseNumber { get; private set; }

	/// <summary>The real ball it turned into (null until then).</summary>
	public Portaball Became { get; private set; }

	/// <summary>How it ended: Clear (flew free) or OffPaint (met the opening's edge). InMouth while still going.</summary>
	public MouthOutcome Ended { get; private set; } = MouthOutcome.InMouth;

	/// <summary>Centre and velocity right now, metres and m/s.</summary>
	public Vec3 PositionMetres => _ball.Position;
	public Vec3 VelocityMetres => _ball.Velocity;
	public int Crossings => _ball.Crossings;

	private MouthBall _ball;
	private HeldLaunch _launch;          // how the hand let it go (it may already have been carried once, in the hand)
	private Vec3 _releasePosition, _releaseVelocity;
	private float _age;
	private bool _done;

	internal static PortalMouthBall Begin( TestRoom room, PaintColor paint, in MouthBall ball, float splatRadius, int releaseNumber, in HeldLaunch launch,
		Vec3 releasePosition, Vec3 releaseVelocity )
	{
		GameObject go = new GameObject( room.Scene, false, paint == PaintColor.Orange ? "Portaball in an opening (orange, picture only)" : "Portaball in an opening (blue, picture only)" );
		go.WorldPosition = SboxConvert.ToInches( ball.Position );
		go.LocalScale = SboxConvert.SphereScaleFor( Portaball.RadiusMetres );
		ModelRenderer renderer = go.AddComponent<ModelRenderer>();
		renderer.Model = Model.Sphere;
		renderer.Tint = Portaball.TintFor( paint );

		PortalMouthBall mouth = go.AddComponent<PortalMouthBall>();
		mouth.Room = room;
		mouth.Paint = paint;
		mouth.SplatRadiusMetres = splatRadius;
		mouth.ReleaseNumber = releaseNumber;
		mouth._ball = ball;
		mouth._launch = launch;
		mouth._releasePosition = releasePosition;
		mouth._releaseVelocity = releaseVelocity;

		// Drawn at both ends while it cuts the wall, by the same code that does the hands.
		PortalTwinSource twin = PortalTwinSource.Register( go, go, paint );
		twin.ReachMetres = Portaball.RadiusMetres + 0.05f;
		go.Enabled = true;
		return mouth;
	}

	// On the physics clock, like a real ball: a stopped clock (a picture being taken) holds it still.
	protected override void OnFixedUpdate()
	{
		if ( _done ) return;
		PortalLink portals = Room.IsValid() ? Room.Portals : null;
		if ( !portals.IsValid() ) { Finish( MouthOutcome.Clear, "the room went away" ); return; }
		portals.Sync();
		if ( !portals.HasPair ) { Finish( MouthOutcome.Clear, "the portal closed" ); return; }

		Vec3 gravity = SboxConvert.ToMetres( Scene.PhysicsWorld.Gravity );
		int before = _ball.Crossings;
		MouthOutcome outcome = PortalReach.Step( ref _ball, portals.Pair, portals.MaskOf( PaintColor.Orange ), portals.MaskOf( PaintColor.Blue ), gravity, Time.Delta,
			Portaball.RadiusMetres, Portaball.EngineTouchMarginMetres, portals.Settings );
		_age += Time.Delta;

		WorldPosition = SboxConvert.ToInches( _ball.Position );
		if ( _ball.Crossings != before )
		{
			Transform.ClearInterpolation();   // it jumped to the other end: do not draw it sliding across the room
			Log.Info( $"[HandRelease]   MOUTH  {ReleaseNumber}: the centre went through {( _ball.LastFromA ? "ORANGE" : "BLUE" )} at u={_ball.EntryUV.X:0.000} v={_ball.EntryUV.Y:0.000}: {HandPortalRelease.V( _ball.PositionBefore )} -> {HandPortalRelease.V( _ball.PositionAfter )} m, vel {HandPortalRelease.V( _ball.VelocityBefore )} -> {HandPortalRelease.V( _ball.VelocityAfter )} m/s, exit slid {_ball.ExitMovedBy * 1000f:0.0} mm." );
		}

		if ( outcome != MouthOutcome.InMouth ) Finish( outcome, outcome == MouthOutcome.Clear ? "clear of the wall" : "it met the opening's edge: it bursts there like any ball on a rim" );
		else if ( _age > MaxSeconds ) Finish( MouthOutcome.Clear, $"still in the opening after {MaxSeconds:0} s" );
	}

	private void Finish( MouthOutcome outcome, string why )
	{
		_done = true;
		Ended = outcome;
		if ( Room.IsValid() )
		{
			Became = HandPortalRelease.SpawnReal( Room, Paint, _ball, SplatRadiusMetres, _launch.Carried ? 1 : 0, _launch, _releasePosition, _releaseVelocity );
			Log.Info( $"[HandRelease]   BALL   {ReleaseNumber}: #{Became.Seed} put into the world after {_age * 1000f:0} ms in the opening ({why}) at {HandPortalRelease.V( _ball.Position )} m, vel {HandPortalRelease.V( _ball.Velocity )} m/s, went through {_ball.Crossings} time(s)." );
		}
		GameObject.Destroy();
	}
}
