using Sandbox;
using Sandbox.VR;
using Portaballs.Core;

namespace Portaballs;

/// <summary>What one throw did, for the readout panel and the console.</summary>
public struct ThrowReport
{
	/// <summary>False until this hand has thrown at least once.</summary>
	public bool Valid;

	/// <summary>Counts up with every throw from this hand.</summary>
	public int Number;

	/// <summary>When it was thrown, shared clock (see HelperClock). Lets a reader pick the newest of two hands.</summary>
	public double ClockTime;

	public ThrowHand.HandSide Side;
	public PaintColor Paint;

	/// <summary>The ball's speed as it left the hand (after aim assist), m/s.</summary>
	public float Speed;

	/// <summary>True when the throw was worked out from the helper's 500 Hz samples; false: the engine's per-frame poses.</summary>
	public bool FromHelper;

	/// <summary>The release delay taken off the grip's release time for this throw, milliseconds.</summary>
	public float DelayMs;

	/// <summary>True when that delay was learned from earlier throws rather than set by hand.</summary>
	public bool DelayLearned;

	public bool IsDrop;
	public AimAssistOutcome Aim;
	public AimSource AimSource;

	/// <summary>True when the aim point was the eyes but only a glance, not a steady look (it then pulls like the head does).</summary>
	public bool AimGlance;

	/// <summary>How long after the release moment the frame came that noticed it, milliseconds.</summary>
	public float NoticedLateMs;
}

/// <summary>
/// One hand that can throw paint balls. There is one of these per hand.
///
/// It can work from two sources and picks the better one every frame:
///
///   HELPER   the helper program's samples, 500 a second, each with the time it was taken, plus the
///            time each grip reading last changed and where the eyes are looking. Used while the
///            link is fresh and the helper's space has been lined up with the play area.
///   FRAME    the engine's own controller pose and grip, read once per drawn frame and stamped with
///            that frame's time. Used whenever the helper is missing, stale, or not lined up yet.
///
/// Both run all the time they have data, each with its own pose history, its own release detector
/// and its own learned delay, so switching between them needs no warm-up. Whichever is in use, the
/// steps are the ones the core documents:
///   1. poses into a pose history;
///   2. grip readings into a release detector;
///   3. on "Released": work out the launch from the history, nudge it with aim assist, spawn the ball;
///   4. about 80 ms later: let the delay calibrator look at that throw.
///
/// Neither history is kept in world space. The FRAME history is in the play area's own space (the
/// VR anchor's local space) and the HELPER history is in the helper's space, so moving the anchor
/// (stepping through a portal) never looks like the hand flying across the room. The throw is
/// worked out there, with gravity turned into that space, and only the result is carried into the
/// world, using wherever the anchor is at the moment of the throw.
///
/// This component does all the engine talking. All the maths lives in core\src\Throwing.
/// </summary>
public sealed class ThrowHand : Component
{
	public enum HandSide
	{
		Left,
		Right,
	}

	/// <summary>Which control counts as "squeezing".</summary>
	public enum SqueezeSource
	{
		Grip,
		Trigger,
		/// <summary>Whichever of grip and trigger is squeezed harder.</summary>
		Strongest,
	}

	// ---- set by VrRig (or by the desk test rig) ----

	[Property] public HandSide Side { get; set; } = HandSide.Right;

	/// <summary>The paint this hand throws.</summary>
	[Property] public PaintColor Paint { get; set; } = PaintColor.Blue;

	/// <summary>The room whose walls the aim rays are tested against. No room, no aim assist.</summary>
	public TestRoom Room { get; set; }

	/// <summary>The link to the helper program. None: only the engine's per-frame poses are ever used.</summary>
	public HelperLink Link { get; set; }

	/// <summary>
	/// Desk test only: the object standing in for the play-area anchor. In VR the engine's own anchor
	/// (Input.VR.Anchor) is used and this is ignored.
	/// </summary>
	public GameObject AnchorObject { get; set; }

	/// <summary>
	/// Desk test only (PortaballsGame.HelperDeskTest): there is no VR, so this hand is driven entirely
	/// by helper samples, and this object is moved to follow the helper's controller pose so it can be seen.
	/// </summary>
	public bool DeskHelperTest { get; set; }

	// ---- feel settings: grip ----

	[Property] public SqueezeSource Squeeze { get; set; } = SqueezeSource.Grip;

	/// <summary>The hand counts as holding once the squeeze rises through this (0 to 1).</summary>
	[Property] public float GrabThreshold { get; set; } = 0.5f;

	/// <summary>Let go when the squeeze falls below this share of the hardest squeeze since grabbing.</summary>
	[Property] public float ReleaseFraction { get; set; } = 0.75f;

	/// <summary>The let-go level never goes below this.</summary>
	[Property] public float ReleaseFloor { get; set; } = 0.15f;

	// ---- feel settings: throw ----

	/// <summary>Use the helper's samples when they are fresh. Untick to force the engine's per-frame poses.</summary>
	[Property] public bool UseHelper { get; set; } = true;

	/// <summary>
	/// FRAME source: how long before the release was noticed the player really meant to let go, seconds.
	/// Replaced by the learned value once calibration is trusted.
	/// </summary>
	[Property] public float ReleaseTimeOffset { get; set; } = 0.011f;

	/// <summary>
	/// HELPER source: the same, but starting from zero, because the helper's grip readings carry the
	/// time they really changed instead of the time a frame happened to look. Also replaced by its own
	/// learned value once that is trusted.
	/// </summary>
	[Property] public float HelperReleaseTimeOffset { get; set; } = 0f;

	/// <summary>Length of the stretch of hand motion used to measure velocity, seconds.</summary>
	[Property] public float FitWindow { get; set; } = 0.060f;

	/// <summary>Use the fastest moment of the swing for the speed.</summary>
	[Property] public bool UsePeakSpeed { get; set; } = true;

	/// <summary>
	/// Overall strength multiplier for real throws (the core allows 0.5 to 2). Only a full throw is boosted; a let-go
	/// stays a let-go. History, all from the tester in the headset on 2026-11-19:
	///   1.0  throws measured 4.3 to 7.9 m/s, several "out of reach" for the aim help: "it doesn't seem like it's actually reaching".
	///   1.5  199 real throws, 8.0 m/s in the middle (5.8 at the 10th percentile, 11.2 at the 90th, 14.5 the fastest):
	///        "a little too weak ... just a little bit faster ... not too fast".
	///   1.7  13 percent faster: the middle throw goes from 8.0 to about 9.0 m/s, reaches a wall 12 percent sooner and
	///        drops about a fifth less on the way (0.53 m to 0.41 m).
	/// With the same arm a faster ball lands HIGHER, and at 1.5 their throws already landed above where they were looking
	/// (0.27 m in the middle case before aim help). The stronger eye pull below is sized so the ball still ends nearer
	/// the look point than it did (0.23 m against 0.28 m), though a little higher above it (0.17 m against 0.13 m).
	/// </summary>
	[Property] public float SpeedScale { get; set; } = 1.7f;

	/// <summary>No throw leaves faster than this, m/s.</summary>
	[Property] public float MaxSpeed { get; set; } = 30f;

	/// <summary>At or below this hand speed (m/s) a release is a plain let-go.</summary>
	[Property] public float DropSpeed { get; set; } = 0.6f;

	/// <summary>At or above this hand speed (m/s) a release is a full throw.</summary>
	[Property] public float FullThrowSpeed { get; set; } = 2.0f;

	/// <summary>
	/// Where the held ball sits relative to the controller's grip point, in the controller's own axes,
	/// METRES (not inches). Zero means "in the middle of the palm". Not yet tuned in a headset.
	/// </summary>
	[Property] public Vector3 BallOffsetMetres { get; set; } = new Vector3( 0f, 0f, 0f );

	/// <summary>
	/// Size of the splat a thrown ball makes, metres. 0.42 is the size the paint was tuned and pictured
	/// with: big enough that one hit can run down into a doorway (an opening needs a 0.30 m circle,
	/// which a 0.25 m splat can never hold).
	/// </summary>
	[Property] public float SplatRadius { get; set; } = 0.42f;

	// ---- feel settings: aim assist ----

	[Property] public AimAssistMode AimMode { get; set; } = AimAssistMode.EyeWithHeadFallback;

	/// <summary>Head aim: throws within this angle of the perfect throw get the full pull, degrees.</summary>
	[Property] public float HeadAimInnerDegrees { get; set; } = 10f;

	/// <summary>Head aim: throws further off than this get no pull, degrees.</summary>
	[Property] public float HeadAimOuterDegrees { get; set; } = 25f;

	/// <summary>
	/// Head aim: share of the error removed at full pull (0 to 1). Also what a GLANCE pulls with (see GlanceLookBack):
	/// both are a less sure guess at the target than a steady look. 0.4 until 2026-11-19.
	/// </summary>
	[Property] public float HeadAimStrength { get; set; } = 0.45f;

	/// <summary>Eye aim (needs the helper and an eye tracker): full pull inside this angle, degrees.</summary>
	[Property] public float EyeAimInnerDegrees { get; set; } = 15f;

	/// <summary>Eye aim: no pull beyond this angle, degrees. This is what leaves a throw meant for somewhere else alone.</summary>
	[Property] public float EyeAimOuterDegrees { get; set; } = 30f;

	/// <summary>
	/// Eye aim: share of the error removed at full pull (0 to 1). 0.5 until the tester's session of 2026-11-19: "too weak ...
	/// strengthen it just a little bit". Rebuilt from that session, 0.5 left the ball 0.28 m from the look point in the
	/// middle case (0.38 m on the far wall), from 0.53 m with no help. With the faster throw 0.6 would leave 0.26 m,
	/// which they would not notice; 0.65 leaves 0.23 m. Sensible range 0.5 to 0.8.
	/// </summary>
	[Property] public float EyeAimStrength { get; set; } = 0.65f;

	/// <summary>
	/// Eye aim: how far back from the release to look for a steady gaze, seconds. Left at 0.30: in the tester's session the
	/// looks that had ended more than 100 ms before the release were worse targets than fresh ones.
	/// </summary>
	[Property] public float GazeLookBack { get; set; } = 0.30f;

	/// <summary>Eye aim: the gaze must hold steady at least this long to count, seconds. 0.10 until 2026-11-19.</summary>
	[Property] public float GazeMinDuration { get; set; } = 0.07f;

	/// <summary>
	/// Eye aim: "steady" means every reading within this angle of the look's average, degrees. 2 until 2026-11-19,
	/// when 14 percent of the tester's real throws found no steady look and fell back to the head: the looks that were found
	/// crowded the 2 degree line, because a hard throw shakes the head and the reading with it.
	/// </summary>
	[Property] public float GazeMaxSpreadDegrees { get; set; } = 3f;

	/// <summary>
	/// Eye aim, second try. When there is no steady look, use a GLANCE: where the eyes were pointing just before the
	/// release, if they held one way for GlanceMinDuration within this many seconds of it. A glance pulls with the
	/// head's gentler numbers. 0 switches the glance off (no steady look then means head aim, as before 2026-11-19).
	/// </summary>
	[Property] public float GlanceLookBack { get; set; } = 0.12f;

	/// <summary>Glance: how long the eyes must have pointed one way, seconds.</summary>
	[Property] public float GlanceMinDuration { get; set; } = 0.04f;

	/// <summary>Glance: "one way" means within this angle, degrees. Tight enough to refuse eyes in the middle of a jump.</summary>
	[Property] public float GlanceMaxSpreadDegrees { get; set; } = 4.5f;

	// ---- feel settings: delay calibration and haptics ----

	/// <summary>Use the release delay learned from real throws once it is trusted.</summary>
	[Property] public bool UseLearnedDelay { get; set; } = true;

	/// <summary>How long after a release the calibrator looks at that throw, seconds. Later lets it see the hand slow down.</summary>
	[Property] public float CalibrationDelay { get; set; } = 0.08f;

	/// <summary>A short buzz in the hand at the moment of release.</summary>
	[Property] public bool ReleaseHaptics { get; set; } = true;

	// ---- for the readout panel ----

	/// <summary>The last throw, in world space.</summary>
	public ThrowResult LastThrow { get; private set; }
	public AimAssistResult LastAssist { get; private set; }
	public ThrowReport LastReport { get; private set; }
	public bool IsHolding => _holding;

	/// <summary>True on frames where a throw right now would be worked out from helper samples.</summary>
	public bool HelperInUse { get; private set; }

	/// <summary>Why the helper is not in use, in a few words ("" when it is).</summary>
	public string HelperBlocker { get; private set; } = "no helper";

	// ------------------------------------------------------------------ the two sources

	/// <summary>Everything one source keeps between frames.</summary>
	private sealed class Source
	{
		public readonly string Name;
		public readonly GripReleaseDetector Grip = new GripReleaseDetector();
		public readonly ReleaseDelayCalibrator Calibrator = new ReleaseDelayCalibrator();
		public Vec3 BallLocalOffset;
		public bool ObservePending;
		public double ObserveAt;
		public double ObserveEventTime;
		public bool DelayAnnounced;

		public Source( string name )
		{
			Name = name;
		}
	}

	private readonly Source _frame = new Source( "frame" );
	private readonly Source _helper = new Source( "helper" );

	/// <summary>FRAME source poses: the engine's grip pose in the anchor's own space, stamped with the frame's time.</summary>
	private readonly PoseHistory _frameHistory = new PoseHistory();

	/// <summary>Turns the helper's "time of last change" grip readings into detector readings.</summary>
	private readonly ChangeTimeGripFeed _helperFeed = new ChangeTimeGripFeed();

	/// <summary>
	/// A second detector that is only shown the newest helper grip value once per frame, the way the
	/// FRAME source sees a grip. It throws nothing; it is there to measure, on every helper throw, how
	/// far off a once-per-frame reading would have put the release.
	/// </summary>
	private readonly GripReleaseDetector _perFrameShadow = new GripReleaseDetector();

	/// <summary>The same again, but only shown a value 90 times a second: what a headset running at 90 frames a second would see.</summary>
	private readonly GripReleaseDetector _ninetyShadow = new GripReleaseDetector();
	private double _ninetyNextAt;
	private double _perFrameShadowRelease = double.NaN;
	private double _ninetyShadowRelease = double.NaN;
	private bool _gainPending;
	private double _gainEventTime;
	private double _gainDeadline;
	private int _gainNumber;

	private readonly ThrowSettings _throw = new ThrowSettings();
	private readonly AimAssistSettings _aim = new AimAssistSettings();
	private readonly GazeFixationSettings _gaze = new GazeFixationSettings();
	private readonly GazeFixationSettings _glance = GazeFixationSettings.ForGlance();

	private GameObject _heldBall;
	private bool _holding;
	private long _cursor;                 // next helper sample this hand has not looked at yet
	private bool _helperHasSqueeze;
	private float _helperSqueeze;
	private bool _helperWasFresh;
	private bool _frameReleasePending;    // the frame source let go while the helper still says "held"
	private double _frameReleasePendingSince;
	private double _frameReleaseEventTime;
	private int _throwCount;
	private bool _lastSourceWasHelper;
	private bool _sourceAnnounced;

	protected override void OnStart()
	{
		// The ball you see in your hand: a plain sphere with no physics, shown only while holding.
		_heldBall = new GameObject( GameObject, false, "Held Ball" );
		_heldBall.LocalPosition = SboxConvert.ToInches( SboxConvert.ToCore( BallOffsetMetres ) );
		_heldBall.LocalScale = SboxConvert.SphereScaleFor( Portaball.RadiusMetres );
		PortalTwinSource.Register( GameObject, _heldBall, Paint );   // shown at both ends of a portal while the hand is in an opening (PortalBallTwins)

		ModelRenderer renderer = _heldBall.AddComponent<ModelRenderer>();
		renderer.Model = Model.Sphere;
		PaintLook.Glow( renderer, Paint );
	}

	protected override void OnUpdate()
	{
		bool vr = Game.IsRunningInVR && Input.VR is not null;
		if ( !vr && !DeskHelperTest ) return;

		VRController controller = null;
		if ( vr ) controller = Side == HandSide.Left ? Input.VR.LeftHand : Input.VR.RightHand;

		// Two clocks, never mixed. The FRAME source uses the scene clock: the engine reads controller
		// poses once per frame and gives them no timestamps, so "this frame's time" is the best there is.
		// The HELPER source uses the PC-wide clock its samples are stamped with.
		double frameNow = Time.NowDouble;
		double clockNow = HelperClock.Now;

		ApplyGripSettings( _frame.Grip.Settings );
		ApplyGripSettings( _helper.Grip.Settings );
		ApplyGripSettings( _perFrameShadow.Settings );
		ApplyGripSettings( _ninetyShadow.Settings );

		// ---- FRAME source: this frame's pose and grip ----
		bool frameGrabbed = false, frameReleased = false;
		if ( controller is not null )
		{
			// 1. Pose into the history, in the anchor's own space, in metres. Skipped while the controller
			//    is not tracked, so the history never fills with a frozen pose.
			if ( controller.Active )
			{
				SboxConvert.ToCorePose( Input.VR.Anchor.ToLocal( controller.Transform ), out Vec3 positionMetres, out Quat rotation );
				_frameHistory.Add( frameNow, positionMetres, rotation );
			}

			// 2. Grip into the release detector.
			GripEvent e = _frame.Grip.Update( frameNow, ReadSqueeze( controller ) );
			frameGrabbed = e == GripEvent.Grabbed;
			frameReleased = e == GripEvent.Released;
		}

		// ---- HELPER source: every sample that arrived since last frame ----
		bool helperGrabbed = false, helperReleased = false;
		bool linkFresh = UseHelper && Link.IsValid() && Link.IsFresh( clockNow );
		PoseHistory helperHistory = HelperHistory();
		if ( Link.IsValid() ) ReadHelperSamples( ref helperGrabbed, ref helperReleased );

		if ( !linkFresh && _helperWasFresh )
		{
			// The helper stopped. Its detector must not sit there "holding" a ball until it comes back.
			_helper.Grip.Reset();
			_helperFeed.Reset();
			_perFrameShadow.Reset();
			_ninetyShadow.Reset();
			_helperHasSqueeze = false;
			_helper.ObservePending = false;
			_gainPending = false;
		}
		_helperWasFresh = linkFresh;

		// The two comparison detectors: the newest helper grip value, looked at once per frame, and 90 times a second.
		if ( linkFresh && _helperHasSqueeze )
		{
			if ( _perFrameShadow.Update( clockNow, _helperSqueeze ) == GripEvent.Released ) _perFrameShadowRelease = _perFrameShadow.ReleaseTime;
			if ( clockNow >= _ninetyNextAt )
			{
				_ninetyNextAt = clockNow - _ninetyNextAt > 0.05 ? clockNow + 1.0 / 90.0 : _ninetyNextAt + 1.0 / 90.0;
				if ( _ninetyShadow.Update( clockNow, _helperSqueeze ) == GripEvent.Released ) _ninetyShadowRelease = _ninetyShadow.ReleaseTime;
			}
		}

		bool helperUsable = HelperUsable( linkFresh, helperHistory, clockNow );
		HelperInUse = helperUsable;
		AnnounceSource( helperUsable );

		if ( DeskHelperTest ) FollowHelperPose( helperHistory );

		// ---- 3. Grabbed and released ----
		// Either source noticing a grab is enough to show the ball in the hand.
		if ( helperGrabbed || frameGrabbed ) _holding = true;

		if ( _holding )
		{
			if ( helperUsable )
			{
				if ( helperReleased )
				{
					Throw( true, _helper.Grip.ReleaseTime, controller, frameNow, clockNow );
				}
				else if ( !_helper.Grip.IsHeld )
				{
					// The helper is the source in use but its grip never saw this hold (it came back mid-hold,
					// or its grip reading is not live). Let the engine's own grip decide.
					if ( frameReleased ) Throw( false, _frame.Grip.ReleaseTime, controller, frameNow, clockNow );
					else if ( !_frame.Grip.IsHeld ) _holding = false;
				}
				else if ( frameReleased )
				{
					// The engine's grip let go first. The helper's is normally a few milliseconds behind: wait for it.
					_frameReleasePending = true;
					_frameReleasePendingSince = clockNow;
					_frameReleaseEventTime = _frame.Grip.ReleaseTime;
				}

				if ( _holding && _frameReleasePending && clockNow - _frameReleasePendingSince > 0.10 )
				{
					Throw( false, _frameReleaseEventTime, controller, frameNow, clockNow );   // it never came
				}
			}
			else
			{
				if ( frameReleased ) Throw( false, _frame.Grip.ReleaseTime, controller, frameNow, clockNow );
				else if ( _frameReleasePending ) Throw( false, _frameReleaseEventTime, controller, frameNow, clockNow );
				else if ( !_frame.Grip.IsHeld ) _holding = false;
			}
		}
		if ( !_holding ) _frameReleasePending = false;

		if ( _heldBall.IsValid() && _heldBall.Enabled != _holding )
		{
			_heldBall.Enabled = _holding;
		}

		// ---- 4. A little after a release: calibration, each source from its own history and clock ----
		RunPendingCalibration( _frame, _frameHistory, frameNow );
		if ( helperHistory is not null ) RunPendingCalibration( _helper, helperHistory, clockNow );

		LogPendingGain( clockNow );
	}

	// ------------------------------------------------------------------ reading the two sources

	private void ApplyGripSettings( GripSettings settings )
	{
		settings.GrabThreshold = GrabThreshold;
		settings.ReleaseFraction = ReleaseFraction;
		settings.ReleaseFloor = ReleaseFloor;
	}

	private float ReadSqueeze( VRController controller )
	{
		float grip = controller.Grip.Value;
		float trigger = controller.Trigger.Value;
		switch ( Squeeze )
		{
			case SqueezeSource.Trigger: return trigger;
			case SqueezeSource.Strongest: return grip > trigger ? grip : trigger;
			default: return grip;
		}
	}

	/// <summary>This hand's poses as the helper sent them: raw SteamVR controller poses in the helper's space.</summary>
	private PoseHistory HelperHistory()
	{
		if ( !Link.IsValid() ) return null;
		return Side == HandSide.Left ? Link.Left : Link.Right;
	}

	/// <summary>
	/// Look at every helper sample that has arrived since the last frame and feed this hand's grip to
	/// the release detector. The pair fed is (time the reading last CHANGED, value), not (time the
	/// helper polled, value): that is what places a release finer than the helper's own 2 ms polling.
	/// </summary>
	private void ReadHelperSamples( ref bool grabbed, ref bool released )
	{
		long end = Link.TotalSamples;
		if ( _cursor > end ) _cursor = end;
		if ( end - _cursor > HelperLink.RingCapacity ) _cursor = end - HelperLink.RingCapacity;

		for ( ; _cursor < end; _cursor++ )
		{
			LinkSample s = Link.SampleAt( _cursor );
			LinkHand hand = Side == HandSide.Left ? s.Left : s.Right;
			if ( !hand.InputActive ) continue;

			float value;
			double changedAt;
			switch ( Squeeze )
			{
				case SqueezeSource.Trigger:
					value = hand.Trigger;
					changedAt = hand.TriggerTime;
					break;
				case SqueezeSource.Strongest:
					value = hand.Grip > hand.Trigger ? hand.Grip : hand.Trigger;
					changedAt = hand.GripTime > hand.TriggerTime ? hand.GripTime : hand.TriggerTime;
					break;
				default:
					value = hand.Grip;
					changedAt = hand.GripTime;
					break;
			}

			_helperHasSqueeze = true;
			_helperSqueeze = value;
			GripEvent e = _helperFeed.Feed( _helper.Grip, changedAt, value );
			if ( e == GripEvent.Grabbed ) grabbed = true;
			else if ( e == GripEvent.Released ) released = true;
		}
	}

	private bool HelperUsable( bool linkFresh, PoseHistory helperHistory, double clockNow )
	{
		if ( !UseHelper ) { HelperBlocker = "switched off"; return false; }
		if ( !Link.IsValid() || !Link.Connected ) { HelperBlocker = "no helper"; return false; }
		if ( !linkFresh ) { HelperBlocker = "helper stale"; return false; }
		if ( !Link.AlignmentValid ) { HelperBlocker = "hold head still"; return false; }
		if ( !Link.GripOffsetValid( Side ) ) { HelperBlocker = "hold hand still"; return false; }
		if ( helperHistory is null || helperHistory.Count < 3 || clockNow - helperHistory.NewestTime > Link.StaleSeconds )
		{
			HelperBlocker = "hand not tracked";
			return false;
		}
		HelperBlocker = "";
		return true;
	}

	private void AnnounceSource( bool helperUsable )
	{
		if ( _sourceAnnounced && helperUsable == _lastSourceWasHelper ) return;
		_sourceAnnounced = true;
		_lastSourceWasHelper = helperUsable;
		Log.Info( helperUsable
			? $"[ThrowHand] {Side}: throwing from HELPER samples now."
			: $"[ThrowHand] {Side}: throwing from the engine's per-frame poses ({HelperBlocker})." );
	}

	/// <summary>Where the play area is in the world right now.</summary>
	private RigidTransform AnchorToWorld()
	{
		if ( Game.IsRunningInVR && Input.VR is not null ) return SboxConvert.ToRigid( Input.VR.Anchor );
		if ( AnchorObject.IsValid() ) return SboxConvert.ToRigid( AnchorObject.WorldTransform );
		return RigidTransform.Identity;
	}

	/// <summary>Desk test: put this object where the helper says the controller's grip point is, so the hand can be seen.</summary>
	private void FollowHelperPose( PoseHistory helperHistory )
	{
		if ( helperHistory is null || helperHistory.Count == 0 ) return;
		PoseSample raw = helperHistory[helperHistory.Count - 1];
		RigidTransform grip = Link.GripOffset( Side );
		RigidTransform helperToWorld = Link.HelperToAnchor.Then( AnchorToWorld() );
		Vec3 position = helperToWorld.TransformPoint( raw.Position + Vec3.Transform( grip.Translation, raw.Rotation ) );
		Quat rotation = helperToWorld.TransformRotation( raw.Rotation * grip.Rotation );
		WorldPosition = SboxConvert.ToInches( position );
		WorldRotation = SboxConvert.ToEngine( rotation );
	}

	// ------------------------------------------------------------------ the throw

	/// <param name="fromHelper">Which source's history and clock to use.</param>
	/// <param name="eventTime">The release time from that source's detector, on that source's clock.</param>
	private void Throw( bool fromHelper, double eventTime, VRController controller, double frameNow, double clockNow )
	{
		_holding = false;
		_frameReleasePending = false;

		Source source = fromHelper ? _helper : _frame;
		ApplyThrowSettings( source, fromHelper );

		// The space the history is in, and how to get from it to the world AT THIS MOMENT.
		RigidTransform anchorToWorld = AnchorToWorld();
		RigidTransform localToWorld;
		PoseHistory history;
		double now;
		Vec3 ballInGrip = SboxConvert.ToCore( BallOffsetMetres );
		if ( fromHelper )
		{
			localToWorld = Link.HelperToAnchor.Then( anchorToWorld );
			history = HelperHistory();
			now = clockNow;
			// The history holds SteamVR's raw controller pose, not the engine's grip pose. The ball's place
			// on the raw pose is the grip point's place on it, plus the ball's place on the grip.
			_throw.BallLocalOffset = Link.GripOffset( Side ).TransformPoint( ballInGrip );
		}
		else
		{
			localToWorld = anchorToWorld;
			history = _frameHistory;
			now = frameNow;
			_throw.BallLocalOffset = ballInGrip;
		}
		source.BallLocalOffset = _throw.BallLocalOffset;

		// Use whatever gravity the engine is really using, so the ball's catch-up along its path and the
		// aim assist both agree with how the ball will actually fly. The estimator works inside the
		// history's space, so it gets gravity as seen from in there.
		Vec3 worldGravity = SboxConvert.ToMetres( Scene.PhysicsWorld.Gravity );
		_throw.Gravity = ThrowSpace.GravityInLocal( localToWorld, worldGravity );
		_aim.Gravity = worldGravity;

		ThrowResult local = ThrowEstimator.Estimate( history, eventTime, now, _throw );
		if ( !local.Valid )
		{
			Log.Info( $"[ThrowHand] {Side}: released, but the {source.Name} source has no poses to throw from. Nothing thrown." );
			return;
		}
		ThrowResult result = ThrowSpace.ToWorld( local, localToWorld );
		LastThrow = result;

		// The release moment on the shared clock, for looking up the gaze (and for the log).
		double releaseOnClock = fromHelper ? result.ReleaseTime : clockNow - ( frameNow - result.ReleaseTime );
		float noticedLateMs = (float)( ( now - result.ReleaseTime ) * 1000.0 );

		// Aim assist works on the launch as it left the hand, in the world.
		AimPoint headPoint = FindHeadAimPoint( anchorToWorld );
		AimPoint eyePoint = FindEyeAimPoint( releaseOnClock, clockNow, anchorToWorld, out string gazeNote );
		AimAssistResult assist = AimAssist.Apply( _aim, result.ReleasePosition, result.ReleaseVelocity, result.IsDrop, headPoint, eyePoint );
		LastAssist = assist;

		// The estimator has already moved the ball along its path to where it would be by now.
		Vec3 spawnPosition = result.NowPosition;
		Vec3 spawnVelocity = result.NowVelocity;
		if ( assist.Applied )
		{
			// The launch changed, so redo that same catch-up with the nudged velocity.
			float flown = result.Diagnostics.RollbackSeconds;
			spawnPosition = Ballistics.PositionAt( result.ReleasePosition, assist.Velocity, worldGravity, flown );
			spawnVelocity = Ballistics.VelocityAt( assist.Velocity, worldGravity, flown );
		}

		// A hand in a portal's opening or through it lets go THERE: the ball starts on the far side, carried as a ball in
		// flight is, and with no aim help (the look point is in the room the hand left). Anywhere else this does nothing
		// and the ball is spawned exactly as before. See HandPortalRelease (Code\Game\Portals).
		if ( HandPortalRelease.TryRelease( GameObject, Room, Paint, result.ReleasePosition, result.ReleaseVelocity, result.AngularVelocity,
			result.Diagnostics.RollbackSeconds, worldGravity, SplatRadius, Side.ToString() ) )
			LastAssist = assist = AimAssist.Apply( _aim, result.ReleasePosition, result.ReleaseVelocity, result.IsDrop, AimPoint.None, AimPoint.None );
		else
		Portaball.Spawn( Scene, Paint, spawnPosition, spawnVelocity, result.AngularVelocity, SplatRadius );

		if ( ReleaseHaptics && controller is not null )
		{
			controller.TriggerHaptics( HapticEffect.SoftImpact );
		}

		// Ask the calibrator to look at this throw shortly, with the SAME event time the estimator got.
		source.ObservePending = true;
		source.ObserveEventTime = eventTime;
		source.ObserveAt = now + CalibrationDelay;

		bool learned = UseLearnedDelay && source.Calibrator.IsConfident;
		_throwCount++;
		ThrowReport report = new ThrowReport();
		report.Valid = true;
		report.Number = _throwCount;
		report.ClockTime = clockNow;
		report.Side = Side;
		report.Paint = Paint;
		report.Speed = assist.Velocity.Length();
		report.FromHelper = fromHelper;
		report.DelayMs = (float)( _throw.ReleaseTimeOffset * 1000.0 );
		report.DelayLearned = learned;
		report.IsDrop = result.IsDrop;
		report.Aim = assist.Outcome;
		report.AimSource = assist.Source;
		report.AimGlance = assist.Glance;
		report.NoticedLateMs = noticedLateMs;
		LastReport = report;

		Log.Info( $"[ThrowHand] {Side} threw {Paint} #{_throwCount}: {report.Speed:0.00} m/s, SOURCE {source.Name}, from ({result.ReleasePosition.X:0.00}, {result.ReleasePosition.Y:0.00}, {result.ReleasePosition.Z:0.00}) m, method {result.Diagnostics.Method} ({result.Diagnostics.SamplesUsed} poses, {result.Diagnostics.FitUsed} fit), drop {result.IsDrop}, aim assist {assist.Outcome} via {assist.Source}{( assist.Glance ? " (glance)" : "" )}" );
		Log.Info( $"[ThrowHand]   TIMING #{_throwCount}: release used t={result.ReleaseTime:0.0000} s = grip let-go t={eventTime:0.0000} s minus delay {report.DelayMs:0.0} ms ({( learned ? "learned" : "set" )}); the frame that noticed was t={now:0.0000} s, {noticedLateMs:0.0} ms later{( result.Diagnostics.RollbackClamped ? " (catch-up was capped)" : "" )}" );
		Log.Info( $"[ThrowHand]   GAZE #{_throwCount}: {gazeNote}" );
		Log.Info( $"[ThrowHand]   AIM #{_throwCount}: {AimNote( result, assist )}" );

		if ( fromHelper )
		{
			// The comparison detectors let go a frame or so after the helper's; report once they have.
			_gainPending = true;
			_gainEventTime = eventTime;
			_gainNumber = _throwCount;
			_gainDeadline = clockNow + 0.10;
		}
	}

	/// <summary>
	/// A little after a helper throw: say how much finer the helper placed the release than a grip
	/// value read once per frame (and one read 90 times a second) would have. With the helper's fake
	/// source the true moment is known too, so all three are scored against it.
	/// </summary>
	private void LogPendingGain( double clockNow )
	{
		if ( !_gainPending ) return;
		// A comparison detector's release belongs to this throw if it happened around the same time.
		bool frameReady = !double.IsNaN( _perFrameShadowRelease ) && _perFrameShadowRelease > _gainEventTime - 0.05;
		bool ninetyReady = !double.IsNaN( _ninetyShadowRelease ) && _ninetyShadowRelease > _gainEventTime - 0.05;
		if ( !( frameReady && ninetyReady ) && clockNow < _gainDeadline ) return;
		_gainPending = false;

		string perFrame = frameReady ? Ms( _perFrameShadowRelease - _gainEventTime ) + " ms" : "(it never saw this grab)";
		string ninety = ninetyReady ? Ms( _ninetyShadowRelease - _gainEventTime ) + " ms" : "(it never saw this grab)";
		string line = $"against the helper's let-go time, a grip read once per frame at this frame rate ({( Time.Delta > 0f ? 1f / Time.Delta : 0f ):0} fps) is off by {perFrame}; read 90 times a second, by {ninety}";

		if ( Link.IsValid() && Link.SourceName == "fake" && Squeeze == SqueezeSource.Grip && Side == HandSide.Right )
		{
			// The helper's made-up throw (helper\app\src\FakeThrow.cs) starts opening the right grip 1.1837 s
			// into each 2 s cycle and takes 40 ms over it, so the moment it passes the let-go level is known exactly.
			double cycle = System.Math.Floor( _gainEventTime / 2.0 ) * 2.0;
			double trueLetGo = cycle + 1.1837 + ( 1.0 - ReleaseFraction ) * 0.040;
			line += $". FAKE TRUTH t={trueLetGo:0.0000} s: helper off by {Ms( _gainEventTime - trueLetGo )} ms"
				+ ( frameReady ? $", once-per-frame off by {Ms( _perFrameShadowRelease - trueLetGo )} ms" : "" )
				+ ( ninetyReady ? $", 90-a-second off by {Ms( _ninetyShadowRelease - trueLetGo )} ms" : "" );
		}
		Log.Info( $"[ThrowHand]   GAIN #{_gainNumber}: {line}" );
	}

	/// <summary>Seconds as signed milliseconds to three places, with no "-0.000".</summary>
	private static string Ms( double seconds )
	{
		double ms = System.Math.Round( seconds * 1000.0, 3 );
		if ( ms == 0.0 ) ms = 0.0;
		return ( ms >= 0.0 ? "+" : "" ) + ms.ToString( "0.000", System.Globalization.CultureInfo.InvariantCulture );
	}

	// Copy the properties into the core's settings just before each throw, so edits made while playing take effect.
	private void ApplyThrowSettings( Source source, bool fromHelper )
	{
		_throw.ReleaseTimeOffset = fromHelper ? HelperReleaseTimeOffset : ReleaseTimeOffset;
		_throw.FitWindowSeconds = FitWindow;
		_throw.FitWindowAfterSeconds = 0.0;
		_throw.Fit = VelocityFit.Line;
		_throw.UsePeakSpeed = UsePeakSpeed;
		_throw.SpeedScale = SpeedScale;
		_throw.MaxSpeed = MaxSpeed;
		_throw.DropSpeed = DropSpeed;
		_throw.FullThrowSpeed = FullThrowSpeed;

		_aim.Mode = AimMode;
		_aim.Head = new AimCone( HeadAimInnerDegrees, HeadAimOuterDegrees, HeadAimStrength );
		_aim.Eye = new AimCone( EyeAimInnerDegrees, EyeAimOuterDegrees, EyeAimStrength );
		_aim.Glance = _aim.Head;   // a glance is trusted as much as the head, no more

		_gaze.LookBackSeconds = GazeLookBack;
		_gaze.MinDurationSeconds = GazeMinDuration;
		_gaze.MaxSpreadAngle = GazeMaxSpreadDegrees * ( System.MathF.PI / 180f );
		_glance.LookBackSeconds = GlanceLookBack;
		_glance.MinDurationSeconds = GlanceMinDuration;
		_glance.MaxSpreadAngle = GlanceMaxSpreadDegrees * ( System.MathF.PI / 180f );

		// Last, so a trusted learned delay wins over the hand-set one (it also switches the fit to Curve).
		// Each source learns its own: the two see the release with very different lateness.
		if ( UseLearnedDelay )
		{
			source.Calibrator.ApplyTo( _throw );
		}
	}

	/// <summary>Where the head's forward ray meets a wall of the room.</summary>
	private AimPoint FindHeadAimPoint( RigidTransform anchorToWorld )
	{
		if ( !Room.IsValid() || Room.WallCount == 0 ) return AimPoint.None;

		Vec3 headPosition;
		Quat headRotation;
		if ( Game.IsRunningInVR && Input.VR is not null )
		{
			SboxConvert.ToCorePose( Input.VR.Head, out headPosition, out headRotation );
		}
		else if ( Link.IsValid() && Link.Head.Count > 0 )
		{
			// Desk test: the only head there is, is the helper's.
			PoseSample head = Link.Head[Link.Head.Count - 1];
			RigidTransform helperToWorld = Link.HelperToAnchor.Then( anchorToWorld );
			headPosition = helperToWorld.TransformPoint( head.Position );
			headRotation = helperToWorld.TransformRotation( head.Rotation );
		}
		else
		{
			return AimPoint.None;
		}

		Vec3 headForward = Vec3.Transform( new Vec3( 1f, 0f, 0f ), headRotation );   // +X is forward
		if ( AimAssist.FindAimPoint( headPosition, headForward, Room.WallFrames, Room.WallCount, 50f, out AimPoint point, out _, out _ ) )
		{
			return point;
		}
		return AimPoint.None;
	}

	/// <summary>
	/// Where the player was looking when they let go: the most recent steady look before the release
	/// moment (people glance away or blink as they throw), or failing that a glance (where the eyes
	/// pointed in the last moments, marked on the point so it pulls more gently), followed to the
	/// surface it lands on: a wall, or the floor or ceiling once the room lists those.
	/// Needs fresh helper data with a valid gaze, and the helper's space lined up; otherwise there is
	/// no eye point and aim assist falls back to the head by itself.
	/// </summary>
	/// <param name="releaseOnClock">The throw's release time on the shared clock.</param>
	/// <param name="note">What happened, for the console.</param>
	private AimPoint FindEyeAimPoint( double releaseOnClock, double clockNow, RigidTransform anchorToWorld, out string note )
	{
		if ( !Link.IsValid() || !Link.IsFresh( clockNow ) ) { note = "no helper, so no eye tracking: head aim"; return AimPoint.None; }
		if ( !Link.AlignmentValid ) { note = "helper space not lined up yet: head aim"; return AimPoint.None; }
		if ( !Room.IsValid() || Room.WallCount == 0 ) { note = "no room"; return AimPoint.None; }

		// A steady look if there was one; if not, a glance (where the eyes pointed just before the release).
		GazeLookKind kind = Link.Gaze.FindLook( releaseOnClock, _gaze, _glance, out GazeFixation inHelperSpace );

		// What the eyes were doing over the same stretch, for the console only: it is how the next tuning
		// round will know whether a missing look was a blink, moving eyes, or a shaky reading.
		GazeSpanSummary span = Link.Gaze.Summarise( releaseOnClock - GazeLookBack, releaseOnClock );
		string usable = $"{span.ValidReadings} of {span.Readings} readings usable";

		if ( kind == GazeLookKind.None )
		{
			note = span.ValidReadings > 0
				? $"no steady look and no glance in the {GazeLookBack * 1000f:0} ms before the release ({usable}, the eyes swung {span.Swing * 57.29578f:0.0} deg, last usable reading {span.LatestAge * 1000f:0.0} ms before it): head aim"
				: $"gaze not valid (no eye tracker, or eyes not tracked; {usable}): head aim";
			return AimPoint.None;
		}

		string what = kind == GazeLookKind.Glance ? "glance" : "steady look";
		GazeFixation look = ThrowSpace.ToWorld( inHelperSpace, Link.HelperToAnchor.Then( anchorToWorld ) );
		if ( !AimAssist.FindAimPoint( look.Origin, look.Direction, Room.WallFrames, Room.WallCount, 50f, out AimPoint point, out int wallIndex, out float distance ) )
		{
			note = $"{what} found ({look.Duration * 1000f:0} ms long) but it is not on a paintable surface: head aim";
			return AimPoint.None;
		}
		point.Glance = kind == GazeLookKind.Glance;

		// How far the eyes had moved on from that look by the release (the angle is the same in any space).
		float movedOn = span.HasLatest ? MathUtil.AngleBetween( inHelperSpace.Direction, span.LatestDirection ) * 57.29578f : 0f;
		string surface = point.Normal.Z > 0.7f ? "floor" : point.Normal.Z < -0.7f ? "ceiling" : "wall";
		Vec2 uv = Room.WallFrames[wallIndex].ToUV( point.Point );
		note = $"eye point ({point.Point.X:0.00}, {point.Point.Y:0.00}, {point.Point.Z:0.00}) m on {surface} {wallIndex} at u={uv.X:0.00} v={uv.Y:0.00}, {distance:0.00} m away; {what} of {look.Duration * 1000f:0} ms ({look.SampleCount} readings, spread {look.Spread * 57.29578f:0.00} deg) that ended {look.AgeAtRelease * 1000f:0.0} ms before the release; at the release the eyes were {movedOn:0.0} deg from it ({usable})";
		return point;
	}

	/// <summary>
	/// One line for the console saying what the speed scale and the aim help did to a throw, with the
	/// numbers a tuning round needs: how far off the throw was, how much of that was taken away, and how
	/// far from the aim point the ball lands without and with the help.
	/// </summary>
	private string AimNote( ThrowResult thrown, AimAssistResult assist )
	{
		ThrowDiagnostics d = thrown.Diagnostics;
		string speed = $"strength x{SpeedScale:0.00} made the hand's {d.FitSpeed:0.00} m/s (fastest moment {d.PeakSpeed:0.00}) into {thrown.ReleaseVelocity.Length():0.00} m/s";
		string dials = $"[eye {EyeAimStrength:0.00} in {EyeAimInnerDegrees:0}-{EyeAimOuterDegrees:0} deg, head and glance {HeadAimStrength:0.00} in {HeadAimInnerDegrees:0}-{HeadAimOuterDegrees:0} deg]";

		if ( assist.Source == AimSource.None )
		{
			return $"{speed}; {( assist.Outcome == AimAssistOutcome.Off ? "aim help is off" : "nothing to aim at" )} {dials}";
		}

		string point = assist.Source == AimSource.Head ? "HEAD point" : assist.Glance ? "EYE point (glance)" : "EYE point (steady look)";
		float away = ( assist.TargetPoint - thrown.ReleasePosition ).Length();
		string where = assist.LandingOnSurface ? "lands" : "passes";

		if ( assist.Applied )
		{
			float off = assist.AngleOff * 57.29578f;
			string faster = assist.SpeedChange > 0.0005f ? $", and {assist.SpeedChange * 100f:0.0} percent faster to reach" : "";
			return $"{speed}; pulled toward the {point} {away:0.00} m away: the throw was {off:0.0} deg off it, {assist.Weight * 100f:0} percent of that taken away ({off * assist.Weight:0.0} deg){faster}; without help it {where} {assist.MissWithout:0.00} m from the point, with help {assist.MissWith:0.00} m {dials}";
		}

		string why;
		switch ( assist.Outcome )
		{
			case AimAssistOutcome.OutsideCone: why = $"the throw was {assist.AngleOff * 57.29578f:0.0} deg off it, too far to be meant for it"; break;
			case AimAssistOutcome.WouldBeWorse: why = $"the throw was {assist.AngleOff * 57.29578f:0.0} deg off it and a nudge would have landed further away"; break;
			case AimAssistOutcome.OutOfReach: why = $"it needs {assist.MinSpeedNeeded:0.0} m/s to get there"; break;
			case AimAssistOutcome.Drop: why = "a let-go or too slow to be a throw"; break;
			case AimAssistOutcome.BadDistance: why = "too near or too far to be a target"; break;
			default: why = assist.Outcome.ToString(); break;
		}
		return $"{speed}; left alone, {assist.Outcome}: {point} {away:0.00} m away, {why}; it {where} {assist.MissWithout:0.00} m from the point {dials}";
	}

	private void RunPendingCalibration( Source source, PoseHistory history, double now )
	{
		if ( !source.ObservePending || now < source.ObserveAt ) return;

		CalibrationObservation observation = source.Calibrator.Observe( history, source.ObserveEventTime, source.BallLocalOffset );

		// "NeedMoreData" means the hand was still speeding up at the newest pose. Look again next frame,
		// but give up after a quarter of a second, before the history has forgotten the throw.
		if ( observation.Outcome == CalibrationOutcome.NeedMoreData && now < source.ObserveEventTime + 0.25 ) return;

		source.ObservePending = false;

		if ( UseLearnedDelay && source.Calibrator.IsConfident && !source.DelayAnnounced )
		{
			source.DelayAnnounced = true;
			Log.Info( $"[ThrowHand] {Side}: release delay learned for the {source.Name} source, {source.Calibrator.DelaySeconds * 1000.0:0.0} ms from {source.Calibrator.Count} throws (scatter {source.Calibrator.SpreadSeconds * 1000.0:0.0} ms). Using it from now on." );
		}
	}
}
