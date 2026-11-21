using System;
using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// The picture side of one wall's paint: a flat panel the size of the wall, a few millimetres in front
/// of it, drawn with <c>shaders/portaballs_paint.shader</c>, plus the small texture that shader reads.
///
/// The texture is the paint grid itself (core PaintSurface): one texel per paint texel, R = orange,
/// G = blue, B = wetness, A = thickness. Row 0 is the BOTTOM of the wall, and the panel's v coordinate
/// is 0 at the bottom too, so nothing is flipped anywhere.
///
/// A <see cref="PaintWall"/> owns one of these. It is a plain class, not a component.
/// </summary>
public sealed class PaintPanel
{
	public const string ShaderPath = "shaders/portaballs_paint.shader";

	/// <summary>The panel's own object in the scene (no collider: balls go straight through to the wall).</summary>
	public GameObject Object { get; private set; }

	public ModelRenderer Renderer { get; private set; }

	/// <summary>The live paint texture.</summary>
	public Texture Mask { get; private set; }

	/// <summary>Bytes sent to the graphics card so far, for the timing report.</summary>
	public long BytesUploaded { get; private set; }

	/// <summary>How many times part of the texture was sent.</summary>
	public int Uploads { get; private set; }

	private readonly int _width;
	private readonly int _height;
	private readonly byte[] _rgba;      // the whole grid, kept up to date by PaintSurface.CopyDirtyToRgba
	private readonly byte[] _packed;    // the changed rectangle, rows packed tight, as Texture.Update wants it
	private Texture _ghost;

	public PaintPanel( Scene scene, string name, WallFrame frame, PaintSurface surface, float offsetMetres )
	{
		_width = surface.Width;
		_height = surface.Height;
		_rgba = new byte[surface.RgbaByteCount];
		_packed = new byte[surface.RgbaByteCount];

		Mask = Texture.Create( _width, _height, ImageFormat.RGBA8888 )
			.WithName( "portaballs_paint_" + name )
			.WithDynamicUsage()
			.WithData( _rgba )
			.Finish();

		Object = new GameObject( scene, false, name );
		Object.WorldPosition = SboxConvert.ToInches( frame.Origin );

		Renderer = Object.AddComponent<ModelRenderer>();
		Renderer.Model = BuildModel( frame, offsetMetres );
		Renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		Object.Enabled = true;

		// Per-wall values go on the renderer: Material.FromShader hands every wall the same material.
		RenderAttributes a = Renderer.Attributes;
		a.Set( "PaintMask", Mask );

		// The fading picture of a replaced patch gets its texture now, while loading: making it in the frame
		// of the first replace cost over a millisecond. It shows nothing until StartGhost (GhostAlpha is 0).
		_ghost = Texture.Create( _width, _height, ImageFormat.RGBA8888 )
			.WithName( "portaballs_paint_ghost" )
			.WithDynamicUsage()
			.WithData( _rgba )
			.Finish();
		a.Set( "GhostMask", _ghost );
		a.Set( "PaintMaskSize", new Vector4( _width, _height, _width / frame.Width, _height / frame.Height ) );
		a.Set( "WallU", SboxConvert.ToEngine( frame.U ) );
		a.Set( "WallV", SboxConvert.ToEngine( frame.V ) );
		a.Set( "WallN", SboxConvert.ToEngine( frame.Normal ) );
		// Where the wall is in the world (inches) and how far the panel stands off it: the shader draws
		// nothing for an eye that is behind the panel (see the see-through view, PortalView).
		a.Set( "WallOrigin", SboxConvert.ToInches( frame.Origin ) );
		a.Set( "WallSize", new Vector3( SboxConvert.LengthToInches( frame.Width ), SboxConvert.LengthToInches( frame.Height ), SboxConvert.LengthToInches( offsetMetres ) ) );
		a.Set( "OrangeColor", LinearColour( PaintColor.Orange ) );
		a.Set( "BlueColor", LinearColour( PaintColor.Blue ) );
		a.Set( "GhostAlpha", 0f );
		a.Set( "GhostIsBlue", 0f );
	}

	/// <summary>The paint colour the shader should use, taken from the ball's tint so the two always match.</summary>
	public static Vector3 LinearColour( PaintColor paint )
	{
		Color c = Portaball.TintFor( paint );
		return new Vector3( SrgbToLinear( c.r ), SrgbToLinear( c.g ), SrgbToLinear( c.b ) );
	}

	private static float SrgbToLinear( float v )
	{
		return v <= 0.04045f ? v / 12.92f : MathF.Pow( ( v + 0.055f ) / 1.055f, 2.4f );
	}

	// Four corners. u runs along the wall to the viewer's right, v runs up; texture (0,0) is the lower-left corner.
	private static Model BuildModel( WallFrame frame, float offsetMetres )
	{
		Material material = Material.FromShader( ShaderPath );

		Vector3 normal = SboxConvert.ToEngine( frame.Normal );
		Vector3 tangent = SboxConvert.ToEngine( frame.U );
		Vertex[] v = new Vertex[4];
		Vec2[] corners = { new Vec2( 0f, 0f ), new Vec2( 1f, 0f ), new Vec2( 1f, 1f ), new Vec2( 0f, 1f ) };
		Vector3 min = new Vector3( float.MaxValue, float.MaxValue, float.MaxValue );
		Vector3 max = new Vector3( float.MinValue, float.MinValue, float.MinValue );
		for ( int k = 0; k < 4; k++ )
		{
			// Position relative to the wall's lower-left corner, in inches (the panel object sits on that corner).
			Vec3 metres = frame.U * ( corners[k].X * frame.Width ) + frame.V * ( corners[k].Y * frame.Height ) + frame.Normal * offsetMetres;
			Vector3 p = SboxConvert.ToInches( metres );
			v[k] = new Vertex( p, normal, tangent, new Vector4( corners[k].X, corners[k].Y, 0f, 0f ) );
			min = Vector3.Min( min, p );
			max = Vector3.Max( max, p );
		}

		// Both windings, so it does not matter which one the engine calls the front: the other is culled.
		int[] indices = { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 };

		Mesh mesh = new Mesh( material );
		mesh.CreateVertexBuffer<Vertex>( v.Length, v );
		mesh.CreateIndexBuffer( indices.Length, indices );
		Vector3 margin = new Vector3( 1f, 1f, 1f );
		mesh.Bounds = new BBox( min - margin, max + margin );
		return Model.Builder.AddMesh( mesh ).Create();
	}

	/// <summary>
	/// Send whatever changed in the paint since the last call to the graphics card. Does nothing, and
	/// costs almost nothing, when the paint did not change. Returns true when something was sent.
	/// </summary>
	public bool UploadChanges( PaintSurface surface )
	{
		TexelRect r = surface.CopyDirtyToRgba( _rgba );
		if ( r.IsEmpty ) return false;

		int w = r.X1 - r.X0 + 1;
		int h = r.Y1 - r.Y0 + 1;
		int rowBytes = w * 4;
		for ( int y = 0; y < h; y++ )
		{
			Array.Copy( _rgba, ( ( r.Y0 + y ) * _width + r.X0 ) * 4, _packed, y * rowBytes, rowBytes );
		}

		// Texture row = paint row. Row 0 is the bottom of the wall in both.
		Mask.Update( new ReadOnlySpan<byte>( _packed, 0, rowBytes * h ), r.X0, r.Y0, w, h );
		BytesUploaded += rowBytes * h;
		Uploads++;
		return true;
	}

	/// <summary>
	/// Keep a picture of the paint as it is right now, to fade out after the real paint has been erased.
	/// Call this BEFORE erasing. Only the given colour of the picture is shown.
	/// </summary>
	public void StartGhost( PaintSurface surface, PaintColor color )
	{
		// Bring our byte copy of the grid up to date (this converts only what changed since last frame),
		// then hand the whole copy to the ghost texture. No second conversion of the wall is needed.
		UploadChanges( surface );
		_ghost.Update( new ReadOnlySpan<byte>( _rgba, 0, _rgba.Length ), 0, 0, _width, _height );
		BytesUploaded += _rgba.Length;
		Uploads++;
		Renderer.Attributes.Set( "GhostIsBlue", color == PaintColor.Blue ? 1f : 0f );
		SetGhostAlpha( 1f );
	}

	/// <summary>How strongly the ghost shows: 1 = fully, 0 = gone.</summary>
	public void SetGhostAlpha( float alpha )
	{
		Renderer.Attributes.Set( "GhostAlpha", alpha );
	}

	/// <summary>Set one of the shader's look values by name (for tuning from a script).</summary>
	public void SetLook( string name, float value )
	{
		Renderer.Attributes.Set( name, value );
	}
}
