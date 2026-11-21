namespace Sandbox.Rendering;


public sealed unsafe partial class CommandList
{
	/// <summary>
	/// Render a <see cref="Renderer"/> with the specified overrides.
	/// </summary>
	public void DrawRenderer( Renderer renderer, RendererSetup rendererSetup = default )
	{
		if ( !renderer.IsValid() )
			return;

		static void Execute( ref Entry entry, CommandList commandList )
		{
			var renderer = (Renderer)entry.Object1;
			if ( !renderer.IsValid() )
				return;

			renderer.RenderSceneObject( (RendererSetup)entry.Object2 );
		}

		AddEntry( &Execute, new Entry { Object1 = renderer, Object2 = rendererSetup } );
	}

	/// <summary>
	/// Renders the view from a camera to the specified render target.
	/// When present, the target's depth texture is written directly in the unflipped camera projection.
	/// </summary>
	/// <returns>Deferred metadata for this view, available to subsequent attribute commands.</returns>
	public ViewHandle DrawView( CameraComponent camera, RenderTargetHandle target, ViewSetup viewSetup = default )
	{
		var view = CreateViewHandle();

		static void Execute( ref Entry entry, CommandList commandList )
		{
			commandList.state.views[entry.ViewId] = default;

			var camera = (CameraComponent)entry.Object1;
			if ( !camera.IsValid() ) return;

			var rt = commandList.GetRenderTarget( (string)entry.Object5 );
			if ( rt == null ) return;

			// This is a secondary view, don't draw reflections in it
			// or we'll end up with an infinite loop!
			if ( Graphics.IsChildView ) return;

			camera.RenderToTexture( rt.ColorTarget, rt.DepthTarget, (ViewSetup)entry.Object2, out var result );
			commandList.state.views[entry.ViewId] = result;
		}

		AddEntry( &Execute, new Entry { Object1 = camera, Object5 = target.Name, Object2 = viewSetup, ViewId = view.Id } );
		return view;
	}

	/// <summary>
	/// Render a planar reflection using the specified camera and the specified plane.
	/// When present, the target's depth texture receives the oblique camera depth before colour flips.
	/// </summary>
	/// <returns>Deferred metadata for this view, available to subsequent attribute commands.</returns>
	public ViewHandle DrawReflection( CameraComponent camera, Plane plane, in RenderTargetHandle target, ReflectionSetup reflectionSetup = default )
	{
		var view = CreateViewHandle();

		static void Execute( ref Entry entry, CommandList commandList )
		{
			commandList.state.views[entry.ViewId] = default;

			var camera = (CameraComponent)entry.Object1;
			if ( !camera.IsValid() ) return;

			var setup = (ReflectionSetup)entry.Object2;
			var plane = new Plane( new Vector3( entry.Data1.x, entry.Data1.y, entry.Data1.z ), entry.Data2.x );

			var rt = commandList.GetRenderTarget( (string)entry.Object5 );
			if ( rt == null ) return;

			// Don't render if we're the other side!
			if ( !plane.IsInFront( Graphics.CameraTransform.Position ) && !setup.RenderBehind )
				return;

			// This is a secondary view, don't draw reflections in it
			// or we'll end up with an infinite loop!
			if ( Graphics.IsChildView )
			{
				if ( setup.FallbackColor is { } clearColor )
				{
					Graphics.RenderTarget = rt;
					Graphics.Clear( clearColor );
					Graphics.RenderTarget = null;
				}
				return;
			}

			commandList.state.views[entry.ViewId] = RenderReflection( camera, plane, rt, setup );
		}

		AddEntry( &Execute, new Entry { Object1 = camera, Object2 = reflectionSetup, ViewId = view.Id, Object5 = target.Name, Data1 = new Vector4( plane.Normal.x, plane.Normal.y, plane.Normal.z, 0 ), Data2 = new Vector4( plane.Distance, 0, 0, 0 ) } );
		return view;
	}

	/// <summary>
	/// Whether reflections render shadows. Off, they're lit but unshadowed, so they never wait on a shadow map and
	/// can render before every other view.
	/// </summary>
	[ConVar( "r.reflections.shadows", Help = "Render shadows in planar reflections. Off, reflections are lit but unshadowed and render before every other view." )]
	internal static bool ReflectionShadows { get; set; }

	/// <summary>
	/// Render the current view mirrored in a plane into a render target, clipped to the plane's far side.
	/// Called during playback, outside a child view.
	/// </summary>
	internal static ViewData RenderReflection( CameraComponent camera, Plane plane, RenderTarget rt, ReflectionSetup setup )
	{
		var viewport = Graphics.Viewport;
		return RenderReflection( camera.SceneCamera, Graphics.SceneView, Graphics.CameraTransform, Graphics.FieldOfView, viewport.Width / viewport.Height,
			camera.ZNear, camera.ZFar, plane, rt, setup, $"{camera.GameObject.Name}.Reflection" );
	}

	/// <summary>
	/// Render a view seen from <paramref name="eye"/> mirrored in a plane into a render target, clipped to the plane's
	/// far side, as a child of <paramref name="parent"/>, or a top-level view when it isn't valid.
	/// </summary>
	internal static ViewData RenderReflection( SceneCamera camera, NativeEngine.ISceneView parent, Transform eye, float fieldOfView, float aspect,
		float zNear, float zFar, Plane plane, RenderTarget rt, ReflectionSetup setup, string name )
	{
		var reflectedTransform = eye.Mirror( plane );
		var projection = Matrix.CreateProjection( zNear, zFar, fieldOfView, aspect, setup.ViewSetup.ClipSpaceBounds ?? new Vector4( -1, -1, 1, 1 ) );

		var clipOffset = setup.ClipOffset;
		var clipPlane = new Plane( plane.Position - clipOffset * plane.Normal, plane.Normal );

		var oblique = Matrix.CreateObliqueProjection( in reflectedTransform, in clipPlane, in projection );

		setup.ViewSetup.Transform ??= reflectedTransform;
		setup.ViewSetup.FieldOfView ??= fieldOfView;
		setup.ViewSetup.ZNear ??= zNear;
		setup.ViewSetup.ZFar ??= zFar;
		setup.ViewSetup.ProjectionMatrix ??= oblique;
		setup.ViewSetup.FlipX ??= true; // we're mirroring!
		setup.ViewSetup.Reflection = true;

		camera.RenderToTexture( rt.ColorTarget, rt.DepthTarget, parent, setup.ViewSetup, name, out var result );
		return result;
	}

	/// <summary>
	/// Render a planar refraction using the specified camera and the specified plane. This is for all intents and purposes a
	/// regular view with a plane clipping it. Usually used for rendering under water.
	/// When present, the target's depth texture receives the oblique camera depth before colour flips.
	/// </summary>
	/// <returns>Deferred metadata for this view, available to subsequent attribute commands.</returns>
	public ViewHandle DrawRefraction( CameraComponent camera, Plane plane, in RenderTargetHandle target, RefractionSetup refractionSetup = default )
	{
		var view = CreateViewHandle();

		static void Execute( ref Entry entry, CommandList commandList )
		{
			commandList.state.views[entry.ViewId] = default;

			var camera = (CameraComponent)entry.Object1;
			if ( !camera.IsValid() ) return;

			var setup = (RefractionSetup)entry.Object2;
			var plane = new Plane( new Vector3( entry.Data1.x, entry.Data1.y, entry.Data1.z ), entry.Data2.x );

			var rt = commandList.GetRenderTarget( (string)entry.Object5 );
			if ( rt == null ) return;

			// Don't render if we're the other side!
			if ( !plane.IsInFront( Graphics.CameraTransform.Position ) && !setup.RenderBehind )
				return;

			// This is a secondary view, don't draw reflections in it
			// or we'll end up with an infinite loop!
			if ( Graphics.IsChildView )
			{
				if ( setup.FallbackColor is { } clearColor )
				{
					Graphics.RenderTarget = rt;
					Graphics.Clear( clearColor );
					Graphics.RenderTarget = null;
				}
				return;
			}

			var transform = Graphics.CameraTransform;
			var projection = Matrix.CreateProjection( camera.ZNear, camera.ZFar, Graphics.FieldOfView, Graphics.Viewport.Width / Graphics.Viewport.Height, setup.ViewSetup.ClipSpaceBounds ?? new Vector4( -1, -1, 1, 1 ) );

			var eyeDistance = plane.GetDistance( transform.Position );
			var clipOffset = setup.ClipOffset;

			var clipPlane = new Plane( plane.Position + clipOffset * plane.Normal, plane.Normal );

			var oblique = Matrix.CreateObliqueProjection( in transform, in clipPlane, in projection );

			setup.ViewSetup.Transform ??= transform;
			setup.ViewSetup.FieldOfView ??= Graphics.FieldOfView;
			setup.ViewSetup.ProjectionMatrix ??= oblique;

			camera.RenderToTexture( rt.ColorTarget, rt.DepthTarget, setup.ViewSetup, out var result );
			commandList.state.views[entry.ViewId] = result;
		}

		AddEntry( &Execute, new Entry { Object1 = camera, Object2 = refractionSetup, ViewId = view.Id, Object5 = target.Name, Data1 = new Vector4( plane.Normal.x, plane.Normal.y, plane.Normal.z, 0 ), Data2 = new Vector4( plane.Distance, 0, 0, 0 ) } );
		return view;
	}

	// ---- Portaballs patch P1 (additive: nothing above this line is changed) -------------------------------

	/// <summary>
	/// Portaballs patch P1. How many portal views <see cref="DrawPortal"/> has submitted since the engine started.
	/// Written while rendering, so read it only as a rough counter.
	/// </summary>
	public static int PortalViewsDrawn { get; private set; }

	/// <summary>Portaballs patch P1 diagnostics: the eye the latest <see cref="DrawPortal"/> playback was drawn for.</summary>
	public static Transform PortalLastEye { get; private set; }

	/// <summary>Portaballs patch P1 diagnostics: that eye's projection as rebuilt from its side planes (before the clip).</summary>
	public static Matrix PortalLastProjectionFromPlanes { get; private set; }

	/// <summary>Portaballs patch P1 diagnostics: the engine's own projection matrix for that view, as native reports it.</summary>
	public static Matrix PortalLastProjectionFromEngine { get; private set; }

	/// <summary>
	/// Portaballs patch P1. Render what the eye of the view being drawn would see after being carried through a
	/// portal, into a render target. Everything is worked out at playback, from the view the list is playing in,
	/// so in VR each eye gets its own picture: the virtual eye is <paramref name="crossing"/> applied to that eye,
	/// the projection is that eye's own (so the picture lines up with the view pixel for pixel when pasted by
	/// screen position), and its near plane is moved onto <paramref name="keepPlane"/> so that nothing behind the
	/// exit wall is drawn.
	/// </summary>
	/// <param name="camera">The viewing camera (its scene, tags and near/far are used).</param>
	/// <param name="crossing">Rigid transform (scale 1) that carries a real eye to the virtual eye.</param>
	/// <param name="keepPlane">World plane on the exit wall. Only what is in front of it is drawn.</param>
	/// <param name="target">Render target to draw into.</param>
	/// <param name="setup">As for <see cref="DrawRefraction"/>. ClipOffset moves the plane along its normal.
	/// RenderBehind: when the virtual eye is already in front of the plane, draw without the clip instead of skipping.</param>
	/// <param name="projectionSource">0 = rebuild the eye's projection from the view's side planes. 1 = use the
	/// view's own projection matrix as native reports it.</param>
	/// <param name="plainWithin">When the virtual eye is closer than this behind the plane (world units), draw without
	/// the clip. That close, an oblique near plane leaves too little depth precision for anything far away, and all
	/// that lies between the eye and the plane is the back of the exit wall itself.</param>
	/// <returns>Deferred metadata for this view, available to subsequent attribute commands.</returns>
	public ViewHandle DrawPortal( CameraComponent camera, Transform crossing, Plane keepPlane, in RenderTargetHandle target, RefractionSetup setup = default, int projectionSource = 0, float plainWithin = 0f )
	{
		var view = CreateViewHandle();

		static void Execute( ref Entry entry, CommandList commandList )
		{
			commandList.state.views[entry.ViewId] = default;

			var camera = (CameraComponent)entry.Object1;
			if ( !camera.IsValid() ) return;

			var setup = (RefractionSetup)entry.Object2;

			var rt = commandList.GetRenderTarget( (string)entry.Object5 );
			if ( rt == null ) return;

			// Same rule as its siblings: a view drawn from inside another view draws no views of its own.
			if ( Graphics.IsChildView )
			{
				if ( setup.FallbackColor is { } clearColor )
				{
					Graphics.RenderTarget = rt;
					Graphics.Clear( clearColor );
					Graphics.RenderTarget = null;
				}
				return;
			}

			var keepPlane = new Plane( new Vector3( entry.Data1.x, entry.Data1.y, entry.Data1.z ), entry.Data2.x );
			var crossing = new Transform( new Vector3( entry.Data3.x, entry.Data3.y, entry.Data3.z ),
				new Rotation( entry.Data4.x, entry.Data4.y, entry.Data4.z, entry.Data4.w ) );
			int projectionSource = (int)entry.Data2.y;

			// This view's own eye (in VR: the eye being drawn), carried through the portal.
			var eye = new Transform( Graphics.CameraTransform.Position, Graphics.CameraTransform.Rotation );
			var virtualEye = crossing.ToWorld( eye );

			float zn = setup.ViewSetup.ZNear ?? camera.ZNear;
			float zf = setup.ViewSetup.ZFar ?? camera.ZFar;

			// This view's own projection, so that only depth differs from the view the picture is pasted into.
			var fromPlanes = PortalProjectionOfCurrentView( zn, zf );
			var fromEngine = Graphics.ViewFrustum.GetProj();
			var projection = projectionSource == 1 ? fromEngine : fromPlanes;

			PortalLastEye = eye;
			PortalLastProjectionFromPlanes = fromPlanes;
			PortalLastProjectionFromEngine = fromEngine;

			var clipPlane = new Plane( keepPlane.Position + setup.ClipOffset * keepPlane.Normal, keepPlane.Normal );
			float behind = -clipPlane.GetDistance( virtualEye.Position );
			float plainWithin = entry.Data2.z;
			if ( behind < 0f || behind >= plainWithin )
			{
				if ( !PortalObliqueNearPlane( ref projection, virtualEye, clipPlane ) )
				{
					// The virtual eye is not behind the plane, so there is nothing to cut away in front of it.
					if ( !setup.RenderBehind )
						return;
				}
			}

			setup.ViewSetup.Transform = virtualEye;
			setup.ViewSetup.ZNear ??= zn;
			setup.ViewSetup.ZFar ??= zf;
			setup.ViewSetup.ProjectionMatrix = projection;

			camera.RenderToTexture( rt.ColorTarget, rt.DepthTarget, setup.ViewSetup, out var result );
			commandList.state.views[entry.ViewId] = result;
			PortalViewsDrawn++;
		}

		AddEntry( &Execute, new Entry
		{
			Object1 = camera,
			Object2 = setup,
			ViewId = view.Id,
			Object5 = target.Name,
			Data1 = new Vector4( keepPlane.Normal.x, keepPlane.Normal.y, keepPlane.Normal.z, 0 ),
			Data2 = new Vector4( keepPlane.Distance, projectionSource, plainWithin, 0 ),
			Data3 = new Vector4( crossing.Position.x, crossing.Position.y, crossing.Position.z, 0 ),
			Data4 = new Vector4( crossing.Rotation.x, crossing.Rotation.y, crossing.Rotation.z, crossing.Rotation.w )
		} );
		return view;
	}

	/// <summary>
	/// Portaballs patch P1. The off-axis projection of the view being drawn, rebuilt from its four side planes, in the
	/// layout of <see cref="Matrix.CreateProjection"/> (depth 0 at near, 1 at far). Each side plane passes through
	/// the eye, so its normal in the eye's own axes (x forward, y left, z up) gives the tangent of its angle
	/// whichever way the normal points.
	/// </summary>
	static Matrix PortalProjectionOfCurrentView( float zn, float zf )
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

	/// <summary>
	/// Portaballs patch P1. Moves a projection's near plane onto a world plane (Lengyel's oblique near plane, written
	/// out for this engine's matrix layout). Unlike <see cref="Matrix.CreateObliqueProjection"/> it stays correct
	/// however close the eye is to the plane. Returns false, and leaves the projection alone, when the eye is not
	/// behind the plane. The same lines, with tests, are in Portaballs' core (PortalViewMath.ObliqueNearPlaneColumnLayout).
	/// </summary>
	static bool PortalObliqueNearPlane( ref Matrix projection, in Transform eye, in Plane keep )
	{
		var nl = eye.Rotation.Inverse * keep.Normal;                 // the eye's own axes: x forward, y left, z up
		var c = new System.Numerics.Vector4( -nl.y, nl.z, -nl.x, keep.GetDistance( eye.Position ) );   // view space; w < 0 behind the plane
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

}




