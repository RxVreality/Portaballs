using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// The one place where engine numbers and core numbers meet.
///
/// The engine works in inches (1 unit = 1 inch). The core maths works in metres. Both use the same
/// axes (+X forward, +Y left, +Z up), so a conversion is only ever a scale, never an axis swap.
///
/// Type names matter here: <c>Vector3</c> / <c>Rotation</c> are the ENGINE's types, <c>Vec3</c> / <c>Quat</c>
/// are the core's (System.Numerics). The engine converts between them silently, which would let a
/// metres value slip into an inches slot without anyone noticing. So game code never relies on that:
/// it always goes through one of these functions, and the function name says which unit comes out.
/// </summary>
public static class SboxConvert
{
	public const float InchesPerMetre = Units.SourceUnitsPerMeter;
	public const float MetresPerInch = Units.MetersPerSourceUnit;

	/// <summary>Edge length of the engine's built-in cube model (models/dev/box.vmdl), in inches.</summary>
	public const float DevBoxSize = 50f;

	/// <summary>Radius of the engine's built-in sphere model (models/dev/sphere.vmdl), in inches.</summary>
	public const float DevSphereRadius = 32f;

	// ---- positions and velocities: inches <-> metres ----

	/// <summary>An engine position or velocity (inches) as core metres.</summary>
	public static Vec3 ToMetres( Vector3 inches )
	{
		return new Vec3( inches.x * MetresPerInch, inches.y * MetresPerInch, inches.z * MetresPerInch );
	}

	/// <summary>A core position or velocity (metres) as engine inches.</summary>
	public static Vector3 ToInches( Vec3 metres )
	{
		return new Vector3( metres.X * InchesPerMetre, metres.Y * InchesPerMetre, metres.Z * InchesPerMetre );
	}

	/// <summary>A single length in metres as inches.</summary>
	public static float LengthToInches( float metres )
	{
		return metres * InchesPerMetre;
	}

	// ---- directions and plain numbers: same values, different type ----

	/// <summary>An engine vector as a core vector with the SAME numbers (for directions, or values already in metres).</summary>
	public static Vec3 ToCore( Vector3 v )
	{
		return new Vec3( v.x, v.y, v.z );
	}

	/// <summary>A core vector as an engine vector with the SAME numbers (for directions).</summary>
	public static Vector3 ToEngine( Vec3 v )
	{
		return new Vector3( v.X, v.Y, v.Z );
	}

	// ---- rotations: no units, same axes, so the four numbers carry straight across ----

	public static Quat ToCore( Rotation r )
	{
		return new Quat( r.x, r.y, r.z, r.w );
	}

	public static Rotation ToEngine( Quat q )
	{
		return new Rotation( q.X, q.Y, q.Z, q.W );
	}

	/// <summary>An engine pose (position in inches) as the core wants it: position in metres plus rotation.</summary>
	public static void ToCorePose( Transform engine, out Vec3 positionMetres, out Quat rotation )
	{
		positionMetres = ToMetres( engine.Position );
		rotation = ToCore( engine.Rotation );
	}

	/// <summary>
	/// An engine transform (an object's place in the world, position in inches) as the core's
	/// turn-and-shift in metres: it carries a point of that object's own space into the world.
	/// Any scale on the transform is ignored; the play-area anchor is never scaled.
	/// </summary>
	public static RigidTransform ToRigid( Transform engine )
	{
		return new RigidTransform( ToCore( engine.Rotation ), ToMetres( engine.Position ) );
	}

	/// <summary>
	/// A spin from the core (axis in world space, length in radians per second) for a Rigidbody.
	/// ASSUMPTION, not confirmed in the engine source: Rigidbody.AngularVelocity is in radians per
	/// second. If balls visibly spin about 57 times too slowly, it is degrees: multiply by 57.2958 here.
	/// </summary>
	public static Vector3 SpinToEngine( Vec3 radiansPerSecond )
	{
		return new Vector3( radiansPerSecond.X, radiansPerSecond.Y, radiansPerSecond.Z );
	}

	// ---- sizing the built-in models ----

	/// <summary>The scale that makes the built-in cube model this big (size in metres along X, Y, Z).</summary>
	public static Vector3 BoxScaleFor( Vec3 sizeMetres )
	{
		float k = InchesPerMetre / DevBoxSize;
		return new Vector3( sizeMetres.X * k, sizeMetres.Y * k, sizeMetres.Z * k );
	}

	/// <summary>The scale that gives the built-in sphere model this radius in metres.</summary>
	public static Vector3 SphereScaleFor( float radiusMetres )
	{
		float s = radiusMetres * InchesPerMetre / DevSphereRadius;
		return new Vector3( s, s, s );
	}
}
