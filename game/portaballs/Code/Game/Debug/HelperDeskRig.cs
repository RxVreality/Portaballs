using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// The no-headset test of throwing from helper samples.
///
/// It stands in for the VR player: this object is the play-area "anchor", and it has two hands, each
/// with the real <see cref="ThrowHand"/>. There is no VR, so the hands are driven entirely by what
/// the helper sends (start it with:  helper\dist\PortaballsHelper.exe --fake --port 8443 ), and
/// the helper's space is simply taken to be this anchor's own space.
///
/// Everything from the pose history onward is the real code path: release detection from the
/// helper's grip change times, the throw estimate, aim assist with the fake gaze, the ball.
///
/// Also shows a small yellow dot where the helper's gaze currently meets a wall, and a grey block
/// for the head, so a screenshot shows what the fake player is doing.
///
/// Switched on by PortaballsGame.HelperDeskTest (scene: scenes/portaballs_helper_desk.scene).
/// </summary>
public sealed class HelperDeskRig : Component
{
	/// <summary>The room the hands aim at. Set by PortaballsGame.</summary>
	public TestRoom Room { get; set; }

	/// <summary>The link to the helper. Set by PortaballsGame.</summary>
	public HelperLink Link { get; set; }

	/// <summary>Normally the right hand throws blue and the left throws orange. Tick to swap.</summary>
	[Property] public bool SwapHandColours { get; set; }

	public ThrowHand LeftHand { get; private set; }
	public ThrowHand RightHand { get; private set; }

	/// <summary>Where the helper's gaze currently meets a wall, world metres. Only meaningful when GazeOnWall.</summary>
	public Vec3 GazePoint { get; private set; }
	public bool GazeOnWall { get; private set; }

	private GameObject _head;
	private GameObject _gazeDot;
	private bool _built;

	protected override void OnStart()
	{
		Build();
	}

	/// <summary>Make the rig now. Safe to call more than once.</summary>
	public void Build()
	{
		if ( _built ) return;
		_built = true;

		PaintColor rightPaint = SwapHandColours ? PaintColor.Orange : PaintColor.Blue;
		PaintColor leftPaint = SwapHandColours ? PaintColor.Blue : PaintColor.Orange;
		LeftHand = BuildHand( "Left Hand (helper)", ThrowHand.HandSide.Left, leftPaint );
		RightHand = BuildHand( "Right Hand (helper)", ThrowHand.HandSide.Right, rightPaint );

		_head = new GameObject( GameObject, false, "Head (helper)" );
		_head.LocalScale = SboxConvert.BoxScaleFor( new Vec3( 0.18f, 0.16f, 0.12f ) );
		ModelRenderer headBlock = _head.AddComponent<ModelRenderer>();
		headBlock.Model = Model.Cube;
		headBlock.Tint = new Color( 0.55f, 0.55f, 0.6f );

		_gazeDot = new GameObject( GameObject, false, "Gaze Dot (helper)" );
		_gazeDot.LocalScale = SboxConvert.SphereScaleFor( 0.05f );
		ModelRenderer dot = _gazeDot.AddComponent<ModelRenderer>();
		dot.Model = Model.Sphere;
		dot.Tint = new Color( 1f, 0.9f, 0.1f );

		Log.Info( $"[HelperDeskRig] Built at ({WorldPosition.x * SboxConvert.MetresPerInch:0.00}, {WorldPosition.y * SboxConvert.MetresPerInch:0.00}, {WorldPosition.z * SboxConvert.MetresPerInch:0.00}) m. Hands are driven by helper samples only. Left throws {leftPaint}, right throws {rightPaint}." );
	}

	private ThrowHand BuildHand( string handName, ThrowHand.HandSide side, PaintColor paint )
	{
		GameObject hand = new GameObject( GameObject, false, handName );

		ThrowHand thrower = hand.AddComponent<ThrowHand>();
		thrower.Side = side;
		thrower.Paint = paint;
		thrower.Room = Room;
		thrower.Link = Link;
		thrower.AnchorObject = GameObject;
		thrower.DeskHelperTest = true;

		// The same stand-in block the VR hands have.
		GameObject block = new GameObject( hand, false, "Hand Block" );
		block.LocalScale = SboxConvert.BoxScaleFor( new Vec3( 0.10f, 0.035f, 0.05f ) );
		ModelRenderer renderer = block.AddComponent<ModelRenderer>();
		renderer.Model = Model.Cube;
		Color paintTint = Portaball.TintFor( paint );
		renderer.Tint = new Color( 0.5f + 0.5f * paintTint.r, 0.5f + 0.5f * paintTint.g, 0.5f + 0.5f * paintTint.b );
		block.Enabled = true;

		// Shown at both ends of a portal while the hand is in an opening (PortalBallTwins).
		PortalTwinSource.Register( hand, block );

		hand.Enabled = true;
		return thrower;
	}

	protected override void OnUpdate()
	{
		bool fresh = Link.IsValid() && Link.IsFresh( HelperClock.Now ) && Link.TotalSamples > 0;
		bool showHead = fresh && Link.Head.Count > 0;
		bool showDot = false;

		if ( fresh )
		{
			RigidTransform helperToWorld = Link.HelperToAnchor.Then( SboxConvert.ToRigid( WorldTransform ) );

			if ( showHead )
			{
				PoseSample head = Link.Head[Link.Head.Count - 1];
				_head.WorldPosition = SboxConvert.ToInches( helperToWorld.TransformPoint( head.Position ) );
				_head.WorldRotation = SboxConvert.ToEngine( helperToWorld.TransformRotation( head.Rotation ) );
			}

			LinkSample newest = Link.Newest;
			if ( newest.Gaze.IsUsable && Room.IsValid() )
			{
				Vec3 origin = helperToWorld.TransformPoint( newest.Gaze.Origin );
				Vec3 direction = helperToWorld.TransformDirection( newest.Gaze.Direction );
				if ( AimAssist.FindAimPoint( origin, direction, Room.WallFrames, Room.WallCount, 50f, out AimPoint point, out _, out _ ) )
				{
					showDot = true;
					GazePoint = point.Point;
					// Sit just proud of the wall so the paint panel does not hide it.
					_gazeDot.WorldPosition = SboxConvert.ToInches( point.Point + point.Normal * 0.03f );
				}
			}
		}

		GazeOnWall = showDot;
		if ( _head.Enabled != showHead ) _head.Enabled = showHead;
		if ( _gazeDot.Enabled != showDot ) _gazeDot.Enabled = showDot;
	}
}
