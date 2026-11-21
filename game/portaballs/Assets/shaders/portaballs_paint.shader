// Portaballs wall paint.
//
// Drawn on a flat panel a few millimetres in front of a wall (built by Code/Game/Room/PaintPanel.cs).
// The panel's UVs are u to the right, v UP, with (0,0) at the wall's lower-left corner, which matches
// the paint grid of core PaintSurface: texel row 0 is the bottom of the wall. No flip anywhere.
//
// PaintMask is DATA, not colour (core PaintSurface.CopyDirtyToRgba):
//   R = orange field, G = blue field (the paint edge is where a field crosses 0.5),
//   B = wetness (1 just painted .. 0 dry), A = paint film thickness (1 = 4 mm).
//
// What it does per pixel:
//   1. One cheap bilinear look. No paint near here: nothing is drawn.
//   2. Reads the 4 x 4 texels around the pixel once and rebuilds the fields with cubic curves instead
//      of straight lines, so the outline is a smooth curve close up although a texel is 1.6 cm.
//   3. Cuts the edge at 0.5 over exactly one screen pixel (anti-aliased, never blurry).
//   4. Builds a height from the field near the edge (a rounded rim) plus the thickness channel, and
//      tilts the surface normal by its slope, so light and highlights show a raised layer of paint.
//   5. Wet paint is smoother (sharper highlights) than dry paint.
//   6. Neon (the "neon" dials below): the paint is self-lit, brighter along its borderline, and its light
//      spills a few centimetres onto the wall beside it (the halo). An open portal's rim is brighter still,
//      with a hot line round the opening. PaintNeon 0 gives the old matte paint with its contact shadow.
//      Wider light on the room comes from Code/Game/Room/PaintGlowLights.cs, not from this shader.
//   7. Optional "ghost": a second mask holding paint that was just replaced, fading out.
//   8. An open portal (a linked patch): inside the patch, a rim's width in from the paint's edge, the
//      paint gives way to a picture of the far side, pasted by screen position (PortalElsewhere). The
//      picture is drawn by Code/Game/Portals/PortalView.cs just before this panel is. Where there is
//      no picture (a portal seen through a portal, or view mode C) it is a dark surface instead.
//
// The same shader also draws the "guard sheet" (GuardMode): a sheet fixed just in front of the camera
// that takes over an opening while the eye is nearly touching it, when the camera's near cut would
// otherwise slice through the wall panel and show the wall or the void for a few frames.
// Panel and sheet both decide from the position of the eye of the view BEING DRAWN (in a headset each
// eye is its own view): nearer the wall than GuardDistance, or past it, the opening is the sheet's.
//
// A picture of the far side is either pasted by screen position (it was drawn for this view, this
// frame) or looked up in world space (PortalMode 2: it was KEPT from an earlier frame, or this is an
// opening seen through an opening): the line of sight is carried through the crossing and followed to
// where it leaves the room's box, and that point is found in the kept picture with its own camera.

HEADER
{
	Description = "Portaballs wall paint";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	// Blended on top of the wall, so it is drawn in the forward pass only and writes no depth.
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

	// The camera's guard sheet sets D_GUARD_SHEET. It then writes, per pixel, the depth of the point of the
	// wall panel it stands in for (or the near cut's own depth where the panel is nearer than that), so the
	// ordinary depth test treats it exactly as the panel: a hand in front of the opening still hides it, and
	// where it hangs in space does not matter. It hangs well beyond the near distance (4 x), so the eye
	// pass's near cut can never remove it, whatever near distance the headset's eye projection really has.
	DynamicCombo( D_GUARD_SHEET, 0..1, Sys( ALL ) );

	#include "common/pixel.hlsl"

	// ---- set per wall from C# with renderer.Attributes.Set( name, value ) ----
	Texture2D PaintMask < Attribute( "PaintMask" ); SrgbRead( false ); >;
	Texture2D GhostMask < Attribute( "GhostMask" ); SrgbRead( false ); >;
	// xy = mask size in texels, zw = texels per metre along u and v.
	float4 PaintMaskSize < Attribute( "PaintMaskSize" ); Default4( 256, 256, 64, 64 ); >;
	// The wall's own axes in world space: u (to the viewer's right), v (up), normal (into the room).
	float3 WallU < Attribute( "WallU" ); Default3( 0, -1, 0 ); >;
	float3 WallV < Attribute( "WallV" ); Default3( 0, 0, 1 ); >;
	float3 WallN < Attribute( "WallN" ); Default3( -1, 0, 0 ); >;
	// Paint colours, linear.
	float3 OrangeColor < Attribute( "OrangeColor" ); Default3( 1.0, 0.17, 0.004 ); >;
	float3 BlueColor < Attribute( "BlueColor" ); Default3( 0.01, 0.17, 1.0 ); >;
	// Ghost of a replaced patch: 0 = none, 1 = fully shown. GhostIsBlue picks its channel.
	float GhostAlpha < Attribute( "GhostAlpha" ); Default( 0 ); >;
	float GhostIsBlue < Attribute( "GhostIsBlue" ); Default( 0 ); >;

	// ---- look ----
	// How tall the paint is drawn, as metres of bulge per unit of height. Bigger = rounder rim.
	float BumpMetres < Attribute( "PaintBump" ); Default( 0.012 ); >;
	float DryRoughness < Attribute( "PaintDryRoughness" ); Default( 0.42 ); >;
	float WetRoughness < Attribute( "PaintWetRoughness" ); Default( 0.15 ); >;
	// Strength of the darkening on the wall right beside the paint.
	float ContactShadow < Attribute( "PaintContactShadow" ); Default( 0.30 ); >;
	// 1 = cubic outline (normal). 0 = plain bilinear, for comparing.
	float CubicEdge < Attribute( "PaintCubicEdge" ); Default( 1 ); >;

	// ---- neon (2026-11-19, the tester: "a neon ambient orange and blue instead of the current matte finish") ----
	// The paint is SELF-LIT: it gives off its own colour wherever the room's lamp is. Every "glow" number is a
	// multiple of the paint colour. The screen cuts each colour channel off at 1, so up to about 1.5 the colour
	// only gets brighter, and above that it heats up (orange toward amber and yellow, blue toward cyan), which
	// is what the hot middle of a neon tube looks like.
	// The master dial: 0 = the old matte paint exactly, 1 = neon as tuned, in between and above also work.
	float PaintNeon < Attribute( "PaintNeon" ); Default( 1 ); >;
	// The body of a splat.
	float PaintGlow < Attribute( "PaintGlow" ); Default( 1.0 ); >;
	// Added along the borderline of the paint (brightest at the outline, gone 2 to 3 cm in).
	float PaintEdgeGlow < Attribute( "PaintEdgeGlow" ); Default( 1.2 ); >;
	// How much of the ordinary lamp-lit colour is kept under the glow (1 = all of it, as matte paint has).
	// 0 = none: the paint looks the same wherever the lamps are, and the room's lights are not worked out
	// for it at all (the cheapest). Anything above 0 brings the lamps' shading and highlights back.
	float PaintLit < Attribute( "PaintLit" ); Default( 0 ); >;
	// The paint's form in its own light: slopes facing up and to the left glow more, the others less (and
	// thick paint a little more than thin). 0 = flat colour, like a sticker.
	float PaintForm < Attribute( "PaintForm" ); Default( 1.4 ); >;
	// The halo: light spilling onto the bare wall beside the paint. Strength at the outline (0 to 1; it fades
	// to nothing 2 to 3 cm out, where the paint data ends) and its colour as a multiple of the paint colour.
	float PaintHalo < Attribute( "PaintHalo" ); Default( 0.7 ); >;
	float PaintHaloGlow < Attribute( "PaintHaloGlow" ); Default( 1.6 ); >;
	// All of the above is multiplied by this on a patch that is an OPEN portal, so its rim is the brightest
	// thing in the room.
	float PortalOpenBoost < Attribute( "PortalOpenBoost" ); Default( 1.25 ); >;

	// ---- open portals ----
	// Per wall, set by Code/Game/Room/PortalLink.cs: 1 while that colour's patch ON THIS WALL is one end
	// of a linked portal, else 0. With both at 0 this shader draws exactly what it drew before portals.
	float PortalOpenOrange < Attribute( "PortalOpenOrange" ); Default( 0 ); >;
	float PortalOpenBlue < Attribute( "PortalOpenBlue" ); Default( 0 ); >;
	// Width of the band of paint left around the edge of an open patch, metres. 0.04 since 2026-11-19 (was 0.08):
	// The tester asked to see more of the portal inside its splat. Only the picture grows; where the player may pass is
	// the core's own rule (PortalCrossing.Passage), which this does not change.
	float PortalRimMetres < Attribute( "PortalRimMetres" ); Default( 0.04 ); >;
	// The dark surface shown when there is no picture of the far side (a portal seen through a portal,
	// or view mode C): its colour (linear), and how much of the paint colour glows into it at the rim.
	float3 PortalDarkColor < Attribute( "PortalDarkColor" ); Default3( 0.004, 0.005, 0.010 ); >;
	float PortalRimGlow < Attribute( "PortalRimGlow" ); Default( 0.9 ); >;
	// Neon: the same glow reaching a little way (about 3 cm) over the PICTURE of the far side: how much of the
	// picture is tinted with the paint's light right at the opening's edge (0 to 1), fading to none.
	// 0 = the picture runs clean up to the lip.
	float PortalPictureGlow < Attribute( "PortalPictureGlow" ); Default( 0.5 ); >;
	// The lip: a thin line right round the opening. Matte paint (PaintNeon 0): darker paint, PortalLipShade
	// times the paint colour. Neon: the hottest line of all, PortalLipGlow times the paint colour (far over 1,
	// so it burns out toward yellow on orange and cyan on blue) plus PortalLipHeat of plain white, which takes
	// it the rest of the way to white-hot. Its width is in "paint all around" units
	// (0.06 is about half a centimetre on the wall with the
	// 4 cm rim: these units shrink with the rim, so halving the rim doubled this to keep the lip as wide).
	float PortalLipShade < Attribute( "PortalLipShade" ); Default( 0.5 ); >;
	float PortalLipGlow < Attribute( "PortalLipGlow" ); Default( 3.5 ); >;
	float PortalLipHeat < Attribute( "PortalLipHeat" ); Default( 0.2 ); >;
	float PortalLipWidth < Attribute( "PortalLipWidth" ); Default( 0.06 ); >;
	// The same lip where the opening ends at the paint's own outline: how far below the paint's edge
	// value (0.5) the paint field may be and still be drawn as lip (0.18 is a few millimetres on the wall).
	float PortalLipField < Attribute( "PortalLipField" ); Default( 0.18 ); >;

	// ---- the see-through view (Code/Game/Portals/PortalView.cs) ----
	// Set per VIEW by the render commands that run just before this panel is drawn: the picture of the
	// far side as that view's eye would see it, and where in that picture this view's screen sits.
	// The picture is pasted by SCREEN position, so the shader never needs to know which eye it is.
	bool HasPortalOrange < Attribute( "HasPortalOrange" ); Default( 0 ); >;
	bool HasPortalBlue < Attribute( "HasPortalBlue" ); Default( 0 ); >;
	Texture2D PortalTexOrange < Attribute( "PortalTexOrange" ); SrgbRead( false ); >;
	Texture2D PortalTexBlue < Attribute( "PortalTexBlue" ); SrgbRead( false ); >;
	float4 PortalRectOrange < Attribute( "PortalRectOrange" ); Default4( 0, 0, 1, 1 ); >;
	float4 PortalRectBlue < Attribute( "PortalRectBlue" ); Default4( 0, 0, 1, 1 ); >;
	// 1 on a wall's panel while the camera's guard sheet is drawing this wall's openings instead (the panel
	// then leaves them out as real holes), and where
	// that camera is (world, inches). The sheet only exists for the player's own view: in any other view
	// (the far side of a portal, drawn from somewhere else) the panel goes on drawing its own openings.
	float PortalHolesByGuard < Attribute( "PortalHolesByGuard" ); Default( 0 ); >;
	float3 GuardEyeWs < Attribute( "GuardEyeWs" ); Default3( 0, 0, 0 ); >;
	// How near this wall (inches in front of it, or past it) the eye of the view being drawn must be for
	// the sheet to draw its openings. Set on the panel and on the sheet to the same value, so the two
	// agree for every view. The default (very large) means "PortalHolesByGuard alone decides".
	float GuardDistance < Attribute( "GuardDistance" ); Default( 1000000 ); >;
	// Debug only (0 = off; set on the panels and the sheet with renderer.Attributes.Set): 1 paints a panel's
	// openings magenta where it leaves them to the sheet, and the sheet green where it draws anything (yellow if
	// its D_GUARD_SHEET variant is not in use), so a test picture shows who draws what; 2 also tints the sheet
	// blue where it stands aside; 3 makes the sheet cyan and nearest of all (is it there at all?).
	float GuardDebug < Attribute( "GuardDebug" ); Default( 0 ); >;
	// How far in front of the panel's point the sheet takes its depth from, inches (see PaintPixel).
	float GuardDepthBias < Attribute( "GuardDepthBias" ); Default( 0.04 ); >;

	// ---- kept pictures (engine patch P4) ----
	// Per view: 1 = paste the picture by screen position, 2 = look it up in world space with the rows of
	// its camera's world-to-clip matrix (Vp0 = x, Vp1 = y, Vp3 = w), inside the part of it that was drawn
	// (Valid: uv min and max).
	float PortalModeOrange < Attribute( "PortalModeOrange" ); Default( 1 ); >;
	float PortalModeBlue < Attribute( "PortalModeBlue" ); Default( 1 ); >;
	float4 PortalVp0Orange < Attribute( "PortalVp0Orange" ); Default4( 1, 0, 0, 0 ); >;
	float4 PortalVp1Orange < Attribute( "PortalVp1Orange" ); Default4( 0, 1, 0, 0 ); >;
	float4 PortalVp3Orange < Attribute( "PortalVp3Orange" ); Default4( 0, 0, 0, 1 ); >;
	float4 PortalValidOrange < Attribute( "PortalValidOrange" ); Default4( 0, 0, 1, 1 ); >;
	float4 PortalVp0Blue < Attribute( "PortalVp0Blue" ); Default4( 1, 0, 0, 0 ); >;
	float4 PortalVp1Blue < Attribute( "PortalVp1Blue" ); Default4( 0, 1, 0, 0 ); >;
	float4 PortalVp3Blue < Attribute( "PortalVp3Blue" ); Default4( 0, 0, 0, 1 ); >;
	float4 PortalValidBlue < Attribute( "PortalValidBlue" ); Default4( 0, 0, 1, 1 ); >;
	// Per wall, from the game: each end's crossing as three rows (xyz = the turn, w = the move, inches),
	// and the room's box (inches), which stands in for the depth of what a kept picture shows.
	float4 PortalCrossOrange0 < Attribute( "PortalCrossOrange0" ); Default4( 1, 0, 0, 0 ); >;
	float4 PortalCrossOrange1 < Attribute( "PortalCrossOrange1" ); Default4( 0, 1, 0, 0 ); >;
	float4 PortalCrossOrange2 < Attribute( "PortalCrossOrange2" ); Default4( 0, 0, 1, 0 ); >;
	float4 PortalCrossBlue0 < Attribute( "PortalCrossBlue0" ); Default4( 1, 0, 0, 0 ); >;
	float4 PortalCrossBlue1 < Attribute( "PortalCrossBlue1" ); Default4( 0, 1, 0, 0 ); >;
	float4 PortalCrossBlue2 < Attribute( "PortalCrossBlue2" ); Default4( 0, 0, 1, 0 ); >;
	float3 PortalRoomMin < Attribute( "PortalRoomMin" ); Default3( -1000, -1000, -1000 ); >;
	float3 PortalRoomMax < Attribute( "PortalRoomMax" ); Default3( 1000, 1000, 1000 ); >;
	// What an opening shows when there is no picture at all for it yet (the very first frame of a portal seen
	// through a portal): rgb, and in a how much it replaces the dark surface (0 = the dark surface, as in
	// view mode C; 1 = this colour).
	float4 PortalNoPicture < Attribute( "PortalNoPicture" ); Default4( 0, 0, 0, 0 ); >;
	// How far each end's picture has faded in over the dark surface, 0..1. An end seen THROUGH the other
	// end is always the dark surface (no picture is possible there), so when the player steps through,
	// the end they now look back at starts dark, exactly as it looked a frame earlier, and opens over a
	// moment instead of popping.
	float PortalFadeOrange < Attribute( "PortalFadeOrange" ); Default( 1 ); >;
	float PortalFadeBlue < Attribute( "PortalFadeBlue" ); Default( 1 ); >;

	// ---- only open where the far end has paint too ----
	// The crossing carries each point of this patch to a point of the other wall. Where that point is
	// bare wall (the two patches are different shapes, or one hangs lower than the other), a player
	// cannot go through, and looking through would show the back of the far wall's surroundings or the
	// space under its floor. So the opening is only drawn where the far patch has paint as well.
	// PortalMatchExit 1 = do this. The maps take this wall's 0..1 position to the far wall's:
	// far = A.xy + u * A.zw + v * B.xy; B.zw = the far mask's size in texels.
	float PortalMatchExit < Attribute( "PortalMatchExit" ); Default( 0 ); >;
	Texture2D PortalExitMaskOrange < Attribute( "PortalExitMaskOrange" ); SrgbRead( false ); >;
	Texture2D PortalExitMaskBlue < Attribute( "PortalExitMaskBlue" ); SrgbRead( false ); >;
	float4 PortalExitMapOrangeA < Attribute( "PortalExitMapOrangeA" ); Default4( 0, 0, 1, 0 ); >;
	float4 PortalExitMapOrangeB < Attribute( "PortalExitMapOrangeB" ); Default4( 0, 1, 256, 256 ); >;
	float4 PortalExitMapBlueA < Attribute( "PortalExitMapBlueA" ); Default4( 0, 0, 1, 0 ); >;
	float4 PortalExitMapBlueB < Attribute( "PortalExitMapBlueB" ); Default4( 0, 1, 256, 256 ); >;

	// ---- the guard sheet ----
	// 1 (or 2 = standing by, draws nothing) when this is not a wall panel but the sheet fixed just in
	// front of the camera. The sheet works
	// out, per pixel, which point of the wall's paint panel that pixel looks at, and draws what the panel
	// would show there: all of it where the camera's near cut has removed the real panel, and only the
	// opening elsewhere. Used while the eye is nearly touching an open patch.
	float GuardMode < Attribute( "GuardMode" ); Default( 0 ); >;
	// The wall's lower-left corner in the world (inches), and its width, height and the panel's stand-off (inches).
	float3 WallOrigin < Attribute( "WallOrigin" ); Default3( 0, 0, 0 ); >;
	float3 WallSize < Attribute( "WallSize" ); Default3( 100, 100, 0.118 ); >;

	// How much of the wall AROUND this pixel is covered by one colour's paint, 0 to 1. 'channel' picks
	// the field: 0 = orange (R), 1 = blue (G). It is the field averaged over two rings around the pixel.
	// Deep inside a patch it is 1; near the edge part of each ring falls off the paint and it drops,
	// which gives an outline that follows any shape, drips included, a rim's width in from the paint's
	// own edge. Thin drips and stray droplets are narrower than the rings, so they score low and stay
	// plain paint.
	float PortalAround( float2 uv, float channel )
	{
		float2 texel = 1.0 / PaintMaskSize.xy;
		float2 reach = PortalRimMetres * PaintMaskSize.zw * texel;   // rim width as a step in uv
		float sum = 0.0;
		for ( int k = 0; k < 8; k++ )
		{
			float a = 0.7853982 * k;
			float2 dir = float2( cos( a ), sin( a ) );
			float4 outer = PaintMask.SampleLevel( g_sBilinearClamp, uv + dir * reach, 0 );
			float4 inner = PaintMask.SampleLevel( g_sBilinearClamp, uv + dir * reach * 0.5, 0 );
			// Anything the paint counts as covered (field 0.5 and up) counts fully, so thin places
			// inside the patch do not show up as streaks.
			sum += saturate( ( lerp( outer.r, outer.g, channel ) - 0.2 ) * 3.34 );
			sum += saturate( ( lerp( inner.r, inner.g, channel ) - 0.2 ) * 3.34 );
		}
		return sum / 16.0;
	}

	// Is this pixel inside the opening of an open patch: 0 = no (paint, or not in the patch), 1 = yes.
	// The edge is cut over exactly one screen pixel, like the paint's own outline, so it is crisp from
	// any distance.
	//   around   PortalAround for this pixel
	//   width    how much 'around' changes across one screen pixel
	//   cover    how much this pixel itself is covered by that colour's paint, 0..1
	// A pixel with no paint of its own only counts when nearly everything around it is paint: that
	// closes over a pinhole the paint left in the middle of a patch, without opening the bare wall
	// in the gaps between the strands at the patch's edge.
	float PortalHole( float around, float width, float cover )
	{
		float open = saturate( ( around - 0.765 ) / width + 0.5 );
		float closed = saturate( ( around - 0.875 ) / width + 0.5 );
		return open * max( cover, closed );
	}

	// Where a line of sight ends up after going through an opening: carried through the crossing (rows
	// c0..c2), then followed to where it leaves the room's box. (Core: PortalPictureMath.Lookup.)
	float3 PortalRoomPoint( float3 eyeWs, float3 rayWs, float4 c0, float4 c1, float4 c2 )
	{
		float3 o = float3( dot( c0.xyz, eyeWs ) + c0.w, dot( c1.xyz, eyeWs ) + c1.w, dot( c2.xyz, eyeWs ) + c2.w );
		float3 d = float3( dot( c0.xyz, rayWs ), dot( c1.xyz, rayWs ), dot( c2.xyz, rayWs ) );
		d = normalize( d );
		float3 safe = float3( abs( d.x ) < 1e-6 ? 1e-6 : d.x, abs( d.y ) < 1e-6 ? 1e-6 : d.y, abs( d.z ) < 1e-6 ? 1e-6 : d.z );
		float3 tFar = max( ( PortalRoomMin - o ) / safe, ( PortalRoomMax - o ) / safe );
		float t = max( min( tFar.x, min( tFar.y, tFar.z ) ), 0.0 );
		return o + d * t;
	}

	// Read a kept picture at a point of the room, with the picture's own camera. inside = 1 when the point
	// is in the part of the picture that was drawn; otherwise the nearest drawn edge is read.
	float3 PortalKept( Texture2D tex, float4 vp0, float4 vp1, float4 vp3, float4 valid, float3 pointWs, out float inside )
	{
		float4 p = float4( pointWs, 1.0 );
		float w = dot( vp3, p );
		float2 uv = 0.5 + float2( 0.5, -0.5 ) * float2( dot( vp0, p ), dot( vp1, p ) ) / max( w, 1e-4 );
		inside = ( w > 1e-4 && uv.x >= valid.x && uv.x <= valid.z && uv.y >= valid.y && uv.y <= valid.w ) ? 1.0 : 0.0;
		uv = clamp( uv, valid.xy, valid.zw );
		return tex.SampleLevel( g_sBilinearClamp, uv, 0 ).rgb;
	}

	// THE ONE PLACE the inside of an open portal is drawn: the picture of the far side. Returns false when
	// there is no picture for this view (then 'colour' is the dark surface).
	//   rayWs     this pixel's line of sight, from the eye of the view being drawn (any length)
	//   isBlue    0 = this is the orange end, 1 = the blue end
	bool PortalElsewhere( PixelInput i, float3 rayWs, float isBlue, out float3 colour )
	{
		float2 screenUv = CalculateViewportUv( i.vPositionSs.xy );
		colour = PortalDarkColor;
		if ( isBlue > 0.5 )
		{
			if ( !HasPortalBlue ) return false;
			if ( PortalModeBlue < 1.5 )
			{
				// Drawn for this view, this frame: paste it by where this pixel is on the screen.
				colour = PortalTexBlue.SampleLevel( g_sBilinearClamp, PortalRectBlue.xy + screenUv * PortalRectBlue.zw, 0 ).rgb;
				return true;
			}
			float3 roomPoint = PortalRoomPoint( g_vCameraPositionWs.xyz, rayWs, PortalCrossBlue0, PortalCrossBlue1, PortalCrossBlue2 );
			float inside;
			colour = PortalKept( PortalTexBlue, PortalVp0Blue, PortalVp1Blue, PortalVp3Blue, PortalValidBlue, roomPoint, inside );
			if ( inside < 0.5 && HasPortalOrange )
			{
				// Not in this end's kept picture. The other end's is a picture of the same room from somewhere else.
				float insideOther;
				float3 other = PortalKept( PortalTexOrange, PortalVp0Orange, PortalVp1Orange, PortalVp3Orange, PortalValidOrange, roomPoint, insideOther );
				if ( insideOther > 0.5 ) colour = other;
			}
			return true;
		}
		if ( !HasPortalOrange ) return false;
		if ( PortalModeOrange < 1.5 )
		{
			colour = PortalTexOrange.SampleLevel( g_sBilinearClamp, PortalRectOrange.xy + screenUv * PortalRectOrange.zw, 0 ).rgb;
			return true;
		}
		float3 roomPointO = PortalRoomPoint( g_vCameraPositionWs.xyz, rayWs, PortalCrossOrange0, PortalCrossOrange1, PortalCrossOrange2 );
		float insideO;
		colour = PortalKept( PortalTexOrange, PortalVp0Orange, PortalVp1Orange, PortalVp3Orange, PortalValidOrange, roomPointO, insideO );
		if ( insideO < 0.5 && HasPortalBlue )
		{
			float insideOtherO;
			float3 otherO = PortalKept( PortalTexBlue, PortalVp0Blue, PortalVp1Blue, PortalVp3Blue, PortalValidBlue, roomPointO, insideOtherO );
			if ( insideOtherO > 0.5 ) colour = otherO;
		}
		return true;
	}

	// Catmull-Rom: passes exactly through the texel values, so thin drips keep their width.
	void CatmullRom( float t, out float4 w, out float4 dw )
	{
		float t2 = t * t, t3 = t2 * t;
		w = float4( -0.5 * t3 + t2 - 0.5 * t, 1.5 * t3 - 2.5 * t2 + 1.0, -1.5 * t3 + 2.0 * t2 + 0.5 * t, 0.5 * t3 - 0.5 * t2 );
		dw = float4( -1.5 * t2 + 2.0 * t - 0.5, 4.5 * t2 - 5.0 * t, -4.5 * t2 + 4.0 * t + 0.5, 1.5 * t2 - t );
	}

	// One colour's paint field on ANOTHER wall's mask, rebuilt with the same curve as this wall's outline
	// (so the edge it makes is as smooth). channel: 0 = orange, 1 = blue. Off the wall there is no paint.
	float FarField( Texture2D mask, float2 size, float2 uv, float channel )
	{
		if ( uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 ) return 0.0;
		float2 q = uv * size - 0.5;
		float2 iq = floor( q );
		float2 fq = q - iq;
		float4 wx, dwx, wy, dwy;
		CatmullRom( fq.x, wx, dwx );
		CatmullRom( fq.y, wy, dwy );
		int2 first = int2( iq ) - 1;
		int2 last = int2( size ) - 1;
		float value = 0.0;
		for ( int row = 0; row < 4; row++ )
		{
			int y = clamp( first.y + row, 0, last.y );
			float rowValue = 0.0;
			for ( int col = 0; col < 4; col++ )
			{
				int x = clamp( first.x + col, 0, last.x );
				float4 t = mask.Load( int3( x, y, 0 ) );
				rowValue += lerp( t.r, t.g, channel ) * wx[col];
			}
			value += rowValue * wy[row];
		}
		return value;
	}

	// Cubic B-spline: smoother still (no kinks in the slope), used for the height so highlights have no grid in them.
	void BSpline( float t, out float4 w, out float4 dw )
	{
		float t2 = t * t, t3 = t2 * t;
		float s = 1.0 - t;
		w = float4( s * s * s, 3.0 * t3 - 6.0 * t2 + 4.0, -3.0 * t3 + 3.0 * t2 + 3.0 * t + 1.0, t3 ) / 6.0;
		dw = float4( -0.5 * s * s, 1.5 * t2 - 2.0 * t, -1.5 * t2 + t + 0.5, 0.5 * t2 );
	}

	// Bilinear weights written the same way, for the comparison switch.
	void Linear( float t, out float4 w, out float4 dw )
	{
		w = float4( 0.0, 1.0 - t, t, 0.0 );
		dw = float4( 0.0, -1.0, 1.0, 0.0 );
	}

	// The whole pixel. depthOut: the depth this pixel is tested with (the guard sheet's own, see D_GUARD_SHEET).
	float4 PaintPixel( PixelInput i, out float depthOut )
	{
		float2 uv = i.vTextureCoords.xy;
		depthOut = i.vPositionSs.z;

		// The guard sheet: this pixel's place on the wall comes from where its line of sight meets the
		// paint panel, not from the mesh. guardShow = there is something to work out for this pixel;
		// guardCut = the camera's near cut has removed the real panel here, so the sheet draws all of it.
		float guardShow = 1.0;
		float guardCut = 0.0;
		// This pixel's line of sight, and how far in front of the wall the eye of THIS view is (minus = past it).
		float3 rayWs = ( i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz ) - g_vCameraPositionWs.xyz;
		float eyeOutOfWall = dot( g_vCameraPositionWs.xyz - WallOrigin, WallN );
		// Every depth the sheet writes is in the view's own depth range (g_flViewportMinZ = far, g_flViewportMaxZ =
		// near: the buffer is reversed), never plain 0..1: see where the sheet's depth is worked out below. A sheet
		// pixel with nothing to draw takes the far end, so it can never win the depth test against anything.
		if ( GuardMode > 1.5 )
		{
			// The sheet is standing by (the eye is near an opening but not yet nearly touching it): nothing to draw.
			depthOut = g_flViewportMinZ;
			return float4( 0, 0, 0, 0 );
		}
		if ( GuardMode > 0.5 && eyeOutOfWall >= GuardDistance )
		{
			// The sheet, in a view whose eye is still comfortably in front of the wall: the panel draws the opening.
			depthOut = g_flViewportMinZ;
			if ( GuardDebug > 1.5 ) return float4( 0, 0, 1, 0.25 );
			return float4( 0, 0, 0, 0 );
		}
		if ( GuardMode < 0.5 && dot( g_vCameraPositionWs.xyz - WallOrigin, WallN ) < WallSize.z )
		{
			// A wall panel seen from behind (an eye between the panel and its wall, or past the wall, in
			// the moment of going through): the paint is on the room side only.
			return float4( 0, 0, 0, 0 );
		}
		if ( GuardMode > 0.5 )
		{
			float3 eyeWs = g_vCameraPositionWs.xyz;
			float3 ray = ( i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz ) - eyeWs;   // eye to this pixel of the sheet
			float eyeOut = dot( eyeWs - WallOrigin, WallN );          // this eye's distance in front of the wall
			float3 hit = eyeWs - WallN * eyeOut;                      // straight down onto the wall
			if ( eyeOut > 0.0 )
			{
				// A plane that is always in front of the eye: the panel's own, or half way to the wall when closer than that.
				float plane = min( WallSize.z, eyeOut * 0.5 );
				float toward = -dot( ray, WallN );                     // how fast the line of sight closes on the wall
				float t = ( eyeOut - plane ) / max( toward, 1e-6 );    // 1 = the plane is exactly at the sheet
				guardShow = toward > 1e-6 ? 1.0 : 0.0;
				t = min( t, 4000.0 );
				hit = eyeWs + ray * t;
				// Is that point of the panel nearer than this view's near cut (so the real panel is cut away
				// there)? Asked of the view's own projection, the one the rasteriser clips with: the depth
				// buffer is reversed (near = 1), so "nearer than the near cut" is a depth above 1.
				float4 hitPs = Position3WsToPs( hit );
				float hitDepth = hitPs.w > 1e-6 ? hitPs.z / hitPs.w : 2.0;
				guardCut = hitDepth > 0.99999 ? 1.0 : 0.0;
				// The depth the sheet is tested with: that of a point a hair (GuardDepthBias, 1 mm) in front of the
				// panel's point, so it never ties with the wall behind it and still loses to anything really in front
				// of the opening, such as a hand. WRITTEN IN THE VIEW'S OWN DEPTH RANGE: the engine keeps each view's
				// depth between g_flViewportMinZ and g_flViewportMaxZ (common/classes/Depth.hlsl reads it back that
				// way), not 0..1. Written as plain 0..1 it landed in the wrong place, and the sheet lost the test to
				// the wall slab over a band of the opening that depended on the viewing angle: the band was drawn by
				// nobody and the bare slab showed, the tester's "flash of wall in gray" (2026-11-19; found with GuardDebug and
				// engine\seethrough_eye.ps1).
				float4 frontPs = Position3WsToPs( hit + WallN * GuardDepthBias );
				float frontDepth = frontPs.w > 1e-6 ? frontPs.z / frontPs.w : 2.0;
				depthOut = lerp( g_flViewportMinZ, g_flViewportMaxZ, saturate( max( frontDepth, hitDepth ) ) );
			}
			else
			{
				// The eye is already past the wall's plane (one headset eye can be, a moment before or after
				// the head is carried through): it is standing IN the opening. Looking on into the wall it
				// sees the far side; looking back out, the room it is leaving, which the ordinary view
				// already shows it (a wall is not drawn from behind).
				guardCut = 1.0;
				guardShow = dot( ray, WallN ) < 0.0 ? 1.0 : 0.0;
				// Onward is the far side: nothing of this room can be in front of it.
				depthOut = g_flViewportMaxZ;
			}
			float3 rel = hit - WallOrigin;
			uv = float2( dot( rel, WallU ), dot( rel, WallV ) ) / WallSize.xy;
			if ( uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 ) guardShow = 0.0;
			uv = saturate( uv );
		}

		float2 size = PaintMaskSize.xy;
		float2 st = uv * size;                 // position in texels
		float2 stDx = ddx( st );               // texels crossed per screen pixel, sideways and down
		float2 stDy = ddy( st );
		float footprint = 0.5 * ( length( stDx ) + length( stDy ) );

		// 1. Cheap look: is there any paint near this pixel at all?
		float4 near = PaintMask.SampleLevel( g_sBilinearClamp, uv, 0 );
		float nearField = max( near.r, near.g );

		float ghostCover = 0.0;
		if ( GhostAlpha > 0.001 )
		{
			float4 g = GhostMask.SampleLevel( g_sBilinearClamp, uv, 0 );
			float gf = GhostIsBlue > 0.5 ? g.g : g.r;
			float gw = max( 0.02, 0.33 * footprint );
			ghostCover = smoothstep( 0.5 - gw, 0.5 + gw, gf ) * GhostAlpha;
		}

		if ( nearField < 0.004 && ghostCover < 0.004 )
		{
			return float4( 0, 0, 0, 0 );
		}

		// 2. The 4 x 4 texels around the pixel, read once.
		float2 p = st - 0.5;
		float2 ip = floor( p );
		float2 f = p - ip;
		int2 first = int2( ip ) - 1;
		int2 last = int2( size ) - 1;

		float4 ex, edx, ey, edy;               // weights for the outline
		if ( CubicEdge > 0.5 )
		{
			CatmullRom( f.x, ex, edx );
			CatmullRom( f.y, ey, edy );
		}
		else
		{
			Linear( f.x, ex, edx );
			Linear( f.y, ey, edy );
		}
		float4 hx, hdx, hy, hdy;               // weights for the height
		BSpline( f.x, hx, hdx );
		BSpline( f.y, hy, hdy );

		float4 edge = 0, edgeDu = 0, edgeDv = 0;
		float4 soft = 0, softDu = 0, softDv = 0;
		for ( int row = 0; row < 4; row++ )
		{
			int y = clamp( first.y + row, 0, last.y );
			float4 rowEdge = 0, rowEdgeD = 0, rowSoft = 0, rowSoftD = 0;
			for ( int col = 0; col < 4; col++ )
			{
				int x = clamp( first.x + col, 0, last.x );
				float4 t = PaintMask.Load( int3( x, y, 0 ) );
				rowEdge += t * ex[col];
				rowEdgeD += t * edx[col];
				rowSoft += t * hx[col];
				rowSoftD += t * hdx[col];
			}
			edge += rowEdge * ey[row];
			edgeDu += rowEdgeD * ey[row];
			edgeDv += rowEdge * edy[row];
			soft += rowSoft * hy[row];
			softDu += rowSoftD * hy[row];
			softDv += rowSoft * hdy[row];
		}

		// 3. Outline: one screen pixel wide at any distance.
		float2 gradO = float2( edgeDu.r, edgeDv.r );      // field change per texel
		float2 gradB = float2( edgeDu.g, edgeDv.g );
		float minWidth = max( 0.003, 0.17 * footprint );   // far away, never thinner than the field's own ramp
		float widthO = max( minWidth, length( float2( dot( gradO, stDx ), dot( gradO, stDy ) ) ) );
		float widthB = max( minWidth, length( float2( dot( gradB, stDx ), dot( gradB, stDy ) ) ) );
		float coverO = saturate( ( edge.r - 0.5 ) / widthO + 0.5 );
		float coverB = saturate( ( edge.g - 0.5 ) / widthB + 0.5 );
		float cover = saturate( coverO + coverB );
		float wet = saturate( edge.b );

		// 4. Height and its slope. Orange and blue together make one layer, so no groove where they meet.
		float total = saturate( soft.r + soft.g );
		float2 totalGrad = float2( softDu.r + softDu.g, softDv.r + softDv.g );
		float s = saturate( ( total - 0.42 ) / 0.58 );
		float rim = s * s * ( 3.0 - 2.0 * s );
		float rimSlope = 6.0 * s * ( 1.0 - s ) / 0.58;
		float thick = saturate( soft.a );
		float body = 0.55 + 1.1 * thick;
		float2 rimTilt = rimSlope * totalGrad * body * PaintMaskSize.zw * BumpMetres;
		// Relief inside the paint from its thickness (drip trails, beads). Held to a gentle tilt: where a
		// lot of paint piles up (the pool at the floor) the raw slope is steep and would catch hard glints.
		float2 bodyTilt = rim * 1.1 * float2( softDu.a, softDv.a ) * PaintMaskSize.zw * BumpMetres;
		bodyTilt /= sqrt( 1.0 + dot( bodyTilt, bodyTilt ) / ( 0.2 * 0.2 ) );
		float2 slope = rimTilt + bodyTilt;
		float3 nTs = normalize( float3( -slope.x, -slope.y, 1.0 ) );

		// The ghost is flat and sits under the live paint.
		float ghostOnly = ghostCover * ( 1.0 - cover );
		float paintAlpha = cover + ghostOnly;
		float liveShare = cover / max( paintAlpha, 0.0001 );
		nTs = normalize( lerp( float3( 0, 0, 1 ), nTs, liveShare ) );

		float3 liveColor = lerp( BlueColor, OrangeColor, coverO / max( coverO + coverB, 0.0001 ) );
		float3 ghostColor = GhostIsBlue > 0.5 ? BlueColor : OrangeColor;
		float3 paint = lerp( ghostColor, liveColor, liveShare );

		// 5. Light it. Neon: with its own glow, which is what makes it neon and not matte (and, when PaintLit
		//    is above 0, a share of the scene's own lights under it). Matte (PaintNeon 0): the scene's lights only.
		float neon = max( PaintNeon, 0.0 );
		float neon01 = saturate( neon );
		// Is this pixel's colour an open portal on this wall? (Those glow more: PortalOpenBoost.)
		float orangeShare = coverO / max( coverO + coverB, 0.0001 );
		float openBoost = lerp( 1.0, PortalOpenBoost, lerp( PortalOpenBlue, PortalOpenOrange, orangeShare ) * liveShare );
		// The borderline: 1 at the paint's outline, easing to 0 where the paint is at its full height.
		float border = ( 1.0 - rim ) * liveShare;
		// Form in its own light, so it is not a flat sticker: slopes facing a fixed "up and to the left" (in the
		// wall's own axes, like the glint below) glow more and the others less, and thick paint (beads, the
		// heads of drips) glows a little more than thin.
		float relief = clamp( 1.0 + PaintForm * dot( nTs.xy, float2( -0.6, 0.8 ) ), 0.6, 1.5 ) * ( 0.9 + 0.3 * thick * liveShare );
		float glow = neon * openBoost * ( PaintGlow * relief + PaintEdgeGlow * border * border );   // the borderline is brighter all the way round

		// A small fixed glint on slopes that face up and to the left, so the rim reads as glossy from
		// anywhere in the room, whatever the lamps are doing. It does not move with the head.
		float3 glintHalf = normalize( float3( -0.21, 0.28, 0.94 ) );
		float glint = pow( saturate( dot( nTs, glintHalf ) ), lerp( 40.0, 140.0, wet ) ) * lerp( 0.10, 0.55, wet ) * liveShare;
		glint *= lerp( 1.0, 2.5, neon01 );                 // neon paint is glossy: its only highlight is this one

		// The paint's own light. This is all there is at PaintNeon 1 with PaintLit 0 (the default): the room's
		// lights are then not worked out for the paint at all, which is also the cheapest this shader gets.
		float4 lit = float4( glint.xxx + paint * glow, 1.0 );

		float litShare = lerp( 1.0, PaintLit, neon01 );
		if ( litShare > 0.002 )
		{
			float curve = 1.0 - nTs.z;                     // 0 on flat paint, up to ~0.3 on the rim
			Material m = Material::Init( i );
			m.Albedo = paint * ( 1.0 - 0.9 * curve ) * litShare;
			m.Normal = normalize( WallU * nTs.x + WallV * nTs.y + WallN * nTs.z );
			m.Roughness = lerp( DryRoughness, WetRoughness, wet * liveShare );
			m.Metalness = 0.0;
			m.AmbientOcclusion = 1.0;
			m.Opacity = 1.0;
			m.Emission = lit.rgb;
			lit = ShadingModelStandard::Shade( m );
		}

		// 6. The bare wall beside the paint. Neon: the halo, the paint's light spilling a few centimetres
		//    over the wall (as far as the paint data reaches: no extra texture reads). Matte: a contact shadow.
		float bare = 1.0 - paintAlpha;
		float fieldO = max( soft.r, 0.0 ), fieldB = max( soft.g, 0.0 );
		float spill = saturate( ( fieldO + fieldB ) * 2.0 );           // 1 at the outline, 0 where the paint data ends
		float haloOrange = fieldO / max( fieldO + fieldB, 0.0001 );
		float halo = PaintHalo * neon01 * spill * spill * bare;
		float3 haloColor = lerp( BlueColor, OrangeColor, haloOrange ) * ( PaintHaloGlow * neon * lerp( 1.0, PortalOpenBoost, lerp( PortalOpenBlue, PortalOpenOrange, haloOrange ) ) );
		float shadow = ContactShadow * ( 1.0 - neon01 ) * smoothstep( 0.05, 0.5, saturate( edge.r + edge.g ) ) * bare;

		float alpha = paintAlpha + halo + shadow;
		float3 rgb = ( lit.rgb * paintAlpha + haloColor * halo ) / max( alpha, 0.0001 );

		// 8. An open portal: inside a linked patch the paint gives way to the picture of the far side,
		//    with a clean rim of the paint itself left around the edge. Nothing here runs on unlinked walls.
		float hole = 0.0;
		float3 inside = float3( 0, 0, 0 );
		bool holesByGuard = PortalHolesByGuard > 0.5 && distance( g_vCameraPositionWs.xyz, GuardEyeWs ) < 12.0 && eyeOutOfWall < GuardDistance;
		if ( PortalOpenOrange + PortalOpenBlue > 0.5 )
		{
			float aroundO = PortalOpenOrange > 0.5 ? PortalAround( uv, 0.0 ) : 0.0;
			float aroundB = PortalOpenBlue > 0.5 ? PortalAround( uv, 1.0 ) : 0.0;
			// Is there paint at the place on the far wall that this pixel is carried to? (and a little wider, for the lip)
			float farO = 1.0, farWideO = 1.0, farB = 1.0, farWideB = 1.0;
			if ( PortalMatchExit > 0.5 )
			{
				if ( PortalOpenOrange > 0.5 )
				{
					float2 farUv = PortalExitMapOrangeA.xy + uv.x * PortalExitMapOrangeA.zw + uv.y * PortalExitMapOrangeB.xy;
					float field = FarField( PortalExitMaskOrange, PortalExitMapOrangeB.zw, farUv, 1.0 );   // orange leads to blue
					float w = max( fwidth( field ), 0.003 );
					farO = saturate( ( field - 0.5 ) / w + 0.5 );
					farWideO = saturate( ( field - 0.5 + PortalLipField ) / w + 0.5 );
				}
				if ( PortalOpenBlue > 0.5 )
				{
					float2 farUv = PortalExitMapBlueA.xy + uv.x * PortalExitMapBlueA.zw + uv.y * PortalExitMapBlueB.xy;
					float field = FarField( PortalExitMaskBlue, PortalExitMapBlueB.zw, farUv, 0.0 );       // blue leads to orange
					float w = max( fwidth( field ), 0.003 );
					farB = saturate( ( field - 0.5 ) / w + 0.5 );
					farWideB = saturate( ( field - 0.5 + PortalLipField ) / w + 0.5 );
				}
			}

			float holeO = PortalHole( aroundO, max( fwidth( aroundO ), 0.0004 ), coverO ) * farO;
			float holeB = PortalHole( aroundB, max( fwidth( aroundB ), 0.0004 ), coverB ) * farB;
			hole = saturate( max( holeO, holeB ) );

			// The lip: a thin line of darker paint right round the opening, cut as crisply as the opening
			// itself: wherever the opening would be if it were a little bigger, and is not. On the paint it
			// reads as the edge of a layer. Where the paint's own outline is ragged (gaps between the strands
			// of a splat) it is what keeps the opening from bordering bare wall: the rim goes all the way round.
			// "A little bigger" both ways the opening can end: further out through the paint, and (in a gap,
			// where the opening ends at the paint's own outline) a few millimetres out over the bare wall.
			float coverWideO = saturate( ( edge.r - 0.5 + PortalLipField ) / widthO + 0.5 );
			float coverWideB = saturate( ( edge.g - 0.5 + PortalLipField ) / widthB + 0.5 );
			float widerO = PortalHole( aroundO + PortalLipWidth, max( fwidth( aroundO ), 0.0004 ), coverWideO ) * farWideO;
			float widerB = PortalHole( aroundB + PortalLipWidth, max( fwidth( aroundB ), 0.0004 ), coverWideB ) * farWideB;
			float lip = saturate( max( widerO, widerB ) ) * ( 1.0 - hole );
			float3 lipColor = ( widerB > widerO ? BlueColor : OrangeColor ) * lerp( PortalLipShade, PortalLipGlow * neon, neon01 ) + PortalLipHeat * neon01;   // neon: the hottest line
			float under = alpha * ( 1.0 - lip );
			rgb = ( rgb * under + lipColor * lip ) / max( under + lip, 0.0001 );
			alpha = under + lip;

			if ( holesByGuard && GuardDebug > 0.5 && hole > 0.5 ) return float4( 1, 0, 1, 1 );
			if ( holesByGuard )
			{
				// The camera's guard sheet is drawing this wall's openings for this view: leave them out
				// here, as real holes. (Not "draw paint there": the engine may draw the panel and the
				// sheet in either order, and whichever comes second must not cover the other.)
				alpha *= 1.0 - hole;
				hole = 0.0;
			}
			else if ( hole > 0.0005 )
			{
				float isBlue = holeB > holeO ? 1.0 : 0.0;
				// The dark surface, with the paint colour glowing a little way into it.
				float3 rimColor = isBlue > 0.5 ? BlueColor : OrangeColor;
				float around = isBlue > 0.5 ? aroundB : aroundO;
				float deep = smoothstep( 0.765, 0.93, around );
				float3 dark = PortalDarkColor + rimColor * PortalRimGlow * ( 1.0 - deep ) * ( 1.0 - deep );
				// No picture at all yet for an opening seen through an opening: something plausible, never black.
				dark = lerp( dark, PortalNoPicture.rgb, saturate( PortalNoPicture.a ) );
				// The picture of the far side, where this view has one.
				float3 picture;
				bool hasPicture = PortalElsewhere( i, rayWs, isBlue, picture );
				float fade = hasPicture ? saturate( isBlue > 0.5 ? PortalFadeBlue : PortalFadeOrange ) : 0.0;
				inside = lerp( dark, picture, fade );
				// Neon (the paint look's one hook in here): the rim's glow reaches a little way over the picture too.
				inside = lerp( inside, rimColor * 1.6, saturate( PortalPictureGlow * neon * fade ) * ( 1.0 - deep ) * ( 1.0 - deep ) );
				rgb = lerp( rgb, inside, hole );
				alpha = max( alpha, hole );
			}
		}

		if ( GuardMode > 0.5 )
		{
			// Where the real panel has been cut away: everything it would have shown. Elsewhere: only the opening.
			float4 sheet = guardCut > 0.5 ? float4( rgb, alpha * guardShow ) : float4( inside, hole * guardShow );
			// Nothing to draw here: no depth either (see the top of PaintPixel).
			if ( sheet.a < 0.002 ) depthOut = g_flViewportMinZ;
			#if ( D_GUARD_SHEET )
				if ( GuardDebug > 2.5 ) { depthOut = g_flViewportMaxZ; return float4( 0, 1, 1, 1 ); }
				if ( GuardDebug > 0.5 && sheet.a >= 0.002 ) return float4( 0, 1, 0, 1 );
			#else
				if ( GuardDebug > 0.5 && sheet.a >= 0.002 ) return float4( 1, 1, 0, 1 );
			#endif
			return sheet;
		}

		return float4( rgb, alpha );
	}

	#if ( D_GUARD_SHEET )
		struct GuardOutput
		{
			float4 vColor : SV_Target0;
			float flDepth : SV_Depth;
		};

		GuardOutput MainPs( PixelInput i )
		{
			GuardOutput o;
			o.vColor = PaintPixel( i, o.flDepth );
			return o;
		}
	#else
		float4 MainPs( PixelInput i ) : SV_Target0
		{
			float depthUnused;
			return PaintPixel( i, depthUnused );
		}
	#endif
}
