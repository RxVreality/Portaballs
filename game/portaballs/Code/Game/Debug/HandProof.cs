using System;
using System.Collections.Generic;
using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// Scripted test of a HAND pushed through a portal, with nobody at the keyboard and no headset. Desk mode only.
///
/// It paints a linked pair (orange on Wall +X, blue on Wall -Y), builds a stand-in hand exactly like a VR
/// hand (the same block, the same held ball, the same PortalTwinSource) and moves it through the orange
/// opening in steps with the clock stopped. At each step it checks what PortalBallTwins did and stops for
/// pictures from three places: the player's side looking into orange, a view of blue, and through blue
/// back toward the player. Then: bare wall and the paint rim (no twin), reaching sideways once through,
/// letting go with the hand through (the real HandPortalRelease, the call ThrowHand makes), letting go
/// with the ball in the opening's mouth, and what it all costs when the hand is nowhere near.
///
/// Two things in the pictures are stand-ins that the game does not have: a grey block where the player's
/// head would be, and in the "forearm" pictures a grey stick behind the hand, so there is something long
/// enough to see straddling the surface. The first set of pictures is the bare hand, as in the headset.
///
/// Start it from a script (engine\hand_proof_capture.ps1 does):  Portaballs.HandProof.Begin( scene, true )
/// Every check writes "[HandProof] PASS ..." or "[HandProof] FAIL ...".
/// </summary>
public sealed class HandProof : Component
{
	public TestRoom Room { get; set; }
	public FlatThrower Desk { get; set; }

	/// <summary>When the clock is stopped for a picture: which one. Empty otherwise.</summary>
	public string PausedAt { get; private set; } = "";
	public bool PauseForPictures { get; set; }
	public bool Running { get; private set; }
	public bool Finished { get; private set; }
	public int Passes { get; private set; }
	public int Failures { get; private set; }

	private readonly Stack<IEnumerator<object>> _stack = new Stack<IEnumerator<object>>();
	private bool _timedOut;

	private GameObject _hand, _block, _heldBall, _forearm, _head;
	private PortalTwinSource _source;
	private const PaintColor O = PaintColor.Orange;
	private const PaintColor B = PaintColor.Blue;

	// The ball being watched after a let-go: where it is each physics step, against free flight from its start.
	private Portaball _watched;
	private Vec3 _watchStartP, _watchStartV, _watchLastP;
	private float _watchTime, _watchWorst, _watchFirstStepError = -1f;
	private int _watchSteps;

	/// <summary>Find the room and desk camera in a running desk-mode scene, add the test and start it.</summary>
	/// <param name="helperScene">
	/// True = the other script, for scenes/portaballs_helper_desk.scene with  PortaballsHelper.exe --fake --port 8443  running:
	/// the stand-in player's real ThrowHand lets go with its hand through a portal.
	/// </param>
	public static string Begin( Scene scene, bool pauseForPictures, bool helperScene = false )
	{
		if ( scene is null ) return "no scene";
		TestRoom room = null;
		FlatThrower desk = null;
		foreach ( TestRoom r in scene.GetAllComponents<TestRoom>() ) room = r;
		foreach ( FlatThrower d in scene.GetAllComponents<FlatThrower>() ) desk = d;
		if ( !room.IsValid() || !desk.IsValid() ) return "no room or desk camera in the running scene (is it playing, without VR?)";
		HandProof proof = room.GameObject.GetComponent<HandProof>();
		if ( !proof.IsValid() ) proof = room.GameObject.AddComponent<HandProof>();
		proof.Room = room;
		proof.Desk = desk;
		proof.Run( pauseForPictures, helperScene );
		return "ok";
	}

	public void Run( bool pauseForPictures, bool helperScene = false )
	{
		_stack.Clear();
		PausedAt = "";
		Scene.TimeScale = 1f;
		PauseForPictures = pauseForPictures;
		Passes = 0;
		Failures = 0;
		Finished = false;
		Running = true;
		_stack.Push( helperScene ? HelperMain() : Main() );
		Log.Info( $"[HandProof] START {( helperScene ? "helper scene" : "stand-in hand" )} (pictures: {pauseForPictures})." );
	}

	/// <summary>Let the script go on after a picture stop.</summary>
	public void Resume()
	{
		PausedAt = "";
	}

	protected override void OnUpdate()
	{
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
				Log.Warning( $"[HandProof] FAIL the script stopped with an error: {e.Message}" );
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
			Log.Info( $"[HandProof] FINISHED: {Passes} checks passed, {Failures} failed." );
		}
	}

	protected override void OnFixedUpdate()
	{
		if ( !_watched.IsValid() || _watched.Spent ) return;
		// Called before this step's physics: the ball is where the steps so far put it.
		Vec3 p = _watched.PositionMetres;
		Vec3 g = SboxConvert.ToMetres( Scene.PhysicsWorld.Gravity );
		float error = ( p - BallTransit.At( _watchStartP, _watchStartV, g, _watchTime ) ).Length();
		if ( _watchSteps == 1 ) _watchFirstStepError = error;
		if ( error > _watchWorst ) _watchWorst = error;
		_watchLastP = p;
		_watchTime += Time.Delta;
		_watchSteps++;
	}

	private void Watch( Portaball ball )
	{
		_watched = ball;
		_watchStartP = ball.PositionMetres;
		_watchStartV = ball.VelocityMetres;
		_watchLastP = _watchStartP;
		_watchTime = 0f;
		_watchWorst = 0f;
		_watchFirstStepError = -1f;
		_watchSteps = 0;
	}

	// ------------------------------------------------------------------ small tools

	private void Check( string name, bool ok, string detail )
	{
		if ( ok ) Passes++; else Failures++;
		if ( ok ) Log.Info( $"[HandProof] PASS {name}: {detail}" );
		else Log.Warning( $"[HandProof] FAIL {name}: {detail}" );
	}

	private void Say( string text )
	{
		Log.Info( $"[HandProof] {text}" );
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

	/// <summary>Wait this many drawn frames (works with the clock stopped).</summary>
	private IEnumerator<object> Frames( int frames )
	{
		for ( int i = 0; i < frames; i++ ) yield return null;
	}

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

	/// <summary>Stop for a picture and wait to be resumed (or one real minute). The clock is left as it was.</summary>
	private IEnumerator<object> Picture( string label )
	{
		if ( !PauseForPictures ) yield break;
		yield return Frames( 3 );   // the camera has just moved: let the portal view and the guard sheet settle
		PausedAt = label;
		Say( $"stopped for picture {label}" );
		double started = RealTime.Now;
		while ( PausedAt != "" && RealTime.Now - started < 60.0 ) yield return null;
		PausedAt = "";
		yield return null;
	}

	/// <summary>
	/// Lay the right-angle pair again (same seeds, so the same openings) and wait, clock running, until it is linked and
	/// the paint has stopped; the clock is then left as it was. A ball that lands on the floor paints it now, which can
	/// move a patch, so each part after a let-go starts from the same layout.
	/// </summary>
	private IEnumerator<object> Relay()
	{
		float scale = Scene.TimeScale;
		Scene.TimeScale = 1f;
		Say( "re-laying the two patches: " + SeeThroughProof.Layout( Room, "rightangle" ) );
		yield return Until( () => Room.Portals.HasPair, 6f );
		yield return Until( () => !AnyPaintMoving(), 16f );
		yield return Wait( 0.3f );
		Room.Portals.Sync();
		Scene.TimeScale = scale;
	}

	private bool AnyPaintMoving()
	{
		for ( int i = 0; i < Room.Walls.Count; i++ )
		{
			if ( Room.Walls[i].IsPaintMoving ) return true;
		}
		return false;
	}

	private int TotalHits()
	{
		int n = 0;
		for ( int i = 0; i < Room.Walls.Count; i++ ) n += Room.Walls[i].Hits.Count;
		return n;
	}

	// ------------------------------------------------------------------ the stand-in hand

	private void BuildHand()
	{
		if ( _hand.IsValid() ) _hand.Destroy();
		if ( _head.IsValid() ) _head.Destroy();

		_hand = new GameObject( Room.GameObject, false, "Proof Hand" );

		// The same block a VR hand has (VrRig.BuildHand).
		_block = new GameObject( _hand, false, "Hand Block" );
		_block.LocalScale = SboxConvert.BoxScaleFor( new Vec3( 0.10f, 0.035f, 0.05f ) );
		ModelRenderer blockRenderer = _block.AddComponent<ModelRenderer>();
		blockRenderer.Model = Model.Cube;
		Color tint = Portaball.TintFor( O );
		blockRenderer.Tint = new Color( 0.5f + 0.5f * tint.r, 0.5f + 0.5f * tint.g, 0.5f + 0.5f * tint.b );
		_block.Enabled = true;

		// The same held ball a ThrowHand shows (ThrowHand.OnStart), at the middle of the palm.
		_heldBall = new GameObject( _hand, false, "Held Ball" );
		_heldBall.LocalScale = SboxConvert.SphereScaleFor( Portaball.RadiusMetres );
		ModelRenderer ballRenderer = _heldBall.AddComponent<ModelRenderer>();
		ballRenderer.Model = Model.Sphere;
		ballRenderer.Tint = tint;
		_heldBall.Enabled = true;

		// NOT in the game: a stick behind the hand, so the pictures have something long enough to see straddling the surface.
		_forearm = new GameObject( _hand, false, "Forearm (proof only)" );
		_forearm.LocalPosition = SboxConvert.ToInches( new Vec3( -0.225f, 0f, 0f ) );
		_forearm.LocalScale = SboxConvert.BoxScaleFor( new Vec3( 0.35f, 0.045f, 0.045f ) );
		ModelRenderer armRenderer = _forearm.AddComponent<ModelRenderer>();
		armRenderer.Model = Model.Cube;
		armRenderer.Tint = new Color( 0.75f, 0.6f, 0.5f );
		_forearm.Enabled = false;

		_source = PortalTwinSource.Register( _hand, _block );
		PortalTwinSource.Register( _hand, _heldBall, O );
		PortalTwinSource.Register( _hand, _forearm );
		_hand.Enabled = true;

		// NOT in the game either: where the player's head would be, so "back toward the player" has a player in it.
		_head = new GameObject( Room.GameObject, false, "Player head (proof only)" );
		_head.LocalScale = SboxConvert.BoxScaleFor( new Vec3( 0.18f, 0.16f, 0.22f ) );
		ModelRenderer headRenderer = _head.AddComponent<ModelRenderer>();
		headRenderer.Model = Model.Cube;
		headRenderer.Tint = new Color( 0.5f, 0.5f, 0.56f );
		_head.Enabled = true;
	}

	/// <summary>
	/// Put the hand's centre <paramref name="depth"/> metres in front of a wall point (minus = through), pointing
	/// into the wall, moved <paramref name="sideways"/> along the wall, rolled a little so it is not square-on.
	/// </summary>
	private Vec3 PlaceHand( in WallFrame wall, Vec2 uv, float depth, float sideways = 0f )
	{
		Vec3 centre = wall.ToWorld( uv ) + wall.Normal * depth + wall.U * sideways;
		Rotation aim = Rotation.LookAt( SboxConvert.ToEngine( -wall.Normal ), SboxConvert.ToEngine( wall.V ) );
		_hand.WorldPosition = SboxConvert.ToInches( centre );
		_hand.WorldRotation = aim * Rotation.FromRoll( 25f );
		return centre;
	}

	private static float Distance( GameObject a, Vec3 metres )
	{
		return ( SboxConvert.ToMetres( a.WorldPosition ) - metres ).Length();
	}

	// ------------------------------------------------------------------ the script

	private IEnumerator<object> Main()
	{
		if ( !Room.IsValid() || !Desk.IsValid() )
		{
			Check( "desk mode", false, "no room or no desk camera" );
			yield break;
		}
		PortalLink portals = Room.Portals;
		PortalBallTwins twins = Room.Twins;

		Say( "======== setting up: orange on Wall +X, blue on Wall -Y ========" );
		Say( SeeThroughProof.Furnish( Room ) );
		Say( SeeThroughProof.Layout( Room, "rightangle" ) );
		Desk.TourView();
		yield return Until( () => portals.HasPair, 6f );
		Check( "the two patches link", portals.HasPair, portals.HasPair ? $"pair revision {portals.PairRevision}" : "no pair after 6 s" );
		if ( !portals.HasPair ) yield break;
		yield return Until( () => !AnyPaintMoving(), 16f );
		yield return Wait( 0.3f );
		portals.Sync();
		if ( !portals.HasPair ) { Check( "still linked once the paint settled", false, "the pair came apart" ); yield break; }

		PortalPair pair = portals.Pair;
		PortalEnd orange = pair.A, blue = pair.B;
		int orangeWall = -1, blueWall = -1;
		for ( int i = 0; i < Room.Walls.Count; i++ )
		{
			if ( Room.Walls[i] == portals.WallOf( O ) ) orangeWall = i;
			if ( Room.Walls[i] == portals.WallOf( B ) ) blueWall = i;
		}
		Vec2 uvO = orange.Opening.CenterUV, uvB = blue.Opening.CenterUV;
		Say( $"settled{( _timedOut ? " (gave up waiting for the last drips)" : "" )}. ORANGE on {portals.WallOf( O ).WallName} centre {V( orange.PaintCenter )} m, circle {orange.Opening.InscribedRadius:0.000} m. BLUE on {portals.WallOf( B ).WallName} centre {V( blue.PaintCenter )} m, circle {blue.Opening.InscribedRadius:0.000} m. Pair keeps upright: {pair.KeepsUpright}." );

		// Which crossing: the far-side picture is drawn with the pair's PLAYER crossing; hand twins use the full one
		// unless the switch says otherwise. For this pair they must be the same turn-and-move.
		RigidTransform viewCrossing = pair.PlayerCrossing( true );
		RigidTransform twinCrossing = PortalReach.Crossing( pair, true, PortalBallTwins.UsePlayerCrossing );
		Vec3 probe = orange.PaintCenter + new Vec3( 0.3f, -0.2f, 0.4f );
		float crossingGap = ( viewCrossing.TransformPoint( probe ) - twinCrossing.TransformPoint( probe ) ).Length();
		Check( "twin crossing = the crossing the far-side picture is drawn with", crossingGap < 1e-4f, $"a test point carried by both lands {crossingGap * 1000f:0.000} mm apart (UsePlayerCrossing {PortalBallTwins.UsePlayerCrossing})" );

		BuildHand();
		_head.WorldPosition = SboxConvert.ToInches( orange.PaintCenter + orange.Normal * 0.70f + orange.Wall.V * 0.12f );
		Scene.TimeScale = 0f;
		int hitsAtStart = TotalHits();
		int entryHitsAtStart = Room.Walls[orangeWall].Hits.Count;
		int revisionAtStart = portals.PairRevision;

		// ---- 1. the hand in steps through the middle of the orange opening ----
		// depth (m, + = in front), forearm on?, pictures?
		float[] depths = { 0.40f, 0.10f, 0.00f, -0.06f, 0.00f, -0.12f, -0.28f };
		bool[] arm = { false, false, false, false, true, true, true };
		for ( int s = 0; s < depths.Length; s++ )
		{
			float d = depths[s];
			_forearm.Enabled = arm[s];
			Vec3 centre = PlaceHand( orange.Wall, uvO, d );
			yield return Frames( 2 );

			string name = $"step {s + 1} ({( d >= 0f ? $"{d * 100f:0} cm in front" : $"{-d * 100f:0} cm through" )}{( arm[s] ? ", with forearm" : "" )})";
			ReachState state = _source.State;
			int want = d <= _source.ReachMetres ? ( arm[s] ? 3 : 2 ) : 0;
			Check( $"{name}: twins on show", _source.ActiveTwins == want && twins.ActiveHandTwins == want,
				$"{_source.ActiveTwins} twin(s) (want {want}); at a portal {state.AtPortal}, through {state.Through}, depth {state.Depth * 100f:0.0} cm" );
			Check( $"{name}: through or not", state.Through == ( d < 0f ), $"through {state.Through}, latch {_source.Latch.Through}" );

			if ( want > 0 )
			{
				RigidTransform crossing = PortalReach.Crossing( pair, true, PortalBallTwins.UsePlayerCrossing );
				GameObject blockTwin = _source.TwinOf( _block ), ballTwin = _source.TwinOf( _heldBall );
				bool have = blockTwin.IsValid() && ballTwin.IsValid();
				Vec3 seam = blue.Normal * twins.SeamShiftMetres;
				float blockError = have ? Distance( blockTwin, crossing.TransformPoint( SboxConvert.ToMetres( _block.WorldPosition ) ) + seam ) : 99f;
				float ballError = have ? Distance( ballTwin, crossing.TransformPoint( SboxConvert.ToMetres( _heldBall.WorldPosition ) ) + seam ) : 99f;
				float outOfBlue = have ? blue.Wall.SignedDistance( SboxConvert.ToMetres( blockTwin.WorldPosition ) ) - twins.SeamShiftMetres : 99f;
				// The twin's own axes are the hand's, carried: its "into the wall" axis now points out of blue.
				Vec3 twinForward = have ? SboxConvert.ToCore( blockTwin.WorldRotation.Forward ) : Vec3.Zero;
				float pointsOut = Vec3.Dot( twinForward, blue.Normal );
				Check( $"{name}: twins are where the crossing puts them", have && blockError < 0.001f && ballError < 0.001f && MathF.Abs( outOfBlue + d ) < 0.001f && pointsOut > 0.999f,
					$"block twin {blockError * 1000f:0.000} mm and ball twin {ballError * 1000f:0.000} mm from the carried pose; twin centre {outOfBlue * 100f:0.0} cm in front of the blue wall (hand centre {d * 100f:0.0} cm in front of orange); twin points out of blue {pointsOut:0.0000}; same size {( have ? ( blockTwin.WorldScale - _block.WorldScale ).Length : 0f ):0.0000}" );
			}

			if ( d <= 0.10f )
			{
				string tag = $"{s + 1:00}_{( d >= 0f ? "front" : "through" )}{MathF.Abs( d ) * 100f:00}cm{( arm[s] ? "_forearm" : "" )}";
				Desk.ViewWallPoint( orangeWall, uvO.X, uvO.Y, 1.15f, 38f, 12f );
				yield return Picture( $"hand_{tag}_1_into_orange" );
				Desk.ViewWallPoint( blueWall, uvB.X, uvB.Y, 1.15f, -38f, 12f );
				yield return Picture( $"hand_{tag}_2_at_blue" );
				Desk.ViewWallPoint( blueWall, uvB.X, uvB.Y, 1.7f, 14f, 6f );
				yield return Picture( $"hand_{tag}_3_through_blue_to_player" );

				if ( s == 5 )
				{
					// The same two views with the far side drawn the way the headset draws it (mode B: by the patched
					// engine, once per drawn view). The twin is an ordinary object, so it must be in that picture too.
					PortalViewMode was = Room.View.Mode;
					Room.View.Mode = PortalViewMode.B;
					Desk.ViewWallPoint( orangeWall, uvO.X, uvO.Y, 1.15f, 38f, 12f );
					yield return Frames( 3 );
					Say( $"view mode asked for B; in use: {Room.View.ModeInUse}, drawn per view by the engine: {Room.View.PerViewInUse}" );
					yield return Picture( $"hand_{tag}_1_into_orange_modeB" );
					Desk.ViewWallPoint( blueWall, uvB.X, uvB.Y, 1.15f, -38f, 12f );
					yield return Picture( $"hand_{tag}_2_at_blue_modeB" );
					Room.View.Mode = was;
					yield return Frames( 3 );
				}
			}
		}

		// ---- 1b. the surface from close up and from the side, straight in and at a slant, with and without the seam shift ----
		{
			float keep = twins.SeamShiftMetres;
			_forearm.Enabled = true;
			for ( int k = 0; k < 4; k++ )
			{
				bool slant = k >= 2;
				twins.SeamShiftMetres = ( k % 2 == 0 ) ? 0f : 0.008f;
				PlaceHand( orange.Wall, uvO, -0.03f );
				if ( slant ) _hand.WorldRotation = Rotation.LookAt( SboxConvert.ToEngine( Vec3.Normalize( -orange.Normal + orange.Wall.U * 0.7f ) ), SboxConvert.ToEngine( orange.Wall.V ) ) * Rotation.FromRoll( 25f );
				yield return Frames( 2 );
				Desk.ViewWallPoint( orangeWall, uvO.X, uvO.Y, 0.42f, 62f, 8f );
				yield return Picture( $"seam_{( slant ? "slant" : "straight" )}_{( k % 2 == 0 ? "shift0" : "shift8mm" )}_into_orange" );
			}
			twins.SeamShiftMetres = keep;
		}

		// ---- 2. the same hand, the other way: into blue, out of orange ----
		_forearm.Enabled = true;
		PlaceHand( blue.Wall, uvB, -0.12f );
		yield return Frames( 2 );
		{
			ReachState state = _source.State;
			GameObject blockTwin = _source.TwinOf( _block );
			float outOfOrange = blockTwin.IsValid() ? orange.Wall.SignedDistance( SboxConvert.ToMetres( blockTwin.WorldPosition ) ) - twins.SeamShiftMetres : 99f;
			Check( "into blue: the twin comes out of orange", state.Through && !state.FromA && _source.ActiveTwins == 3 && MathF.Abs( outOfOrange - 0.12f ) < 0.001f,
				$"through {state.Through}, at {( state.FromA ? "orange" : "blue" )}, {_source.ActiveTwins} twins, twin centre {outOfOrange * 100f:0.0} cm in front of the orange wall" );
			Desk.ViewWallPoint( orangeWall, uvO.X, uvO.Y, 1.15f, 38f, 12f );
			yield return Picture( "hand_08_into_blue_seen_at_orange" );
		}

		// ---- 3. bare wall and the paint rim: nothing on the far side ----
		_forearm.Enabled = true;
		WallFrame wallO = orange.Wall;
		Vec2 bare = new Vec2( uvO.X + 1.9f, uvO.Y );
		PlaceHand( wallO, bare, -0.12f );
		yield return Frames( 2 );
		Check( "hand pushed 12 cm into BARE wall: no twin", !_source.State.AtPortal && _source.ActiveTwins == 0 && twins.ActiveHandTwins == 0,
			$"at a portal {_source.State.AtPortal}, twins {_source.ActiveTwins}; wall point u={bare.X:0.00} v={bare.Y:0.00}, paint there: {portals.MaskOf( O ).IsPainted( O, bare )}" );
		Desk.ViewWallPoint( blueWall, uvB.X, uvB.Y, 1.15f, -38f, 12f );
		yield return Picture( "hand_09_in_bare_wall_seen_at_blue" );
		Desk.ViewWallPoint( orangeWall, uvO.X + 0.95f, uvO.Y, 1.6f, 30f, 12f );
		yield return Picture( "hand_09_in_bare_wall_seen_at_orange" );

		// The rim: walk out from the centre along the wall until the paint ends, then come 2 cm back in. On paint, not in the opening.
		float reach = 0f;
		while ( reach < 2f && portals.MaskOf( O ).IsPainted( O, uvO + new Vec2( reach + 0.005f, 0f ) ) ) reach += 0.005f;
		Vec2 rim = uvO + new Vec2( reach - 0.02f, 0f );
		PlaceHand( wallO, rim, -0.12f );
		yield return Frames( 2 );
		Check( "hand pushed into plain PAINT (the rim, 2 cm inside the paint's edge): no twin", !_source.State.AtPortal && _source.ActiveTwins == 0,
			$"paint reaches {reach * 100f:0} cm from the centre along the wall; hand at {( reach - 0.02f ) * 100f:0} cm, on paint: {portals.MaskOf( O ).IsPainted( O, rim )}; at a portal {_source.State.AtPortal}, twins {_source.ActiveTwins}" );

		// ---- 4. once through, the hand is in the far room: it may reach sideways past the outline ----
		PlaceHand( wallO, uvO, -0.25f );
		yield return Frames( 2 );
		bool wentIn = _source.State.Through;
		Vec3 sideCentre = PlaceHand( wallO, uvO, -0.30f, reach + 0.25f );
		yield return Frames( 2 );
		{
			GameObject blockTwin = _source.TwinOf( _block );
			float error = blockTwin.IsValid() ? Distance( blockTwin, pair.Crossing( true ).TransformPoint( sideCentre ) + blue.Normal * twins.SeamShiftMetres ) : 99f;
			Check( "through, then 25 cm sideways past the paint's edge: still drawn on the far side", wentIn && _source.State.Through && _source.ActiveTwins == 3 && error < 0.001f,
				$"through {_source.State.Through}, twins {_source.ActiveTwins}, block twin {error * 1000f:0.000} mm from the carried pose" );
			Desk.ViewWallPoint( blueWall, uvB.X, uvB.Y, 1.5f, -30f, 12f );
			yield return Picture( "hand_10_through_and_reaching_sideways_seen_at_blue" );
		}
		PlaceHand( wallO, uvO, 0.20f, reach + 0.25f );
		yield return Frames( 2 );
		Check( "pulled back out beside the patch: no twin", !_source.State.AtPortal && !_source.Latch.Through && _source.ActiveTwins == 0, $"at a portal {_source.State.AtPortal}, twins {_source.ActiveTwins}" );

		// ---- 5. what it costs with the hand nowhere near ----
		PlaceHand( wallO, uvO, 1.5f );
		yield return Frames( 2 );
		{
			long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
			const int runs = 2000;
			for ( int i = 0; i < runs; i++ ) twins.PlaceHandTwins();
			double micro = ( System.Diagnostics.Stopwatch.GetTimestamp() - t0 ) * 1e6 / System.Diagnostics.Stopwatch.Frequency / runs;
			Check( "cost with no hand near a patch", micro < 20.0 && twins.ActiveHandTwins == 0, $"{micro:0.00} millionths of a second per frame for the hand twins (1 hand, linked pair, hand 1.5 m away); twins on show {twins.ActiveHandTwins}" );
		}

		// ---- 6. let go with the hand through ----
		_forearm.Enabled = true;
		Vec3 releaseAt = PlaceHand( wallO, uvO, -0.25f );
		yield return Frames( 2 );
		Check( "ready to let go: hand 25 cm through orange", _source.State.Through && _source.State.FromA, $"depth {_source.State.Depth * 100f:0.0} cm" );
		Desk.ViewWallPoint( blueWall, uvB.X, uvB.Y, 2.2f, -55f, 14f );
		yield return Picture( "hand_11_before_letting_go_seen_at_blue" );

		Vec3 gravity = SboxConvert.ToMetres( Scene.PhysicsWorld.Gravity );
		// Thrown on into the wall at 4 m/s, to the left and a little up (so that on the far side it comes down on clear floor).
		Vec3 releaseVelocity = -wallO.Normal * 4f - wallO.U * 1.2f + wallO.V * 0.8f;
		Vec3 spin = new Vec3( 0f, 3f, 0f );
		const float flown = 0.011f;
		int crossingsBefore = portals.CrossingCount;
		bool handled = HandPortalRelease.TryRelease( _hand, Room, O, releaseAt, releaseVelocity, spin, flown, gravity, 0.42f, "Proof hand" );
		_heldBall.Enabled = false;
		HandPortalRelease.Outcome outcome = HandPortalRelease.Last;
		Check( "let go 25 cm through: handled as a portal let-go", handled && outcome.Launch.Carried && outcome.Launch.FromA && outcome.Ball.IsValid(),
			$"handled {handled}, carried {outcome.Launch.Carried}, real ball made {outcome.Ball.IsValid()}" );
		if ( handled && outcome.Ball.IsValid() )
		{
			RigidTransform crossing = pair.Crossing( true );
			Vec3 farP = crossing.TransformPoint( releaseAt ), farV = crossing.TransformDirection( releaseVelocity );
			Vec3 expectP = BallTransit.At( farP, farV, gravity, flown ), expectV = farV + gravity * flown;
			float pErr = ( outcome.StartPosition - expectP ).Length(), vErr = ( outcome.StartVelocity - expectV ).Length();
			float speedChange = outcome.Launch.Velocity.Length() - releaseVelocity.Length();
			float inFront = blue.Wall.SignedDistance( outcome.StartPosition );
			float outOf = Vec3.Dot( outcome.Launch.Velocity, blue.Normal ), sideIn = Vec3.Dot( releaseVelocity, wallO.U ), sideOut = Vec3.Dot( outcome.Launch.Velocity, blue.Wall.U );
			float upIn = Vec3.Dot( releaseVelocity, wallO.V ), upOut = Vec3.Dot( outcome.Launch.Velocity, blue.Wall.V );
			Check( "the ball starts on the far side with the mapped velocity", pErr < 0.001f && vErr < 0.001f && MathF.Abs( speedChange ) < 0.0005f && inFront > 0.25f
				&& MathF.Abs( outOf - 4f ) < 0.001f && MathF.Abs( sideOut + sideIn ) < 0.001f && MathF.Abs( upOut - upIn ) < 0.001f,
				$"start {V( outcome.StartPosition )} m, {pErr * 1000f:0.000} mm and {vErr:0.0000} m/s from the core's crossing of the hand's launch flown {flown * 1000f:0} ms; {inFront * 100f:0.0} cm in front of the blue wall; speed {releaseVelocity.Length():0.0000} -> {outcome.Launch.Velocity.Length():0.0000}; into orange 4.000 -> out of blue {outOf:0.000}; sideways {sideIn:0.000} -> {sideOut:0.000}; up {upIn:0.000} -> {upOut:0.000}" );

			// Where the mapped path says it comes down: free flight from the far-side launch until its skin meets the floor.
			float floorZ = SboxConvert.ToMetres( Room.WorldPosition ).Z + Portaball.RadiusMetres;
			float tLand = BallTransit.TimeToDistance( farP.Z - floorZ, farV.Z, gravity.Z, 0f );
			Vec3 landing = BallTransit.At( farP, farV, gravity, tLand );
			Say( $"mapped path: launch {V( farP )} m at {V( farV )} m/s on the far side; it should meet the floor after {tLand:0.000} s at {V( landing )} m" );

			Watch( outcome.Ball );
			Portaball ball = outcome.Ball;
			Scene.TimeScale = 1f;
			yield return Wait( 0.12f );
			Scene.TimeScale = 0f;
			Desk.ViewWallPoint( blueWall, uvB.X, uvB.Y, 2.2f, -55f, 14f );
			yield return Picture( "hand_12_just_after_letting_go_seen_at_blue" );
			Desk.ViewWallPoint( orangeWall, uvO.X, uvO.Y, 1.15f, 38f, 12f );
			yield return Picture( "hand_12_just_after_letting_go_into_orange" );
			Scene.TimeScale = 1f;
			yield return Until( () => !ball.IsValid() || ball.Spent, 4f );
			yield return Wait( 0.3f );
			Scene.TimeScale = 0f;

			// The last place it was seen (one physics step or less before it burst) against the mapped path and its end.
			float landError = new Vec2( _watchLastP.X - landing.X, _watchLastP.Y - landing.Y ).Length();
			float offPath = float.MaxValue;
			for ( float t = 0f; t <= tLand + 0.1f; t += 0.001f ) offPath = MathF.Min( offPath, ( _watchLastP - BallTransit.At( farP, farV, gravity, t ) ).Length() );
			Check( "the ball flies the mapped path and lands where it says", !_timedOut && _watchFirstStepError >= 0f && _watchFirstStepError < 0.01f && offPath < 0.08f && landError < 0.25f && _watchLastP.Z < floorZ + 0.25f,
				$"one physics step after the start it was {_watchFirstStepError * 1000f:0.0} mm from the mapped path; worst at the same moment over the {_watchTime:0.00} s flight {_watchWorst * 100f:0.0} cm (the physics engine's own stepping); last seen at {V( _watchLastP )} m, {offPath * 100f:0.0} cm off the mapped path and {landError * 100f:0.0} cm across the floor from the predicted landing {V( landing )} m" );
			// Since the floor takes paint (2026-11-19, the tester: "I should be able to portal through the floor"), the ball
			// paints where it lands on the floor and may move its colour's patch there. What a let-go at a portal must
			// never do is burst or paint on the wall that was reached into.
			int newOnEntry = Room.Walls[orangeWall].Hits.Count - entryHitsAtStart;
			Check( "no burst and no paint on the wall that was reached into", newOnEntry == 0,
				$"new splats on {Room.Walls[orangeWall].WallName}: {newOnEntry}; all splats since the start {TotalHits() - hitsAtStart} (the landing on the floor paints it); crossings recorded for the let-go: {portals.CrossingCount - crossingsBefore}" );
		}

		// The landing may have moved the orange patch to the floor: lay the same two patches again (same seeds, so the
		// same openings) before the next part.
		yield return Relay();
		hitsAtStart = TotalHits();
		entryHitsAtStart = Room.Walls[orangeWall].Hits.Count;
		revisionAtStart = portals.PairRevision;

		// ---- 7. let go with the ball in the opening's mouth (straddling the surface), moving in slowly ----
		_heldBall.Enabled = true;
		releaseAt = PlaceHand( wallO, uvO, 0.01f );
		yield return Frames( 2 );
		crossingsBefore = portals.CrossingCount;
		handled = HandPortalRelease.TryRelease( _hand, Room, O, releaseAt, -wallO.Normal * 0.8f + wallO.V * 0.5f, spin, flown, gravity, 0.42f, "Proof hand" );
		_heldBall.Enabled = false;
		outcome = HandPortalRelease.Last;
		PortalMouthBall mouth = outcome.Mouth;
		Check( "let go straddling the surface: flown with no physics, not burst on the opening", handled && !outcome.Launch.Carried && mouth.IsValid(),
			$"handled {handled}, carried in the hand {outcome.Launch.Carried}, picture-only ball made {mouth.IsValid()}, state after catch-up {outcome.AfterCatchUp}" );
		if ( mouth.IsValid() )
		{
			yield return Frames( 2 );
			PortalTwinSource mouthSource = mouth.GameObject.GetComponent<PortalTwinSource>();
			Check( "the straddling ball is drawn at both ends", mouthSource.IsValid() && mouthSource.ActiveTwins == 1, $"twins {( mouthSource.IsValid() ? mouthSource.ActiveTwins : -1 )}" );
			Desk.ViewWallPoint( blueWall, uvB.X, uvB.Y, 1.15f, -38f, 12f );
			yield return Picture( "hand_13_ball_let_go_in_the_mouth_seen_at_blue" );

			Scene.TimeScale = 1f;
			yield return Until( () => !mouth.IsValid() || mouth.Became.IsValid(), 3.5f );
			Portaball became = mouth.Became;
			// The component is destroyed as it hands over; the newest ball in the scene is the one it made.
			if ( !became.IsValid() )
			{
				foreach ( Portaball b in Scene.GetAllComponents<Portaball>() )
				{
					if ( !became.IsValid() || b.Seed > became.Seed ) became = b;
				}
			}
			bool ok = became.IsValid();
			float inFront = ok ? blue.Wall.SignedDistance( became.PositionMetres ) : -1f;
			float outSpeed = ok ? Vec3.Dot( became.VelocityMetres, blue.Normal ) : 0f;
			Check( "it went through and became a real ball clear of the blue wall", ok && inFront >= Portaball.RadiusMetres + Portaball.EngineTouchMarginMetres - 0.002f && outSpeed > 0.7f
				&& portals.CrossingCount - crossingsBefore == 1,
				$"real ball {ok}, {inFront * 100f:0.0} cm in front of the blue wall, moving out of it at {outSpeed:0.00} m/s; crossings recorded {portals.CrossingCount - crossingsBefore}" );
			if ( ok )
			{
				yield return Until( () => !became.IsValid() || became.Spent, 4f );
				yield return Wait( 0.3f );
			}
			int newOnEntryMouth = Room.Walls[orangeWall].Hits.Count - entryHitsAtStart;
			Check( "still no paint on the wall that was reached into", newOnEntryMouth == 0,
				$"new splats on {Room.Walls[orangeWall].WallName}: {newOnEntryMouth}; all splats since the start {TotalHits() - hitsAtStart} (the landing on the floor paints it)" );
		}
		yield return Relay();

		// ---- 8. the switches ----
		Scene.TimeScale = 0f;
		_heldBall.Enabled = true;
		PlaceHand( wallO, uvO, -0.12f );
		yield return Frames( 2 );
		int on = _source.ActiveTwins;
		twins.HandTwins = false;
		yield return Frames( 2 );
		int off = _source.ActiveTwins;
		twins.HandTwins = true;
		HandPortalRelease.Enabled = false;
		bool handledWhenOff = HandPortalRelease.TryRelease( _hand, Room, O, SboxConvert.ToMetres( _hand.WorldPosition ), -wallO.Normal, spin, flown, gravity, 0.42f, "Proof hand" );
		HandPortalRelease.Enabled = true;
		yield return Frames( 2 );
		Check( "switches: HandTwins off hides them, HandPortalRelease.Enabled off leaves the let-go alone", on == 3 && off == 0 && _source.ActiveTwins == 3 && !handledWhenOff,
			$"twins with the switch on {on}, off {off}, on again {_source.ActiveTwins}; let-go handled with its switch off: {handledWhenOff}" );

		// ---- 9. the head goes through (a VR hand rides the play space). Made up here: the hand is moved exactly as the
		//         play space is moved at a crossing, and the room is told a player crossed, as VrRig tells it. ----
		_source.FollowsPlayer = true;
		_forearm.Enabled = true;
		RigidTransform carriedWithHead = pair.PlayerCrossing( true );
		{
			// (a) a hand that trails 25 cm behind the head
			Vec3 before = PlaceHand( wallO, uvO, 0.25f );
			Rotation turnBefore = _hand.WorldRotation;
			yield return Frames( 2 );
			bool nothingYet = !_source.State.AtPortal && _source.ActiveTwins == 0;
			_hand.WorldPosition = SboxConvert.ToInches( carriedWithHead.TransformPoint( before ) );
			_hand.WorldRotation = SboxConvert.ToEngine( carriedWithHead.TransformRotation( SboxConvert.ToCore( turnBefore ) ) );
			portals.Record( new PortalLink.CrossingRecord { IsPlayer = true, FromOrange = true } );
			yield return Frames( 2 );
			GameObject blockTwin = _source.TwinOf( _block );
			float backWhereItWas = blockTwin.IsValid() ? Distance( blockTwin, before + orange.Normal * twins.SeamShiftMetres ) : 99f;
			float behindBlue = -blue.Wall.SignedDistance( SboxConvert.ToMetres( _hand.WorldPosition ) );
			Check( "the head goes through orange with a hand trailing 25 cm behind: the hand is drawn reaching back through blue", nothingYet && _source.State.Through && !_source.State.FromA
				&& _source.ActiveTwins == 3 && backWhereItWas < 0.001f,
				$"before: at a portal {!nothingYet}; after: the real hand is {behindBlue * 100f:0.0} cm behind the blue wall, through {_source.State.Through} at {( _source.State.FromA ? "orange" : "blue" )}, {_source.ActiveTwins} twins, block twin {backWhereItWas * 1000f:0.000} mm from where the hand was in the orange room" );

			// (b) a hand that leads: 20 cm through orange before the head goes
			Vec3 leading = PlaceHand( wallO, uvO, -0.20f );
			turnBefore = _hand.WorldRotation;
			yield return Frames( 2 );
			GameObject twinBefore = _source.TwinOf( _block );
			bool wasThrough = _source.State.Through && _source.State.FromA && twinBefore.IsValid();
			Vec3 twinWas = twinBefore.IsValid() ? SboxConvert.ToMetres( twinBefore.WorldPosition ) : Vec3.Zero;
			_hand.WorldPosition = SboxConvert.ToInches( carriedWithHead.TransformPoint( leading ) );
			_hand.WorldRotation = SboxConvert.ToEngine( carriedWithHead.TransformRotation( SboxConvert.ToCore( turnBefore ) ) );
			portals.Record( new PortalLink.CrossingRecord { IsPlayer = true, FromOrange = true } );
			yield return Frames( 2 );
			float jump = Distance( _block, twinWas - blue.Normal * twins.SeamShiftMetres );
			Check( "the head goes through with a hand leading 20 cm: the hand is simply in the new room, exactly where its twin was", wasThrough && !_source.State.AtPortal && !_source.Latch.Through
				&& _source.ActiveTwins == 0 && jump < 0.001f,
				$"before: through orange {wasThrough}; after: at a portal {_source.State.AtPortal}, twins {_source.ActiveTwins}, the real hand is {jump * 1000f:0.000} mm from where its twin was drawn" );
		}
		_source.FollowsPlayer = false;

		PlaceHand( wallO, uvO, 1.5f );
		_forearm.Enabled = false;
		Scene.TimeScale = 1f;
		Desk.TourView();
		yield return Wait( 0.6f );   // lets the end the made-up player "went into" open again (PortalView fades it in over a quarter of a second)
	}

	// ------------------------------------------------------------------ the real ThrowHand, driven by the helper's fake source

	/// <summary>
	/// The whole real path with no headset: the helper desk test's stand-in player (HelperDeskRig: real ThrowHand,
	/// poses and grip from the helper's made-up swing) is stood so that its hand lets go 15 cm THROUGH the orange
	/// opening. The throw then comes from ThrowHand.Throw itself, helper samples and all.
	/// </summary>
	private IEnumerator<object> HelperMain()
	{
		Say( "======== THE REAL ThrowHand LETS GO THROUGH A PORTAL (needs PortaballsHelper.exe --fake --port 8443) ========" );
		PortaballsGame game = null;
		foreach ( PortaballsGame g in Scene.GetAllComponents<PortaballsGame>() ) game = g;
		HelperDeskRig rig = game.IsValid() ? game.HelperDesk : null;
		bool have = rig.IsValid() && rig.RightHand.IsValid() && Room.IsValid() && Desk.IsValid();
		Check( "helper desk scene", have, have ? "stand-in player found" : "no HelperDeskRig: load scenes/portaballs_helper_desk.scene first" );
		if ( !have ) yield break;
		ThrowHand hand = rig.RightHand;
		PortalLink portals = Room.Portals;

		// 1. An ordinary throw, nowhere near a portal: it must still go the old way.
		int releasesAtStart = HandPortalRelease.Last.Number;
		int firstThrow = hand.LastReport.Number;
		yield return Until( () => hand.LastReport.Number > firstThrow, 25f );
		bool threw = hand.LastReport.Number > firstThrow;
		Check( "helper: an ordinary throw is untouched", threw && hand.LastReport.FromHelper && HandPortalRelease.Last.Number == releasesAtStart,
			threw ? $"throw #{hand.LastReport.Number} at {hand.LastReport.Speed:0.00} m/s from helper samples: {hand.LastReport.FromHelper}; aim {hand.LastReport.Aim}; portal let-goes so far {HandPortalRelease.Last.Number - releasesAtStart}" : "no throw in 25 s (is the helper running with --fake --port 8443?)" );
		if ( !threw ) yield break;
		Vec3 releasePoint = hand.LastThrow.ReleasePosition;
		Say( $"the stand-in lets go at {V( releasePoint )} m, moving at {V( hand.LastThrow.ReleaseVelocity )} m/s" );

		// 2. Stop the throws while the pair is painted (every blue ball that lands would move the blue patch).
		rig.RightHand.UseHelper = false;
		rig.LeftHand.UseHelper = false;
		yield return Wait( 0.3f );
		Say( SeeThroughProof.Layout( Room, "rightangle" ) );
		yield return Until( () => portals.HasPair, 6f );
		Check( "helper: the two patches link", portals.HasPair, portals.HasPair ? $"pair revision {portals.PairRevision}" : "no pair after 6 s" );
		if ( !portals.HasPair ) yield break;
		yield return Until( () => !AnyPaintMoving(), 16f );
		yield return Wait( 0.3f );
		portals.Sync();
		if ( !portals.HasPair ) { Check( "helper: still linked once the paint settled", false, "the pair came apart" ); yield break; }
		PortalPair pair = portals.Pair;
		PortalEnd orange = pair.A, blue = pair.B;
		int orangeWall = -1, blueWall = -1;
		for ( int i = 0; i < Room.Walls.Count; i++ )
		{
			if ( Room.Walls[i] == portals.WallOf( O ) ) orangeWall = i;
			if ( Room.Walls[i] == portals.WallOf( B ) ) blueWall = i;
		}
		Vec2 uvO = orange.Opening.CenterUV, uvB = blue.Opening.CenterUV;

		// 3. Stand the player so that the same let-go point is 15 cm through the middle of orange.
		Vec3 target = orange.PaintCenter - orange.Normal * 0.15f;
		rig.GameObject.WorldPosition += SboxConvert.ToInches( target - releasePoint );
		Say( $"stand-in player moved by {V( target - releasePoint )} m: its let-go point is now {V( target )} m, 15 cm through ORANGE on {portals.WallOf( O ).WallName}" );
		int orangeWallHits = Room.Walls[orangeWall].Hits.Count;
		int throwsBefore = hand.LastReport.Number;
		int revision = portals.PairRevision;
		Desk.ViewWallPoint( blueWall, uvB.X, uvB.Y, 1.6f, -40f, 12f );
		hand.UseHelper = true;
		yield return Until( () => hand.LastReport.Number > throwsBefore, 15f );
		Scene.TimeScale = 0f;
		threw = hand.LastReport.Number > throwsBefore;
		ThrowReport report = hand.LastReport;
		HandPortalRelease.Outcome outcome = HandPortalRelease.Last;
		Check( "helper: the real ThrowHand let go with its hand through, and the let-go was carried", threw && outcome.Number == releasesAtStart + 1 && outcome.Launch.Carried && outcome.Launch.FromA,
			threw ? $"throw #{report.Number} from helper samples: {report.FromHelper}; ball {-outcome.Launch.Depth * 100f:0.0} cm through orange at the let-go; carried {outcome.Launch.Carried}" : "no throw in 15 s" );
		if ( !threw || outcome.Number != releasesAtStart + 1 ) { Scene.TimeScale = 1f; yield break; }

		Check( "helper: no aim help for a let-go at a portal", report.Aim == AimAssistOutcome.NoTarget && !hand.LastAssist.Applied, $"aim assist outcome {report.Aim}, applied {hand.LastAssist.Applied}" );
		Vec3 handVelocity = hand.LastThrow.ReleaseVelocity;
		RigidTransform crossing = pair.Crossing( true );
		float vErr = ( outcome.Launch.Velocity - crossing.TransformDirection( handVelocity ) ).Length();
		float pErr = ( outcome.Launch.Position - crossing.TransformPoint( hand.LastThrow.ReleasePosition ) ).Length();
		bool real = outcome.Ball.IsValid() || outcome.Mouth.IsValid();
		Vec3 ballAt = outcome.Ball.IsValid() ? outcome.Ball.PositionMetres : outcome.StartPosition;
		Check( "helper: the ball starts on the far side with the mapped velocity", real && vErr < 0.001f && pErr < 0.001f && blue.Wall.SignedDistance( outcome.StartPosition ) > 0f
			&& Vec3.Dot( outcome.Launch.Velocity, blue.Normal ) > 0f && MathF.Abs( outcome.Launch.Velocity.Length() - report.Speed ) < 0.001f,
			$"hand: {V( hand.LastThrow.ReleasePosition )} m at {V( handVelocity )} m/s ({handVelocity.Length():0.000}); far side: {V( outcome.Launch.Position )} m at {V( outcome.Launch.Velocity )} m/s ({outcome.Launch.Velocity.Length():0.000}), {pErr * 1000f:0.000} mm and {vErr:0.0000} m/s from the core's crossing; ball now {blue.Wall.SignedDistance( ballAt ) * 100f:0.0} cm in front of the blue wall, moving out of it at {Vec3.Dot( outcome.Launch.Velocity, blue.Normal ):0.00} m/s; ThrowHand reports {report.Speed:0.000} m/s" );

		PortalTwinSource source = hand.GameObject.GetComponent<PortalTwinSource>();
		yield return Frames( 2 );
		Check( "helper: the stand-in's hand is drawn at both ends", source.IsValid() && source.State.Through && source.ActiveTwins >= 1,
			source.IsValid() ? $"through {source.State.Through}, {-source.State.Depth * 100f:0.0} cm deep, twins on show {source.ActiveTwins}" : "the hand has no PortalTwinSource" );
		yield return Picture( "helper_1_let_go_through_orange_seen_at_blue" );
		Desk.ViewWallPoint( orangeWall, uvO.X, uvO.Y, 1.3f, 40f, 12f );
		yield return Picture( "helper_2_let_go_through_orange_seen_into_orange" );

		// 4. On it flies. Nothing lands on the wall that was reached into.
		hand.UseHelper = false;
		Scene.TimeScale = 1f;
		Portaball ball = outcome.Ball;
		yield return Until( () => !ball.IsValid() || ball.Spent, 5f );
		yield return Wait( 0.4f );
		int newOnOrangeWall = Room.Walls[orangeWall].Hits.Count - orangeWallHits;
		Check( "helper: no burst and no paint on the wall that was reached into", newOnOrangeWall == 0, $"new splats on {Room.Walls[orangeWall].WallName}: {newOnOrangeWall}; pair revision {revision} -> {portals.PairRevision} (a blue ball that lands on another wall moves the blue patch there, as any thrown ball does)" );
		Desk.TourView();
		yield return Wait( 0.2f );
	}
}
