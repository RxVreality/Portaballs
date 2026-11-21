using System;
using Sandbox;
using Sandbox.Rendering;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// The window test (rendering experiment E1): the simplest possible check of the see-through machinery.
///
/// A sheet stands in the middle of the room. Its picture is the scene drawn AGAIN from the very same eye
/// (no portal crossing at all), cut off at the sheet, and pasted back by screen position. If the extra
/// view, the projection handed to the engine, the cut and the paste are all right, the sheet cannot be
/// seen: it is a clean hole onto whatever is behind it. Edges of things that stick out past the sheet
/// line up across its border, near things hide far things, and nothing is flipped or shifted.
///
/// Things placed for the test:
///   - a long bar and a tall post behind the sheet, both sticking out past its edges (edges must line up);
///   - a cube pushed half way THROUGH the sheet (the front half is drawn by the main view, the back half
///     by the pasted picture: the two halves must meet);
///   - a cube in front of the sheet (it must hide the sheet: near hides far);
///   - cubes at several depths behind (near ones must hide far ones inside the picture).
///
/// Build it from a script:  PortalWindowTest.Create( room )   and remove it with  .GameObject.Destroy().
/// <see cref="Tint"/> below 1 darkens the picture on purpose so the sheet can be found; 1 = invisible.
/// </summary>
public sealed class PortalWindowTest : Component
{
	public const string ShaderPath = "shaders/portaballs_window.shader";

	public TestRoom Room { get; set; }

	/// <summary>Multiplies the pasted picture. 1 = the sheet is invisible when all is well.</summary>
	public float Tint { get; set; } = 1f;

	/// <summary>Width of the yellow frame round the sheet, as a share of its size. 0 = none.</summary>
	public float Frame { get; set; } = 0f;

	/// <summary>False = the sheet is hidden (for the "what should it look like" comparison picture).</summary>
	public bool ShowSheet { get; set; } = true;

	/// <summary>Centre of the sheet, metres.</summary>
	public Vec3 Centre { get; private set; }

	/// <summary>The way the sheet faces (toward the viewer), and its size in metres.</summary>
	public Vec3 Facing { get; private set; }
	public float Size { get; private set; } = 1.2f;

	private ModelRenderer _sheet;
	private GameObject _sheetObject;
	private readonly CommandList[] _lists = { new CommandList( "Portaballs window test a" ), new CommandList( "Portaballs window test b" ) };
	private int _flip;

	public static PortalWindowTest Create( TestRoom room )
	{
		GameObject go = new GameObject( room.GameObject, true, "Portal window test" );
		PortalWindowTest test = go.AddComponent<PortalWindowTest>();
		test.Room = room;
		test.Build();
		return test;
	}

	private void Build()
	{
		Vec3 floor = SboxConvert.ToMetres( Room.WorldPosition );
		Centre = floor + new Vec3( 0.2f, 0f, 1.5f );
		Facing = new Vec3( -1f, 0f, 0f );   // toward the desk camera's end of the room

		// The sheet: a square in the YZ plane, both windings.
		Material material = Material.FromShader( ShaderPath );
		float h = SboxConvert.LengthToInches( Size * 0.5f );
		Vector3 normal = SboxConvert.ToEngine( Facing );
		Vector3 tangent = new Vector3( 0f, -1f, 0f );
		Vertex[] v =
		{
			new Vertex( new Vector3( 0f, h, -h ), normal, tangent, new Vector4( 0f, 0f, 0f, 0f ) ),
			new Vertex( new Vector3( 0f, -h, -h ), normal, tangent, new Vector4( 1f, 0f, 0f, 0f ) ),
			new Vertex( new Vector3( 0f, -h, h ), normal, tangent, new Vector4( 1f, 1f, 0f, 0f ) ),
			new Vertex( new Vector3( 0f, h, h ), normal, tangent, new Vector4( 0f, 1f, 0f, 0f ) ),
		};
		int[] indices = { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 };
		Mesh mesh = new Mesh( material );
		mesh.CreateVertexBuffer<Vertex>( v.Length, v );
		mesh.CreateIndexBuffer( indices.Length, indices );
		mesh.Bounds = new BBox( new Vector3( -1f, -h, -h ), new Vector3( 1f, h, h ) );

		_sheetObject = new GameObject( GameObject, false, "Window sheet" );
		_sheetObject.WorldPosition = SboxConvert.ToInches( Centre );
		_sheet = _sheetObject.AddComponent<ModelRenderer>();
		_sheet.Model = Model.Builder.AddMesh( mesh ).Create();
		_sheet.RenderType = ModelRenderer.ShadowRenderType.Off;
		_sheetObject.Enabled = true;

		// Behind the sheet (further along +X), sticking out past its edges.
		Box( "bar behind, sticks out both sides", Centre + new Vec3( 0.9f, 0f, 0.15f ), new Vec3( 0.12f, 2.6f, 0.12f ), new Color( 0.9f, 0.2f, 0.2f ) );
		Box( "post behind, sticks out top and bottom", Centre + new Vec3( 1.3f, -0.3f, 0f ), new Vec3( 0.12f, 0.12f, 2.6f ), new Color( 0.2f, 0.8f, 0.3f ) );
		// Depth order inside the picture: the near yellow cube must hide part of the far blue one.
		Box( "near cube behind", Centre + new Vec3( 0.6f, 0.25f, -0.25f ), new Vec3( 0.3f, 0.3f, 0.3f ), new Color( 0.95f, 0.85f, 0.2f ) );
		Box( "far cube behind", Centre + new Vec3( 2.2f, 0.45f, -0.15f ), new Vec3( 0.7f, 0.7f, 0.7f ), new Color( 0.2f, 0.4f, 0.95f ) );
		// Half way through the sheet, at its left edge as the viewer sees it.
		Box( "cube through the sheet", Centre + new Vec3( 0f, 0.45f, 0.35f ), new Vec3( 0.5f, 0.2f, 0.2f ), new Color( 0.9f, 0.5f, 0.9f ) );
		// In front of the sheet: must hide it.
		Box( "cube in front", Centre + new Vec3( -0.6f, -0.35f, -0.3f ), new Vec3( 0.2f, 0.2f, 0.2f ), new Color( 0.95f, 0.95f, 0.95f ) );
		// A tilted plank crossing the sheet's lower right corner, behind it.
		GameObject plank = Box( "tilted plank behind", Centre + new Vec3( 1.0f, -0.6f, -0.55f ), new Vec3( 0.08f, 1.4f, 0.08f ), new Color( 0.2f, 0.9f, 0.9f ) );
		plank.WorldRotation = Rotation.From( 0f, 25f, 35f );
	}

	private GameObject Box( string name, Vec3 centre, Vec3 size, Color tint )
	{
		GameObject go = new GameObject( GameObject, false, name );
		go.WorldPosition = SboxConvert.ToInches( centre );
		go.LocalScale = SboxConvert.BoxScaleFor( size );
		ModelRenderer r = go.AddComponent<ModelRenderer>();
		r.Model = Model.Cube;
		r.Tint = tint;
		go.Enabled = true;
		return go;
	}

	protected override void OnPreRender()
	{
		Rebuild();
	}

	/// <summary>Write this frame's render commands now (see PortalView.Rebuild for when a script must call it).</summary>
	public void Rebuild()
	{
		if ( !_sheet.IsValid() || !Room.IsValid() || !Room.View.IsValid() ) return;
		if ( _sheetObject.Enabled != ShowSheet ) _sheetObject.Enabled = ShowSheet;
		if ( !ShowSheet ) return;

		CameraComponent camera = Scene.Camera;
		if ( !camera.IsValid() ) return;
		PortalView view = Room.View;
		view.ReadCamera( camera, out Vec3 eye, out Quat eyeRotation, out LensTangents drawLens, out _, out float w, out float h, out _ );

		_sheet.Attributes.Set( "WindowTint", new Vector3( Tint, Tint, Tint ) );
		_sheet.Attributes.Set( "WindowFrame", Frame );

		CommandList list = _lists[_flip ^= 1];
		list.Reset();
		list.Attributes.Set( "HasPortalOrange", false );

		// Keep what is beyond the sheet as the viewer sees it. Seen from behind, nothing is drawn (magenta).
		Vec3 away = -Facing;
		float eyeSide = Vec3.Dot( eye - Centre, Facing );
		PortalViewMode mode = view.ModeInUse;
		if ( eyeSide > 0f && mode != PortalViewMode.C )
		{
			// 2 mm beyond the sheet, so the sheet itself (which cannot show a picture inside its own picture) is cut away.
			Plane3 keep = new Plane3( away, -Vec3.Dot( away, Centre ) - 0.002f );
			view.AddView( list, camera, 0, RigidTransform.Identity, keep, view.ExcludeAlways, eye, eyeRotation, drawLens, w, h, mode, 7 );
		}
		_sheet.ExecuteBefore = list;
	}
}
