using System.Collections.Generic;
using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// One surface of the room that paint can land on: a wall, and just as well a floor, a ceiling or a slope.
///
/// It owns the core paint simulation for its surface (<see cref="Surface"/>) and the picture of it
/// (<see cref="Panel"/>), and knows which colours' portal patches it holds and their openings.
///
/// WHICH WAY PAINT RUNS comes from the frame alone (core PaintSurface.GravityAlong): down a wall, not at all
/// along a floor or a ceiling. Nothing has to tell it. PAINT MOVES BETWEEN SURFACES here (the tester, 2026-11-19:
/// "pour across the ground as well as it puddles at the bottom"): what runs off the foot of this one is
/// poured onto whichever registered paint surface lies under that point (<see cref="PassOnRunOff"/>,
/// <see cref="TakeRunOff"/>), and a drop that falls off a ceiling becomes a flying droplet. Paint that
/// merely arrived like that is LOOSE PAINT, exactly like a droplet's dab: it is never a hit, never starts,
/// joins, moves or cuts a portal patch, and is wiped when its surface later takes that colour's patch.
///
/// PAINT IS SPREAD OVER FRAMES. A hit (<see cref="Splat"/>) does not paint in the frame it happens:
///   1. the hit waits its turn in the room's queue (<see cref="PortalPatches"/>), which applies the
///      one-patch-per-colour rule when everything thrown before it is on the wall: at once if nothing is
///      on its way, otherwise a frame or more later;
///   2. the splat is then prepared a slice a frame (core PaintSurface.QueueSplat + PaintPacer) and lands
///      whole a few frames later;
///   3. when it has landed (<see cref="LandedCount"/> goes up) the wall asks for the opening search,
///      which is also done in slices; the answer arrives a few frames after that.
/// The room (<see cref="PortalPatches"/>) calls the per-frame steps in order: <see cref="StepPaint"/>,
/// the pacer, <see cref="AfterPacer"/>, <see cref="UploadPaint"/>.
///
/// Reading the result: <see cref="GetOpening"/> / <see cref="HoldsPatch"/> here, or better the room-wide
/// record <see cref="PortalPatches"/> (<c>room.Patches.Get( colour )</c>), which also says which wall.
/// </summary>
public sealed class PaintWall : Component
{
	/// <summary>One recorded hit, kept so tests can read them back.</summary>
	public struct SplatHit
	{
		public PaintColor Paint;
		public Vec2 UV;
		public float Radius;
		public Vec2 WallVelocity;
		public uint Seed;
		public float NormalSpeed;
		/// <summary>When the ball hit.</summary>
		public double Time;
		/// <summary>What the one-patch-per-colour rule said. Only meaningful once <see cref="Decided"/>.</summary>
		public PatchDecision Decision;
		/// <summary>The rule has been applied and the splat is being prepared (at the hit, or a frame or more after it).</summary>
		public bool Decided;
		/// <summary>The paint is on the wall (a few frames after the hit).</summary>
		public bool Landed;
		public double LandedTime;
	}

	/// <summary>
	/// The wall's own 2D coordinates as seen from INSIDE the room, in metres (see core WallFrame.cs):
	/// Origin is the lower-left corner, U runs to the viewer's right, V runs up, Normal points into the room.
	/// </summary>
	public WallFrame Frame { get; private set; }

	/// <summary>Name used in the log, for example "Wall +X".</summary>
	public string WallName { get; private set; } = "Wall";

	/// <summary>
	/// Old placeholder: a flat coloured tile on the wall at each hit. Off now that real paint is drawn;
	/// tick it to see exactly where and how big each hit was reported.
	/// </summary>
	[Property] public bool ShowDebugMarkers { get; set; } = false;

	/// <summary>While paint is moving, look for the portal openings at most this often, seconds.</summary>
	[Property] public float OpeningSearchInterval { get; set; } = 0.1f;

	/// <summary>How far in front of the wall the paint panel sits, metres.</summary>
	public const float PanelOffsetMetres = 0.003f;

	/// <summary>The core paint simulation for this wall (also the core IPaintMask the portal code wants).</summary>
	public PaintSurface Surface { get; private set; }

	/// <summary>The core opening finder for this wall's paint. Its patch labels are what GapToPatch reads.</summary>
	public OpeningFinder Finder { get; private set; }

	/// <summary>The panel and texture that show the paint.</summary>
	public PaintPanel Panel { get; private set; }

	/// <summary>The room's record of the two patches. Set by TestRoom. Without it every splat simply adds paint.</summary>
	public PortalPatches Patches { get; set; }

	/// <summary>Every hit so far, oldest first. A hit is listed at once; its paint lands a few frames later (see <see cref="SplatHit.Landed"/>).</summary>
	public IReadOnlyList<SplatHit> Hits => _hits;

	/// <summary>How many of <see cref="Hits"/> have their paint on the wall.</summary>
	public int LandedCount { get; private set; }

	/// <summary>True while a hit is still on its way to the wall or drips are still running on it.</summary>
	public bool IsPaintMoving => Surface != null && ( _hitsWaiting > 0 || !Surface.IsSettled );

	/// <summary>Goes up each time the wall is wiped, so a queued hit from before the wipe can be told apart.</summary>
	public int WipeCount { get; private set; }

	private readonly List<SplatHit> _hits = new List<SplatHit>();
	private readonly Queue<int> _landing = new Queue<int>();          // hits handed to the core, in the order they will land
	private int _hitsWaiting;                                         // hits not yet handed to the core
	private int _landedSeen;                                          // Surface.SplatsLanded as last looked at

	// Per colour (index = (int)PaintColor).
	private readonly bool[] _holds = new bool[2];                      // that colour's patch has paint on this wall
	private readonly Vec2[] _seed = new Vec2[2];                       // that colour's latest landed hit here
	private readonly PortalOpening[] _opening = new PortalOpening[2];  // latest answer from the finder
	private readonly bool[] _searched = new bool[2];                   // the finder has answered since the patch started
	private readonly int[] _finishedSeen = new int[2];                 // Finder.SearchesFinished as last looked at
	private readonly bool[] _loosePaint = new bool[2];                 // loose paint of that colour lies here: droplet specks, puddles of run-off

	private bool _searchPending;      // the paint changed since the last search
	private bool _lastLook;           // a search was asked for after the paint settled: its answer is the final one
	private float _sinceSearch;
	private float _ghostAlpha;
	private float _ghostFadeSeconds;
	private PaintPacer _ownPacer;     // only for a wall with no room record

	/// <summary>Called once by TestRoom when it builds the wall.</summary>
	public void Setup( string wallName, WallFrame frame )
	{
		WallName = wallName;
		Frame = frame;

		Surface = new PaintSurface( frame );
		Finder = new OpeningFinder( Surface );
		Panel = new PaintPanel( Scene, wallName + " paint", frame, Surface, PanelOffsetMetres );
		_opening[0].Color = PaintColor.Orange;
		_opening[1].Color = PaintColor.Blue;

		// The droplets that bounce off a burst: made now, while loading, so the first burst does not pay for it.
		PaintDroplets.Ensure( Scene );
	}

	// ------------------------------------------------------------------ reading

	/// <summary>True when that colour's portal patch has paint on this wall right now.</summary>
	public bool HoldsPatch( PaintColor color )
	{
		return _holds[(int)color];
	}

	/// <summary>
	/// The latest opening found for a colour on this wall (core PortalOpening, wall metres). Valid is false
	/// when this wall does not hold that colour's patch or the patch is too small to step through.
	/// Its Revision goes up whenever the patch's shape changes.
	/// </summary>
	public PortalOpening GetOpening( PaintColor color )
	{
		return _opening[(int)color];
	}

	/// <summary>Where that colour's latest ball landed on this wall, wall metres: the seed of its patch.</summary>
	public Vec2 PatchSeed( PaintColor color )
	{
		return _seed[(int)color];
	}

	// ------------------------------------------------------------------ painting

	/// <summary>
	/// A paint ball burst on this wall. Parameters match core PaintSurface.QueueSplat exactly. Nothing is
	/// painted in this call: the hit joins the room's queue, the one-patch-per-colour rule is applied when
	/// its turn comes (in this call when nothing earlier is still on its way), and the paint lands a few
	/// frames later.
	/// </summary>
	/// <param name="color">Paint colour.</param>
	/// <param name="uv">Impact point in wall metres (from Frame.Origin, along U and V).</param>
	/// <param name="radius">Size of the splat body in metres.</param>
	/// <param name="wallVelocity">The ball's velocity along the wall at impact (m/s along U and V).</param>
	/// <param name="seed">Same seed, same splat.</param>
	/// <param name="normalSpeed">Speed into the wall in m/s; negative means "use the core's default".</param>
	public void Splat( PaintColor color, Vec2 uv, float radius, Vec2 wallVelocity, uint seed, float normalSpeed = -1f )
	{
		SplatHit hit = new SplatHit();
		hit.Paint = color;
		hit.UV = uv;
		hit.Radius = radius;
		hit.WallVelocity = wallVelocity;
		hit.Seed = seed;
		hit.NormalSpeed = normalSpeed;
		hit.Time = Time.NowDouble;
		_hits.Add( hit );
		_hitsWaiting++;

		if ( Patches.IsValid() ) Patches.QueueHit( this, _hits.Count - 1 );
		else StartSplat( _hits.Count - 1, PatchDecision.AddToExisting );
	}

	/// <summary>
	/// The hit's turn has come and the rule has been applied (by <see cref="PortalPatches"/>): start preparing
	/// its splat. It lands a few frames from now.
	/// </summary>
	public void StartSplat( int hitIndex, PatchDecision decision )
	{
		SplatHit hit = _hits[hitIndex];
		hit.Decision = decision;
		hit.Decided = true;
		_hits[hitIndex] = hit;
		_hitsWaiting--;

		// This wall is taking that colour's patch from elsewhere (or starting it): wipe the specks droplets left
		// here in that colour first, so the patch's wall only ever holds the patch's own paint. Every bit of a
		// colour on its portal's wall counts as portal (for balls and for the see-through view), so a leftover
		// speck would otherwise be a tiny extra portal. The room only starts a splat when none is on its way here.
		int c = (int)hit.Paint;
		if ( decision != PatchDecision.AddToExisting && !_holds[c] && _loosePaint[c] )
		{
			Surface.EraseColor( hit.Paint );
			_loosePaint[c] = false;
			Say( $"[PaintWall] {WallName}: loose {hit.Paint} paint (droplet specks, run-off puddles) wiped: this surface takes the {hit.Paint} patch." );
		}

		int before = Surface.QueuedSplatCount;
		Surface.QueueSplat( hit.Paint, hit.UV, hit.Radius, hit.WallVelocity, hit.Seed, hit.NormalSpeed );
		if ( Surface.QueuedSplatCount > before ) _landing.Enqueue( hitIndex );
		else Landed( hitIndex );   // the core took nothing (a splat with no size): there is nothing to wait for
	}

	/// <summary>
	/// Distance in metres from a point on this wall to the nearest paint of that colour's patch, or
	/// float.MaxValue when the patch is further away than <paramref name="reach"/> (or not on this wall).
	/// 0 means the point is on the patch.
	///
	/// It reads the patch found by the latest finished opening search and does no searching itself:
	/// <see cref="PortalPatches"/> asks for a fresh search and waits for it before calling this.
	/// </summary>
	public float GapToPatch( PaintColor color, Vec2 uv, float reach )
	{
		int c = (int)color;
		if ( !_holds[c] ) return float.MaxValue;

		float su = Surface.TexelSizeU, sv = Surface.TexelSizeV;
		int x0 = System.Math.Max( 0, (int)System.MathF.Floor( ( uv.X - reach ) / su ) );
		int x1 = System.Math.Min( Surface.Width - 1, (int)System.MathF.Floor( ( uv.X + reach ) / su ) );
		int y0 = System.Math.Max( 0, (int)System.MathF.Floor( ( uv.Y - reach ) / sv ) );
		int y1 = System.Math.Min( Surface.Height - 1, (int)System.MathF.Floor( ( uv.Y + reach ) / sv ) );

		float best = float.MaxValue;
		for ( int y = y0; y <= y1; y++ )
		{
			for ( int x = x0; x <= x1; x++ )
			{
				if ( !Finder.InLastPatch( color, x, y ) ) continue;
				// Nearest point of this texel's square to the hit.
				float dx = System.MathF.Max( 0f, System.MathF.Max( x * su - uv.X, uv.X - ( x + 1 ) * su ) );
				float dy = System.MathF.Max( 0f, System.MathF.Max( y * sv - uv.Y, uv.Y - ( y + 1 ) * sv ) );
				float d = System.MathF.Sqrt( dx * dx + dy * dy );
				if ( d < best ) best = d;
			}
		}
		return best <= reach ? best : float.MaxValue;
	}

	/// <summary>
	/// Take that colour's paint off this wall, all of it, drips included. The paint itself goes at once
	/// (so openings are right immediately); what you see fades out over <paramref name="fadeSeconds"/>.
	/// Called by <see cref="PortalPatches"/> when the colour's patch is replaced, and only while no splat
	/// is on its way to this wall (the core would have to land it on the spot first).
	/// </summary>
	public void RemovePatch( PaintColor color, float fadeSeconds )
	{
		int c = (int)color;

		if ( fadeSeconds > 0.01f )
		{
			Panel.StartGhost( Surface, color );   // a picture of the paint as it is, before it goes
			_ghostAlpha = 1f;
			_ghostFadeSeconds = fadeSeconds;
		}

		Surface.EraseColor( color );   // the colour everywhere on this wall, and its drips

		_holds[c] = false;
		_loosePaint[c] = false;      // droplet specks of that colour went too
		bool wasValid = _opening[c].Valid;
		// Finds nothing now, so it does no real work: marks the opening gone and bumps its Revision.
		_opening[c] = Finder.Find( color, _seed[c] );
		_finishedSeen[c] = Finder.SearchesFinished( color );
		_searched[c] = false;
		_searchPending = true;
		_lastLook = false;
		_sinceSearch = OpeningSearchInterval;

		Say( $"[PaintWall] {WallName}: {color} patch removed{( wasValid ? " (its opening is closed)" : "" )}." );
	}

	// ------------------------------------------------------------------ droplets

	/// <summary>Droplets that left a small splat on this wall, and droplets that soaked in and left nothing.</summary>
	public int DropletsPainted { get; private set; }
	public int DropletsSoakedIn { get; private set; }

	/// <summary>
	/// A droplet thrown off a burst (see <see cref="PaintDroplets"/>; the tester's request, 2026-11-19) hit this wall. It
	/// paints a small splat with the core's own splat stamp, exactly as a ball's but small (core SplatFeel), and it
	/// lands at once: it is tiny, and nothing else can be on its way here (see below).
	///
	/// A droplet is NOT a hit. It is not in <see cref="Hits"/>, the one-patch-per-colour rule never sees it, and it
	/// never starts, joins, moves or replaces a portal patch or asks for an opening search. So that it cannot change
	/// a patch either, it only paints bare wall, and soaks in (leaves nothing) when:
	///   - this wall holds, or is about to take, its own colour's patch: every bit of a colour on its portal's wall
	///     counts as portal, so a speck there would be a tiny extra portal;
	///   - a ball's splat is on its way to this wall (the paint here is not final yet);
	///   - any paint of either colour, a running drip, or a waiting splat is within its splat's reach plus
	///     SplatFeel.DropletClearMargin (core PaintSurface.IsClearForSplat). Its paint never runs (it is smaller
	///     than PaintSettings.DripMinSplatRadius), so it stays where it lands.
	/// Specks of a colour are wiped when this wall later takes that colour's patch (see <see cref="StartSplat"/>).
	/// Returns true when it painted.
	/// </summary>
	public bool SplatDroplet( PaintColor color, Vec2 uv, float radius, Vec2 wallVelocity, uint seed, float normalSpeed )
	{
		int c = (int)color;
		bool clear = Surface != null && radius > 0f && Frame.Contains( uv )
			&& !_holds[c]
			&& !( Patches.IsValid() && Patches.Get( color ).Exists && Patches.Get( color ).Wall == this )
			&& _hitsWaiting == 0 && _landing.Count == 0 && Surface.QueuedSplatCount == 0
			&& Surface.IsClearForSplat( uv, radius, wallVelocity, normalSpeed, SplatFeel.DropletClearMargin );
		if ( !clear )
		{
			DropletsSoakedIn++;
			return false;
		}

		Surface.Splat( color, uv, radius, wallVelocity, seed, normalSpeed );
		// That landing was the droplet's own: no ball's splat was on its way here, so nothing is waiting to be told.
		_landedSeen = Surface.SplatsLanded;
		_loosePaint[c] = true;
		DropletsPainted++;
		return true;
	}

	// ------------------------------------------------------------------ paint moving from one surface to the next

	/// <summary>Streams of run-off this surface has taken as puddles, and refused (it holds that colour's patch).</summary>
	public int RunOffTaken { get; private set; }
	public int RunOffRefused { get; private set; }

	/// <summary>Paint taken as run-off so far, cubic metres.</summary>
	public double RunOffTakenVolume { get; private set; }

	/// <summary>Streams this surface handed to the one below, streams that found nothing below, and drops that fell off it (a ceiling).</summary>
	public int RunOffPassedOn { get; private set; }
	public int RunOffLost { get; private set; }
	public int DropsFallen { get; private set; }

	/// <summary>How far out from this surface the point under its foot is looked for, and how far off the other surface's plane it may be, metres.</summary>
	public const float RunOffStepOutMetres = 0.03f;
	public const float RunOffPlaneGapMetres = 0.08f;

	private int _surfacesSeen = -1;      // Patches.WallCount when "what lies below" was last worked out
	private bool _runOffSaid;            // the log line for this splat's run-off has been written

	/// <summary>
	/// The paint surface under a point at this surface's foot: a registered surface (PortalPatches.AddWall) that
	/// faces upward, whose plane passes within <see cref="RunOffPlaneGapMetres"/> of the point and whose rectangle
	/// holds it. Null when there is none (then nothing is handed over and the paint pools at the foot).
	/// </summary>
	public PaintWall SurfaceUnder( Vec3 worldPoint )
	{
		if ( !Patches.IsValid() ) return null;
		// A step out from this surface, so the point is ON the other one and not on its very edge.
		Vec3 p = worldPoint + Frame.Normal * RunOffStepOutMetres;
		PaintWall best = null;
		float bestGap = RunOffPlaneGapMetres;
		for ( int i = 0; i < Patches.WallCount; i++ )
		{
			PaintWall w = Patches.WallAt( i );
			if ( !w.IsValid() || w == this || w.Surface == null ) continue;
			if ( w.Surface.GravityOff > -0.3f ) continue;   // paint only pours onto something it can lie on
			float gap = System.MathF.Abs( w.Frame.SignedDistance( p ) );
			if ( gap > bestGap ) continue;
			Vec2 uv = w.Frame.ToUV( p );
			const float slack = 0.05f;
			if ( uv.X < -slack || uv.Y < -slack || uv.X > w.Frame.Width + slack || uv.Y > w.Frame.Height + slack ) continue;
			best = w;
			bestGap = gap;
		}
		return best;
	}

	// Does a surface lie under the edge paint runs toward (a floor under a wall)? Worked out again whenever the
	// room's list of paint surfaces changes, so a floor added later starts taking run-off by itself.
	private void FindWhatLiesBelow()
	{
		int count = Patches.IsValid() ? Patches.WallCount : 0;
		if ( count == _surfacesSeen ) return;
		_surfacesSeen = count;
		bool below = false;
		if ( Surface.PaintRuns && count > 0 )
		{
			// From the middle of the surface, along the way paint runs, to the edge.
			Vec2 d = Surface.RunDirection;
			Vec2 c = new Vec2( Frame.Width * 0.5f, Frame.Height * 0.5f );
			float tu = d.X > 1e-4f ? c.X / d.X : ( d.X < -1e-4f ? c.X / -d.X : float.MaxValue );
			float tv = d.Y > 1e-4f ? c.Y / d.Y : ( d.Y < -1e-4f ? c.Y / -d.Y : float.MaxValue );
			Vec2 edge = c + d * System.MathF.Min( tu, tv );
			below = SurfaceUnder( Frame.ToWorld( edge ) ).IsValid();
		}
		if ( below != Surface.HandsOverRunOff )
		{
			Surface.HandsOverRunOff = below;
			Say( $"[PaintWall] {WallName}: {( below ? "a paint surface lies under its foot: paint that reaches the bottom pours onto it" : "nothing to take paint lies under its foot: paint that reaches the bottom pools there" )}." );
		}
	}

	/// <summary>
	/// Frame step 1b: what left this surface in the step just done (core PaintSurface.GetRunOff). Run-off over the
	/// foot is poured onto the surface under that point; a drop off a ceiling is let go as a flying droplet.
	/// </summary>
	private void PassOnRunOff()
	{
		for ( int i = 0; i < Surface.RunOffCount; i++ )
		{
			PaintRunOff r = Surface.GetRunOff( i );
			if ( r.Fell )
			{
				// Its dab: the size its paint would make at the usual film (the droplets keep it small).
				float dab = System.MathF.Sqrt( r.Volume / ( System.MathF.PI * System.MathF.Max( 1e-5f, Surface.Settings.SplatThickness ) ) );
				float draw = SplatFeel.CeilingDropDrawRadius;
				PaintDroplets.Drop( Scene, r.Color, Frame.ToWorld( r.UV, draw + 0.006f ), dab, r.Seed );
				DropsFallen++;
				continue;
			}

			Vec3 at = Frame.ToWorld( r.UV );
			PaintWall below = SurfaceUnder( at );
			bool taken = below.IsValid() && below.TakeRunOff( r.Color, at, r.Volume, r.HalfWidth, r.Seed );
			if ( taken ) RunOffPassedOn++;
			else RunOffLost++;
			if ( !_runOffSaid )
			{
				_runOffSaid = true;
				Say( $"[PaintWall] {WallName}: {r.Color} paint reached the foot at u={r.UV.X:0.00} m and {( taken ? $"pours onto {below.WallName}" : below.IsValid() ? $"was refused by {below.WallName} (it holds the {r.Color} patch)" : "found nothing below" )}." );
			}
		}
	}

	/// <summary>
	/// Paint that ran off another surface arrives here (see <see cref="PassOnRunOff"/>): it grows a puddle from
	/// that point (core PaintSurface.Pour), limited by how much paint arrived.
	///
	/// Like a droplet's dab it is LOOSE PAINT, not a hit: the one-patch-per-colour rule never sees it and no
	/// opening search is asked for. It is refused outright when this surface holds, or is about to take, its own
	/// colour's patch (every bit of a colour on its portal's surface counts as portal), and the core only ever
	/// lets it cover bare surface and its own colour, so it can never cut into the other colour's patch either.
	/// What it leaves is wiped when this surface later takes that colour's patch (see <see cref="StartSplat"/>).
	/// Returns true when it was taken.
	/// </summary>
	public bool TakeRunOff( PaintColor color, Vec3 worldPoint, float volume, float halfWidth, uint seed )
	{
		int c = (int)color;
		bool ok = Surface != null && volume > 0f
			&& !_holds[c]
			&& !( Patches.IsValid() && Patches.Get( color ).Exists && Patches.Get( color ).Wall == this );
		if ( !ok || !Surface.Pour( color, Frame.ToUV( worldPoint ), volume, halfWidth, seed ) )
		{
			RunOffRefused++;
			return false;
		}
		_loosePaint[c] = true;
		RunOffTaken++;
		RunOffTakenVolume += volume;
		return true;
	}

	/// <summary>Wipe this wall completely: both colours, drips, openings, and any hit still on its way.</summary>
	public void ClearPaint()
	{
		Surface.Clear();   // also forgets splats still being prepared
		PaintDroplets.ClearIn( Scene );   // and droplets still flying at the room's walls
		WipeCount++;
		_landing.Clear();
		_hitsWaiting = 0;
		_landedSeen = Surface.SplatsLanded;
		LandedCount = 0;
		for ( int c = 0; c < 2; c++ )
		{
			_holds[c] = false;
			_loosePaint[c] = false;
			_opening[c] = Finder.Find( (PaintColor)c, _seed[c] );
			_finishedSeen[c] = Finder.SearchesFinished( (PaintColor)c );
			_searched[c] = false;
		}
		_ghostAlpha = 0f;
		Panel.SetGhostAlpha( 0f );
		_searchPending = false;
		_lastLook = false;
		_hits.Clear();
	}

	// ------------------------------------------------------------------ every frame

	/// <summary>
	/// Frame step 1: sag, drips, puddles and drying, then pass on whatever ran off this surface. Nothing to do once
	/// the surface is dry. Returns true when it did work.
	/// </summary>
	public bool StepPaint( float dt )
	{
		if ( Surface == null || !( dt > 0f ) ) return false;
		FindWhatLiesBelow();
		if ( Surface.IsDry ) return false;
		Surface.Step( dt );
		if ( Surface.RunOffCount > 0 ) PassOnRunOff();
		if ( Surface.Time >= StopClockAt )
		{
			StopClockAt = double.MaxValue;
			Scene.TimeScale = 0f;
		}
		return true;
	}

	/// <summary>
	/// For picture scripts (engine\paint_feel_capture.ps1): stop the game's clock when this surface's own paint
	/// clock (core PaintSurface.Time, which only runs while its paint is wet) reaches this value, so a picture
	/// shows the paint at an exact moment. Used once, then it is switched off again. Start the clock again with
	/// Scene.TimeScale = 1.
	/// </summary>
	public double StopClockAt { get; set; } = double.MaxValue;

	/// <summary>
	/// Frame step 3, after the pacer has done its slice of work: notice splats that have just landed and
	/// ask for their openings, and take in any opening search that has just finished.
	/// </summary>
	public void AfterPacer( float dt )
	{
		if ( Surface == null ) return;

		// Splats land in the order they were handed over.
		int landed = Surface.SplatsLanded;
		while ( _landedSeen != landed && _landing.Count > 0 )
		{
			_landedSeen++;
			Landed( _landing.Dequeue() );
		}
		_landedSeen = landed;

		// Answers.
		for ( int c = 0; c < 2; c++ )
		{
			int finished = Finder.SearchesFinished( (PaintColor)c );
			if ( finished == _finishedSeen[c] ) continue;
			_finishedSeen[c] = finished;
			if ( _holds[c] ) TakeAnswer( (PaintColor)c, Finder.Last( (PaintColor)c ) );
		}

		// While the paint is changing, look again every so often; one last look once it has stopped.
		if ( !_searchPending ) return;
		if ( _lastLook )
		{
			if ( !Surface.IsSettled ) _lastLook = false;   // more paint arrived meanwhile
			else if ( !SearchWaiting() )
			{
				_searchPending = false;   // that was the last look: the shape cannot change by itself any more
				_lastLook = false;
				LogSettled();
			}
			return;
		}
		_sinceSearch += dt;
		if ( _sinceSearch >= OpeningSearchInterval )
		{
			_sinceSearch = 0f;
			_lastLook = Surface.IsSettled;
			RequestSearches();
		}
	}

	/// <summary>Frame step 4: send the changed texels to the graphics card, only if something changed. Returns true when it did.</summary>
	public bool UploadPaint()
	{
		return Surface != null && Panel.UploadChanges( Surface );
	}

	protected override void OnUpdate()
	{
		if ( Surface == null ) return;
		float dt = Time.Delta;

		// A wall in a room is driven by the room (PortalPatches), which shares one work budget between
		// the walls. A wall on its own does the same steps here.
		if ( !Patches.IsValid() && dt > 0f )
		{
			if ( _ownPacer == null )
			{
				_ownPacer = new PaintPacer();
				_ownPacer.Add( Surface, Finder );
			}
			StepPaint( dt );
			_ownPacer.AdvanceFor( PortalPatches.DefaultBudgetMs );
			AfterPacer( dt );
			UploadPaint();
		}

		// The fading picture of a replaced patch.
		if ( _ghostAlpha > 0f && dt > 0f )
		{
			_ghostAlpha -= dt / System.MathF.Max( 0.01f, _ghostFadeSeconds );
			if ( _ghostAlpha < 0f ) _ghostAlpha = 0f;
			// Ease out, so it thins quickly at first and the last trace lingers a moment.
			Panel.SetGhostAlpha( _ghostAlpha * _ghostAlpha );
		}
	}

	// A hit's paint is on the wall now. Everything that reacts to new paint starts here.
	private void Landed( int hitIndex )
	{
		SplatHit hit = _hits[hitIndex];
		hit.Landed = true;
		hit.LandedTime = Time.NowDouble;
		_hits[hitIndex] = hit;
		LandedCount++;

		int c = (int)hit.Paint;
		_holds[c] = true;
		_runOffSaid = false;
		_seed[c] = hit.UV;
		if ( hit.Decision != PatchDecision.AddToExisting ) _searched[c] = false;   // a fresh patch: its first answer is always news
		_searchPending = true;
		_lastLook = false;
		_sinceSearch = 0f;
		RequestSearches();   // both colours: new paint of one colour cuts into the other

		Say( $"[PaintWall] {WallName}: {hit.Paint} splat at u={hit.UV.X:0.00} v={hit.UV.Y:0.00} m, radius {hit.Radius:0.00} m, along wall ({hit.WallVelocity.X:0.0}, {hit.WallVelocity.Y:0.0}) m/s, into wall {hit.NormalSpeed:0.0} m/s, seed {hit.Seed}, on wall {Frame.Contains( hit.UV )}, patch: {hit.Decision}, landed {( hit.LandedTime - hit.Time ) * 1000.0:0} ms after the hit" );

		if ( ShowDebugMarkers )
		{
			AddDebugMarker( hit.Paint, hit.UV, hit.Radius );
		}
	}

	/// <summary>
	/// Ask for a fresh opening search of one colour's patch here (done in slices by the pacer). The room uses
	/// this before measuring the gap to a new hit. Costs next to nothing when the patch has not changed.
	/// </summary>
	public void RequestSearch( PaintColor color )
	{
		if ( _holds[(int)color] ) Finder.Request( color, _seed[(int)color] );
	}

	/// <summary>True while a search asked for that colour has not finished: the patch GapToPatch reads may be out of date.</summary>
	public bool SearchWaitingFor( PaintColor color )
	{
		return _holds[(int)color] && Finder.IsPending( color );
	}

	private void RequestSearches()
	{
		for ( int c = 0; c < 2; c++ )
		{
			if ( _holds[c] ) Finder.Request( (PaintColor)c, _seed[c] );
		}
	}

	private bool SearchWaiting()
	{
		return ( _holds[0] && Finder.IsPending( PaintColor.Orange ) ) || ( _holds[1] && Finder.IsPending( PaintColor.Blue ) );
	}

	// A finished search's answer for one colour: keep it and pass on any change.
	private void TakeAnswer( PaintColor color, PortalOpening now )
	{
		int c = (int)color;
		PortalOpening before = _opening[c];
		bool first = !_searched[c];
		_searched[c] = true;
		_opening[c] = now;

		if ( !first && now.Revision == before.Revision && now.Valid == before.Valid ) return;

		if ( Patches.IsValid() ) Patches.ReportOpening( this, color, now );

		if ( now.Valid && ( first || !before.Valid ) )
		{
			Vec2 size = now.BoundsMax - now.BoundsMin;
			Say( $"[PaintWall] {WallName}: {color} OPENING VALID: centre u={now.CenterUV.X:0.00} v={now.CenterUV.Y:0.00} m, fits a circle of radius {now.InscribedRadius:0.00} m, patch {size.X:0.00} x {size.Y:0.00} m, area {now.Area:0.00} m2, revision {now.Revision}" );
		}
		else if ( !now.Valid && !first && before.Valid )
		{
			Say( $"[PaintWall] {WallName}: {color} opening no longer valid (largest circle {now.InscribedRadius:0.00} m), revision {now.Revision}" );
		}
	}

	private void LogSettled()
	{
		for ( int c = 0; c < 2; c++ )
		{
			if ( !_holds[c] ) continue;
			PortalOpening o = _opening[c];
			Vec2 size = o.BoundsMax - o.BoundsMin;
			Say( $"[PaintWall] {WallName}: {(PaintColor)c} paint settled: opening valid {o.Valid}, centre u={o.CenterUV.X:0.00} v={o.CenterUV.Y:0.00} m, circle radius {o.InscribedRadius:0.00} m, patch {size.X:0.00} x {size.Y:0.00} m from v={o.BoundsMin.Y:0.00} to v={o.BoundsMax.Y:0.00}, revision {o.Revision}" );
		}
	}

	// Paint's log lines go out one a frame (see PortalPatches.Say): writing one costs far more than it looks.
	private void Say( string line )
	{
		if ( Patches.IsValid() ) Patches.Say( line );
		else Log.Info( line );
	}

	// A thin square tile lying flat on the wall. It has no collider, so later balls still reach the wall.
	private void AddDebugMarker( PaintColor color, Vec2 uv, float radius )
	{
		const float thickness = 0.01f;   // metres

		GameObject marker = new GameObject( Scene, false, "Splat Marker (placeholder)" );
		marker.WorldPosition = SboxConvert.ToInches( Frame.ToWorld( uv, thickness * 0.5f ) );
		// LookAt makes the object's own X axis point out of the wall, so X is the tile's thin side.
		marker.WorldRotation = Rotation.LookAt( SboxConvert.ToEngine( Frame.Normal ), SboxConvert.ToEngine( Frame.V ) );
		marker.LocalScale = SboxConvert.BoxScaleFor( new Vec3( thickness, radius * 2f, radius * 2f ) );

		ModelRenderer renderer = marker.AddComponent<ModelRenderer>();
		renderer.Model = Model.Cube;
		renderer.Tint = Portaball.TintFor( color );

		marker.Enabled = true;
	}
}
