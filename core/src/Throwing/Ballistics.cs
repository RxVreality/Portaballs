using System;

namespace Portaballs.Core
{
    /// <summary>
    /// The answer to "how must I throw from here to hit that point at this speed?".
    /// There are usually two ways: a flat fast one (low arc) and a lob (high arc).
    /// </summary>
    public struct LaunchSolution
    {
        /// <summary>True when the point can be reached at the speed asked for.</summary>
        public bool Reachable;

        /// <summary>Launch velocity and flight time of the flatter throw. Only meaningful when Reachable.</summary>
        public Vec3 LowArcVelocity;
        public float LowArcTime;

        /// <summary>Launch velocity and flight time of the lob. Only meaningful when Reachable.</summary>
        public Vec3 HighArcVelocity;
        public float HighArcTime;

        /// <summary>The slowest throw that can reach the point at all, in m/s. Filled in whether or not Reachable.</summary>
        public float MinSpeed;

        /// <summary>The launch velocity of that slowest throw.</summary>
        public Vec3 MinSpeedVelocity;
    }

    /// <summary>
    /// Free-flight maths for a ball under gravity with no air drag: where it is after a time, where it
    /// hits a wall, and how to launch it to hit a chosen point. Everything is plain functions with no
    /// memory, so they are safe to call from anywhere.
    /// </summary>
    public static class Ballistics
    {
        /// <summary>Where the ball is, a time after leaving position p0 with velocity v0.</summary>
        public static Vec3 PositionAt(Vec3 p0, Vec3 v0, Vec3 gravity, float time)
        {
            return p0 + v0 * time + gravity * (0.5f * time * time);
        }

        /// <summary>How fast the ball is moving, a time after leaving with velocity v0.</summary>
        public static Vec3 VelocityAt(Vec3 v0, Vec3 gravity, float time)
        {
            return v0 + gravity * time;
        }

        /// <summary>
        /// When and where the ball's path first crosses a flat infinite surface.
        /// Returns false when the path never reaches it.
        /// </summary>
        /// <param name="planePoint">Any point on the surface.</param>
        /// <param name="planeNormal">Direction the surface faces (any length).</param>
        /// <param name="time">Seconds of flight until the crossing.</param>
        /// <param name="hit">World position of the crossing.</param>
        public static bool TryHitPlane(Vec3 p0, Vec3 v0, Vec3 gravity, Vec3 planePoint, Vec3 planeNormal,
            out float time, out Vec3 hit)
        {
            time = 0f;
            hit = p0;
            Vec3 n = MathUtil.SafeNormalize(planeNormal, default(Vec3));
            if (n.LengthSquared() < 0.5f) return false;

            // Height above the surface over time is  c + b t + a t^2.
            double a = 0.5 * Vec3.Dot(gravity, n);
            double b = Vec3.Dot(v0, n);
            double c = Vec3.Dot(p0 - planePoint, n);
            if (double.IsNaN(a) || double.IsNaN(b) || double.IsNaN(c)) return false;

            const double MinTime = 1e-6;
            double best = -1.0;
            if (Math.Abs(a) < 1e-9)
            {
                if (Math.Abs(b) < 1e-9) return false;
                double t = -c / b;
                if (t > MinTime) best = t;
            }
            else
            {
                double disc = b * b - 4.0 * a * c;
                if (disc < 0.0) return false;
                double root = Math.Sqrt(disc);
                // Stable form of the two roots (avoids subtracting nearly equal numbers).
                double q = -0.5 * (b + (b >= 0.0 ? root : -root));
                double t1 = q / a;
                double t2 = Math.Abs(q) > 1e-300 ? c / q : double.NaN;
                if (!double.IsNaN(t1) && t1 > MinTime) best = t1;
                if (!double.IsNaN(t2) && t2 > MinTime && (best < 0.0 || t2 < best)) best = t2;
            }
            if (best < 0.0 || double.IsInfinity(best)) return false;

            time = (float)best;
            hit = PositionAt(p0, v0, gravity, time);
            return true;
        }

        /// <summary>
        /// When and where the ball's path first crosses a wall's surface.
        /// </summary>
        /// <param name="uv">Wall coordinates of the crossing (metres from the wall's lower-left corner).</param>
        /// <param name="insideWall">True when the crossing lies on the wall's rectangle, not beyond its edges.</param>
        /// <returns>False when the path never reaches the wall's surface at all.</returns>
        public static bool TryHitWall(Vec3 p0, Vec3 v0, Vec3 gravity, WallFrame wall,
            out float time, out Vec3 hit, out Vec2 uv, out bool insideWall)
        {
            uv = default(Vec2);
            insideWall = false;
            if (!TryHitPlane(p0, v0, gravity, wall.Origin, wall.Normal, out time, out hit)) return false;
            uv = wall.ToUV(hit);
            insideWall = wall.Contains(uv);
            return true;
        }

        /// <summary>
        /// Work out how to throw from one point to hit another at a given speed.
        /// Always fills in MinSpeed, so when the point is out of reach the caller knows how much
        /// harder the throw would need to be.
        /// </summary>
        public static LaunchSolution SolveLaunch(Vec3 from, Vec3 target, float speed, Vec3 gravity)
        {
            LaunchSolution sol = default(LaunchSolution);
            if (float.IsNaN(speed) || speed < 0f) speed = 0f;

            double dx = (double)target.X - from.X;
            double dy = (double)target.Y - from.Y;
            double dz = (double)target.Z - from.Z;
            double gx = gravity.X, gy = gravity.Y, gz = gravity.Z;
            double dist2 = dx * dx + dy * dy + dz * dz;
            double g2 = gx * gx + gy * gy + gz * gz;
            double dg = dx * gx + dy * gy + dz * gz;
            if (double.IsNaN(dist2) || double.IsNaN(g2)) return sol;

            double dist = Math.Sqrt(dist2);
            if (dist < 1e-6)
            {
                // Already there: nothing to solve.
                return sol;
            }

            double s2 = (double)speed * speed;

            if (g2 < 1e-12)
            {
                // No gravity: a straight line, any speed above zero gets there.
                sol.MinSpeed = 0f;
                if (speed > 1e-6f)
                {
                    double t = dist / speed;
                    Vec3 v = new Vec3((float)(dx / t), (float)(dy / t), (float)(dz / t));
                    sol.Reachable = true;
                    sol.LowArcVelocity = v;
                    sol.HighArcVelocity = v;
                    sol.LowArcTime = (float)t;
                    sol.HighArcTime = (float)t;
                    sol.MinSpeedVelocity = v;
                }
                return sol;
            }

            double g = Math.Sqrt(g2);

            // Slowest possible throw: speed^2 = |g| * distance - (d . g).
            double minS2 = g * dist - dg;
            if (minS2 < 0.0) minS2 = 0.0;
            sol.MinSpeed = (float)Math.Sqrt(minS2);
            {
                // Its flight time T satisfies T^2 = 2 * distance / |g|.
                double tMin = Math.Sqrt(2.0 * dist / g);
                sol.MinSpeedVelocity = VelocityForTime(dx, dy, dz, gx, gy, gz, tMin);
            }

            // Flight time T for the asked-for speed solves
            //   (|g|^2 / 4) T^4 - (d . g + s^2) T^2 + |d|^2 = 0     (a quadratic in T^2).
            double qa = 0.25 * g2;
            double qb = -(dg + s2);
            double disc = qb * qb - 4.0 * qa * dist2;
            if (disc < 0.0 || -qb <= 0.0) return sol;   // too slow to reach

            double root = Math.Sqrt(disc);
            double uLow = (-qb - root) / (2.0 * qa);
            double uHigh = (-qb + root) / (2.0 * qa);
            if (uLow <= 1e-12)
            {
                // Guard against rounding when the flat throw is nearly a straight line.
                uLow = dist2 / (uHigh * qa);   // product of the roots is |d|^2 / qa
            }
            if (uLow <= 0.0 || uHigh <= 0.0 || double.IsNaN(uLow) || double.IsNaN(uHigh)) return sol;

            double tLow = Math.Sqrt(uLow);
            double tHigh = Math.Sqrt(uHigh);
            sol.Reachable = true;
            sol.LowArcTime = (float)tLow;
            sol.HighArcTime = (float)tHigh;
            sol.LowArcVelocity = VelocityForTime(dx, dy, dz, gx, gy, gz, tLow);
            sol.HighArcVelocity = VelocityForTime(dx, dy, dz, gx, gy, gz, tHigh);
            return sol;
        }

        /// <summary>
        /// Like <see cref="SolveLaunch"/>, but picks whichever of the two throws points closest to a
        /// direction you give (normally the way the hand was actually moving).
        /// </summary>
        /// <param name="velocity">The chosen launch velocity. When out of reach this is the slowest throw that would get there.</param>
        /// <param name="flightTime">Seconds until it arrives.</param>
        /// <param name="minSpeed">The slowest speed that can reach the point at all.</param>
        /// <returns>True when the point can be reached at the speed asked for.</returns>
        public static bool SolveLaunchClosestTo(Vec3 from, Vec3 target, float speed, Vec3 gravity, Vec3 preferredDirection,
            out Vec3 velocity, out float flightTime, out float minSpeed)
        {
            LaunchSolution sol = SolveLaunch(from, target, speed, gravity);
            minSpeed = sol.MinSpeed;
            if (!sol.Reachable)
            {
                velocity = sol.MinSpeedVelocity;
                flightTime = 0f;
                return false;
            }
            float angleLow = MathUtil.AngleBetween(preferredDirection, sol.LowArcVelocity);
            float angleHigh = MathUtil.AngleBetween(preferredDirection, sol.HighArcVelocity);
            if (angleHigh < angleLow)
            {
                velocity = sol.HighArcVelocity;
                flightTime = sol.HighArcTime;
            }
            else
            {
                velocity = sol.LowArcVelocity;
                flightTime = sol.LowArcTime;
            }
            return true;
        }

        /// <summary>
        /// The point on the ball's path that comes nearest to a target, looking up to maxTime seconds ahead.
        /// Used to judge a throw when it never reaches the surface the target is on.
        /// </summary>
        public static Vec3 ClosestApproach(Vec3 p0, Vec3 v0, Vec3 gravity, Vec3 target, float maxTime,
            out float time, out float distance)
        {
            if (!(maxTime > 0f)) maxTime = 0f;
            const int Steps = 48;
            float bestT = 0f;
            float bestD2 = (p0 - target).LengthSquared();
            for (int i = 1; i <= Steps; i++)
            {
                float t = maxTime * i / Steps;
                float d2 = (PositionAt(p0, v0, gravity, t) - target).LengthSquared();
                if (d2 < bestD2) { bestD2 = d2; bestT = t; }
            }
            // Narrow down around the best coarse step.
            float step = maxTime / Steps;
            float lo = MathF.Max(0f, bestT - step);
            float hi = MathF.Min(maxTime, bestT + step);
            for (int i = 0; i < 24; i++)
            {
                float m1 = lo + (hi - lo) / 3f;
                float m2 = hi - (hi - lo) / 3f;
                float d1 = (PositionAt(p0, v0, gravity, m1) - target).LengthSquared();
                float d2 = (PositionAt(p0, v0, gravity, m2) - target).LengthSquared();
                if (d1 < d2) hi = m2; else lo = m1;
            }
            time = 0.5f * (lo + hi);
            Vec3 point = PositionAt(p0, v0, gravity, time);
            distance = (point - target).Length();
            if (float.IsNaN(distance))
            {
                time = 0f;
                distance = 0f;
                return p0;
            }
            return point;
        }

        // Launch velocity that covers displacement d in time t under gravity g:  v = d / t - g t / 2.
        private static Vec3 VelocityForTime(double dx, double dy, double dz, double gx, double gy, double gz, double t)
        {
            if (t < 1e-9) return default(Vec3);
            double h = 0.5 * t;
            return new Vec3((float)(dx / t - gx * h), (float)(dy / t - gy * h), (float)(dz / t - gz * h));
        }
    }
}
