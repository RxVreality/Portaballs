// Portaballs glowing paint: balls in the air, the ball in the hand, droplets.
//
// The same neon as the paint on the walls (portaballs_paint.shader): the thing is SELF-LIT in its paint
// colour, with a share of the room's own light kept so it still has a round form and sits in the room.
// The middle of the ball as you see it is hotter than its edge, like a neon bulb.
//
// Put on a renderer by Code/Game/Room/PaintLook.cs (PaintLook.Glow), which also hands over the colour.
// Glow numbers are multiples of the paint colour; the screen cuts each channel off at 1, so above about
// 1.5 the colour heats up (orange toward amber, blue toward cyan).

HEADER
{
	Description = "Portaballs glowing paint ball";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth();
}

COMMON
{
	#include "common/shared.hlsl"
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );
		return FinalizeVertex( o );
	}
}

PS
{
	#include "common/pixel.hlsl"

	// The paint colour, linear (the same value the wall paint gets: PaintPanel.LinearColour).
	float3 GlowColor < Attribute( "GlowColor" ); Default3( 1.0, 0.17, 0.004 ); >;
	// The glow at the ball's edge, and how much more the middle (the part facing you) gives off.
	float GlowAmount < Attribute( "GlowAmount" ); Default( 1.15 ); >;
	float GlowCore < Attribute( "GlowCore" ); Default( 0.7 ); >;
	// How much of the ordinary lamp-lit colour is kept under the glow.
	float GlowLit < Attribute( "GlowLit" ); Default( 0.35 ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		Material m = Material::Init( i );
		float3 n = normalize( i.vNormalWs.xyz );
		float3 toEye = normalize( g_vCameraPositionWs.xyz - ( i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz ) );
		float facing = saturate( dot( n, toEye ) );

		m.Albedo = GlowColor * GlowLit;
		m.Normal = n;
		m.Roughness = 0.25;
		m.Metalness = 0.0;
		m.AmbientOcclusion = 1.0;
		m.Opacity = 1.0;
		m.Emission = GlowColor * GlowAmount * ( 1.0 + GlowCore * facing * facing );
		return ShadingModelStandard::Shade( m );
	}
}
