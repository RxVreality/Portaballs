using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// The neon look of paint that is NOT on a wall: balls in the air, the ball in the hand, droplets.
/// (Paint on a wall is <c>shaders/portaballs_paint.shader</c>, its "neon" dials; the light the patches
/// throw on the room is <see cref="PaintGlowLights"/>.)
///
/// One call dresses a renderer: <c>PaintLook.Glow( renderer, paint )</c>. It sets the tint as before and
/// swaps the plain lit material for <c>shaders/portaballs_glow.shader</c>, which is self-lit in the paint's
/// colour, so a ball matches the glowing paint it makes. The colour itself still comes from
/// <see cref="Portaball.TintFor"/>, the one source for every paint colour in the game.
/// </summary>
public static class PaintLook
{
	public const string GlowShaderPath = "shaders/portaballs_glow.shader";

	/// <summary>False = plain lit balls and droplets, as before the neon look (for comparing).</summary>
	public static bool GlowingBalls { get; set; } = true;

	private static Material _glow;

	/// <summary>Give a ball, a held ball or a droplet its paint colour and the glowing material.</summary>
	public static void Glow( ModelRenderer renderer, PaintColor paint )
	{
		renderer.Tint = Portaball.TintFor( paint );
		if ( !GlowingBalls )
		{
			renderer.MaterialOverride = null;
			return;
		}
		if ( _glow == null || !_glow.IsValid() ) _glow = Material.FromShader( GlowShaderPath );
		renderer.MaterialOverride = _glow;
		// Per renderer, not on the material: every ball shares the one material.
		renderer.Attributes.Set( "GlowColor", PaintPanel.LinearColour( paint ) );
	}
}
