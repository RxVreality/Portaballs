using System.Collections.Generic;
using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// A board fixed in the room that says, in large plain text, how the helper link is doing and what
/// the last throw did. It exists because whoever is wearing the headset cannot see the console.
///
/// Top half: the link. Bottom half: the last throw (either hand, whichever threw most recently).
/// Green means "as hoped", amber means "working, but on the fallback", white is plain information.
/// The same facts go to the console every few seconds as one line starting [Readout].
///
/// This object's own +X is the way the board faces. Put it where the player can see it and turn it
/// toward them; PortaballsGame does that.
/// </summary>
public sealed class HelperReadout : Component
{
	/// <summary>The link to report on. May be missing: the board then says so.</summary>
	public HelperLink Link { get; set; }

	/// <summary>The hands whose throws are shown.</summary>
	public List<ThrowHand> Hands { get; } = new List<ThrowHand>();

	/// <summary>Height of one line of text, metres. Letters come out about 6 cm tall, for reading from three metres in a headset.</summary>
	[Property] public float LineHeight { get; set; } = 0.10f;

	/// <summary>Board width, metres.</summary>
	[Property] public float BoardWidth { get; set; } = 2.0f;

	/// <summary>Seconds between console lines.</summary>
	[Property] public float LogEverySeconds { get; set; } = 5f;

	/// <summary>Flip the text left-to-right, in case a later engine version turns its world text round.</summary>
	[Property] public bool MirrorText { get; set; }

	private const int LineCount = 11;
	private const float FontPixels = 64f;

	private readonly TextRenderer[] _lines = new TextRenderer[LineCount];
	private readonly string[] _shown = new string[LineCount];
	private readonly Color[] _shownColor = new Color[LineCount];
	private bool _built;
	private double _nextRefresh;
	private double _nextLog;

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

		float height = LineHeight * ( LineCount + 1 );

		// The dark board the text sits on. It has no collider, so balls pass through it.
		GameObject board = new GameObject( GameObject, false, "Board" );
		board.LocalPosition = SboxConvert.ToInches( new Vec3( -0.02f, 0f, 0f ) );
		board.LocalScale = SboxConvert.BoxScaleFor( new Vec3( 0.02f, BoardWidth, height ) );
		ModelRenderer slab = board.AddComponent<ModelRenderer>();
		slab.Model = Model.Cube;
		slab.Tint = new Color( 0.05f, 0.06f, 0.08f );
		board.Enabled = true;

		// World size of text = font pixels * scale, in inches.
		float scale = SboxConvert.LengthToInches( LineHeight * 0.85f ) / FontPixels;

		// The engine's world text reads correctly from the side its own -X points to (seen in the
		// engine: left unturned it shows mirrored from in front of the board). So each line is turned
		// half way round, and starts at the reader's left, which is this object's -Y when it faces them.
		float left = BoardWidth * 0.5f - 0.06f;
		for ( int i = 0; i < LineCount; i++ )
		{
			GameObject line = new GameObject( GameObject, false, "Line " + ( i + 1 ) );
			float z = height * 0.5f - LineHeight * ( i + 1 );
			line.LocalPosition = SboxConvert.ToInches( new Vec3( 0.005f, MirrorText ? left : -left, z ) );
			if ( !MirrorText ) line.LocalRotation = Rotation.FromYaw( 180f );

			TextRenderer text = line.AddComponent<TextRenderer>();
			text.FontSize = FontPixels;
			text.FontWeight = 600;
			text.Scale = scale;
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

	protected override void OnUpdate()
	{
		double now = HelperClock.Now;
		if ( now < _nextRefresh ) return;
		_nextRefresh = now + 0.2;

		bool vr = Game.IsRunningInVR;
		bool connected = Link.IsValid() && Link.Connected;
		bool fresh = connected && Link.IsFresh( now );

		// ---- the link ----
		if ( !connected )
		{
			Set( 0, "HELPER   NOT CONNECTED", Fallback );
			Set( 1, "throws use the game's own poses", Dim );
			Set( 2, " ", Plain );
			Set( 3, "GAZE   OFF  (head aim)", Fallback );
			Set( 4, " ", Plain );
			Set( 5, " ", Plain );
		}
		else
		{
			string source = Link.SourceName == "fake" ? "  (FAKE DATA)" : "";
			if ( Link.VersionMismatch ) Set( 0, $"HELPER   WRONG VERSION {Link.ProtocolVersion}", Fallback );
			else if ( !Link.Streaming && !fresh ) Set( 0, "HELPER   WAITING FOR STEAMVR", Fallback );
			else Set( 0, $"HELPER   CONNECTED{source}", fresh ? Good : Fallback );

			bool rateGood = Link.MeasuredHz > 400.0;
			Set( 1, $"RATE   {Link.MeasuredHz:0} Hz     AGE   {Link.SampleAgeMean * 1000.0:0} ms", fresh && rateGood ? Good : Fallback );
			Set( 2, $"CLOCK   {Link.BatchDelayMin * 1000.0:0.0} ms apart     LOST   {Link.MissedSamples}", Link.BatchDelayMin > -0.001 && Link.BatchDelayMin < 0.02 ? Dim : Fallback );
			// "Active" rides over blinks: any usable reading in the last second counts.
			bool gaze = Link.GazeActive( now );
			Set( 3, gaze ? "GAZE   ACTIVE" : "GAZE   OFF  (head aim)", gaze ? Good : Fallback );

			if ( Link.IdentityAlignment )
			{
				Set( 4, "ALIGN   DESK TEST  (none needed)", Dim );
				Set( 5, " ", Plain );
			}
			else if ( !Link.AlignmentValid )
			{
				bool tilted = Link.Aligner.RejectedTilt > 20;
				Set( 4, tilted ? $"ALIGN   HEAD AXES DISAGREE  {Link.Aligner.LastTilt * 57.29578f:0} deg" : "ALIGN   HOLD YOUR HEAD STILL", Fallback );
				Set( 5, " ", Plain );
			}
			else
			{
				SpaceAligner a = Link.Aligner;
				Set( 4, $"ALIGN   OK   {a.ResidualDistance * 1000f:0} mm   tilt {a.LastTilt * 57.29578f:0.0}   redo {a.Reseeds}", a.ResidualDistance < 0.02f ? Good : Fallback );
				bool l = Link.GripOffsetValid( ThrowHand.HandSide.Left );
				bool r = Link.GripOffsetValid( ThrowHand.HandSide.Right );
				Set( 5, $"HANDS   L {HandWord( ThrowHand.HandSide.Left, l )}     R {HandWord( ThrowHand.HandSide.Right, r )}", l && r ? Good : Fallback );
			}
		}

		// ---- which source a throw would use right now ----
		bool anyHelper = false;
		string blocker = "";
		for ( int i = 0; i < Hands.Count; i++ )
		{
			if ( !Hands[i].IsValid() ) continue;
			if ( Hands[i].HelperInUse ) anyHelper = true;
			else if ( blocker.Length == 0 ) blocker = Hands[i].HelperBlocker;
		}
		// The usual reasons for the fallback are already on the board above (NOT CONNECTED, HOLD YOUR HEAD
		// STILL, HOLD STILL). Only the ones that are not get a word here, to keep the line inside the board.
		string why = blocker == "helper stale" ? "  (stale)" : blocker == "hand not tracked" ? "  (hand lost)" : blocker == "switched off" ? "  (off)" : "";
		Set( 6, anyHelper ? "THROWS FROM   HELPER  500 Hz" : $"THROWS FROM   GAME POSES{why}", anyHelper ? Good : Fallback );

		// ---- the last throw ----
		ThrowReport last = default;
		for ( int i = 0; i < Hands.Count; i++ )
		{
			if ( !Hands[i].IsValid() ) continue;
			ThrowReport r = Hands[i].LastReport;
			if ( r.Valid && ( !last.Valid || r.ClockTime > last.ClockTime ) ) last = r;
		}

		if ( !last.Valid )
		{
			Set( 7, "LAST THROW   none yet", Dim );
			Set( 8, " ", Plain );
			Set( 9, " ", Plain );
			Set( 10, " ", Plain );
		}
		else
		{
			Set( 7, $"LAST THROW   #{last.Number}  {last.Paint}  {last.Side} hand", Plain );
			Set( 8, $"SPEED   {last.Speed:0.0} m/s{( last.IsDrop ? "  (drop)" : "" )}     SOURCE   {( last.FromHelper ? "HELPER" : "GAME" )}", last.FromHelper ? Good : Fallback );
			Set( 9, $"DELAY   {last.DelayMs:0} ms ({( last.DelayLearned ? "learned" : "set" )})     seen {last.NoticedLateMs:0} ms late", Plain );
			string aim = last.Aim == AimAssistOutcome.Applied ? "APPLIED" : last.Aim.ToString();
			string via = last.AimSource == AimSource.Eye ? ( last.AimGlance ? "EYES (glance)" : "EYES" ) : last.AimSource == AimSource.Head ? "HEAD" : "nothing";
			Set( 10, $"AIM   {aim}   via {via}", last.AimSource == AimSource.Eye ? Good : last.AimSource == AimSource.Head ? Fallback : Plain );
		}

		if ( now >= _nextLog )
		{
			_nextLog = now + System.Math.Max( 1f, LogEverySeconds );
			string link = Link.IsValid() ? Link.StatusLine() : "no helper link in this scene";
			if ( Link.IsValid() && Link.PretendEngine ) link += " | " + Link.PretendReport();
			string thrown = last.Valid
				? $"last throw #{last.Number} {last.Paint} {last.Side}: {last.Speed:0.0} m/s, source {( last.FromHelper ? "helper" : "frame" )}, delay {last.DelayMs:0.0} ms ({( last.DelayLearned ? "learned" : "set" )}), aim {last.Aim} via {last.AimSource}{( last.AimGlance ? " (glance)" : "" )}"
				: "no throw yet";
			Log.Info( $"[Readout] {link} | throwing from {( anyHelper ? "helper" : "frame poses (" + blocker + ")" )} | {thrown}" );
		}
	}

	private string HandWord( ThrowHand.HandSide side, bool ok )
	{
		if ( ok ) return "OK";
		return Link.GripOffsetSolver( side ).RejectedTooFar > 20 ? "WRONG HAND?" : "HOLD STILL";
	}
}
