namespace Portaballs;

/// <summary>What happens to a colour's existing portal patch when a new ball of that colour lands.</summary>
public enum PatchDecision
{
	/// <summary>That colour has no patch anywhere yet: this splat starts one.</summary>
	StartNew,

	/// <summary>The splat landed on or touching the colour's patch: it joins it and makes it bigger.</summary>
	AddToExisting,

	/// <summary>The splat landed somewhere else: the old patch is erased and this splat starts a new one.</summary>
	ReplaceExisting,
}

/// <summary>
/// THE ONE PLACE that decides "one portal patch per colour, like Portal".
///
/// Rule: a colour has at most one patch in the whole room. A new splat of that colour
///   - on a different wall, or far from the patch on the same wall, REPLACES the old patch;
///   - on or touching the patch ADDS to it.
/// "Wall" here means any paint surface: the floor and the ceiling count exactly like the four walls.
///
/// Everything about how that feels is tuned here. <see cref="PortalPatches"/> asks this class and then
/// does what it says; nothing else in the game makes this decision.
/// </summary>
public static class PortalPatchPolicy
{
	/// <summary>
	/// "Touching" means: the centre of the new hit is no further from the existing patch's paint than
	/// this many splat radii. 0 = the ball must land on the paint itself. 1 = the splat's round body
	/// just reaches the paint. The default is a little under 1 because a splat's outline is ragged
	/// (up to 20% in or out), and a splat that "touches" but does not actually join would leave two
	/// separate blobs of one colour. Range 0..1.5.
	/// </summary>
	public static float TouchReachInRadii = 0.8f;

	/// <summary>How long a replaced patch takes to fade out, seconds. 0 = it pops. Range 0..0.6.</summary>
	public static float ReplaceFadeSeconds = 0.25f;

	/// <summary>How far from a hit the game looks for the existing patch, metres, for a splat of this radius.</summary>
	public static float TouchReachMetres( float splatRadius )
	{
		return TouchReachInRadii * splatRadius;
	}

	/// <param name="patchExists">The colour already has a patch somewhere in the room.</param>
	/// <param name="sameWall">The new hit is on the wall that holds that patch.</param>
	/// <param name="gapMetres">Distance from the centre of the new hit to the nearest paint of that patch (0 when it
	/// landed on it). Only meaningful when sameWall is true.</param>
	/// <param name="splatRadius">Radius of the new splat's body, metres.</param>
	public static PatchDecision Decide( bool patchExists, bool sameWall, float gapMetres, float splatRadius )
	{
		if ( !patchExists ) return PatchDecision.StartNew;
		if ( sameWall && gapMetres <= TouchReachMetres( splatRadius ) ) return PatchDecision.AddToExisting;
		return PatchDecision.ReplaceExisting;
	}
}
