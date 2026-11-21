using NativeEngine;
using System.Collections.Concurrent;

namespace Sandbox.Rendering;

/// <summary>
/// Portaballs patch P2. The far side of up to two portals, drawn once for EVERY view of a camera (in a headset: once
/// per eye) and handed to that view's materials through the view's own attributes, the way
/// <see cref="PlanarReflections"/> hands each view its reflection.
///
/// Why not a command list on the portal's renderer (patch P1, <see cref="CommandList.DrawPortal"/>): measured in a
/// headset on 2026-11-19, the engine runs a renderer's ExecuteBefore list in the LEFT eye's pass only. The right eye
/// then pasted the left eye's picture, which made the tester's eyes feel crossed.
///
/// Two steps per view:
/// 1. While the view's layers are set up (<see cref="AddToView"/>, called from StandardPipeline): a picture is set
///    aside for each active end and published in the view's attributes (<see cref="End.TextureAttribute"/>,
///    <see cref="End.RectAttribute"/>, <see cref="End.HasAttribute"/> = true). Nothing is drawn yet: adding a view
///    at this point is refused by the engine ("adding views while previous rendering is running", seen 2026-11-19).
/// 2. While the view itself renders, right after its depth prepass (<see cref="RenderForCurrentView"/>, called from
///    SceneCamera's AfterDepthPrepass stage, which runs once per view): the view's eye is carried through each end,
///    its own projection is cut off at the exit wall, and the far side is drawn into that picture as a child of the
///    view, exactly as patch P1 does from a command list. The paint is drawn later in the same view and reads it.
///
/// Every other view of the camera (the pictures themselves, reflections) gets <see cref="End.HasAttribute"/> = false:
/// a portal seen through a portal shows the material's fallback.
///
/// Portaballs patch P4 (the part of this file below "patch P4") adds pictures that are kept from frame to frame, for a
/// request made with the Set overload that takes <see cref="Options"/>: a portal seen through a portal shows last
/// frame's picture, a picture need not be drawn every frame, and only the part of it that shows is drawn. A request
/// made with the old Set is handled exactly as before.
/// </summary>
public static class PortalViews
{
	/// <summary>One portal end: what to draw behind it, and which attributes carry the picture.</summary>
	public struct End
	{
		/// <summary>Draw this end in this frame. False = its HasAttribute is set to false in every view.</summary>
		public bool Active;

		/// <summary>Carries a real eye to the virtual eye (world units, scale 1).</summary>
		public Transform Crossing;

		/// <summary>The exit wall. Only what is in front of it is drawn.</summary>
		public Plane Keep;

		/// <summary>Moves <see cref="Keep"/> along its normal before cutting, world units.</summary>
		public float ClipOffset;

		/// <summary>When the virtual eye is less than this behind the cut, draw without the cut (world units).</summary>
		public float PlainWithin;

		/// <summary>0 = rebuild the view's projection from its side planes, 1 = the view's own matrix as native reports it.</summary>
		public int ProjectionSource;

		/// <summary>Tags left out of the picture. Not copied: don't change the set after handing it over.</summary>
		public TagSet ExcludeTags;

		public string HasAttribute;
		public string TextureAttribute;
		public string RectAttribute;

		// ---- patch P4. All optional: left at their defaults, an End behaves as it did under P2. ----

		/// <summary>
		/// Patch P4: the opening's box on the ENTRY wall, four world points in order round it. With these, only the
		/// part of the view the box covers is drawn (when <see cref="Options.Crop"/> is on), and nothing when it is
		/// out of the view.
		/// </summary>
		public bool HasCorners;
		public Vector3 Corner0, Corner1, Corner2, Corner3;

		/// <summary>
		/// Patch P4: the ENTRY wall, normal pointing out of it into the room. With this, a view that looks away from
		/// the wall gets no picture at all.
		/// </summary>
		public bool HasEntry;
		public Plane Entry;

		/// <summary>Patch P4: an eye nearer the entry wall than this (world units), or past it, always gets the whole view drawn.</summary>
		public float FullWithin;

		/// <summary>
		/// Patch P4: attributes for a picture the material must look up in world space instead of pasting by screen
		/// position. Mode (float): 0 none, 1 paste, 2 look up. Vp0 / Vp1 / Vp3 (float4): rows of the picture
		/// camera's world-to-clip matrix. Valid (float4): the part of the picture that was drawn, uv min and max.
		/// Without them a view is never handed a kept picture.
		/// </summary>
		public string ModeAttribute, Vp0Attribute, Vp1Attribute, Vp3Attribute, ValidAttribute;
	}

	/// <summary>Patch P4: how the pictures are made and kept. See the Set overload that takes it.</summary>
	public struct Options
	{
		/// <summary>Picture size against the view, 0.1 to 1.</summary>
		public float Scale;

		/// <summary>Draw only the part of the view an opening covers (needs <see cref="End.HasCorners"/>).</summary>
		public bool Crop;

		/// <summary>Extra drawn round that part, in the view's -1..1 units (0.08 = 4% of the view's width each side).</summary>
		public float CropPad;

		/// <summary>0 = every picture every frame, 1 = each every other frame, 2 = never more than one a frame.</summary>
		public int RateMode;

		/// <summary>How many eye views the camera has: 1 on a flat screen, 2 in a headset.</summary>
		public int Eyes;

		/// <summary>An opening seen inside a far-side picture shows that view's kept picture (the tunnel between facing portals).</summary>
		public bool Feedback;

		/// <summary>When this changes every picture is drawn afresh (the player was carried through, or a portal moved).</summary>
		public int Generation;

		/// <summary>The frame number the turns are counted by. Below 0 = the engine's own frame count.</summary>
		public int Tick;

		/// <summary>For testing: even a fresh picture is shown through the world-space look-up.</summary>
		public bool ForceLookup;
	}

	sealed class Request
	{
		public CameraComponent Camera;
		public SceneCamera SceneCamera;
		public End First, Second;
		public float Scale;

		// patch P4
		public bool Advanced;
		public Options Options;
		public float ZNear, ZFar;
	}

	/// <summary>What step 1 set aside for one view, waiting for step 2.</summary>
	sealed class Pending
	{
		public Request Request;
		public RenderTarget First, Second;
		public ulong Frame;

		// patch P4
		public Slot Slot;
		public List<QueuedDraw> Draws;
	}

	static Request _request;
	static readonly ConcurrentDictionary<IntPtr, Pending> _pending = new();

	/// <summary>Pictures drawn since the engine started (both ends, all views). A rough counter for scripts.</summary>
	public static int PicturesDrawn => System.Threading.Volatile.Read( ref _picturesDrawn );
	static int _picturesDrawn;

	/// <summary>Pictures set aside in step 1 since the engine started.</summary>
	public static int PicturesPlanned => System.Threading.Volatile.Read( ref _picturesPlanned );
	static int _picturesPlanned;

	/// <summary>Views of the requested camera seen in step 1 since the engine started (eyes, pictures, reflections).</summary>
	public static int ViewsSeen => System.Threading.Volatile.Read( ref _viewsSeen );
	static int _viewsSeen;

	/// <summary>The eye of the latest view a picture was drawn for. For scripts.</summary>
	public static Transform LastEye { get; private set; }

	/// <summary>
	/// Draw these two ends in every view of <paramref name="camera"/> from now on. Call from the game thread, every
	/// frame the ends move. <paramref name="resolutionScale"/>: picture size against the view, 0.1 to 1.
	/// </summary>
	public static void Set( CameraComponent camera, in End first, in End second, float resolutionScale )
	{
		if ( !camera.IsValid() ) { Clear(); return; }
		var r = new Request
		{
			Camera = camera,
			SceneCamera = camera.SceneCamera,
			First = first,
			Second = second,
			Scale = Math.Clamp( resolutionScale, 0.1f, 1f )
		};
		System.Threading.Volatile.Write( ref _request, r );
	}

	/// <summary>No portal pictures any more. Views are left alone (their attributes are not touched).</summary>
	public static void Clear()
	{
		System.Threading.Volatile.Write( ref _request, null );
	}

	/// <summary>
	/// Step 1. Called as each view's layers are set up (StandardPipeline), right after
	/// <see cref="PlanarReflections.AddToView"/>. <paramref name="hooks"/>: the view will run its managed stage hooks
	/// (without them step 2 never comes, so nothing is promised).
	/// </summary>
	internal static void AddToView( ISceneView view, SceneCamera camera, bool hooks )
	{
		var request = System.Threading.Volatile.Read( ref _request );
		if ( request is null || camera is null || !ReferenceEquals( request.SceneCamera, camera ) )
			return;

		System.Threading.Interlocked.Increment( ref _viewsSeen );

		var attributes = view.GetRenderAttributesPtr();

		// Patch P4: a request made with Options keeps its pictures from frame to frame
		if ( request.Advanced )
		{
			AddToViewAdvanced( view, request, attributes, hooks );
			return;
		}

		// "No picture" first, in every view of this camera, so nothing is inherited.
		if ( !string.IsNullOrEmpty( request.First.HasAttribute ) ) attributes.SetBoolValue( request.First.HasAttribute, false );
		if ( !string.IsNullOrEmpty( request.Second.HasAttribute ) ) attributes.SetBoolValue( request.Second.HasAttribute, false );

		// The pictures themselves, reflections, cube captures: none of their own (a portal seen through a portal).
		if ( !hooks || attributes.GetBoolValue( "IsReflection", false ) || view.GetParent().IsValid || view.GetRenderTargetArrayIndex() >= 0 )
			return;

		if ( view.GetFrustum().IsOrthographic() )
			return;

		var viewport = view.GetMainViewport().Rect;
		var pending = new Pending { Request = request, Frame = Application.FrameCount };
		pending.First = SetAside( attributes, request.First, viewport, request.Scale, 0 );
		pending.Second = SetAside( attributes, request.Second, viewport, request.Scale, 1 );
		if ( pending.First is null && pending.Second is null )
			return;

		// Views are made fresh each frame and their memory is used again: drop anything older than a few frames.
		if ( _pending.Count > 16 )
		{
			foreach ( var kv in _pending )
			{
				if ( Application.FrameCount > kv.Value.Frame + 4 ) _pending.TryRemove( kv.Key, out _ );
			}
		}
		_pending[(IntPtr)view] = pending;
	}

	static RenderTarget SetAside( CRenderAttributes attributes, in End end, Rect viewport, float scale, int slot )
	{
		if ( !end.Active || string.IsNullOrEmpty( end.TextureAttribute ) || string.IsNullOrEmpty( end.HasAttribute ) )
			return null;

		int width = Math.Max( 16, (int)MathF.Round( viewport.Width * scale ) );
		int height = Math.Max( 16, (int)MathF.Round( viewport.Height * scale ) );
		// targetId keeps the ends apart; two eyes asking for the same one in a frame get two pictures (the first is on loan).
		var target = RenderTarget.GetTemporary( width, height, ImageFormat.RGBA16161616F, ImageFormat.None, MultisampleAmount.MultisampleNone, 1, "Portaballs portal", slot );

		attributes.SetTextureValue( end.TextureAttribute, target.ColorTarget.native, -1 );
		if ( !string.IsNullOrEmpty( end.RectAttribute ) ) attributes.SetVector4DValue( end.RectAttribute, new Vector4( 0, 0, 1, 1 ) );
		attributes.SetBoolValue( end.HasAttribute, true );
		System.Threading.Interlocked.Increment( ref _picturesPlanned );
		return target;
	}

	/// <summary>
	/// Step 2. Called from SceneCamera's AfterDepthPrepass stage, inside the view being drawn (once per view, so once
	/// per eye in a headset). Draws what step 1 set aside for this view.
	/// </summary>
	internal static void RenderForCurrentView()
	{
		if ( _pending.IsEmpty || !Graphics.IsActive )
			return;

		var view = Graphics.SceneView;
		if ( !view.IsValid || !_pending.TryRemove( (IntPtr)view, out var pending ) )
			return;

		var camera = pending.Request.Camera;
		if ( !camera.IsValid() )
			return;

		// Patch P4
		if ( pending.Draws is not null )
		{
			RenderAdvanced( pending );
			return;
		}

		if ( pending.First is not null ) Draw( camera, pending.Request.First, pending.First, 0 );
		if ( pending.Second is not null ) Draw( camera, pending.Request.Second, pending.Second, 1 );
	}

	static void Draw( CameraComponent camera, in End end, RenderTarget target, int slot )
	{
		// This view's own eye (in a headset: the eye being drawn), carried through the portal.
		var eye = new Transform( Graphics.CameraTransform.Position, Graphics.CameraTransform.Rotation );
		var virtualEye = end.Crossing.ToWorld( eye );

		float zn = camera.ZNear;
		float zf = camera.ZFar;

		// This view's own projection, so the picture lines up with the view pixel for pixel when pasted by screen position.
		Matrix projection = end.ProjectionSource == 1 ? Graphics.ViewFrustum.GetProj() : ProjectionOfCurrentView( zn, zf );

		var clipPlane = new Plane( end.Keep.Position + end.ClipOffset * end.Keep.Normal, end.Keep.Normal );
		float behind = -clipPlane.GetDistance( virtualEye.Position );
		if ( behind < 0f || behind >= end.PlainWithin )
		{
			// False = the virtual eye is not behind the plane: nothing to cut away in front of it, draw it plain.
			ObliqueNearPlane( ref projection, virtualEye, clipPlane );
		}

		var setup = new ViewSetup
		{
			Transform = virtualEye,
			ZNear = zn,
			ZFar = zf,
			ProjectionMatrix = projection,
			EnablePostprocessing = false,
			EnableUI = false,
			EnableDebugOverlays = false,
			ViewHash = HashCode.Combine( "Portaballs portal", slot ),
			ExcludeTags = end.ExcludeTags
		};

		// A child of the view being drawn (CameraComponent passes Graphics.SceneView), like P1's DrawPortal.
		if ( !camera.RenderToTexture( target.ColorTarget, target.DepthTarget, setup, out var data ) || !data.IsValid )
			return;

		LastEye = eye;
		System.Threading.Interlocked.Increment( ref _picturesDrawn );
	}

	/// <summary>
	/// The off-axis projection of the view being drawn, rebuilt from its four side planes, in the layout of
	/// <see cref="Matrix.CreateProjection"/> (depth 0 at near, 1 at far). The same lines as patch P1's
	/// PortalProjectionOfCurrentView.
	/// </summary>
	static Matrix ProjectionOfCurrentView( float zn, float zf )
	{
		var f = Graphics.Frustum;
		var inv = Graphics.CameraTransform.Rotation.Inverse;
		Vector3 r = inv * f.RightPlane.Normal, l = inv * f.LeftPlane.Normal;
		Vector3 t = inv * f.TopPlane.Normal, b = inv * f.BottomPlane.Normal;

		float tanR = r.x / r.y, tanL = l.x / l.y, tanU = -t.x / t.z, tanD = -b.x / b.z;
		if ( tanL > tanR ) (tanL, tanR) = (tanR, tanL);
		if ( tanD > tanU ) (tanD, tanU) = (tanU, tanD);

		System.Numerics.Matrix4x4 m = default;
		m.M11 = 2f / (tanR - tanL);
		m.M22 = 2f / (tanU - tanD);
		m.M13 = (tanR + tanL) / (tanR - tanL);
		m.M23 = (tanU + tanD) / (tanU - tanD);
		m.M33 = zf / (zn - zf);
		m.M34 = zn * zf / (zn - zf);
		m.M43 = -1f;
		return m;
	}

	/// <summary>Patch P1's PortalObliqueNearPlane, unchanged (Lengyel's oblique near plane for this matrix layout).</summary>
	static bool ObliqueNearPlane( ref Matrix projection, in Transform eye, in Plane keep )
	{
		var nl = eye.Rotation.Inverse * keep.Normal;
		var c = new System.Numerics.Vector4( -nl.y, nl.z, -nl.x, keep.GetDistance( eye.Position ) );
		if ( !(c.W < 0f) ) return false;

		System.Numerics.Matrix4x4 m = projection;
		var q = new System.Numerics.Vector4( (MathF.Sign( c.X ) + m.M13) / m.M11, (MathF.Sign( c.Y ) + m.M23) / m.M22, -1f, (1f + m.M33) / m.M34 );
		float d = System.Numerics.Vector4.Dot( c, q );
		if ( MathF.Abs( d ) < 1e-12f ) return false;
		float a = 1f / d;
		if ( !(a > 0f) ) return false;

		m.M31 = a * c.X;
		m.M32 = a * c.Y;
		m.M33 = a * c.Z;
		m.M34 = a * c.W;
		projection = m;
		return true;
	}

	// ---- Portaballs patch P4: pictures that are KEPT from frame to frame -------------------------------------------
	//
	// What it adds to P2, all of it only for a request made with Set( camera, first, second, Options ):
	//
	// 1. Each view keeps its own two pictures per end (one being drawn, one finished: "kept"), with the camera each
	//    was taken with. A view is recognised again next frame by where its eye is (a headset's two eyes are two
	//    views with two sets of pictures).
	// 2. FEEDBACK. While an end's far side is being drawn, an opening seen INSIDE that picture is given the kept
	//    picture from the frame before, together with its camera, and the material looks it up in world space
	//    (attribute Mode = 2). So a portal seen through a portal is last frame's picture one level further in, and
	//    two facing portals fill in as an endless tunnel, one level per frame, for one texture read per pixel.
	// 3. HALF RATE. A picture need not be drawn every frame: on a frame it is skipped the view is handed the kept
	//    picture (Mode = 2) and the material re-projects it to where the eye is now.
	// 4. ONLY WHAT SHOWS. A picture is drawn only over the part of the view its opening covers (a viewport and a
	//    projection cut down to that rectangle), not at all for a view that looks away from the opening's wall,
	//    and not at all when the opening is out of that view.
	//
	// Attribute Mode: 1 = paste the picture by screen position (it was drawn for exactly this view, this frame),
	// 2 = look it up in world space with Vp0 / Vp1 / Vp3 (rows of the picture camera's world-to-clip matrix) inside
	// Valid (the part of the picture that was drawn, as uv min and max).

	sealed class Picture
	{
		public Texture Texture;
		public Vector4 Vp0, Vp1, Vp3;
		public Vector4 Valid;
		public int Generation;
	}

	sealed class SlotEnd
	{
		public readonly Texture[] Textures = new Texture[2];
		public Picture Kept;
		public int KeptIndex = -1;
	}

	/// <summary>One view's pictures (one per eye in a headset).</summary>
	sealed class Slot
	{
		public int Id;
		public int Width, Height;
		public Vector3 Eye;
		public ulong LastFrame;
		/// <summary>Which of the camera's views of a frame this is: 0 = the first set up (the left eye in a headset), 1 = the second.</summary>
		public int Ordinal;
		public readonly SlotEnd[] Ends = { new SlotEnd(), new SlotEnd() };
	}

	/// <summary>Where each far-side picture queued this frame is drawn from, so its own openings can be fed the right set.</summary>
	static readonly List<(Vector3 Eye, Slot Slot, ulong Frame)> _feeding = new();

	sealed class QueuedDraw
	{
		public int Index;
		public SlotEnd SlotEnd;
		public int WriteIndex;
		public Texture Target;
		public ViewSetup Setup;
		public Vector4 Viewport;
		public bool Cropped;
		public Picture Record;
		public Transform Eye;
		public int Pixels, PixelsWhole;
	}

	struct Lens
	{
		public float Left, Right, Down, Up;
	}

	static readonly List<Slot> _slots = new();
	static readonly List<(Texture Texture, ulong Frame)> _graveyard = new();
	static readonly object _slotLock = new();
	static int _slotIds;
	static ulong _ordinalFrame = ulong.MaxValue;
	static int _ordinal;

	/// <summary>The view whose far side is being drawn on this thread right now (its openings get its kept pictures).</summary>
	[ThreadStatic] static Slot _childSlot;

	const int MaxSlots = 6;
	const ulong SlotIdleFrames = 900;

	/// <summary>Patch P4: far-side pictures drawn afresh since the engine started.</summary>
	public static int FreshDrawn => System.Threading.Volatile.Read( ref _freshDrawn );
	static int _freshDrawn;

	/// <summary>Patch P4: times a view was handed a kept picture to re-project instead of a fresh one.</summary>
	public static int KeptShown => System.Threading.Volatile.Read( ref _keptShown );
	static int _keptShown;

	/// <summary>Patch P4: far-side pictures that were themselves handed a kept picture for the openings seen in them.</summary>
	public static int NestedViewsFed => System.Threading.Volatile.Read( ref _nestedViewsFed );
	static int _nestedViewsFed;

	/// <summary>Patch P4: pictures not drawn because the view looked away from the opening's wall.</summary>
	public static int SkippedLookingAway => System.Threading.Volatile.Read( ref _skippedLookingAway );
	static int _skippedLookingAway;

	/// <summary>Patch P4: pictures not drawn because the opening was out of the view.</summary>
	public static int SkippedOutOfView => System.Threading.Volatile.Read( ref _skippedOutOfView );
	static int _skippedOutOfView;

	/// <summary>Patch P4: pixels drawn into fresh pictures since the engine started, and what they would have been uncut.</summary>
	public static long PixelsDrawn => System.Threading.Interlocked.Read( ref _pixelsDrawn );
	static long _pixelsDrawn;
	public static long PixelsUncut => System.Threading.Interlocked.Read( ref _pixelsUncut );
	static long _pixelsUncut;

	/// <summary>Patch P4: sets of kept pictures alive (one per view that has been drawing portals lately).</summary>
	public static int Slots { get { lock ( _slotLock ) return _slots.Count; } }

	/// <summary>
	/// Patch P4. Draw these two ends in every view of <paramref name="camera"/> from now on, keeping each view's
	/// pictures from frame to frame (see <see cref="Options"/>). Call from the game thread every frame.
	/// </summary>
	public static void Set( CameraComponent camera, in End first, in End second, in Options options )
	{
		if ( !camera.IsValid() ) { Clear(); return; }
		var r = new Request
		{
			Camera = camera,
			SceneCamera = camera.SceneCamera,
			First = first,
			Second = second,
			Scale = Math.Clamp( options.Scale, 0.1f, 1f ),
			Advanced = true,
			Options = options,
			ZNear = camera.ZNear,
			ZFar = camera.ZFar
		};
		System.Threading.Volatile.Write( ref _request, r );
	}

	/// <summary>Patch P4: forget every kept picture (they are made again as needed).</summary>
	public static void DropKept()
	{
		lock ( _slotLock )
		{
			foreach ( var slot in _slots ) Retire( slot, Application.FrameCount );
			_slots.Clear();
		}
	}

	static void SetNone( CRenderAttributes attributes, in End end )
	{
		if ( !string.IsNullOrEmpty( end.HasAttribute ) ) attributes.SetBoolValue( end.HasAttribute, false );
		if ( !string.IsNullOrEmpty( end.ModeAttribute ) ) attributes.SetFloatValue( end.ModeAttribute, 0f );
	}

	static void Publish( CRenderAttributes attributes, in End end, Picture picture, float mode )
	{
		attributes.SetTextureValue( end.TextureAttribute, picture.Texture.native, -1 );
		if ( !string.IsNullOrEmpty( end.RectAttribute ) ) attributes.SetVector4DValue( end.RectAttribute, new Vector4( 0, 0, 1, 1 ) );
		if ( !string.IsNullOrEmpty( end.ModeAttribute ) ) attributes.SetFloatValue( end.ModeAttribute, mode );
		if ( !string.IsNullOrEmpty( end.Vp0Attribute ) ) attributes.SetVector4DValue( end.Vp0Attribute, picture.Vp0 );
		if ( !string.IsNullOrEmpty( end.Vp1Attribute ) ) attributes.SetVector4DValue( end.Vp1Attribute, picture.Vp1 );
		if ( !string.IsNullOrEmpty( end.Vp3Attribute ) ) attributes.SetVector4DValue( end.Vp3Attribute, picture.Vp3 );
		if ( !string.IsNullOrEmpty( end.ValidAttribute ) ) attributes.SetVector4DValue( end.ValidAttribute, picture.Valid );
		attributes.SetBoolValue( end.HasAttribute, true );
	}

	static bool PublishKept( CRenderAttributes attributes, in End end, SlotEnd slotEnd )
	{
		var kept = slotEnd.Kept;
		if ( kept is null || kept.Texture is null || string.IsNullOrEmpty( end.TextureAttribute ) || string.IsNullOrEmpty( end.HasAttribute ) )
			return false;
		// Without the look-up attributes the material could only paste it by screen position, which is wrong here.
		if ( string.IsNullOrEmpty( end.ModeAttribute ) || string.IsNullOrEmpty( end.Vp0Attribute ) )
			return false;
		Publish( attributes, end, kept, 2f );
		return true;
	}

	/// <summary>Step 1 for a request made with Options.</summary>
	static void AddToViewAdvanced( ISceneView view, Request request, CRenderAttributes attributes, bool hooks )
	{
		// "No picture" first, in every view of this camera, so nothing is inherited.
		SetNone( attributes, request.First );
		SetNone( attributes, request.Second );

		if ( view.GetParent().IsValid )
		{
			// A far-side picture being set up: the openings seen in it show the pictures kept from the frame before.
			var feeding = _childSlot ?? FindFeeding( view.GetFrustum().GetCameraPosition(), Application.FrameCount );
			if ( feeding is null || !request.Options.Feedback )
				return;

			bool any = false;
			lock ( feeding )
			{
				any |= PublishKept( attributes, request.First, feeding.Ends[0] );
				any |= PublishKept( attributes, request.Second, feeding.Ends[1] );
			}
			if ( any ) System.Threading.Interlocked.Increment( ref _nestedViewsFed );
			return;
		}

		if ( !hooks || attributes.GetBoolValue( "IsReflection", false ) || view.GetRenderTargetArrayIndex() >= 0 )
			return;

		var frustum = view.GetFrustum();
		if ( frustum.IsOrthographic() )
			return;

		ulong frame = Application.FrameCount;
		var viewport = view.GetMainViewport().Rect;
		int width = Math.Max( 16, (int)MathF.Round( viewport.Width * request.Scale ) );
		int height = Math.Max( 16, (int)MathF.Round( viewport.Height * request.Scale ) );
		var eye = new Transform( frustum.GetCameraPosition(), frustum.GetCameraAngles() );

		int ordinal;
		Slot slot;
		lock ( _slotLock )
		{
			Purge( frame );
			if ( _ordinalFrame != frame ) { _ordinalFrame = frame; _ordinal = 0; }
			ordinal = _ordinal++;
			slot = FindSlot( eye.Position, width, height, frame, ordinal );
		}

		Lens lens = LensOf( frustum, eye.Rotation );
		int activeEnds = (request.First.Active ? 1 : 0) + (request.Second.Active ? 1 : 0);
		int tick = request.Options.Tick >= 0 ? request.Options.Tick : (int)(frame & 0x3fffffff);

		Pending pending = null;
		lock ( slot )
		{
			Plan( request, request.First, attributes, frustum, eye, lens, slot, 0, ordinal, tick, activeEnds, width, height, frame, ref pending );
			Plan( request, request.Second, attributes, frustum, eye, lens, slot, 1, ordinal, tick, activeEnds, width, height, frame, ref pending );
		}
		if ( pending is null )
			return;

		if ( _pending.Count > 16 )
		{
			foreach ( var kv in _pending )
			{
				if ( frame > kv.Value.Frame + 4 ) _pending.TryRemove( kv.Key, out _ );
			}
		}
		_pending[(IntPtr)view] = pending;
	}

	/// <summary>Decide what one view gets for one end this frame: nothing, the kept picture, or a fresh one (queued for step 2).</summary>
	static void Plan( Request request, in End end, CRenderAttributes attributes, CFrustum frustum, in Transform eye, in Lens lens, Slot slot, int index,
		int ordinal, int tick, int activeEnds, int width, int height, ulong frame, ref Pending pending )
	{
		if ( !end.Active || string.IsNullOrEmpty( end.TextureAttribute ) || string.IsNullOrEmpty( end.HasAttribute ) )
			return;

		var options = request.Options;
		var slotEnd = slot.Ends[index];

		// A view that looks away from the opening's wall sees nothing of it, from in front or from inside it.
		if ( end.HasEntry && LooksAway( eye.Rotation, lens, end.Entry.Normal ) )
		{
			System.Threading.Interlocked.Increment( ref _skippedLookingAway );
			return;
		}

		// The part of the view the opening covers (-1..1, y up). An eye at or in the opening gets the whole view.
		float minX = -1f, minY = -1f, maxX = 1f, maxY = 1f;
		bool whole = !options.Crop || !end.HasCorners || (end.HasEntry && end.Entry.GetDistance( eye.Position ) < end.FullWithin);
		if ( !whole )
		{
			if ( !BoundsOnScreen( end, eye, lens, request.ZNear, out minX, out minY, out maxX, out maxY ) )
			{
				System.Threading.Interlocked.Increment( ref _skippedOutOfView );
				return;
			}
			float pad = MathF.Max( 0f, options.CropPad );
			minX = MathF.Max( -1f, minX - pad );
			minY = MathF.Max( -1f, minY - pad );
			maxX = MathF.Min( 1f, maxX + pad );
			maxY = MathF.Min( 1f, maxY + pad );
			if ( !(maxX > minX) || !(maxY > minY) )
			{
				System.Threading.Interlocked.Increment( ref _skippedOutOfView );
				return;
			}
		}

		var kept = slotEnd.Kept;
		bool fresh = kept is null || kept.Generation != options.Generation
			|| IsFresh( options.RateMode, tick, ordinal, index, activeEnds, options.Eyes );
		if ( !fresh && !string.IsNullOrEmpty( end.ModeAttribute ) && !string.IsNullOrEmpty( end.Vp0Attribute ) )
		{
			Publish( attributes, end, kept, 2f );
			System.Threading.Interlocked.Increment( ref _keptShown );
			return;
		}

		// ---- a fresh picture ----
		// Whole pixels, grown outward, and the rectangle those pixels really cover.
		int x0 = (int)MathF.Floor( (minX * 0.5f + 0.5f) * width );
		int x1 = (int)MathF.Ceiling( (maxX * 0.5f + 0.5f) * width );
		int y0 = (int)MathF.Floor( (0.5f - maxY * 0.5f) * height );
		int y1 = (int)MathF.Ceiling( (0.5f - minY * 0.5f) * height );
		if ( x1 - x0 < 8 ) { x0 -= 4; x1 = x0 + 8; }
		if ( y1 - y0 < 8 ) { y0 -= 4; y1 = y0 + 8; }
		if ( x0 < 0 ) { x1 = Math.Min( width, x1 - x0 ); x0 = 0; }
		if ( y0 < 0 ) { y1 = Math.Min( height, y1 - y0 ); y0 = 0; }
		if ( x1 > width ) { x0 = Math.Max( 0, x0 - (x1 - width) ); x1 = width; }
		if ( y1 > height ) { y0 = Math.Max( 0, y0 - (y1 - height) ); y1 = height; }
		bool cropped = x0 > 0 || y0 > 0 || x1 < width || y1 < height;
		minX = 2f * x0 / width - 1f;
		maxX = 2f * x1 / width - 1f;
		maxY = 1f - 2f * y0 / height;
		minY = 1f - 2f * y1 / height;

		// This view's own eye carried through the portal, and its own projection cut off at the exit wall (as P2).
		float zn = request.ZNear;
		float zf = request.ZFar;
		var virtualEye = end.Crossing.ToWorld( eye );
		Matrix projection = end.ProjectionSource == 1 ? frustum.GetProj() : ProjectionOf( lens, zn, zf );
		var clipPlane = new Plane( end.Keep.Position + end.ClipOffset * end.Keep.Normal, end.Keep.Normal );
		float behind = -clipPlane.GetDistance( virtualEye.Position );
		if ( behind < 0f || behind >= end.PlainWithin )
		{
			ObliqueNearPlane( ref projection, virtualEye, clipPlane );
		}

		// The picture's camera, for anyone who looks this picture up later from somewhere else. The WHOLE view's
		// projection: the picture sits in its texture where it sits on the screen, however much of it is drawn.
		var record = new Picture { Generation = options.Generation };
		ViewProjectionRows( virtualEye, projection, out record.Vp0, out record.Vp1, out record.Vp3 );
		record.Valid = new Vector4( (x0 + 0.5f) / width, (y0 + 0.5f) / height, (x1 - 0.5f) / width, (y1 - 0.5f) / height );

		if ( cropped )
		{
			// Draw only that rectangle: rows 1 and 2 of the projection take a share of row 4 (core: CropColumnLayout).
			float sx = 2f / (maxX - minX), ox = -(maxX + minX) / (maxX - minX);
			float sy = 2f / (maxY - minY), oy = -(maxY + minY) / (maxY - minY);
			System.Numerics.Matrix4x4 m = projection;
			System.Numerics.Matrix4x4 c = m;
			c.M11 = sx * m.M11 + ox * m.M41;
			c.M12 = sx * m.M12 + ox * m.M42;
			c.M13 = sx * m.M13 + ox * m.M43;
			c.M14 = sx * m.M14 + ox * m.M44;
			c.M21 = sy * m.M21 + oy * m.M41;
			c.M22 = sy * m.M22 + oy * m.M42;
			c.M23 = sy * m.M23 + oy * m.M43;
			c.M24 = sy * m.M24 + oy * m.M44;
			projection = c;
		}

		EnsureSize( slot, width, height, frame );
		int writeIndex = slotEnd.KeptIndex == 0 ? 1 : 0;
		var target = slotEnd.Textures[writeIndex];
		if ( target is null || !target.IsValid )
		{
			target = Texture.CreateRenderTarget().WithFormat( ImageFormat.RGBA16161616F ).WithSize( width, height ).Create( $"portaballs_portal_{slot.Id}_{index}_{writeIndex}" );
			slotEnd.Textures[writeIndex] = target;
		}
		if ( target is null || !target.IsValid )
			return;
		record.Texture = target;

		var setup = new ViewSetup
		{
			Transform = virtualEye,
			ZNear = zn,
			ZFar = zf,
			ProjectionMatrix = projection,
			EnablePostprocessing = false,
			EnableUI = false,
			EnableDebugOverlays = false,
			ViewHash = HashCode.Combine( "Portaballs portal", index, slot.Id ),
			ExcludeTags = end.ExcludeTags
		};

		lock ( _slotLock ) _feeding.Add( (virtualEye.Position, slot, frame) );
		pending ??= new Pending { Request = request, Frame = frame, Slot = slot, Draws = new List<QueuedDraw>( 2 ) };
		pending.Draws.Add( new QueuedDraw
		{
			Index = index,
			SlotEnd = slotEnd,
			WriteIndex = writeIndex,
			Target = target,
			Setup = setup,
			Viewport = new Vector4( x0 / (float)width, y0 / (float)height, (x1 - x0) / (float)width, (y1 - y0) / (float)height ),
			Cropped = cropped,
			Record = record,
			Eye = eye,
			Pixels = (x1 - x0) * (y1 - y0),
			PixelsWhole = width * height
		} );

		Publish( attributes, end, record, options.ForceLookup && !string.IsNullOrEmpty( end.ModeAttribute ) ? 2f : 1f );
	}

	/// <summary>Step 2 for a request made with Options: draw what step 1 queued for this view.</summary>
	static void RenderAdvanced( Pending pending )
	{
		var request = pending.Request;
		var sceneCamera = request.SceneCamera;
		if ( sceneCamera is null )
			return;

		var parent = Graphics.SceneView;
		foreach ( var draw in pending.Draws )
		{
			bool rendered;
			_childSlot = pending.Slot;
			try
			{
				// A child of the view being drawn, as CameraComponent.RenderToTexture makes one, with a viewport.
				using var setup = new CameraRenderer( "Portaballs portal", parent.m_ManagedCameraId );
				lock ( sceneCamera )
				{
					setup.Configure( sceneCamera, draw.Setup );
				}
				if ( draw.Cropped ) setup.Native.Viewport = draw.Viewport;
				rendered = setup.Native.RenderToTexture( draw.Target.native, parent, default );
				setup.Native.ClearSceneWorlds();
			}
			finally
			{
				_childSlot = null;
			}
			if ( !rendered )
				continue;

			lock ( pending.Slot )
			{
				draw.SlotEnd.Kept = draw.Record;
				draw.SlotEnd.KeptIndex = draw.WriteIndex;
			}
			LastEye = draw.Eye;
			System.Threading.Interlocked.Increment( ref _picturesDrawn );
			System.Threading.Interlocked.Increment( ref _freshDrawn );
			System.Threading.Interlocked.Add( ref _pixelsDrawn, draw.Pixels );
			System.Threading.Interlocked.Add( ref _pixelsUncut, draw.PixelsWhole );
		}
	}

	/// <summary>
	/// The set of pictures for a view whose eye is here. Called under _slotLock. A view is the same view as last
	/// frame's nearest one of its size; a second view in the same frame only shares a set when it is the same eye
	/// again (within 2 cm), so a headset's two eyes, 6 to 7 cm apart, always get one each.
	/// </summary>
	static Slot FindSlot( Vector3 eye, int width, int height, ulong frame, int ordinal )
	{
		// The same view as last frame is the one set up in the same turn, at the same size: a headset's left eye is
		// always set up first. (By position alone the two eyes, 6 to 7 cm apart, would swap whenever the head moves
		// sideways faster than that in a frame, and after a crossing, which moves both eyes across the room.)
		Slot best = null;
		foreach ( var s in _slots )
		{
			if ( s.Width != width || s.Height != height || s.Ordinal != ordinal || s.LastFrame == frame ) continue;
			best = s;
			break;
		}
		// Otherwise the nearest one at that size not yet taken this frame (or taken this frame by the same eye again,
		// within 2 cm: a script drawing one camera several times in one frame).
		float bestDistance = float.MaxValue;
		if ( best is null )
		{
			foreach ( var s in _slots )
			{
				if ( s.Width != width || s.Height != height ) continue;
				if ( s.LastFrame == frame && (s.Eye - eye).Length > 0.8f ) continue;
				float d = (s.Eye - eye).Length;
				if ( d < bestDistance ) { best = s; bestDistance = d; }
			}
		}

		if ( best is null )
		{
			if ( _slots.Count >= MaxSlots )
			{
				Slot oldest = _slots[0];
				foreach ( var s in _slots ) if ( s.LastFrame < oldest.LastFrame ) oldest = s;
				Retire( oldest, frame );
				_slots.Remove( oldest );
			}
			best = new Slot { Id = ++_slotIds, Width = width, Height = height };
			_slots.Add( best );
		}

		best.Eye = eye;
		best.LastFrame = frame;
		best.Ordinal = ordinal;
		return best;
	}

	/// <summary>The set whose far-side picture is drawn from this virtual eye this frame (when the child view is not set up on the drawing thread).</summary>
	static Slot FindFeeding( Vector3 eye, ulong frame )
	{
		lock ( _slotLock )
		{
			Slot best = null;
			float bestDistance = 0.1f;   // inches
			foreach ( var f in _feeding )
			{
				if ( f.Frame + 1 < frame ) continue;
				float d = (f.Eye - eye).Length;
				if ( d < bestDistance ) { best = f.Slot; bestDistance = d; }
			}
			return best;
		}
	}

	static void EnsureSize( Slot slot, int width, int height, ulong frame )
	{
		if ( slot.Width == width && slot.Height == height ) return;
		lock ( _slotLock ) Retire( slot, frame );
		slot.Width = width;
		slot.Height = height;
	}

	/// <summary>Hand a set's textures to the graveyard (they may still be in use by a frame being drawn). Under _slotLock.</summary>
	static void Retire( Slot slot, ulong frame )
	{
		foreach ( var slotEnd in slot.Ends )
		{
			for ( int i = 0; i < 2; i++ )
			{
				if ( slotEnd.Textures[i] is not null ) _graveyard.Add( (slotEnd.Textures[i], frame) );
				slotEnd.Textures[i] = null;
			}
			slotEnd.Kept = null;
			slotEnd.KeptIndex = -1;
		}
	}

	/// <summary>Drop sets nobody has used for a while and free retired textures a few frames later. Under _slotLock.</summary>
	static void Purge( ulong frame )
	{
		_feeding.RemoveAll( f => f.Frame + 2 < frame );
		for ( int i = _slots.Count - 1; i >= 0; i-- )
		{
			if ( frame > _slots[i].LastFrame + SlotIdleFrames )
			{
				Retire( _slots[i], frame );
				_slots.RemoveAt( i );
			}
		}
		for ( int i = _graveyard.Count - 1; i >= 0; i-- )
		{
			if ( frame > _graveyard[i].Frame + 8 )
			{
				_graveyard[i].Texture?.Dispose();
				_graveyard.RemoveAt( i );
			}
		}
	}

	/// <summary>The same turns as the game's core (Portaballs.Core.PortalSchedule.IsFresh, which has the tests).</summary>
	static bool IsFresh( int mode, int tick, int eye, int end, int activeEnds, int eyes )
	{
		if ( tick < 0 ) tick = -tick;
		if ( eye < 0 ) eye = 0;
		if ( eyes < 1 ) eyes = 1;
		if ( eye >= eyes ) eye = eyes - 1;
		bool both = activeEnds >= 2;
		switch ( mode )
		{
			case 1:
				return ((tick + (both ? end : eye)) & 1) == 0;
			case 2:
				{
					int count = eyes * (both ? 2 : 1);
					int index = both ? eye * 2 + end : eye;
					return tick % count == index;
				}
			default:
				return true;
		}
	}

	/// <summary>A view's four lens tangents from its side planes (the same lines as ProjectionOfCurrentView).</summary>
	static Lens LensOf( CFrustum frustum, Rotation rotation )
	{
		frustum.GetPlane( 0, out var rn, out _ );
		frustum.GetPlane( 1, out var ln, out _ );
		frustum.GetPlane( 2, out var tn, out _ );
		frustum.GetPlane( 3, out var bn, out _ );
		var inv = rotation.Inverse;
		Vector3 r = inv * rn, l = inv * ln, t = inv * tn, b = inv * bn;
		var lens = new Lens { Right = r.x / r.y, Left = l.x / l.y, Up = -t.x / t.z, Down = -b.x / b.z };
		if ( lens.Left > lens.Right ) (lens.Left, lens.Right) = (lens.Right, lens.Left);
		if ( lens.Down > lens.Up ) (lens.Down, lens.Up) = (lens.Up, lens.Down);
		return lens;
	}

	static Matrix ProjectionOf( in Lens lens, float zn, float zf )
	{
		System.Numerics.Matrix4x4 m = default;
		m.M11 = 2f / (lens.Right - lens.Left);
		m.M22 = 2f / (lens.Up - lens.Down);
		m.M13 = (lens.Right + lens.Left) / (lens.Right - lens.Left);
		m.M23 = (lens.Up + lens.Down) / (lens.Up - lens.Down);
		m.M33 = zf / (zn - zf);
		m.M34 = zn * zf / (zn - zf);
		m.M43 = -1f;
		return m;
	}

	/// <summary>True when no corner of the view closes on a wall with this normal (core: PortalPictureMath.LooksAwayFrom).</summary>
	static bool LooksAway( Rotation rotation, in Lens lens, Vector3 wallNormal )
	{
		float f = Vector3.Dot( rotation.Forward, wallNormal );
		float r = -Vector3.Dot( rotation.Left, wallNormal );
		float u = Vector3.Dot( rotation.Up, wallNormal );
		float least = f + MathF.Min( lens.Left * r, lens.Right * r ) + MathF.Min( lens.Down * u, lens.Up * u );
		return least >= 0f;
	}

	/// <summary>The part of the view the end's four corners cover, cut at the near distance first (core: PortalPictureMath.BoundsOnScreen).</summary>
	static bool BoundsOnScreen( in End end, in Transform eye, in Lens lens, float near, out float minX, out float minY, out float maxX, out float maxY )
	{
		var inv = eye.Rotation.Inverse;
		Vector3 a = inv * (end.Corner0 - eye.Position), b = inv * (end.Corner1 - eye.Position);
		Vector3 c = inv * (end.Corner2 - eye.Position), d = inv * (end.Corner3 - eye.Position);
		minX = float.MaxValue; minY = float.MaxValue; maxX = float.MinValue; maxY = float.MinValue;
		int points = 0;
		Edge( a, b, near, lens, ref minX, ref minY, ref maxX, ref maxY, ref points );
		Edge( b, c, near, lens, ref minX, ref minY, ref maxX, ref maxY, ref points );
		Edge( c, d, near, lens, ref minX, ref minY, ref maxX, ref maxY, ref points );
		Edge( d, a, near, lens, ref minX, ref minY, ref maxX, ref maxY, ref points );
		if ( points == 0 ) return false;
		minX = MathF.Max( minX, -1f );
		minY = MathF.Max( minY, -1f );
		maxX = MathF.Min( maxX, 1f );
		maxY = MathF.Min( maxY, 1f );
		return maxX > minX && maxY > minY;
	}

	// Camera-local points: x forward, y left, z up.
	static void Edge( Vector3 p, Vector3 q, float near, in Lens lens, ref float minX, ref float minY, ref float maxX, ref float maxY, ref int points )
	{
		float dp = p.x - near;
		float dq = q.x - near;
		if ( dp < 0f && dq < 0f ) return;
		if ( dp < 0f ) p = p + (q - p) * (dp / (dp - dq));
		else if ( dq < 0f ) q = q + (p - q) * (dq / (dq - dp));
		Point( p, lens, ref minX, ref minY, ref maxX, ref maxY );
		Point( q, lens, ref minX, ref minY, ref maxX, ref maxY );
		points += 2;
	}

	static void Point( Vector3 v, in Lens lens, ref float minX, ref float minY, ref float maxX, ref float maxY )
	{
		float depth = MathF.Max( 1e-6f, v.x );
		float x = (2f * (-v.y) / depth - (lens.Right + lens.Left)) / (lens.Right - lens.Left);
		float y = (2f * v.z / depth - (lens.Up + lens.Down)) / (lens.Up - lens.Down);
		if ( x < minX ) minX = x;
		if ( x > maxX ) maxX = x;
		if ( y < minY ) minY = y;
		if ( y > maxY ) maxY = y;
	}

	/// <summary>
	/// Rows 1, 2 and 4 of a camera's world-to-clip matrix: clip.x = dot( vp0, (p, 1) ), clip.y = dot( vp1, (p, 1) ),
	/// clip.w = dot( vp3, (p, 1) ). View space is +x right, +y up, looking down -z; the projection is in the layout
	/// of <see cref="Matrix.CreateProjection"/>.
	/// </summary>
	static void ViewProjectionRows( in Transform camera, Matrix projection, out Vector4 vp0, out Vector4 vp1, out Vector4 vp3 )
	{
		Vector3 fwd = camera.Rotation.Forward, left = camera.Rotation.Left, up = camera.Rotation.Up, pos = camera.Position;
		var x = new System.Numerics.Vector4( -left.x, -left.y, -left.z, Vector3.Dot( left, pos ) );
		var y = new System.Numerics.Vector4( up.x, up.y, up.z, -Vector3.Dot( up, pos ) );
		var z = new System.Numerics.Vector4( -fwd.x, -fwd.y, -fwd.z, Vector3.Dot( fwd, pos ) );
		var w = new System.Numerics.Vector4( 0f, 0f, 0f, 1f );
		System.Numerics.Matrix4x4 m = projection;
		var r0 = x * m.M11 + y * m.M12 + z * m.M13 + w * m.M14;
		var r1 = x * m.M21 + y * m.M22 + z * m.M23 + w * m.M24;
		var r3 = x * m.M41 + y * m.M42 + z * m.M43 + w * m.M44;
		vp0 = new Vector4( r0.X, r0.Y, r0.Z, r0.W );
		vp1 = new Vector4( r1.X, r1.Y, r1.Z, r1.W );
		vp3 = new Vector4( r3.X, r3.Y, r3.Z, r3.W );
	}
}
