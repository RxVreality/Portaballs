using System;
using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// Experiment E2, built in: a hook that the engine calls while it DRAWS each view, and that writes down
/// what that view really used: where its camera was, which part of the picture it drew into, and its
/// four lens angles. In a headset each eye is a view of its own, so this is how the game (and the person
/// reading the board) finds out where the engine really puts the two eyes and how far apart.
///
/// The game thread cannot see any of this: the engine keeps the per-eye poses to itself. An invisible
/// object in the scene (SceneCustomObject) is "drawn" in every view; its draw callback reads
/// Graphics.CameraTransform, Graphics.Viewport and Graphics.Frustum, which belong to the view being drawn.
///
/// Views near the game camera count as EYE views (one on a flat screen, two in a headset). Every other
/// view (the far side of a portal, drawn from somewhere else) counts as an OTHER view.
/// </summary>
public sealed class PortalViewProbe : Component
{
	/// <summary>One drawn view, as the render hook saw it.</summary>
	public struct ViewSeen
	{
		/// <summary>Camera position of the view, metres.</summary>
		public Vec3 Position;
		public Quat Rotation;
		/// <summary>Where in the render target it drew: left, top, width, height in pixels.</summary>
		public float ViewportX, ViewportY, ViewportWidth, ViewportHeight;
		/// <summary>Lens angles recovered from the view's side planes. Check <see cref="LensTangents.IsValid"/>.</summary>
		public LensTangents Lens;
		/// <summary>How many times the hook ran for this view in the frame (once per pass the engine draws it in).</summary>
		public int Calls;
	}

	/// <summary>Views near the game camera in the latest finished frame: 1 on a flat screen, 2 in a headset.</summary>
	public int EyeCount { get; private set; }

	/// <summary>Other views in the latest finished frame (portal pictures and anything else).</summary>
	public int OtherCount { get; private set; }

	public ViewSeen Eye( int i ) => _eyes[i];
	public ViewSeen Other( int i ) => _others[i];

	/// <summary>Distance between the first two eye views, metres. 0 with fewer than two.</summary>
	public float EyeSeparation { get; private set; }

	/// <summary>Distance from the game thread's camera (in VR: the head) to each eye view, metres.</summary>
	public float[] EyeFromHead { get; } = new float[MaxEach];

	/// <summary>Total hook calls in the latest finished frame.</summary>
	public int CallsLastFrame { get; private set; }

	/// <summary>Goes up when a new frame's worth has been collected.</summary>
	public int FramesCollected { get; private set; }

	public const int MaxEach = 4;

	private struct Raw
	{
		public int Stamp;
		public Vector3 Position;
		public Rotation Rotation;
		public Rect Viewport;
		public Vector3 Right, Left, Top, Bottom;   // side-plane normals, world
		public bool HasFrustum;
	}

	private const int RingSize = 128;
	private readonly Raw[] _ring = new Raw[RingSize];
	private int _write;          // written by the render hook
	private int _stamp = 1;      // written by the game thread, read by the hook

	private readonly ViewSeen[] _eyes = new ViewSeen[MaxEach];
	private readonly ViewSeen[] _others = new ViewSeen[MaxEach];
	private SceneCustomObject _hook;
	private Vector3 _headLastFrame;
	private Vector3 _headThisFrame;

	protected override void OnEnabled()
	{
		if ( _hook != null ) return;
		_hook = new SceneCustomObject( Scene.SceneWorld );
		_hook.RenderOverride = OnDrawnInView;
		_hook.Flags.CastShadows = false;
	}

	protected override void OnDisabled()
	{
		_hook?.Delete();
		_hook = null;
	}

	// RENDER SIDE. Runs while a view is being drawn, possibly on another thread: it only writes plain
	// numbers into its own slot. (The engine's code whitelist has no locks; a torn record is dropped below.)
	private void OnDrawnInView( SceneObject obj )
	{
		Raw r = new Raw();
		r.Stamp = _stamp;
		r.Position = Graphics.CameraPosition;
		r.Rotation = Graphics.CameraRotation;
		r.Viewport = Graphics.Viewport;
		try
		{
			Frustum f = Graphics.Frustum;
			r.Right = f.RightPlane.Normal;
			r.Left = f.LeftPlane.Normal;
			r.Top = f.TopPlane.Normal;
			r.Bottom = f.BottomPlane.Normal;
			r.HasFrustum = true;
		}
		catch ( Exception )
		{
			r.HasFrustum = false;
		}
		int slot = _write;
		_write = ( slot + 1 ) % RingSize;
		_ring[slot] = r;
	}

	protected override void OnPreRender()
	{
		// Everything stamped with the PREVIOUS number has been drawn by now: collect it, then move on.
		CameraComponent camera = Scene.Camera;
		bool vr = Game.IsRunningInVR && Input.VR is not null;
		Vector3 head = vr ? Input.VR.Head.Position : ( camera.IsValid() ? camera.WorldPosition : Vector3.Zero );
		Collect( _stamp - 1, _headLastFrame );
		_headLastFrame = _headThisFrame;
		_headThisFrame = head;
		_stamp++;
	}

	private void Collect( int stamp, Vector3 head )
	{
		int eyes = 0, others = 0, calls = 0;
		for ( int i = 0; i < RingSize; i++ )
		{
			Raw r = _ring[i];
			if ( r.Stamp != stamp ) continue;
			calls++;
			if ( r.Viewport.Width < 1f || r.Viewport.Height < 1f ) continue;

			ViewSeen seen = new ViewSeen();
			seen.Position = SboxConvert.ToMetres( r.Position );
			seen.Rotation = SboxConvert.ToCore( r.Rotation );
			seen.ViewportX = r.Viewport.Left;
			seen.ViewportY = r.Viewport.Top;
			seen.ViewportWidth = r.Viewport.Width;
			seen.ViewportHeight = r.Viewport.Height;
			seen.Calls = 1;
			if ( r.HasFrustum )
			{
				// Side-plane normals in the camera's own axes give the lens angles (core PortalViewMath).
				Rotation inverse = r.Rotation.Inverse;
				PortalViewMath.TangentsFromSidePlanes( SboxConvert.ToCore( inverse * r.Right ), SboxConvert.ToCore( inverse * r.Left ),
					SboxConvert.ToCore( inverse * r.Top ), SboxConvert.ToCore( inverse * r.Bottom ), out seen.Lens );
			}

			// An eye is within 25 cm of where the game thread had the camera (or head) in that frame or the one after.
			bool isEye = ( r.Position - head ).Length < 10f || ( r.Position - _headThisFrame ).Length < 10f;
			ViewSeen[] list = isEye ? _eyes : _others;
			int count = isEye ? eyes : others;
			bool merged = false;
			for ( int k = 0; k < count; k++ )
			{
				if ( ( list[k].Position - seen.Position ).Length() < 0.0005f && MathF.Abs( list[k].ViewportX - seen.ViewportX ) < 0.5f
					&& MathF.Abs( list[k].ViewportWidth - seen.ViewportWidth ) < 0.5f )
				{
					list[k].Calls++;
					merged = true;
					break;
				}
			}
			if ( merged || count >= MaxEach ) continue;
			list[count] = seen;
			if ( isEye ) eyes++; else others++;
		}
		if ( calls == 0 && FramesCollected > 0 ) return;   // that frame was not drawn (or not yet): keep the last answer

		EyeCount = eyes;
		OtherCount = others;
		CallsLastFrame = calls;
		FramesCollected++;
		EyeSeparation = eyes >= 2 ? ( _eyes[0].Position - _eyes[1].Position ).Length() : 0f;
		Vec3 headMetres = SboxConvert.ToMetres( head );
		for ( int i = 0; i < MaxEach; i++ ) EyeFromHead[i] = i < eyes ? ( _eyes[i].Position - headMetres ).Length() : 0f;
	}

	/// <summary>One line per view, for the console and the engine's log file.</summary>
	public string Report()
	{
		string s = $"{EyeCount} eye view(s), {OtherCount} other view(s), {CallsLastFrame} hook calls";
		for ( int i = 0; i < EyeCount; i++ ) s += " | EYE " + ( i + 1 ) + " " + Describe( _eyes[i] ) + $" {EyeFromHead[i] * 1000f:0.0} mm from the game camera";
		if ( EyeCount >= 2 ) s += $" | EYES {EyeSeparation * 1000f:0.0} mm apart";
		for ( int i = 0; i < OtherCount; i++ ) s += " | OTHER " + ( i + 1 ) + " " + Describe( _others[i] );
		return s;
	}

	public static string Describe( ViewSeen v )
	{
		string lens = "lens ?";
		if ( v.Lens.IsValid )
		{
			v.Lens.ToDegrees( out float l, out float r, out float d, out float u );
			lens = $"lens L {l:0.0} R {r:0.0} D {d:0.0} U {u:0.0} deg";
		}
		return $"pos ({v.Position.X:0.0000}, {v.Position.Y:0.0000}, {v.Position.Z:0.0000}) m, viewport {v.ViewportX:0},{v.ViewportY:0} {v.ViewportWidth:0}x{v.ViewportHeight:0}, {lens}, x{v.Calls}";
	}
}
