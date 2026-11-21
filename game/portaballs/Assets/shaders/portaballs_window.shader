// Portaballs window test (rendering experiment E1).
//
// A plain sheet that shows a picture pasted by SCREEN position: the same paste the paint shader does
// inside an open portal, with nothing else in the way. Code/Game/Debug/PortalWindowTest.cs stands one
// in the middle of the room and feeds it the scene drawn again from the same eye, cut off at the sheet.
// If everything is right the sheet is invisible: it looks like an empty frame.
//
//   WindowTint    multiplies the picture, so the sheet can be told apart on purpose (1,1,1 = invisible)
//   WindowFrame   width of the yellow frame as a share of the sheet (0 = no frame)
//   No picture for this view (for example the sheet seen inside its own picture): plain magenta.

HEADER
{
	Description = "Portaballs window test";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
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
	#define S_TRANSLUCENT 1
	#include "common/pixel.hlsl"

	bool HasPortalOrange < Attribute( "HasPortalOrange" ); Default( 0 ); >;
	Texture2D PortalTexOrange < Attribute( "PortalTexOrange" ); SrgbRead( false ); >;
	float4 PortalRectOrange < Attribute( "PortalRectOrange" ); Default4( 0, 0, 1, 1 ); >;
	float3 WindowTint < Attribute( "WindowTint" ); Default3( 1, 1, 1 ); >;
	float WindowFrame < Attribute( "WindowFrame" ); Default( 0 ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 uv = i.vTextureCoords.xy;
		float edge = min( min( uv.x, 1.0 - uv.x ), min( uv.y, 1.0 - uv.y ) );
		if ( edge < WindowFrame )
		{
			return float4( 1.0, 0.8, 0.05, 1.0 );
		}
		if ( !HasPortalOrange )
		{
			return float4( 1.0, 0.0, 1.0, 1.0 );
		}
		float2 screenUv = CalculateViewportUv( i.vPositionSs.xy );
		float3 c = PortalTexOrange.SampleLevel( g_sBilinearClamp, PortalRectOrange.xy + screenUv * PortalRectOrange.zw, 0 ).rgb;
		return float4( c * WindowTint, 1.0 );
	}
}
