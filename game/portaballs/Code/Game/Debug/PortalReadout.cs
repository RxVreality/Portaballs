using Sandbox;
using Sandbox.Diagnostics;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// A second board in the room, for the see-through portals: which view mode is on (a big A, B or C), and
/// what the engine really did while drawing the last frame (experiment E2): how many eye views it drew,
/// where each one's camera was, how far apart they are, and their lens angles.
///
/// It exists because whoever is wearing the headset cannot see the console, and the one question a
/// headset session has to answer is "does the engine show my code two different eye positions?".
/// The same facts go to the console (and so to the engine's log file) every few seconds as one line
/// starting [PortalE2].
///
/// This object's own +X is the way the board faces, as for HelperReadout.
/// </summary>
public sealed class PortalReadout : Component
{
	public PortalView View { get; set; }

	/// <summary>Height of one line of text, metres.</summary>
	[Property] public float LineHeight { get; set; } = 0.10f;

	/// <summary>Board width, metres.</summary>
	[Property] public float BoardWidth { get; set; } = 2.8f;

	/// <summary>Seconds between console lines.</summary>
	[Property] public float LogEverySeconds { get; set; } = 5f;

	private const int LineCount = 13;        // the first is the big mode line and takes two lines' height
	private const float FontPixels = 64f;

	private readonly TextRenderer[] _lines = new TextRenderer[LineCount];
	private readonly string[] _shown = new string[LineCount];
	private readonly Color[] _shownColor = new Color[LineCount];
	private bool _built;
	private double _nextRefresh;
	private double _nextLog;

	// Frame times since the last log line: what the frame rate really did, not one sample of it.
	private int _frames, _framesLate, _framesVeryLate;
	private double _frameSum, _frameWorst;
	private int _freshAtLog, _keptAtLog, _nestedAtLog, _awayAtLog, _outAtLog;
	private long _pixelsAtLog, _uncutAtLog;
	private bool _countsStarted;

	/// <summary>The latest finished stretch (about <see cref="LogEverySeconds"/> long): mean and worst frame time in ms, and the share of frames that took longer than 12.5 ms (a missed frame at 90 a second), 0..1.</summary>
	public double LastMeanMs { get; private set; }
	public double LastWorstMs { get; private set; }
	public double LastLateShare { get; private set; }

	/// <summary>Far-side pictures drawn afresh, and views handed a re-projected one, per frame, over the latest finished stretch.</summary>
	public double LastFreshPerFrame { get; private set; }
	public double LastKeptPerFrame { get; private set; }

	private static readonly Color Good = new Color( 0.45f, 1f, 0.55f );
	private static readonly Color Fallback = new Color( 1f, 0.78f, 0.25f );
	private static readonly Color Plain = new Color( 1f, 1f, 1f );
	private static readonly Color Dim = new Color( 0.7f, 0.75f, 0.8f );

	protected override void OnStart()
	{
		Build();
	}

	/// <summary>Make the board and its lines. Safe to call more than once.</summary>
	public void Build()
	{
		if ( _built ) return;
		_built = true;

		float height = LineHeight * ( LineCount + 2 );

		GameObject board = new GameObject( GameObject, false, "Board" );
		board.LocalPosition = SboxConvert.ToInches( new Vec3( -0.02f, 0f, 0f ) );
		board.LocalScale = SboxConvert.BoxScaleFor( new Vec3( 0.02f, BoardWidth, height ) );
		ModelRenderer slab = board.AddComponent<ModelRenderer>();
		slab.Model = Model.Cube;
		slab.Tint = new Color( 0.05f, 0.06f, 0.08f );
		board.Enabled = true;

		float scale = SboxConvert.LengthToInches( LineHeight * 0.85f ) / FontPixels;
		float left = BoardWidth * 0.5f - 0.06f;
		for ( int i = 0; i < LineCount; i++ )
		{
			// Line 0 is twice as tall and sits in the top two rows.
			bool big = i == 0;
			float row = big ? 1.5f : i + 2f;
			GameObject line = new GameObject( GameObject, false, "Line " + ( i + 1 ) );
			float z = height * 0.5f - LineHeight * row;
			// World text reads correctly from the side its own -X points to (see HelperReadout).
			line.LocalPosition = SboxConvert.ToInches( new Vec3( 0.005f, -left, z ) );
			line.LocalRotation = Rotation.FromYaw( 180f );

			TextRenderer text = line.AddComponent<TextRenderer>();
			text.FontSize = FontPixels;
			text.FontWeight = big ? 800 : 600;
			text.Scale = big ? scale * 2f : scale;
			text.HorizontalAlignment = TextRenderer.HAlignment.Left;
			text.VerticalAlignment = TextRenderer.VAlignment.Center;
			text.Color = Plain;
			text.Text = " ";
			line.Enabled = true;
			_lines[i] = text;
			_shown[i] = " ";
			_shownColor[i] = Plain;
		}
	}

	private void Set( int line, string text, Color color )
	{
		if ( line < 0 || line >= LineCount || !_lines[line].IsValid() ) return;
		if ( _shown[line] != text )
		{
			_shown[line] = text;
			_lines[line].Text = text;
		}
		if ( _shownColor[line] != color )
		{
			_shownColor[line] = color;
			_lines[line].Color = color;
		}
	}

	/// <summary>The board's text as it stands, one line per row, for scripts and the console.</summary>
	public string Text()
	{
		string s = "";
		for ( int i = 0; i < LineCount; i++ ) s += ( i > 0 ? "\n" : "" ) + _shown[i];
		return s;
	}

	protected override void OnUpdate()
	{
		double now = RealTime.Now;

		// Every frame: how long it took.
		double frameMs = PerformanceStats.FrameTime * 1000.0;
		_frames++;
		_frameSum += frameMs;
		if ( frameMs > _frameWorst ) _frameWorst = frameMs;
		if ( frameMs > 12.5 ) _framesLate++;
		if ( frameMs > 20.0 ) _framesVeryLate++;

		if ( now >= _nextLog && View.IsValid() )
		{
			_nextLog = now + System.Math.Max( 1f, LogEverySeconds );
			PortalViewProbe probe = View.Probe;

			PortalView.EngineCounts( out int fresh, out int kept, out int nested, out int away, out int outOfView, out long pixels, out long uncut, out int sets );
			int frames = System.Math.Max( 1, _frames );
			double freshPerFrame = _countsStarted ? ( fresh - _freshAtLog ) / (double)frames : 0.0;
			double keptPerFrame = _countsStarted ? ( kept - _keptAtLog ) / (double)frames : 0.0;
			double nestedPerFrame = _countsStarted ? ( nested - _nestedAtLog ) / (double)frames : 0.0;
			double awayPerFrame = _countsStarted ? ( away - _awayAtLog ) / (double)frames : 0.0;
			double outPerFrame = _countsStarted ? ( outOfView - _outAtLog ) / (double)frames : 0.0;
			long pixelsNow = _countsStarted ? pixels - _pixelsAtLog : 0, uncutNow = _countsStarted ? uncut - _uncutAtLog : 0;
			LastMeanMs = _frameSum / frames;
			LastWorstMs = _frameWorst;
			LastLateShare = _framesLate / (double)frames;
			LastFreshPerFrame = freshPerFrame;
			LastKeptPerFrame = keptPerFrame;

			Log.Info( $"[PortalE2] mode {View.ModeInUse} (asked {View.Mode}), patch P1 {( PortalView.PatchCompiledIn ? "compiled in" : "NOT compiled in" )}, scale {View.ResolutionScale:0.00}, projection source {View.ProjectionSource}, pictures asked {View.ViewsAsked}, skipped {View.SkippedOffScreen} off screen {View.SkippedTiny} tiny, guard {( View.GuardActive ? "on" : "off" )} | {( probe.IsValid() ? probe.Report() : "no probe" )} | frame {PerformanceStats.FrameTime * 1000.0:0.00} ms, GPU {PerformanceStats.GpuFrametime:0.00} ms" );
			// The second line is new (2026-11-19): what the far-side pictures did to the frame rate over the whole stretch.
			Log.Info( $"[PortalRate] last {frames} frames: mean {LastMeanMs:0.00} ms, worst {_frameWorst:0.00} ms, {100.0 * _framesLate / frames:0}% over 12.5 ms, {100.0 * _framesVeryLate / frames:0}% over 20 ms | far side {RateWord( View.RateMode )}, kept pictures {( View.KeptInUse ? "in use" : PortalView.KeptCompiledIn ? "compiled in, not in use" : "NOT compiled in" )}, feedback {( View.Feedback ? "on" : "off" )}, crop {( View.CropToOpening ? "on" : "off" )} | pictures per frame: {freshPerFrame:0.00} drawn, {keptPerFrame:0.00} re-projected, {nestedPerFrame:0.00} fed a kept picture for a portal seen in them, {awayPerFrame:0.00} skipped looking away, {outPerFrame:0.00} skipped out of view | pixels drawn {( uncutNow > 0 ? 100.0 * pixelsNow / uncutNow : 0.0 ):0}% of uncut ({( frames > 0 ? pixelsNow / (double)frames / 1000000.0 : 0.0 ):0.00} million a frame) | picture sets {sets}, made new {View.Generation} times | guard: per view {( View.GuardPerView ? "yes" : "no" )}, distance {View.GuardDistanceNow * 1000f:0} mm at head speed {View.HeadSpeed:0.00} m/s, sheet {( View.ArmedWall.IsValid() ? "armed on " + View.ArmedWall.WallName : "off" )}" );

			_frames = _framesLate = _framesVeryLate = 0;
			_frameSum = _frameWorst = 0.0;
			_freshAtLog = fresh; _keptAtLog = kept; _nestedAtLog = nested; _awayAtLog = away; _outAtLog = outOfView;
			_pixelsAtLog = pixels; _uncutAtLog = uncut;
			_countsStarted = true;
		}

		if ( now < _nextRefresh ) return;
		_nextRefresh = now + 0.2;
		Refresh();
	}

	private static string RateWord( PortalRateMode mode )
	{
		return mode == PortalRateMode.HalfRate ? "HALF RATE" : mode == PortalRateMode.OnePerFrame ? "ONE A FRAME" : "FULL RATE";
	}

	/// <summary>Bring the board's text up to date now.</summary>
	public void Refresh()
	{
		if ( !View.IsValid() )
		{
			Set( 0, "PORTAL VIEW   ?", Fallback );
			Set( 1, "no portal view in this scene", Dim );
			return;
		}

		bool vr = Game.IsRunningInVR;
		PortalViewMode asked = View.Mode;
		PortalViewMode used = View.ModeInUse;
		Color modeColor = used == PortalViewMode.C ? Fallback : Good;
		Set( 0, $"PORTAL VIEW   MODE {used}", asked == used ? modeColor : Fallback );
		string what = used == PortalViewMode.A ? "A = real view, no engine patch (one picture for both eyes)"
			: used == PortalViewMode.B ? "B = real view, patched engine (each eye its own)"
			: "C = no view: dark surface (safe fallback)";
		if ( asked != used ) what = $"asked for {asked}, but the engine patch is not in this build";
		Set( 1, what, asked == used ? Plain : Fallback );
		Set( 2, PortalView.PatchCompiledIn ? $"PATCH P1   COMPILED IN{( PortalView.PerViewCompiledIn ? "   P2" : "" )}{( PortalView.KeptCompiledIn ? "   P4" : "" )}" : "PATCH P1   NOT IN THIS BUILD  (mode B unavailable)", PortalView.PatchCompiledIn ? Good : Fallback );
		Set( 3, $"PICTURES  {View.ViewsAsked} asked   skipped {View.SkippedOffScreen} off-screen {View.SkippedTiny} tiny   guard {( View.GuardActive ? "ON" : "off" )}", Plain );
		string layout = View.TestLayoutName == "" ? "" : $"     TEST PORTALS   {View.TestLayoutName.ToUpperInvariant()}";
		Set( 4, $"SCALE   {View.ResolutionScale:0.00}     LENS FROM   {( View.ProjectionSource == 0 ? "side planes" : "engine matrix" )}{layout}", Dim );

		PortalViewProbe probe = View.Probe;
		if ( !probe.IsValid() || probe.FramesCollected == 0 )
		{
			Set( 5, "DRAWN VIEWS   nothing seen yet", Fallback );
			for ( int i = 6; i <= 9; i++ ) Set( i, " ", Plain );
		}
		else
		{
			int eyes = probe.EyeCount;
			Set( 5, $"DRAWN VIEWS   {eyes} eye   {probe.OtherCount} other", Plain );
			for ( int i = 0; i < 2; i++ )
			{
				if ( i >= eyes ) { Set( 6 + i, " ", Plain ); continue; }
				PortalViewProbe.ViewSeen v = probe.Eye( i );
				string lens = "lens ?";
				if ( v.Lens.IsValid )
				{
					v.Lens.ToDegrees( out float l, out float r, out float d, out float u );
					lens = $"L {l:0} R {r:0} D {d:0} U {u:0}";
				}
				Set( 6 + i, $"EYE {i + 1}   {v.Position.X:0.000} {v.Position.Y:0.000} {v.Position.Z:0.000}   {lens}", Plain );
			}
			if ( eyes >= 2 )
			{
				float apart = probe.EyeSeparation * 1000f;
				bool two = apart > 1f;
				Set( 8, two ? $"TWO EYE POSITIONS   YES   {apart:0.0} mm apart" : "TWO EYE POSITIONS   NO  (both views from one place)", two ? Good : Fallback );
				Set( 9, $"from head {probe.EyeFromHead[0] * 1000f:0.0} / {probe.EyeFromHead[1] * 1000f:0.0} mm   size {probe.Eye( 0 ).ViewportWidth:0}x{probe.Eye( 0 ).ViewportHeight:0} at x {probe.Eye( 0 ).ViewportX:0} / {probe.Eye( 1 ).ViewportX:0}", Dim );
			}
			else if ( eyes == 1 )
			{
				Set( 8, vr ? "TWO EYE POSITIONS   NO  (only one eye view seen)" : "FLAT SCREEN   one view, as expected", vr ? Fallback : Dim );
				Set( 9, $"size {probe.Eye( 0 ).ViewportWidth:0}x{probe.Eye( 0 ).ViewportHeight:0}   {probe.EyeFromHead[0] * 1000f:0.0} mm from the game camera", Dim );
			}
			else
			{
				Set( 8, "NO EYE VIEW SEEN", Fallback );
				Set( 9, " ", Plain );
			}
		}

		Set( 10, $"FRAME   {PerformanceStats.FrameTime * 1000.0:0.0} ms   last 5 s: mean {LastMeanMs:0.0}  worst {LastWorstMs:0.0}  late {LastLateShare * 100.0:0}%", LastLateShare > 0.1 ? Fallback : Dim );
		// The far-side rate: what the right stick's click flips.
		bool half = View.RateMode != PortalRateMode.EveryFrame;
		string rate = !View.KeptInUse ? "FAR SIDE   FULL RATE  (no kept pictures in this mode)"
			: $"FAR SIDE   {RateWord( View.RateMode )}   {LastFreshPerFrame:0.0} drawn  {LastKeptPerFrame:0.0} re-projected a frame";
		Set( 11, rate, View.KeptInUse && half ? Good : Plain );
		Set( 12, vr ? "B/Y mode  X scale  A lens  L-stick: test portals  R-stick: far-side rate" : "keys 1 2 3 = mode   4 = scale   5 = lens   6 = test portals   7 = far-side rate", Dim );
	}
}
