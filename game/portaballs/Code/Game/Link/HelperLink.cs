using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.VR;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// The game's end of the link to the helper program (helper\dist\PortaballsHelper.exe).
///
/// What it is for: the engine only tells the game where the hands are once per drawn frame, with no
/// clock reading and no eye tracking. The helper reads SteamVR 500 times a second and sends every
/// reading, with the time it was taken, over a WebSocket on this PC. This component connects to it,
/// keeps the last half second of head and hand poses and the last second of gaze, and says in one
/// place how the link is doing. <see cref="ThrowHand"/> throws from this data while it is fresh and
/// from the engine's own per-frame poses when it is not.
///
/// It also works out how the helper's space sits against the engine's play-area space, and where
/// each hand's "grip point" is on the helper's raw controller pose (see <see cref="UpdateCalibration"/>).
///
/// If the helper is not running this does nothing visible: it tries again quietly every second or
/// so and logs one line when the link comes up or goes down.
///
/// THE PORT. s&box only lets game code open a connection to this same PC on ports 80, 443, 8080 or
/// 8443 (C:\sbox\engine\Sandbox.Engine\Utility\Web\Http.cs, IsAllowedLoopbackPort). The helper's
/// own default, 28461, is refused. So the helper is started with "--port 8443" (engine\launch.ps1
/// does that) and this looks there first.
/// </summary>
public sealed class HelperLink : Component
{
	/// <summary>Ports to try on 127.0.0.1, in order, separated by commas. See the note on ports above.</summary>
	[Property] public string Ports { get; set; } = "8443,28461";

	/// <summary>Seconds between attempts while the helper cannot be reached.</summary>
	[Property] public float RetrySeconds { get; set; } = 1.0f;

	/// <summary>Helper data older than this is "stale": throws go back to the engine's per-frame poses, seconds.</summary>
	[Property] public float StaleSeconds { get; set; } = 0.10f;

	/// <summary>If nothing at all arrives for this long the connection is dropped and made again, seconds. (The helper says something at least once a second.)</summary>
	[Property] public float SilenceSeconds { get; set; } = 3.0f;

	/// <summary>
	/// Desk test only: treat the helper's space as the play area's own space, and its raw controller
	/// pose as the grip pose, with nothing to work out. Set by PortaballsGame.HelperDeskTest.
	/// </summary>
	[Property] public bool IdentityAlignment { get; set; }

	// ------------------------------------------------------------------ what the link is doing

	/// <summary>True while the WebSocket is open and the helper has said hello.</summary>
	public bool Connected => _state == LinkPhase.Connected && _decoder.HasHello;

	/// <summary>The helper's protocol version, or 0 before it has said hello.</summary>
	public int ProtocolVersion => _decoder.HasHello ? _decoder.Hello.Version : 0;

	/// <summary>True when the helper speaks a protocol this game does not. Its samples are ignored.</summary>
	public bool VersionMismatch => _decoder.VersionMismatch;

	/// <summary>Where the helper's samples come from: "openvr", "fake", or "" before hello.</summary>
	public string SourceName => _decoder.HasHello ? ( _decoder.Hello.Source ?? "" ) : "";

	/// <summary>The port the current connection is on, or 0.</summary>
	public int Port { get; private set; }

	/// <summary>Samples per second actually arriving, measured from their own timestamps over the last second or so.</summary>
	public double MeasuredHz { get; private set; }

	/// <summary>How old the newest sample was when this frame looked at it, seconds.</summary>
	public double SampleAgeSeconds { get; private set; }

	/// <summary>Average and worst of that over the last second or so.</summary>
	public double SampleAgeMean { get; private set; }
	public double SampleAgeMax { get; private set; }

	/// <summary>This PC's clock read by the game when the hello arrived, minus the helper's clock inside the hello, seconds.
	/// It includes the time the hello spent on its way, so a small positive number means the clocks agree.</summary>
	public double ClockDifferenceAtHello { get; private set; }

	/// <summary>Game clock at arrival minus helper clock at sending, for batches: the smallest and average seen in the last
	/// second or so. If the two clocks were not the same clock the smallest would not sit just above zero.</summary>
	public double BatchDelayMin { get; private set; }
	public double BatchDelayMean { get; private set; }

	/// <summary>The smallest batch delay seen since connecting. Never below zero if the clocks agree.</summary>
	public double BatchDelayMinEver { get; private set; }

	/// <summary>True when the newest sample's gaze was usable (eye tracker on, eyes tracked). False during a blink.</summary>
	public bool GazeValid { get; private set; }

	/// <summary>Timestamp of the newest sample whose gaze was usable, shared clock (0 when there has been none).</summary>
	public double LastUsableGazeTime { get; private set; }

	/// <summary>True when there has been a usable gaze reading within the last second: "eye tracking is working", blinks aside.</summary>
	public bool GazeActive( double clockNow )
	{
		return Connected && LastUsableGazeTime > 0.0 && clockNow - LastUsableGazeTime < 1.0;
	}

	/// <summary>True when the helper says it is reading its source (false: SteamVR is not up).</summary>
	public bool Streaming => _decoder.HasStatus && _decoder.Status.State == LinkState.Streaming;

	/// <summary>How many times the link has come up since the game started.</summary>
	public int Connections { get; private set; }

	/// <summary>Samples received since the game started.</summary>
	public long TotalSamples { get; private set; }

	/// <summary>Samples the helper took that never arrived (judged by their numbers), on this connection.</summary>
	public long MissedSamples => _decoder.MissedSamples;

	/// <summary>Timestamp of the newest sample, shared clock.</summary>
	public double NewestSampleTime { get; private set; }

	/// <summary>The last reason a connection attempt failed, for the readout.</summary>
	public string LastError { get; private set; } = "";

	// ------------------------------------------------------------------ the data

	/// <summary>Half a second of each device's poses, in the HELPER's space (metres, +X forward, +Y left, +Z up), shared-clock times.</summary>
	public PoseHistory Head { get; } = new PoseHistory();
	public PoseHistory Left { get; } = new PoseHistory();
	public PoseHistory Right { get; } = new PoseHistory();

	/// <summary>About a second of gaze readings, in the helper's space.</summary>
	public GazeFixationFilter Gaze { get; } = new GazeFixationFilter( 512 );

	/// <summary>How many whole samples are kept for readers that want every one (<see cref="SampleAt"/>).</summary>
	public const int RingCapacity = 1024;

	private readonly LinkSample[] _ring = new LinkSample[RingCapacity];

	/// <summary>
	/// Sample number <paramref name="index"/> counted from the start of the game (0 .. TotalSamples - 1).
	/// Only the newest <see cref="RingCapacity"/> are kept; a reader keeps its own counter and must not fall further behind.
	/// </summary>
	public LinkSample SampleAt( long index )
	{
		if ( index < 0 ) index = 0;
		return _ring[(int)( index % RingCapacity )];
	}

	/// <summary>The newest whole sample. Only meaningful when TotalSamples is above zero.</summary>
	public LinkSample Newest => _ring[(int)( ( TotalSamples > 0 ? TotalSamples - 1 : 0 ) % RingCapacity )];

	/// <summary>True when samples are arriving and the newest is recent enough to throw with.</summary>
	public bool IsFresh( double clockNow )
	{
		return Connected && !VersionMismatch && TotalSamples > 0 && clockNow - NewestSampleTime < StaleSeconds && clockNow - NewestSampleTime > -0.05;
	}

	// ------------------------------------------------------------------ alignment and grip offsets

	/// <summary>True once the helper's space has been lined up with the play area (always, in the desk test).</summary>
	public bool AlignmentValid => IdentityAlignment || _aligner.HasEstimate;

	/// <summary>Carries a point or pose from the helper's space into the play area's own space (the VR anchor's local space).</summary>
	public RigidTransform HelperToAnchor => IdentityAlignment ? RigidTransform.Identity : _aligner.Current;

	/// <summary>The solver, for its counts and residuals.</summary>
	public SpaceAligner Aligner => _aligner;

	/// <summary>True once this hand's grip point has been found on the helper's raw controller pose (always, in the desk test).</summary>
	public bool GripOffsetValid( ThrowHand.HandSide side )
	{
		return IdentityAlignment || ( side == ThrowHand.HandSide.Left ? _leftOffset : _rightOffset ).HasEstimate;
	}

	/// <summary>The engine's grip pose expressed in the helper's raw controller axes: grip pose = raw pose * this.</summary>
	public RigidTransform GripOffset( ThrowHand.HandSide side )
	{
		if ( IdentityAlignment ) return RigidTransform.Identity;
		return ( side == ThrowHand.HandSide.Left ? _leftOffset : _rightOffset ).Current;
	}

	public RigidOffsetAverager GripOffsetSolver( ThrowHand.HandSide side )
	{
		return side == ThrowHand.HandSide.Left ? _leftOffset : _rightOffset;
	}

	private readonly SpaceAligner _aligner = new SpaceAligner();
	private readonly RigidOffsetAverager _leftOffset = new RigidOffsetAverager();
	private readonly RigidOffsetAverager _rightOffset = new RigidOffsetAverager();
	private StillnessGate _leftStill, _rightStill;
	private EnginePoseTracker _headTrack, _leftTrack, _rightTrack;
	private bool _alignAnnounced, _leftAnnounced, _rightAnnounced;
	private int _reseedsAnnounced;

	// ------------------------------------------------------------------ connection

	private enum LinkPhase
	{
		Idle,
		Connecting,
		Connected,
	}

	private LinkPhase _state = LinkPhase.Idle;
	private WebSocket _socket;
	private int _attempt;               // goes up with every attempt; late callbacks from an old one are ignored
	private int _portIndex;
	private double _nextAttemptAt;
	private double _connectStartedAt;
	private double _lastMessageAt;
	private bool _saidNotFound;
	private bool _everConnected;
	private bool _closing;

	private readonly LinkDecoder _decoder = new LinkDecoder();
	private readonly List<LinkSample> _scratch = new List<LinkSample>( 64 );

	// One-second measuring windows.
	private double _windowStartedAt;
	private double _windowFirstSampleTime;
	private double _windowLastSampleTime;
	private int _windowSamples;
	private double _ageSum, _ageMax;
	private int _ageCount;
	private double _delaySum, _delayMin;
	private int _delayCount;

	protected override void OnUpdate()
	{
		double now = HelperClock.Now;

		switch ( _state )
		{
			case LinkPhase.Idle:
				if ( now >= _nextAttemptAt ) BeginConnect( now );
				break;

			case LinkPhase.Connecting:
				// A connection to a port nobody is listening on normally fails by itself in about two seconds.
				if ( now - _connectStartedAt > 8.0 ) Drop( now, "no answer", false );
				break;

			case LinkPhase.Connected:
				if ( _socket is null || !_socket.IsConnected ) Drop( now, "the helper closed the connection", true );
				else if ( now - _lastMessageAt > SilenceSeconds ) Drop( now, "the helper went silent", true );
				break;
		}

		// How old is the newest sample, as this frame sees it?
		if ( Connected && TotalSamples > 0 )
		{
			double age = now - NewestSampleTime;
			SampleAgeSeconds = age;
			_ageSum += age;
			_ageCount++;
			if ( age > _ageMax ) _ageMax = age;
		}

		if ( now - _windowStartedAt >= 1.0 ) CloseWindow( now );

		UpdateCalibration( now );
	}

	protected override void OnDisabled()
	{
		Close();
	}

	protected override void OnDestroy()
	{
		Close();
	}

	private void Close()
	{
		_closing = true;
		_attempt++;
		WebSocket socket = _socket;
		_socket = null;
		_state = LinkPhase.Idle;
		if ( socket is not null )
		{
			try { socket.Dispose(); } catch ( Exception ) { }
		}
		_closing = false;
	}

	private int PortAt( int index )
	{
		string[] parts = ( Ports ?? "" ).Split( ',' );
		int found = 0;
		int first = 0;
		for ( int i = 0; i < parts.Length; i++ )
		{
			if ( !int.TryParse( parts[i].Trim(), out int port ) || port <= 0 || port > 65535 ) continue;
			if ( found == 0 ) first = port;
			found++;
		}
		if ( found == 0 ) return LinkProtocol.DefaultPort;
		int want = index % found;
		int seen = 0;
		for ( int i = 0; i < parts.Length; i++ )
		{
			if ( !int.TryParse( parts[i].Trim(), out int port ) || port <= 0 || port > 65535 ) continue;
			if ( seen == want ) return port;
			seen++;
		}
		return first;
	}

	private void BeginConnect( double now )
	{
		_attempt++;
		_state = LinkPhase.Connecting;
		_connectStartedAt = now;
		int port = PortAt( _portIndex );
		_ = ConnectAsync( _attempt, port );
	}

	private async System.Threading.Tasks.Task ConnectAsync( int attempt, int port )
	{
		WebSocket socket = null;
		try
		{
			// A batch is a few kilobytes; this leaves room for a burst after a hitch.
			socket = new WebSocket( 256 * 1024 );
			socket.OnMessageReceived += message => OnMessage( attempt, message );
			socket.OnDisconnected += ( status, reason ) => OnSocketClosed( attempt, reason );
			await socket.Connect( "ws://127.0.0.1:" + port + "/" );
		}
		catch ( Exception e )
		{
			if ( socket is not null )
			{
				try { socket.Dispose(); } catch ( Exception ) { }
			}
			if ( attempt != _attempt ) return;
			Failed( HelperClock.Now, port, e.Message );
			return;
		}

		if ( attempt != _attempt || !this.IsValid() )
		{
			try { socket.Dispose(); } catch ( Exception ) { }
			return;
		}

		_socket = socket;
		_state = LinkPhase.Connected;
		_lastMessageAt = HelperClock.Now;
		Port = port;
		Connections++;
		_saidNotFound = false;
		LastError = "";
		ResetWindow( _lastMessageAt );
		Log.Info( $"[HelperLink] Connected to the helper on port {port}{( _everConnected ? " (again)" : "" )}." );
		_everConnected = true;
	}

	private void Failed( double now, int port, string reason )
	{
		_state = LinkPhase.Idle;
		_portIndex++;
		_nextAttemptAt = now + Math.Max( 0.2f, RetrySeconds );
		reason = reason ?? "";
		LastError = "port " + port + ": " + reason;

		// "Not allowed" is the engine refusing the port, which trying again will not fix: say so once per port.
		bool refused = reason.Contains( "not allowed" );
		if ( refused && !_refusedPorts.Contains( port ) )
		{
			_refusedPorts.Add( port );
			Log.Info( $"[HelperLink] The engine does not allow game code to connect to port {port} on this PC ({reason}). Only 80, 443, 8080 and 8443 are allowed; the helper must be started with --port 8443." );
		}
		else if ( !refused && !_saidNotFound )
		{
			_saidNotFound = true;
			Log.Info( $"[HelperLink] The helper is not running (port {port}: {reason}). Throws use the engine's per-frame poses. Still looking, quietly." );
		}
	}

	private readonly List<int> _refusedPorts = new List<int>();

	private void OnSocketClosed( int attempt, string reason )
	{
		if ( attempt != _attempt || _closing ) return;
		if ( _state == LinkPhase.Connected ) Drop( HelperClock.Now, "the helper closed the connection", true );
	}

	/// <summary>Let go of the connection and start looking again.</summary>
	private void Drop( double now, string why, bool wasConnected )
	{
		_attempt++;
		WebSocket socket = _socket;
		_socket = null;
		_state = LinkPhase.Idle;
		_nextAttemptAt = now + Math.Max( 0.2f, RetrySeconds );
		if ( socket is not null )
		{
			_closing = true;
			try { socket.Dispose(); } catch ( Exception ) { }
			_closing = false;
		}

		if ( wasConnected )
		{
			Log.Info( $"[HelperLink] Lost the helper ({why}). Throws use the engine's per-frame poses until it is back." );
			_portIndex = 0;
		}
		else
		{
			_portIndex++;
			LastError = why;
		}

		_decoder.Reset();
		Port = 0;
		MeasuredHz = 0.0;
		SampleAgeSeconds = 0.0;
		SampleAgeMean = 0.0;
		SampleAgeMax = 0.0;
		GazeValid = false;
		LastUsableGazeTime = 0.0;
		// Old poses must not be mixed with whatever a restarted helper sends.
		Head.Clear();
		Left.Clear();
		Right.Clear();
		Gaze.Clear();
	}

	// ------------------------------------------------------------------ messages

	private void OnMessage( int attempt, string message )
	{
		if ( attempt != _attempt ) return;
		double now = HelperClock.Now;
		_lastMessageAt = now;

		bool hadHello = _decoder.HasHello;
		long batchesBefore = _decoder.BatchCount;
		_scratch.Clear();
		_decoder.Decode( message, _scratch );

		if ( !hadHello && _decoder.HasHello )
		{
			ClockDifferenceAtHello = now - _decoder.Hello.Time;
			BatchDelayMinEver = double.MaxValue;
			LinkHello hello = _decoder.Hello;
			Log.Info( $"[HelperLink] Hello: protocol {hello.Version}, source {hello.Source}, {hello.PollHz:0} samples/s in {hello.BatchHz:0} messages/s. CLOCK: game {now:0.000000} s, helper {hello.Time:0.000000} s, difference {ClockDifferenceAtHello * 1000.0:0.000} ms (includes the hello's travel time)." );
			if ( _decoder.VersionMismatch )
			{
				Log.Warning( $"[HelperLink] The helper speaks protocol {hello.Version}; this game speaks {LinkProtocol.Version}. Its samples are ignored. Rebuild the helper (helper\\build.ps1)." );
			}
		}

		if ( _decoder.BatchCount != batchesBefore )
		{
			double delay = now - _decoder.LastBatchSendTime;
			_delaySum += delay;
			_delayCount++;
			if ( delay < _delayMin ) _delayMin = delay;
			if ( delay < BatchDelayMinEver ) BatchDelayMinEver = delay;
		}

		for ( int i = 0; i < _scratch.Count; i++ ) Append( _scratch[i] );
	}

	private void Append( LinkSample s )
	{
		_ring[(int)( TotalSamples % RingCapacity )] = s;
		TotalSamples++;
		NewestSampleTime = s.Time;

		if ( s.Head.IsValid ) Head.Add( s.Time, s.Head.Position, s.Head.Rotation );
		if ( s.Left.Pose.IsValid ) Left.Add( s.Time, s.Left.Pose.Position, s.Left.Pose.Rotation );
		if ( s.Right.Pose.IsValid ) Right.Add( s.Time, s.Right.Pose.Position, s.Right.Pose.Rotation );

		// Every reading goes in, usable or not: a blink has to break up a steady look.
		Gaze.Add( s.Time, s.Gaze.Origin, s.Gaze.Direction, s.Gaze.IsUsable );
		GazeValid = s.Gaze.IsUsable;
		if ( s.Gaze.IsUsable ) LastUsableGazeTime = s.Time;

		if ( _windowSamples == 0 ) _windowFirstSampleTime = s.Time;
		_windowLastSampleTime = s.Time;
		_windowSamples++;
	}

	private void ResetWindow( double now )
	{
		_windowStartedAt = now;
		_windowSamples = 0;
		_ageSum = 0.0;
		_ageMax = 0.0;
		_ageCount = 0;
		_delaySum = 0.0;
		_delayMin = double.MaxValue;
		_delayCount = 0;
	}

	private void CloseWindow( double now )
	{
		if ( Connected )
		{
			double span = _windowLastSampleTime - _windowFirstSampleTime;
			MeasuredHz = _windowSamples >= 2 && span > 1e-6 ? ( _windowSamples - 1 ) / span : 0.0;
			if ( _ageCount > 0 )
			{
				SampleAgeMean = _ageSum / _ageCount;
				SampleAgeMax = _ageMax;
			}
			if ( _delayCount > 0 )
			{
				BatchDelayMean = _delaySum / _delayCount;
				BatchDelayMin = _delayMin;
			}
		}
		ResetWindow( now );
	}

	/// <summary>One line saying how the link is doing, for the console and the readout's log.</summary>
	public string StatusLine()
	{
		if ( !Connected )
		{
			return "helper NOT connected" + ( LastError.Length > 0 ? " (" + LastError + ")" : "" ) + "; throws use per-frame poses";
		}
		string align = IdentityAlignment ? "alignment: desk test (none needed)"
			: _aligner.HasEstimate ? $"alignment ok ({_aligner.Accepted} readings, off by {_aligner.ResidualDistance * 1000f:0.0} mm, tilt {_aligner.LastTilt * 57.29578f:0.0} deg, restarted {_aligner.Reseeds}x), grip offsets L {( _leftOffset.HasEstimate ? "ok" : "no" )} R {( _rightOffset.HasEstimate ? "ok" : "no" )}"
			: "alignment: waiting for a still head";
		return $"helper connected (port {Port}, protocol {ProtocolVersion}, source {SourceName}, {( Streaming ? "streaming" : "waiting for SteamVR" )}): {MeasuredHz:0.0} samples/s, sample age mean {SampleAgeMean * 1000.0:0.0} ms max {SampleAgeMax * 1000.0:0.0} ms, batch delay min {BatchDelayMin * 1000.0:0.00} ms mean {BatchDelayMean * 1000.0:0.00} ms (lowest ever {( BatchDelayMinEver < double.MaxValue ? BatchDelayMinEver * 1000.0 : 0.0 ):0.000} ms), missed {MissedSamples}, gaze {( GazeValid ? "valid" : GazeActive( HelperClock.Now ) ? "valid (blink right now)" : "not valid" )}, {align}";
	}

	// ------------------------------------------------------------------ lining the two spaces up

	/// <summary>Remembers one engine pose from frame to frame so its speed can be measured.</summary>
	private struct EnginePoseTracker
	{
		public bool Has;
		public double Time;
		public Vec3 Position;
		public Quat Rotation;

		/// <summary>Store this frame's pose and return how fast it moved and turned since the last one.</summary>
		public void Update( double time, Vec3 position, Quat rotation, out float linearSpeed, out float angularSpeed )
		{
			linearSpeed = float.MaxValue;
			angularSpeed = float.MaxValue;
			if ( Has && time > Time )
			{
				float dt = (float)( time - Time );
				// A long gap says nothing about how fast it is moving right now.
				if ( dt < 0.1f )
				{
					linearSpeed = ( position - Position ).Length() / dt;
					angularSpeed = AlignmentMath.AngleBetween( Rotation, rotation ) / dt;
				}
			}
			Has = true;
			Time = time;
			Position = position;
			Rotation = rotation;
		}
	}

	/// <summary>
	/// In VR, with fresh helper data: compare the head as the helper sees it with the head as the
	/// engine sees it, and likewise each hand, but only while they are nearly still.
	///
	/// Why "nearly still": the engine's poses are guessed ahead to the moment the picture will be on
	/// the screens, and the helper's are "now". For a still device those are the same pose, so no
	/// guess about the engine's look-ahead is needed. The newest helper sample is simply paired with
	/// this frame's engine pose, and the stillness limit (5 cm/s, about 11 degrees/s) keeps what the
	/// look-ahead could add under a couple of millimetres and half a degree, which the averaging then
	/// shrinks further.
	///
	/// The result is kept relative to the play area (the VR anchor), never to the world: the engine
	/// pose used here is the anchor's own local pose, so moving the anchor (a portal teleport) changes nothing.
	/// </summary>
	private void UpdateCalibration( double now )
	{
		if ( IdentityAlignment ) return;
		if ( !IsFresh( now ) ) return;
		LinkSample s = Newest;

		// The engine's side: head and both grip poses in the anchor's own space, and how fast each is moving.
		bool leftOk, rightOk;
		Vec3 headPosition, leftPosition = default, rightPosition = default;
		Quat headRotation, leftRotation = Quat.Identity, rightRotation = Quat.Identity;
		float headLinear, headAngular, leftLinear = 0f, leftAngular = 0f, rightLinear = 0f, rightAngular = 0f;

		if ( PretendEngine )
		{
			// Desk test of this very method: see PretendEngine.
			PretendPose( s.Head, RigidTransform.Identity, out headPosition, out headRotation );
			headLinear = headAngular = 0f;   // the helper's own speeds, used below, are this pretend pose's speeds too
			leftOk = s.Left.Pose.IsValid;
			rightOk = s.Right.Pose.IsValid;
			if ( leftOk ) PretendPose( s.Left.Pose, PretendGripOffset, out leftPosition, out leftRotation );
			if ( rightOk ) PretendPose( s.Right.Pose, PretendGripOffset, out rightPosition, out rightRotation );
		}
		else
		{
			if ( !Game.IsRunningInVR ) return;
			VRInput vr = Input.VR;
			if ( vr is null ) return;
			Transform anchor = vr.Anchor;

			SboxConvert.ToCorePose( anchor.ToLocal( vr.Head ), out headPosition, out headRotation );
			_headTrack.Update( now, headPosition, headRotation, out headLinear, out headAngular );

			leftOk = vr.LeftHand is not null && vr.LeftHand.Active;
			rightOk = vr.RightHand is not null && vr.RightHand.Active;
			if ( leftOk )
			{
				SboxConvert.ToCorePose( anchor.ToLocal( vr.LeftHand.Transform ), out leftPosition, out leftRotation );
				_leftTrack.Update( now, leftPosition, leftRotation, out leftLinear, out leftAngular );
			}
			else _leftTrack.Has = false;
			if ( rightOk )
			{
				SboxConvert.ToCorePose( anchor.ToLocal( vr.RightHand.Transform ), out rightPosition, out rightRotation );
				_rightTrack.Update( now, rightPosition, rightRotation, out rightLinear, out rightAngular );
			}
			else _rightTrack.Has = false;
		}

		// ---- head: the space alignment ----
		if ( s.Head.IsValid )
		{
			float linear = MathF.Max( headLinear, s.Head.Velocity.Length() );
			float angular = MathF.Max( headAngular, s.Head.AngularVelocity.Length() );
			AlignerOutcome outcome = _aligner.Add( now, s.Head.Position, s.Head.Rotation, headPosition, headRotation, linear, angular );

			if ( _aligner.HasEstimate && !_alignAnnounced )
			{
				_alignAnnounced = true;
				AlignmentMath.SplitYawTilt( _aligner.Current.Rotation, out float yaw, out _ );
				Vec3 t = _aligner.Current.Translation;
				Log.Info( $"[HelperLink] Spaces lined up from a still head: helper space is turned {yaw * 57.29578f:0.0} deg and shifted ({t.X:0.000}, {t.Y:0.000}, {t.Z:0.000}) m against the play area. Tilt of that first reading {_aligner.LastTilt * 57.29578f:0.00} deg (should be near 0)." );
			}
			if ( outcome == AlignerOutcome.Reseeded && _aligner.Reseeds != _reseedsAnnounced )
			{
				_reseedsAnnounced = _aligner.Reseeds;
				Log.Info( $"[HelperLink] The helper's space moved against the play area (SteamVR re-centred?). Lined up again; that has now happened {_aligner.Reseeds} time(s)." );
				_leftOffset.Reset();
				_rightOffset.Reset();
			}
			if ( outcome == AlignerOutcome.TooTilted && _aligner.RejectedTilt == 50 )
			{
				Log.Warning( $"[HelperLink] The head as the helper sees it and as the engine sees it are tipped {_aligner.LastTilt * 57.29578f:0} deg apart, 50 times running. The two disagree about the headset's own axes; helper throws stay off until that is fixed." );
			}
		}

		// ---- hands: where the engine's grip point is on the helper's raw controller pose ----
		if ( !_aligner.HasEstimate ) return;
		UpdateGripOffset( now, ThrowHand.HandSide.Left, leftOk, leftPosition, leftRotation, leftLinear, leftAngular, s.Left, ref _leftStill, _leftOffset, ref _leftAnnounced );
		UpdateGripOffset( now, ThrowHand.HandSide.Right, rightOk, rightPosition, rightRotation, rightLinear, rightAngular, s.Right, ref _rightStill, _rightOffset, ref _rightAnnounced );
	}

	/// <param name="gripPosition">The engine's grip pose for this hand, in the anchor's own space.</param>
	/// <param name="engineLinear">How fast the engine's pose moved since last frame, m/s.</param>
	private void UpdateGripOffset( double now, ThrowHand.HandSide side, bool engineOk, Vec3 gripPosition, Quat gripRotation,
		float engineLinear, float engineAngular, LinkHand hand, ref StillnessGate still, RigidOffsetAverager solver, ref bool announced )
	{
		if ( !engineOk || !hand.Pose.IsValid )
		{
			still.Reset();
			return;
		}

		float linear = MathF.Max( engineLinear, hand.Pose.Velocity.Length() );
		float angular = MathF.Max( engineAngular, hand.Pose.AngularVelocity.Length() );

		// Hands are never as still as a head, so the turning limit is a little looser.
		SpaceAlignerSettings limits = _aligner.Settings;
		if ( !still.Update( now, linear, angular, limits.MaxLinearSpeed, limits.MaxAngularSpeed * 1.5f, limits.StillSeconds ) ) return;

		RigidTransform reading = AlignmentMath.LocalOffset( _aligner.Current, hand.Pose.Position, hand.Pose.Rotation, gripPosition, gripRotation );
		int tooFarBefore = solver.RejectedTooFar;
		solver.Add( reading );

		if ( solver.HasEstimate && !announced )
		{
			announced = true;
			Vec3 t = solver.Current.Translation;
			Log.Info( $"[HelperLink] {side} hand: the engine's grip point is ({t.X * 100f:0.0}, {t.Y * 100f:0.0}, {t.Z * 100f:0.0}) cm from the helper's raw controller pose, turned {AlignmentMath.AngleBetween( solver.Current.Rotation, Quat.Identity ) * 57.29578f:0} deg." );
		}
		if ( solver.RejectedTooFar != tooFarBefore && solver.RejectedTooFar == 50 )
		{
			Log.Warning( $"[HelperLink] {side} hand: the helper's controller and the engine's are {reading.Translation.Length():0.00} m apart, 50 times running. They are not the same controller (left and right swapped?). Helper throws stay off for this hand." );
		}
	}

	// ------------------------------------------------------------------ desk test of the lining-up

	/// <summary>
	/// Desk test only (PortaballsGame.HelperDeskAlignTest). There is no headset, so there are no engine
	/// poses to line the helper's up with. With this on, the "engine's" poses are made up from the
	/// helper's own: each pose is pushed ahead in time by <see cref="PretendLookAheadSeconds"/> using
	/// its reported speed (the way a VR runtime guesses ahead), hands are moved to a made-up grip
	/// point (<see cref="PretendGripOffset"/>), and everything is carried through a made-up
	/// turn-and-shift (<see cref="PretendAlignment"/>). The solver is told none of that and has to
	/// find it, and <see cref="PretendReport"/> says how close it got.
	/// </summary>
	public bool PretendEngine { get; set; }

	/// <summary>The made-up truth: helper space to play-area space.</summary>
	public RigidTransform PretendAlignment { get; set; } = RigidTransform.Identity;

	/// <summary>The made-up truth: the grip point on the raw controller pose, the same for both hands.</summary>
	public RigidTransform PretendGripOffset { get; set; } = RigidTransform.Identity;

	/// <summary>How far ahead the pretend engine guesses its poses, seconds.</summary>
	public float PretendLookAheadSeconds { get; set; } = 0.030f;

	private void PretendPose( LinkPose pose, RigidTransform localOffset, out Vec3 position, out Quat rotation )
	{
		float ahead = PretendLookAheadSeconds;
		Vec3 p = pose.Position + pose.Velocity * ahead;
		Vec3 turn = pose.AngularVelocity * ahead;
		float angle = turn.Length();
		Quat q = angle > 1e-9f ? Quat.CreateFromAxisAngle( turn / angle, angle ) * pose.Rotation : pose.Rotation;
		p += Vec3.Transform( localOffset.Translation, q );
		q = Quat.Normalize( q * localOffset.Rotation );
		position = PretendAlignment.TransformPoint( p );
		rotation = PretendAlignment.TransformRotation( q );
	}

	/// <summary>How far the solved alignment and grip offsets are from the made-up truth. Empty unless PretendEngine.</summary>
	public string PretendReport()
	{
		if ( !PretendEngine ) return "";
		if ( !_aligner.HasEstimate ) return "PRETEND ENGINE: not lined up yet";

		// Judged at a hand's distance from the head and at the far wall.
		Vec3 near = new Vec3( 0.5f, -0.3f, 1.0f );
		Vec3 far = new Vec3( 5f, 0f, 1.3f );
		float nearError = ( _aligner.Current.TransformPoint( near ) - PretendAlignment.TransformPoint( near ) ).Length();
		float farError = ( _aligner.Current.TransformPoint( far ) - PretendAlignment.TransformPoint( far ) ).Length();
		float turnError = AlignmentMath.AngleBetween( _aligner.Current.Rotation, PretendAlignment.Rotation ) * 57.29578f;
		string text = $"PRETEND ENGINE: alignment error {nearError * 1000f:0.0} mm at the hand, {farError * 1000f:0.0} mm at the far wall, {turnError:0.00} deg ({_aligner.Accepted} readings)";
		text += "; grip offset error " + OffsetError( "L", _leftOffset ) + ", " + OffsetError( "R", _rightOffset );
		return text;
	}

	private string OffsetError( string label, RigidOffsetAverager solver )
	{
		if ( !solver.HasEstimate ) return label + " not found yet";
		float mm = ( solver.Current.Translation - PretendGripOffset.Translation ).Length() * 1000f;
		float deg = AlignmentMath.AngleBetween( solver.Current.Rotation, PretendGripOffset.Rotation ) * 57.29578f;
		return $"{label} {mm:0.0} mm {deg:0.00} deg ({solver.Accepted} readings)";
	}
}