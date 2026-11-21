using System;
using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// The light the paint throws on the room: the "ambient" half of the neon look (the tester, 2026-11-19: "an ambient
/// blue and orange, not a solid matte finish"). One small coloured lamp hangs just in front of the orange
/// patch and one in front of the blue patch, with no shadows, so the wall around a patch, the floor under
/// it, balls flying past and the player's hands pick up orange or blue. An open portal's lamp is brighter
/// than a plain splat's. One per room, added by <see cref="TestRoom"/>; it only reads
/// <c>room.Patches</c> (<see cref="PortalPatches"/>) and <c>room.Portals.HasPair</c>.
///
/// The paint's own glow, its bright borderline and the few centimetres of halo right beside it are the
/// shader's (<c>shaders/portaballs_paint.shader</c>, the "neon" dials); balls are <see cref="PaintLook"/>.
///
/// Nothing here assumes a vertical wall: the lamp stands off the patch along the surface's own normal, so
/// a patch on a floor or ceiling is lit the same way.
///
/// Cost: a lamp with no shadows is one more entry in the engine's light list for the pixels within its
/// reach. Measured numbers are in engine\smoke\look\cost_ab.txt. <see cref="LightsOn"/> = false
/// switches them all off.
/// </summary>
public sealed class PaintGlowLights : Component
{
	public TestRoom Room { get; set; }

	/// <summary>The master switch.</summary>
	[Property] public bool LightsOn { get; set; } = true;

	/// <summary>How bright the lamp of an OPEN portal's end is, as a multiple of the paint colour.</summary>
	[Property] public float OpenBrightness { get; set; } = 0.7f;

	/// <summary>The same for a plain splat (a patch that is not an end of a linked pair).</summary>
	[Property] public float PlainBrightness { get; set; } = 0.4f;

	/// <summary>How far the light reaches, metres. Smaller is cheaper and tighter.</summary>
	[Property] public float ReachMetres { get; set; } = 2.5f;

	/// <summary>How far in front of the paint the lamp hangs, metres. Closer = a tighter, hotter pool on the wall.</summary>
	[Property] public float StandOffMetres { get; set; } = 0.45f;

	/// <summary>Seconds a lamp takes to come up or die down, so a patch appearing or being replaced never pops.</summary>
	[Property] public float FadeSeconds { get; set; } = 0.25f;

	/// <summary>How many lamps are lit right now (0 to 4; 2 with both patches on the walls).</summary>
	public int LampsLit { get; private set; }

	// A patch whose middle moves further than this in one go has been replaced: its light is handed to the
	// colour's second lamp (the old place dies down while the new one comes up). Less is paint running.
	private const float HandOverMetres = 0.5f;

	// Two lamps per colour: [colour * 2 + 0 or 1].
	private readonly PointLight[] _lamp = new PointLight[4];
	private readonly float[] _level = new float[4];      // 0 = out, 1 = fully up
	private readonly Vec3[] _at = new Vec3[4];           // metres
	private readonly int[] _front = new int[2];          // which of the colour's two lamps follows the patch
	private readonly float[] _bright = new float[2];     // the colour's brightness right now (eases between plain and open)

	protected override void OnStart()
	{
		for ( int k = 0; k < 4; k++ )
		{
			PaintColor colour = (PaintColor)( k / 2 );
			GameObject go = new GameObject( GameObject, false, colour == PaintColor.Orange ? "Paint Glow (orange)" : "Paint Glow (blue)" );
			PointLight light = go.AddComponent<PointLight>();
			light.Shadows = false;                       // the whole point: light only, nothing to draw twice
			light.LightColor = Color.Black;
			light.Radius = SboxConvert.LengthToInches( ReachMetres );
			_lamp[k] = light;
		}
		_bright[0] = _bright[1] = PlainBrightness;
	}

	protected override void OnUpdate()
	{
		if ( !Room.IsValid() || !Room.Patches.IsValid() ) return;
		float dt = Time.Delta;
		float step = FadeSeconds > 0.001f ? dt / FadeSeconds : 1f;
		bool linked = Room.Portals.IsValid() && Room.Portals.HasPair;
		int lit = 0;

		for ( int c = 0; c < 2; c++ )
		{
			PaintColor colour = (PaintColor)c;
			PortalPatches.Patch patch = Room.Patches.Get( colour );
			bool has = LightsOn && patch.Exists && patch.Wall.IsValid() && patch.Wall.HoldsPatch( colour );
			int live = c * 2 + _front[c];

			if ( has )
			{
				// The middle of the opening when there is one; until then, where the ball landed.
				Vec2 uv = patch.Opening.Valid ? patch.Opening.CenterUV : patch.Wall.PatchSeed( colour );
				Vec3 at = patch.Wall.Frame.ToWorld( uv, StandOffMetres );
				if ( _level[live] <= 0.01f )
				{
					_at[live] = at;
				}
				else if ( ( at - _at[live] ).Length() > HandOverMetres )
				{
					_front[c] = 1 - _front[c];
					live = c * 2 + _front[c];
					_level[live] = 0f;
					_at[live] = at;
				}
				else
				{
					// Paint running moves the middle a little: follow it smoothly.
					_at[live] = Vec3.Lerp( _at[live], at, MathF.Min( 1f, dt * 8f ) );
				}
			}

			float wantBright = linked ? OpenBrightness : PlainBrightness;
			_bright[c] += ( wantBright - _bright[c] ) * MathF.Min( 1f, step );

			for ( int n = 0; n < 2; n++ )
			{
				int k = c * 2 + n;
				float want = has && k == live ? 1f : 0f;
				_level[k] = want > _level[k] ? MathF.Min( want, _level[k] + step ) : MathF.Max( want, _level[k] - step );

				PointLight lamp = _lamp[k];
				if ( !lamp.IsValid() ) continue;
				bool on = _level[k] > 0.003f;
				if ( lamp.GameObject.Enabled != on ) lamp.GameObject.Enabled = on;
				if ( !on ) continue;

				lit++;
				// Eased, so the last of a fade is gentle.
				float shown = _level[k] * _level[k] * ( 3f - 2f * _level[k] ) * _bright[c];
				Color tint = Portaball.TintFor( colour );
				lamp.LightColor = new Color( tint.r * shown, tint.g * shown, tint.b * shown, 1f );
				lamp.Radius = SboxConvert.LengthToInches( ReachMetres );
				lamp.WorldPosition = SboxConvert.ToInches( _at[k] );
			}
		}
		LampsLit = lit;
	}
}
