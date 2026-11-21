using System;

namespace Portaballs.Core
{
    /// <summary>Where the aim point comes from.</summary>
    public enum AimAssistMode
    {
        /// <summary>No help at all.</summary>
        Off = 0,

        /// <summary>Aim where the head is pointing.</summary>
        Head = 1,

        /// <summary>Aim where the eyes are looking. No help when the gaze is not valid.</summary>
        Eye = 2,

        /// <summary>Aim where the eyes are looking; when the gaze is not valid, use the head instead.</summary>
        EyeWithHeadFallback = 3,
    }

    /// <summary>Which aim point was actually used for a throw.</summary>
    public enum AimSource
    {
        None = 0,
        Head = 1,
        Eye = 2,
    }

    /// <summary>What the aim assist decided, for the tuning overlay.</summary>
    public enum AimAssistOutcome
    {
        /// <summary>The throw was nudged toward the aim point.</summary>
        Applied = 0,

        /// <summary>Assist is switched off.</summary>
        Off = 1,

        /// <summary>There was no valid aim point.</summary>
        NoTarget = 2,

        /// <summary>The release was a let-go or too slow to count as a throw.</summary>
        Drop = 3,

        /// <summary>The throw was aimed too far from the aim point (or away from it) to be meant for it.</summary>
        OutsideCone = 4,

        /// <summary>The aim point cannot be reached at anything near the thrown speed.</summary>
        OutOfReach = 5,

        /// <summary>The nudged throw would have landed further from the aim point than the original, so it was left alone.</summary>
        WouldBeWorse = 6,

        /// <summary>The aim point is too close or too far to be a sensible target.</summary>
        BadDistance = 7,
    }

    /// <summary>How strongly, and over what spread of angles, one kind of aim point pulls a throw.</summary>
    public struct AimCone
    {
        /// <summary>Throws within this angle of the perfect throw get the full pull, radians.</summary>
        public float InnerAngle;

        /// <summary>Throws further off than this get no pull at all, radians. The pull fades between the two.</summary>
        public float OuterAngle;

        /// <summary>The share of the error removed at full pull: 0 is none, 1 would steer every throw exactly onto the point.</summary>
        public float Strength;

        public AimCone(float innerDegrees, float outerDegrees, float strength)
        {
            InnerAngle = innerDegrees * (MathF.PI / 180f);
            OuterAngle = outerDegrees * (MathF.PI / 180f);
            Strength = strength;
        }
    }

    /// <summary>
    /// Tunable numbers for aim assist. The angles are our own starting guesses, not published values.
    ///
    /// The strengths were raised after the tester's headset session of 2026-11-19 ("too weak ... strengthen it
    /// just a little bit"): eye 0.5 to 0.65, head 0.4 to 0.45. Rebuilt from that session's 145 throws
    /// that had an eye point, their own throws landed 0.53 m from where they were looking in the middle case
    /// and the 0.5 pull left 0.28 m. The same day the throw was made faster (1.5 to 1.7 times hand
    /// speed), which with the same arm lands higher: at 1.7 a pull of 0.6 would have left 0.26 m, no
    /// better than before; 0.65 leaves 0.23 m. The cones were left alone: they are what keeps a throw
    /// meant for somewhere else from being dragged to the look point.
    /// </summary>
    public sealed class AimAssistSettings
    {
        public AimAssistMode Mode = AimAssistMode.EyeWithHeadFallback;

        /// <summary>Used when aiming with the head: full inside 10 degrees, nothing beyond 25, strength 0.45.
        /// Sensible ranges: inner 5 to 15, outer 15 to 40, strength 0 to 0.8.</summary>
        public AimCone Head = new AimCone(10f, 25f, 0.45f);

        /// <summary>Used when aiming with the eyes (a steady look): full inside 15 degrees, nothing beyond 30, strength 0.65.
        /// Sensible ranges: inner 8 to 20, outer 20 to 45, strength 0 to 0.9.</summary>
        public AimCone Eye = new AimCone(15f, 30f, 0.65f);

        /// <summary>
        /// Used for an eye point marked as a glance (<see cref="AimPoint.Glance"/>): the eyes were pointing
        /// there at the release but had not held still, so it is trusted no more than the head is.
        /// </summary>
        public AimCone Glance = new AimCone(10f, 25f, 0.45f);

        /// <summary>
        /// The most the speed may be changed, as a share of the thrown speed (0.1 = 10 percent). Only used
        /// when the aim point is just out of reach at the thrown speed. Sensible range 0 to 0.2.
        /// </summary>
        public float MaxSpeedChange = 0.10f;

        /// <summary>Throws slower than this (m/s) get no help. Sensible range 1 to 2.5.</summary>
        public float MinSpeed = 1.5f;

        /// <summary>Help reaches full strength at this speed (m/s), fading in from MinSpeed. Sensible range 2 to 4.</summary>
        public float FullSpeed = 3.0f;

        /// <summary>Aim points nearer or further than this from the ball (metres) are ignored.</summary>
        public float MinTargetDistance = 0.5f;
        public float MaxTargetDistance = 40f;

        /// <summary>Gravity used for the flight path, m/s^2. Keep the same as ThrowSettings.Gravity.</summary>
        public Vec3 Gravity = new Vec3(0f, 0f, -9.81f);
    }

    /// <summary>A point in the world the player is aiming at.</summary>
    public struct AimPoint
    {
        /// <summary>False when there is no such point (the ray hit nothing, or the gaze is not valid).</summary>
        public bool Valid;

        /// <summary>World position of the point.</summary>
        public Vec3 Point;

        /// <summary>
        /// Direction the surface at that point faces (out of the wall, toward the room). Leave as zero if
        /// unknown; an upright surface facing the thrower is then assumed when reporting landing points.
        /// </summary>
        public Vec3 Normal;

        /// <summary>
        /// Only read on the eye point. True when it comes from a glance rather than a steady look
        /// (<see cref="GazeLookKind.Glance"/>): aim assist then uses the gentler Glance numbers for it.
        /// </summary>
        public bool Glance;

        public AimPoint(Vec3 point, Vec3 normal)
        {
            Valid = true;
            Point = point;
            Normal = normal;
            Glance = false;
        }

        /// <summary>An aim point that says "nothing to aim at".</summary>
        public static AimPoint None { get { return default(AimPoint); } }
    }

    /// <summary>What aim assist did to one throw.</summary>
    public struct AimAssistResult
    {
        /// <summary>The launch velocity to use. Equal to the input whenever the outcome is not Applied.</summary>
        public Vec3 Velocity;

        public AimAssistOutcome Outcome;

        /// <summary>True when the velocity was changed.</summary>
        public bool Applied;

        public AimSource Source;

        /// <summary>True when the source was the eyes and the eye point was a glance, so the Glance numbers were used.</summary>
        public bool Glance;

        /// <summary>The aim point that was considered.</summary>
        public Vec3 TargetPoint;

        /// <summary>Angle between the thrown direction and the perfect throw, radians.</summary>
        public float AngleOff;

        /// <summary>The share of that angle that was removed (0..1).</summary>
        public float Weight;

        /// <summary>Change in speed as a share of the thrown speed (0.05 = 5 percent faster).</summary>
        public float SpeedChange;

        /// <summary>The launch velocity that would have hit the aim point exactly (zero when none was found).</summary>
        public Vec3 PerfectVelocity;

        /// <summary>The slowest throw that could reach the aim point at all, m/s.</summary>
        public float MinSpeedNeeded;

        /// <summary>
        /// Where the throw would cross the surface the aim point is on, without and with the assist.
        /// When LandingOnSurface is false the path never reaches that surface and these are instead the
        /// points on each path that come nearest to the aim point.
        /// </summary>
        public Vec3 LandingWithout;
        public Vec3 LandingWith;
        public bool LandingOnSurface;

        /// <summary>Distance from each of those points to the aim point, metres.</summary>
        public float MissWithout;
        public float MissWith;
    }

    /// <summary>
    /// A gentle nudge of a throw toward where the player is looking.
    ///
    /// It works out the throw that WOULD hit the aim point at about the speed actually thrown, sees how
    /// far off the real throw was, and removes a share of that error: a full share for throws that were
    /// nearly right, fading to nothing for throws that were clearly meant for somewhere else. It never
    /// touches a drop, never pulls a throw that was aimed away, and never leaves a throw worse than it found it.
    /// </summary>
    public static class AimAssist
    {
        /// <param name="launchPosition">Where the ball leaves the hand.</param>
        /// <param name="launchVelocity">The measured launch velocity (from the throw estimator).</param>
        /// <param name="isDrop">True when the estimator says this was a let-go, not a throw.</param>
        /// <param name="headPoint">Where the head's forward ray hits the world.</param>
        /// <param name="eyePoint">Where the gaze ray hits the world. Pass AimPoint.None when there is no valid gaze.</param>
        public static AimAssistResult Apply(AimAssistSettings settings, Vec3 launchPosition, Vec3 launchVelocity, bool isDrop,
            AimPoint headPoint, AimPoint eyePoint)
        {
            AimAssistResult r = default(AimAssistResult);
            r.Velocity = ThrowMath.FiniteOrZero(launchVelocity);
            launchVelocity = r.Velocity;
            if (settings == null) settings = new AimAssistSettings();

            // Which aim point, and which set of numbers?
            AimPoint target = default(AimPoint);
            AimCone cone = default(AimCone);
            switch (settings.Mode)
            {
                case AimAssistMode.Head:
                    if (IsUsable(headPoint)) { target = headPoint; cone = settings.Head; r.Source = AimSource.Head; }
                    break;
                case AimAssistMode.Eye:
                    if (IsUsable(eyePoint)) { target = eyePoint; cone = eyePoint.Glance ? settings.Glance : settings.Eye; r.Source = AimSource.Eye; }
                    break;
                case AimAssistMode.EyeWithHeadFallback:
                    if (IsUsable(eyePoint)) { target = eyePoint; cone = eyePoint.Glance ? settings.Glance : settings.Eye; r.Source = AimSource.Eye; }
                    else if (IsUsable(headPoint)) { target = headPoint; cone = settings.Head; r.Source = AimSource.Head; }
                    break;
                default:
                    r.Outcome = AimAssistOutcome.Off;
                    return r;
            }
            if (r.Source == AimSource.None)
            {
                r.Outcome = AimAssistOutcome.NoTarget;
                return r;
            }
            r.TargetPoint = target.Point;
            r.Glance = r.Source == AimSource.Eye && target.Glance;

            if (!ThrowMath.IsFinite(launchPosition))
            {
                r.Outcome = AimAssistOutcome.NoTarget;
                r.Source = AimSource.None;
                r.Glance = false;
                return r;
            }

            Vec3 gravity = ThrowMath.FiniteOrZero(settings.Gravity);
            float speed = launchVelocity.Length();
            Vec3 toTarget = target.Point - launchPosition;
            float distance = toTarget.Length();

            // Report where the untouched throw goes, whatever happens next.
            Vec3 surfaceNormal = SurfaceNormal(target, launchPosition);
            float flightLimit = FlightLimit(distance, speed);
            bool onSurface;
            r.LandingWithout = Landing(launchPosition, launchVelocity, gravity, target.Point, surfaceNormal, flightLimit,
                true, out onSurface, out r.MissWithout);
            r.LandingOnSurface = onSurface;
            r.LandingWith = r.LandingWithout;
            r.MissWith = r.MissWithout;

            if (isDrop || speed < settings.MinSpeed || speed < 1e-3f)
            {
                r.Outcome = AimAssistOutcome.Drop;
                return r;
            }
            if (distance < settings.MinTargetDistance || distance > settings.MaxTargetDistance)
            {
                r.Outcome = AimAssistOutcome.BadDistance;
                return r;
            }

            // The throw that would hit the point at the measured speed.
            Vec3 measuredDir = launchVelocity / speed;
            Vec3 perfect;
            float flightTime, minSpeed;
            float perfectSpeed = speed;
            bool reachable = Ballistics.SolveLaunchClosestTo(launchPosition, target.Point, speed, gravity, measuredDir,
                out perfect, out flightTime, out minSpeed);
            r.MinSpeedNeeded = minSpeed;
            if (!reachable)
            {
                // Just short? Allow a small, capped speed-up.
                float needed = minSpeed * 1.002f;
                float allowed = speed * (1f + MathF.Max(0f, settings.MaxSpeedChange));
                if (needed > allowed)
                {
                    r.Outcome = AimAssistOutcome.OutOfReach;
                    return r;
                }
                reachable = Ballistics.SolveLaunchClosestTo(launchPosition, target.Point, needed, gravity, measuredDir,
                    out perfect, out flightTime, out minSpeed);
                if (!reachable)
                {
                    r.Outcome = AimAssistOutcome.OutOfReach;
                    return r;
                }
                perfectSpeed = needed;
            }
            r.PerfectVelocity = perfect;

            // How far off was the real throw, and how much of that do we take away?
            Vec3 perfectDir = MathUtil.SafeNormalize(perfect, measuredDir);
            float angle = MathUtil.AngleBetween(measuredDir, perfectDir);
            r.AngleOff = angle;

            float inner = MathF.Max(0f, cone.InnerAngle);
            float outer = MathF.Max(inner, cone.OuterAngle);
            if (angle >= outer || Vec3.Dot(measuredDir, perfectDir) <= 0f)
            {
                r.Outcome = AimAssistOutcome.OutsideCone;
                return r;
            }
            float fade = 1f - MathUtil.SmoothStep(inner, outer, angle);
            float bySpeed = MathUtil.SmoothStep(settings.MinSpeed, MathF.Max(settings.FullSpeed, settings.MinSpeed + 1e-3f), speed);
            float weight = MathUtil.Saturate(cone.Strength) * fade * bySpeed;
            if (weight <= 1e-5f)
            {
                // Nothing to do: say why (strength set to zero, barely a throw, or right at the cone's edge).
                if (cone.Strength <= 1e-5f) r.Outcome = AimAssistOutcome.Off;
                else if (bySpeed <= 1e-5f) r.Outcome = AimAssistOutcome.Drop;
                else r.Outcome = AimAssistOutcome.OutsideCone;
                return r;
            }

            // Bend along the shortest arc, and move the speed the same share of the way.
            Vec3 newDir = MathUtil.SlerpDirection(measuredDir, perfectDir, weight);
            float newSpeed = MathUtil.Lerp(speed, perfectSpeed, weight);
            Vec3 assisted = newDir * newSpeed;
            if (!ThrowMath.IsFinite(assisted))
            {
                r.Outcome = AimAssistOutcome.WouldBeWorse;
                return r;
            }

            // Never make it worse: judge both throws the same way and keep the original if it was closer.
            bool withOnSurface;
            float missWith;
            Vec3 landingWith = Landing(launchPosition, assisted, gravity, target.Point, surfaceNormal, flightLimit,
                onSurface, out withOnSurface, out missWith);
            float missWithout = r.MissWithout;
            Vec3 landingWithout = r.LandingWithout;
            if (onSurface && !withOnSurface)
            {
                // The nudged throw no longer reaches the surface: compare both by nearest approach instead.
                landingWithout = Landing(launchPosition, launchVelocity, gravity, target.Point, surfaceNormal, flightLimit,
                    false, out onSurface, out missWithout);
            }
            if (missWith > missWithout)
            {
                r.Outcome = AimAssistOutcome.WouldBeWorse;
                return r;
            }

            r.Velocity = assisted;
            r.Applied = true;
            r.Outcome = AimAssistOutcome.Applied;
            r.Weight = weight;
            r.SpeedChange = newSpeed / speed - 1f;
            r.LandingWithout = landingWithout;
            r.MissWithout = missWithout;
            r.LandingWith = landingWith;
            r.MissWith = missWith;
            r.LandingOnSurface = withOnSurface;
            return r;
        }

        /// <summary>
        /// Find what an aim ray (head forward or gaze) points at among a set of walls: the nearest wall
        /// rectangle the ray hits from the room side. "Wall" means any flat paintable rectangle: a floor
        /// or a ceiling frame (level U and V, Normal pointing into the room) works the same way, and
        /// nothing in <see cref="Apply"/> needs the surface to be upright either.
        /// </summary>
        /// <param name="walls">The walls to test. May be longer than wallCount.</param>
        /// <param name="wallCount">How many entries of the array to use.</param>
        /// <param name="maxDistance">Hits further than this along the ray are ignored, metres.</param>
        /// <param name="wallIndex">Which wall was hit, or -1.</param>
        /// <param name="distance">Distance along the ray to the hit, metres.</param>
        public static bool FindAimPoint(Vec3 rayOrigin, Vec3 rayDirection, WallFrame[] walls, int wallCount, float maxDistance,
            out AimPoint point, out int wallIndex, out float distance)
        {
            point = default(AimPoint);
            wallIndex = -1;
            distance = 0f;
            if (walls == null || !ThrowMath.IsFinite(rayOrigin)) return false;
            Vec3 dir = MathUtil.SafeNormalize(rayDirection, default(Vec3));
            if (dir.LengthSquared() < 0.5f) return false;
            if (wallCount > walls.Length) wallCount = walls.Length;

            float best = maxDistance > 0f ? maxDistance : float.MaxValue;
            for (int i = 0; i < wallCount; i++)
            {
                WallFrame wall = walls[i];
                float facing = Vec3.Dot(dir, wall.Normal);
                if (!(facing < -1e-5f)) continue;               // ray runs along or out of the wall
                float height = wall.SignedDistance(rayOrigin);
                if (!(height > 0f)) continue;                    // starting behind the wall
                float t = height / -facing;
                if (!(t < best)) continue;
                Vec3 hit = rayOrigin + dir * t;
                if (!wall.Contains(wall.ToUV(hit))) continue;
                best = t;
                wallIndex = i;
                distance = t;
                point = new AimPoint(hit, wall.Normal);
            }
            return wallIndex >= 0;
        }

        private static bool IsUsable(AimPoint p)
        {
            return p.Valid && ThrowMath.IsFinite(p.Point);
        }

        // The direction the target's surface faces. When the caller did not say, assume an upright
        // surface facing back toward the thrower (or straight at the thrower when it is overhead or underfoot).
        private static Vec3 SurfaceNormal(AimPoint target, Vec3 launchPosition)
        {
            if (ThrowMath.IsFinite(target.Normal) && target.Normal.LengthSquared() > 1e-6f)
            {
                return Vec3.Normalize(target.Normal);
            }
            Vec3 back = launchPosition - target.Point;
            Vec3 flat = new Vec3(back.X, back.Y, 0f);
            if (flat.LengthSquared() > 0.04f) return Vec3.Normalize(flat);
            return MathUtil.SafeNormalize(back, Units.Up);
        }

        private static float FlightLimit(float distance, float speed)
        {
            float t = speed > 0.1f ? 2f * distance / speed + 1f : 3f;
            if (float.IsNaN(t)) t = 3f;
            return ThrowMath.Clamp(t, 0.5f, 6f);
        }

        // Where a throw ends up relative to the target: its crossing of the target's surface when allowed
        // and possible, otherwise the nearest point of its path.
        private static Vec3 Landing(Vec3 from, Vec3 velocity, Vec3 gravity, Vec3 targetPoint, Vec3 normal, float flightLimit,
            bool allowSurface, out bool onSurface, out float miss)
        {
            onSurface = false;
            if (allowSurface)
            {
                float t;
                Vec3 hit;
                if (Ballistics.TryHitPlane(from, velocity, gravity, targetPoint, normal, out t, out hit)
                    && ThrowMath.IsFinite(hit))
                {
                    onSurface = true;
                    miss = (hit - targetPoint).Length();
                    return hit;
                }
            }
            float time;
            Vec3 nearest = Ballistics.ClosestApproach(from, velocity, gravity, targetPoint, flightLimit, out time, out miss);
            return nearest;
        }
    }
}
