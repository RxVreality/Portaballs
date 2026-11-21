using System;

namespace Portaballs.Core
{
    /// <summary>
    /// Moving the VR play space about the room with the sticks, and carrying it through a portal.
    ///
    /// The play space is a <see cref="RigidTransform"/> "anchor": it carries a point of the tracked space
    /// (metres from the middle of the real floor) into the world. Every function here returns a WORLD
    /// transform W to apply on top: newAnchor = anchor.Then( W ). Hands, the head and anything else kept
    /// relative to the anchor come along by themselves.
    /// </summary>
    public static class PlaySpace
    {
        /// <summary>Which way a rotation's forward (X) axis points on the floor map, radians about world up (0 = +X, positive toward +Y, left).</summary>
        public static float YawOf(Quat rotation)
        {
            Vec3 f = Vec3.Transform(new Vec3(1f, 0f, 0f), rotation);
            if (f.X * f.X + f.Y * f.Y < 1e-10f)
            {
                // Looking straight up or down: use where the top of the head points instead.
                Vec3 up = Vec3.Transform(new Vec3(0f, 0f, 1f), rotation);
                return MathF.Atan2(-up.Y * MathF.Sign(f.Z), -up.X * MathF.Sign(f.Z));
            }
            return MathF.Atan2(f.Y, f.X);
        }

        /// <summary>
        /// Floor movement for one frame from a thumbstick, relative to where the head faces.
        /// </summary>
        /// <param name="stick">X = pushed right (+1) or left (-1), Y = pushed forward (+1) or back (-1).</param>
        /// <param name="headYaw">Which way the head faces on the floor map, radians (see <see cref="YawOf"/>).</param>
        /// <param name="speed">Speed with the stick fully pushed, m/s.</param>
        /// <param name="dt">Frame time, seconds.</param>
        /// <param name="deadZone">Stick deflection below this (0 to 1) is ignored; the rest is rescaled to start from 0.</param>
        public static Vec3 StickMove(Vec2 stick, float headYaw, float speed, float dt, float deadZone)
        {
            float mag = stick.Length();
            if (mag <= deadZone || mag < 1e-6f) return Vec3.Zero;
            float scaled = (MathF.Min(mag, 1f) - deadZone) / MathF.Max(1e-4f, 1f - deadZone);
            Vec2 dir = stick / mag;

            float c = MathF.Cos(headYaw), s = MathF.Sin(headYaw);
            Vec3 forward = new Vec3(c, s, 0f);
            Vec3 right = new Vec3(s, -c, 0f);       // +Y is left, so right is forward turned clockwise
            return (forward * dir.Y + right * dir.X) * (speed * scaled * dt);
        }

        /// <summary>A plain shift of the whole play space.</summary>
        public static RigidTransform Shift(Vec3 delta)
        {
            return new RigidTransform(Quat.Identity, delta);
        }

        /// <summary>
        /// Turn the whole play space about the upright line through <paramref name="pivot"/> (use the head's
        /// position, so the player turns on the spot instead of swinging round the middle of the play space).
        /// Positive yaw is to the left (counter-clockwise seen from above).
        /// </summary>
        public static RigidTransform TurnAbout(Vec3 pivot, float yawRadians)
        {
            Quat q = Quat.CreateFromAxisAngle(Units.Up, yawRadians);
            return new RigidTransform(q, pivot - Vec3.Transform(pivot, q));
        }

        /// <summary>
        /// Smooth turning: how hard the turning stick asks to turn, -1 (full left) to 1 (full right).
        /// Inside the dead zone it is 0; from there it grows evenly to 1 at full push.
        /// Multiply by the top turning speed and the frame time.
        /// </summary>
        public static float SmoothTurnRate(float stickX, float deadZone)
        {
            float a = MathF.Abs(stickX);
            if (!(a > deadZone)) return 0f;                  // also catches NaN
            if (a > 1f) a = 1f;
            float scaled = (a - deadZone) / MathF.Max(1e-4f, 1f - deadZone);
            return stickX > 0f ? scaled : -scaled;
        }

        /// <summary>
        /// How far a play space is tipped over, radians: the angle between its own up and world up.
        /// 0 = level, pi/2 = lying on its side, pi = upside down. Safe for any rotation.
        /// </summary>
        public static float TiltOf(Quat rotation)
        {
            Vec3 up = Vec3.Transform(new Vec3(0f, 0f, 1f), rotation);
            return MathF.Atan2(MathF.Sqrt(up.X * up.X + up.Y * up.Y), up.Z);
        }

        /// <summary>
        /// Turn a tipped play space back toward level, about <paramref name="pivot"/> (use the head, so the
        /// player's eyes stay where they are). The turn is the smallest one that brings the play space's
        /// up back to world up, limited to <paramref name="maxAngle"/> radians, so calling this every frame
        /// with a small limit swings it up smoothly.
        /// </summary>
        /// <param name="rotation">The play space's rotation now (the anchor's).</param>
        /// <param name="pivot">World point that must not move.</param>
        /// <param name="maxAngle">Turn at most this far, radians. Use <see cref="TiltOf"/> or more for all at once.</param>
        /// <param name="upsideDownAxis">
        /// Exactly upside down, every level axis is an equally short way round, so this picks one (it is
        /// flattened first; pass the crossing's own turning axis and the player ends up facing the way
        /// they faced before the crossing). Ignored otherwise.
        /// </param>
        public static RigidTransform RightAbout(Quat rotation, Vec3 pivot, float maxAngle, Vec3 upsideDownAxis)
        {
            Vec3 up = Vec3.Transform(new Vec3(0f, 0f, 1f), rotation);
            float level = MathF.Sqrt(up.X * up.X + up.Y * up.Y);
            float tilt = MathF.Atan2(level, up.Z);
            if (!(tilt > 0f) || !(maxAngle > 0f)) return RigidTransform.Identity;

            Vec3 axis;
            if (level > 1e-4f)
            {
                // up x worldUp, which is level: turning about it swings up straight toward world up.
                axis = new Vec3(up.Y / level, -up.X / level, 0f);
            }
            else
            {
                float len = MathF.Sqrt(upsideDownAxis.X * upsideDownAxis.X + upsideDownAxis.Y * upsideDownAxis.Y);
                axis = len > 1e-4f ? new Vec3(upsideDownAxis.X / len, upsideDownAxis.Y / len, 0f) : new Vec3(1f, 0f, 0f);
            }

            Quat q = Quat.CreateFromAxisAngle(axis, MathF.Min(tilt, maxAngle));
            return new RigidTransform(q, pivot - Vec3.Transform(pivot, q));
        }

        /// <summary>
        /// The last touch after <see cref="RightAbout"/>: the EXACTLY level rotation (a pure turn about world
        /// up, X and Y of the quaternion are 0) that faces the same way on the floor map as a nearly level one.
        /// </summary>
        public static Quat Levelled(Quat rotation)
        {
            return Quat.CreateFromAxisAngle(Units.Up, YawOf(rotation));
        }
    }

    /// <summary>
    /// Turns a thumbstick's sideways push into single snap turns: one turn each time the stick is pushed
    /// past <see cref="Threshold"/>, and no more until it has come back inside <see cref="Release"/>.
    /// </summary>
    public sealed class SnapTurnLatch
    {
        /// <summary>Sideways deflection (0 to 1) that triggers a turn.</summary>
        public float Threshold = 0.6f;

        /// <summary>The stick must come back inside this before another turn can trigger.</summary>
        public float Release = 0.3f;

        private bool _armed = true;

        /// <summary>Feed the stick's sideways value every frame. Returns +1 for one turn to the right, -1 to the left, else 0.</summary>
        public int Update(float stickX)
        {
            float a = MathF.Abs(stickX);
            if (!_armed)
            {
                if (a < Release) _armed = true;
                return 0;
            }
            if (a >= Threshold)
            {
                _armed = false;
                return stickX > 0f ? 1 : -1;
            }
            return 0;
        }

        public void Reset()
        {
            _armed = true;
        }
    }
}
