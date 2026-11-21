using System.Collections.Generic;
using System.Diagnostics;
using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// The room's record of the two portal patches: where the orange one is and where the blue one is.
/// One per room, on the room's object (<see cref="TestRoom.Patches"/>). A patch can be on any paint
/// surface: a wall, the floor or the ceiling (each is a <see cref="PaintWall"/>; "wall" below means any of them).
///
/// THE PORTAL JOB READS THIS:
///   <c>room.Patches.Get( PaintColor.Orange )</c> gives a <see cref="Patch"/>:
///     Exists    the colour has a patch on a wall (its paint may still be a few frames from landing)
///     Wall      which wall (its <c>Frame</c> is the core WallFrame, its <c>Surface</c> the core paint mask)
///     Opening   the core PortalOpening: Valid, CenterUV, InscribedRadius, BoundsMin/Max, Area (wall metres)
///     Revision  goes up every time anything above changes, so a reader can tell when to refresh
///   <c>room.Patches.TryGetOpening( colour, out wall, out opening )</c> is true only when the patch is big
///   enough to be a portal (Opening.Valid).
///
/// It also runs the room's paint each frame, in this order, and keeps the heavy part inside a time budget
/// (<see cref="BudgetMs"/>) so no single frame is hurt:
///   1. every wall steps its drips;
///   2. hits waiting in the queue get the one-patch-per-colour rule applied, in the order they happened
///      (the rule itself lives in <see cref="PortalPatchPolicy"/>); a hit that need not wait for earlier
///      paint is dealt with in the hit call itself, before any frame passes;
///   3. the core PaintPacer does a slice of the waiting work: splats being prepared first, then opening searches;
///   4. every wall notices what has just landed or been found, and sends its changed texels to the graphics card;
///   5. one waiting log line is written (<see cref="Say"/>).
/// </summary>
public sealed class PortalPatches : Component
{
	/// <summary>One colour's patch.</summary>
	public struct Patch
	{
		public bool Exists;
		public PaintWall Wall;
		public PortalOpening Opening;
		public int Revision;
	}

	/// <summary>Milliseconds a frame the paint's own processor work may take before the texture is sent.</summary>
	public const double DefaultBudgetMs = 1.0;

	/// <summary>
	/// The time budget, ms a frame, for the paint's processor work: drips and deciding hits come first and
	/// are never cut short; whatever is left of the budget goes to the spread-out work (preparing splats,
	/// searching for openings), which always gets at least one small piece done. About 1 is right for a
	/// headset. A huge number does everything in the frame it is asked for, as the game did before the
	/// work was spread out.
	/// </summary>
	public double BudgetMs { get; set; } = DefaultBudgetMs;

	/// <summary>Write one line every few seconds with what the paint cost per frame, while paint is active.</summary>
	[Property] public bool LogTimings { get; set; }

	private readonly Patch[] _patch = new Patch[2];
	private readonly List<PaintWall> _walls = new List<PaintWall>();
	private readonly PaintPacer _pacer = new PaintPacer();

	/// <summary>The patch of one colour (a copy; ask again after Revision changes).</summary>
	public Patch Get( PaintColor color )
	{
		return _patch[(int)color];
	}

	/// <summary>True when that colour has a patch big enough to be a portal opening.</summary>
	public bool TryGetOpening( PaintColor color, out PaintWall wall, out PortalOpening opening )
	{
		Patch p = _patch[(int)color];
		wall = p.Wall;
		opening = p.Opening;
		return p.Exists && p.Wall.IsValid() && p.Opening.Valid;
	}

	/// <summary>True when both colours have a valid opening, so a portal could link them.</summary>
	public bool BothOpen
	{
		get { return TryGetOpening( PaintColor.Orange, out _, out _ ) && TryGetOpening( PaintColor.Blue, out _, out _ ); }
	}

	/// <summary>Called by TestRoom for each wall it builds: the wall's paint is run from here from now on.</summary>
	public void AddWall( PaintWall wall )
	{
		wall.Patches = this;
		_walls.Add( wall );
		_pacer.Add( wall.Surface, wall.Finder );
	}

	/// <summary>
	/// Every paint surface added so far (walls, and a floor or ceiling once they are added), in the order added.
	/// Run-off and droplets find the surface they land on through this list (PaintWall.SurfaceUnder, PaintDroplets).
	/// </summary>
	public int WallCount => _walls.Count;

	public PaintWall WallAt( int index )
	{
		return _walls[index];
	}

	// ------------------------------------------------------------------ hits waiting their turn

	private struct WaitingHit
	{
		public PaintWall Wall;
		public int HitIndex;      // into Wall.Hits
		public int WipeCount;     // the wall's WipeCount when the hit happened
		public bool LookAsked;    // a fresh look at the colour's patch has been asked for
	}

	private readonly List<WaitingHit> _waiting = new List<WaitingHit>();

	/// <summary>Hits that have happened but whose splat has not been started yet.</summary>
	public int WaitingHits => _waiting.Count;

	/// <summary>
	/// Called by a wall when a ball bursts on it. The hit is dealt with in turn: at once, inside this call,
	/// when nothing thrown before it is still on its way (so a hit that replaces a patch on another wall
	/// closes the old opening immediately, as it always did); otherwise in a later frame.
	/// </summary>
	public void QueueHit( PaintWall wall, int hitIndex )
	{
		_waiting.Add( new WaitingHit { Wall = wall, HitIndex = hitIndex, WipeCount = wall.WipeCount } );
		long start = Stopwatch.GetTimestamp();
		StartWaitingHits();
		_hitTicksOutsideUpdate += Stopwatch.GetTimestamp() - start;   // counted in this frame's paint cost
	}

	private long _hitTicksOutsideUpdate;

	/// <summary>
	/// Deal with waiting hits, oldest first, for as long as the front one can be decided now.
	///
	/// A hit is decided only when all the paint thrown before it that could matter is ON the wall and has
	/// been looked at, so the rule sees exactly what it would have seen had every earlier splat landed at once:
	///   - no splat is still being prepared for its own wall;
	///   - none for the wall holding its colour's patch either (that patch may have to be erased, and
	///     erasing a wall with a splat on its way would make the core land it on the spot);
	///   - when the hit is on the patch's own wall, a fresh opening search of that patch has finished
	///     (the "is it touching?" test reads the patch that search found).
	/// </summary>
	private void StartWaitingHits()
	{
		while ( _waiting.Count > 0 )
		{
			WaitingHit w = _waiting[0];
			PaintWall wall = w.Wall;
			if ( !wall.IsValid() || wall.WipeCount != w.WipeCount || w.HitIndex >= wall.Hits.Count )
			{
				_waiting.RemoveAt( 0 );   // the wall was wiped after the hit: the hit went with it
				continue;
			}
			if ( wall.Surface.QueuedSplatCount > 0 ) return;

			PaintWall.SplatHit hit = wall.Hits[w.HitIndex];
			PaintColor color = hit.Paint;
			int c = (int)color;
			Patch current = _patch[c];
			bool exists = current.Exists && current.Wall.IsValid();
			bool sameWall = exists && current.Wall == wall;
			if ( exists && !sameWall && current.Wall.Surface.QueuedSplatCount > 0 ) return;

			float gap = float.MaxValue;
			if ( sameWall )
			{
				if ( !w.LookAsked )
				{
					wall.RequestSearch( color );
					w.LookAsked = true;
					_waiting[0] = w;
				}
				if ( wall.SearchWaitingFor( color ) ) return;   // the pacer does it; try again next frame
				gap = wall.GapToPatch( color, hit.UV, PortalPatchPolicy.TouchReachMetres( hit.Radius ) );
			}

			PatchDecision decision = PortalPatchPolicy.Decide( exists, sameWall, gap, hit.Radius );

			if ( decision == PatchDecision.ReplaceExisting )
			{
				Say( $"[PortalPatches] {color}: new hit on {wall.WallName} replaces the patch on {current.Wall.WallName} ({( sameWall ? "same wall, too far away to join" : "different wall" )})." );
				current.Wall.RemovePatch( color, PortalPatchPolicy.ReplaceFadeSeconds );
			}

			_patch[c].Exists = true;
			_patch[c].Wall = wall;
			if ( decision != PatchDecision.AddToExisting )
			{
				// A fresh patch has no opening until its paint has landed and been searched.
				_patch[c].Opening = new PortalOpening { Color = color };
			}
			_patch[c].Revision++;

			_waiting.RemoveAt( 0 );
			wall.StartSplat( w.HitIndex, decision );
		}
	}

	/// <summary>Called by a wall whenever its opening search gives a new answer for a colour it holds.</summary>
	public void ReportOpening( PaintWall wall, PaintColor color, PortalOpening opening )
	{
		int c = (int)color;
		if ( _patch[c].Wall != wall ) return;   // an old wall finishing up: not the colour's wall any more
		_patch[c].Opening = opening;
		_patch[c].Revision++;
	}

	/// <summary>Forget everything (the walls are being wiped).</summary>
	public void Forget()
	{
		_waiting.Clear();
		for ( int c = 0; c < 2; c++ )
		{
			int revision = _patch[c].Revision + 1;
			_patch[c] = new Patch { Revision = revision };
		}
	}

	// ------------------------------------------------------------------ log lines, one a frame

	private readonly Queue<string> _log = new Queue<string>();
	private int _logHeldFrames;

	/// <summary>
	/// Write a line to the console, but not now: paint's lines wait here and go out one a frame, in order.
	/// Writing a line costs 0.4 to 0.8 ms in the editor (measured), and a ball landing used to write up to
	/// four in one frame.
	/// </summary>
	public void Say( string line )
	{
		if ( !_warmingUp ) _log.Enqueue( line );
	}

	// ------------------------------------------------------------------ warm-up

	private bool _warmingUp;

	/// <summary>
	/// Run the game's side of a ball landing once, while loading, on the first wall: a hit that starts a
	/// patch, one that joins it, one that replaces it, in both colours, with the opening searches and the
	/// texture upload, then wipe everything. The runtime compiles each method the first time it runs; without
	/// this the first ball of a session paid for that in its own frames (4 to 6 ms, measured). The core's
	/// own maths is warmed separately by PaintPacer.WarmUp. Leaves the room clean and writes no log lines.
	/// </summary>
	public void WarmUp()
	{
		if ( _walls.Count == 0 ) return;
		PaintWall wall = _walls[0];
		_warmingUp = true;
		Vec2 along = new Vec2( 1f, -1f );
		for ( int round = 0; round < 6; round++ )
		{
			PaintColor color = round < 3 ? PaintColor.Orange : PaintColor.Blue;
			float v = round < 3 ? 1.2f : 2.4f;
			int k = round % 3;
			// 0: starts a patch. 1: lands on it and joins. 2: far along the same wall, replaces it.
			Vec2 uv = new Vec2( k == 0 ? 1.0f : k == 1 ? 1.15f : 3.0f, v );
			wall.Splat( color, uv, 0.3f, along, (uint)( round + 1 ), 8f );
			for ( int guard = 0; guard < 20000 && ( _waiting.Count > 0 || _pacer.HasWork ); guard++ )
			{
				StartWaitingHits();
				_pacer.Advance( 20000 );
				wall.AfterPacer( 0.02f );
			}
			wall.StepPaint( 1f / 60f );
			wall.AfterPacer( 0.2f );
			wall.UploadPaint();
		}
		// Let the drips run out, so the "paint has settled" last look and its report are run once too.
		for ( int i = 0; i < 1200 && wall.IsPaintMoving; i++ ) wall.StepPaint( 0.05f );
		for ( int i = 0; i < 6; i++ )
		{
			wall.AfterPacer( 0.2f );
			_pacer.Advance( 200000 );
		}
		for ( int i = 0; i < _walls.Count; i++ ) _walls[i].ClearPaint();
		Forget();
		for ( int i = 0; i < _walls.Count; i++ ) _walls[i].UploadPaint();
		_log.Clear();
		_warmingUp = false;
		_hitTicksOutsideUpdate = 0;
		ResetTimings();
	}

	// ------------------------------------------------------------------ every frame

	protected override void OnUpdate()
	{
		float dt = Time.Delta;
		bool active = false;
		bool pacerWorked = false;
		long t0 = Stopwatch.GetTimestamp(), t1 = t0, t2 = t0, t3 = t0, t4 = t0, t5 = t0;

		// Nothing moves while the game's clock is stopped (a picture stop, a pause), spread-out work included.
		if ( dt > 0f )
		{
			// 1. Drips.
			for ( int i = 0; i < _walls.Count; i++ )
			{
				if ( _walls[i].IsValid() && _walls[i].StepPaint( dt ) ) active = true;
			}
			t1 = Stopwatch.GetTimestamp();

			// 2. Hits whose turn has come.
			if ( _waiting.Count > 0 )
			{
				active = true;
				StartWaitingHits();
			}
			t2 = Stopwatch.GetTimestamp();

			// 3. A slice of the heavy work. What steps 1 and 2 cost this frame (and any hit dealt with
			// since the last frame) comes off the budget.
			double left = BudgetMs - ( t2 - t0 + _hitTicksOutsideUpdate ) * 1000.0 / Stopwatch.Frequency;
			if ( _pacer.AdvanceFor( left > 0.0 ? left : 0.0 ) > 0.0 )
			{
				active = true;
				pacerWorked = true;
			}
			t3 = Stopwatch.GetTimestamp();

			// 4. What has landed, what has been found.
			for ( int i = 0; i < _walls.Count; i++ )
			{
				if ( _walls[i].IsValid() ) _walls[i].AfterPacer( dt );
			}
			t4 = Stopwatch.GetTimestamp();

			// 5. Texture.
			for ( int i = 0; i < _walls.Count; i++ )
			{
				if ( _walls[i].IsValid() && _walls[i].UploadPaint() ) active = true;
			}
			t5 = Stopwatch.GetTimestamp();
		}

		// 6. One waiting log line (also while the clock is stopped, so a paused test can still read them).
		// It gives way to the spread-out work: it is written in a frame that had none, or after waiting 20 frames.
		long t6 = t5;
		if ( _log.Count > 0 )
		{
			active = true;
			if ( !pacerWorked || ++_logHeldFrames > 20 )
			{
				_logHeldFrames = 0;
				Log.Info( _log.Dequeue() );
				t6 = Stopwatch.GetTimestamp();
			}
		}

		if ( _hitTicksOutsideUpdate > 0 ) active = true;
		CountCost( active, t1 - t0, t2 - t1 + _hitTicksOutsideUpdate, t3 - t2, t4 - t3, t5 - t4, t6 - t5 );
		_hitTicksOutsideUpdate = 0;

		if ( LogTimings )
		{
			_sinceLog += dt;
			if ( _sinceLog >= 3f && ActiveFrames > 0 )
			{
				_sinceLog = 0f;
				Log.Info( $"[PortalPatches] {TimingReport()}" );
			}
		}
	}

	// ------------------------------------------------------------------ what the paint costs

	/// <summary>Milliseconds all of the room's paint work (the five steps above, all walls) took in the latest frame.</summary>
	public double LastFrameMs { get; private set; }

	/// <summary>Since the last <see cref="ResetTimings"/>: frames in which paint did any work, and their average and worst cost.</summary>
	public int ActiveFrames { get; private set; }
	public double AverageMs { get { return ActiveFrames > 0 ? _sumMs / ActiveFrames : 0.0; } }
	public double WorstMs { get; private set; }

	/// <summary>The worst frame for each step by itself (ms): drips, deciding hits (and erasing a replaced patch), the pacer's slice, reacting, texture upload, writing a log line.</summary>
	public double WorstStepMs { get; private set; }
	public double WorstHitMs { get; private set; }
	public double WorstPacerMs { get; private set; }
	public double WorstReactMs { get; private set; }
	public double WorstUploadMs { get; private set; }
	public double WorstLogMs { get; private set; }

	/// <summary>The worst frame's paint cost leaving out the log line: the paint's own work.</summary>
	public double WorstWorkMs { get; private set; }

	private double _sumMs, _sumStepMs, _sumHitMs, _sumPacerMs, _sumReactMs, _sumUploadMs, _sumLogMs;
	private float _sinceLog;
	private string _worstFrameParts = "";

	private void CountCost( bool active, long step, long hit, long pacer, long react, long upload, long log )
	{
		double toMs = 1000.0 / Stopwatch.Frequency;
		double paintMs = ( step + hit + pacer + react + upload + log ) * toMs;
		LastFrameMs = paintMs;
		if ( active )
		{
			ActiveFrames++;
			_sumMs += paintMs;
			_sumStepMs += step * toMs;
			_sumHitMs += hit * toMs;
			_sumPacerMs += pacer * toMs;
			_sumReactMs += react * toMs;
			_sumUploadMs += upload * toMs;
			_sumLogMs += log * toMs;
			if ( paintMs > WorstMs )
			{
				WorstMs = paintMs;
				_worstFrameParts = $"drips {step * toMs:0.000} + hits {hit * toMs:0.000} + pacer {pacer * toMs:0.000} + reacting {react * toMs:0.000} + upload {upload * toMs:0.000} + log line {log * toMs:0.000}";
			}
			if ( step * toMs > WorstStepMs ) WorstStepMs = step * toMs;
			if ( hit * toMs > WorstHitMs ) WorstHitMs = hit * toMs;
			if ( pacer * toMs > WorstPacerMs ) WorstPacerMs = pacer * toMs;
			if ( react * toMs > WorstReactMs ) WorstReactMs = react * toMs;
			if ( upload * toMs > WorstUploadMs ) WorstUploadMs = upload * toMs;
			if ( log * toMs > WorstLogMs ) WorstLogMs = log * toMs;
			if ( paintMs - log * toMs > WorstWorkMs ) WorstWorkMs = paintMs - log * toMs;
		}
		CountFrame( paintMs, active );
	}

	// ------------------------------------------------------------------ the whole frame, and the worst of each second

	/// <summary>Write one line a second, while paint is working, with that second's worst and average paint cost and whole-frame time.</summary>
	public bool LogWorstPerSecond { get; set; }

	/// <summary>Since the last <see cref="ResetTimings"/>: every frame's whole length (one PortalPatches update to the next), ms.</summary>
	public int FramesSeen { get; private set; }
	public double AverageFrameMs { get { return FramesSeen > 0 ? _sumFrameMs / FramesSeen : 0.0; } }
	public double WorstFrameMs { get; private set; }
	/// <summary>The whole length of the frame that had the worst paint cost, ms.</summary>
	public double FrameMsAtWorstPaint { get; private set; }

	private long _lastFrameStamp;
	private double _sumFrameMs;
	private double _secElapsedMs, _secWorstPaint, _secSumPaint, _secWorstFrame, _secSumFrame;
	private int _secFrames, _secActive;

	// Called once a frame with what the paint cost in it.
	private void CountFrame( double paintMs, bool active )
	{
		long now = Stopwatch.GetTimestamp();
		double frameMs = _lastFrameStamp == 0 ? 0.0 : ( now - _lastFrameStamp ) * 1000.0 / Stopwatch.Frequency;
		_lastFrameStamp = now;
		if ( frameMs <= 0.0 ) return;

		FramesSeen++;
		_sumFrameMs += frameMs;
		if ( frameMs > WorstFrameMs ) WorstFrameMs = frameMs;
		if ( active && paintMs >= WorstMs ) FrameMsAtWorstPaint = frameMs;

		_secFrames++;
		_secSumFrame += frameMs;
		if ( frameMs > _secWorstFrame ) _secWorstFrame = frameMs;
		if ( active )
		{
			_secActive++;
			_secSumPaint += paintMs;
			if ( paintMs > _secWorstPaint ) _secWorstPaint = paintMs;
		}
		_secElapsedMs += frameMs;
		if ( _secElapsedMs >= 1000.0 )
		{
			if ( LogWorstPerSecond && _secActive > 0 )
			{
				Log.Info( $"[PaintCost] 1 s: paint worst {_secWorstPaint:0.000} ms, average {_secSumPaint / _secActive:0.000} ms over {_secActive} working frames; whole frame worst {_secWorstFrame:0.00} ms, average {_secSumFrame / _secFrames:0.00} ms over {_secFrames} frames" );
			}
			_secElapsedMs = 0.0;
			_secWorstPaint = _secSumPaint = _secWorstFrame = _secSumFrame = 0.0;
			_secFrames = _secActive = 0;
		}
	}

	public void ResetTimings()
	{
		FramesSeen = 0;
		_sumFrameMs = 0.0;
		WorstFrameMs = 0.0;
		FrameMsAtWorstPaint = 0.0;
		ActiveFrames = 0;
		WorstMs = 0.0;
		_worstFrameParts = "";
		WorstStepMs = WorstHitMs = WorstPacerMs = WorstReactMs = WorstUploadMs = WorstLogMs = WorstWorkMs = 0.0;
		_sumMs = _sumStepMs = _sumHitMs = _sumPacerMs = _sumReactMs = _sumUploadMs = _sumLogMs = 0.0;
	}

	/// <summary>One line with the numbers above.</summary>
	public string TimingReport()
	{
		int n = ActiveFrames > 0 ? ActiveFrames : 1;
		return $"paint cost over {ActiveFrames} working frames (budget {BudgetMs:0.0} ms): average {AverageMs:0.000} ms, worst {WorstMs:0.000} ms = {_worstFrameParts}; worst leaving out the log line {WorstWorkMs:0.000} ms (average drips {_sumStepMs / n:0.000}, hits {_sumHitMs / n:0.000}, pacer {_sumPacerMs / n:0.000}, reacting {_sumReactMs / n:0.000}, upload {_sumUploadMs / n:0.000}, log lines {_sumLogMs / n:0.000}; worst frame for each: drips {WorstStepMs:0.000}, hits {WorstHitMs:0.000}, pacer {WorstPacerMs:0.000}, reacting {WorstReactMs:0.000}, upload {WorstUploadMs:0.000}, log line {WorstLogMs:0.000}); whole frame over {FramesSeen} frames: average {AverageFrameMs:0.00} ms, worst {WorstFrameMs:0.00} ms, {FrameMsAtWorstPaint:0.00} ms in the frame with the worst paint cost";
	}
}
