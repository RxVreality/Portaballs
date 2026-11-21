using System;

namespace Portaballs.Core
{
    /// <summary>
    /// Turns SteamVR's axes into the core's axes and back.
    ///
    /// SteamVR (OpenVR) tracking space: metres, +X right, +Y up, -Z forward.
    /// Core (Units.cs):                 metres, +X forward, +Y left, +Z up.
    ///
    /// So:  core X = -openvr Z,   core Y = -openvr X,   core Z = openvr Y.
    ///
    /// Both layouts are right-handed, so this is a plain turn of the axes with no mirror in it. That
    /// is why one rule serves for positions, directions, velocities and spin (angular velocity)
    /// alike, and why a rotation converts by re-labelling its axis the same way and keeping its angle.
    /// The origin does not move: (0,0,0) is the middle of the play-area floor in both.
    ///
    /// A converted rotation also has its "local" side re-labelled: a SteamVR device points along its
    /// own -Z, and after conversion the same device points along its own +X, as core code expects.
    /// </summary>
    public static class OpenVrSpace
    {
        /// <summary>A position, direction, velocity or angular velocity from SteamVR axes to core axes.</summary>
        public static Vec3 ToCore(Vec3 openVr)
        {
            return new Vec3(-openVr.Z, -openVr.X, openVr.Y);
        }

        /// <summary>The same, the other way.</summary>
        public static Vec3 ToOpenVr(Vec3 core)
        {
            return new Vec3(-core.Y, core.Z, -core.X);
        }

        /// <summary>A rotation from SteamVR axes to core axes.</summary>
        public static Quat ToCore(Quat openVr)
        {
            return new Quat(-openVr.Z, -openVr.X, openVr.Y, openVr.W);
        }

        /// <summary>The same, the other way.</summary>
        public static Quat ToOpenVr(Quat core)
        {
            return new Quat(-core.Y, core.Z, -core.X, core.W);
        }

        /// <summary>
        /// The rotation held in the top-left 3x3 of a SteamVR pose matrix (HmdMatrix34_t), still in
        /// SteamVR axes. mRC is row R, column C; the matrix turns a column vector: out = M * in.
        /// The result is unit length, or "no rotation" if the matrix is broken.
        /// </summary>
        public static Quat RotationFromMatrix(
            float m00, float m01, float m02,
            float m10, float m11, float m12,
            float m20, float m21, float m22)
        {
            float x, y, z, w;
            float trace = m00 + m11 + m22;
            if (trace > 0f)
            {
                float s = MathF.Sqrt(trace + 1f) * 2f;   // 4w
                w = 0.25f * s;
                x = (m21 - m12) / s;
                y = (m02 - m20) / s;
                z = (m10 - m01) / s;
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = MathF.Sqrt(1f + m00 - m11 - m22) * 2f;   // 4x
                w = (m21 - m12) / s;
                x = 0.25f * s;
                y = (m01 + m10) / s;
                z = (m02 + m20) / s;
            }
            else if (m11 > m22)
            {
                float s = MathF.Sqrt(1f + m11 - m00 - m22) * 2f;   // 4y
                w = (m02 - m20) / s;
                x = (m01 + m10) / s;
                y = 0.25f * s;
                z = (m12 + m21) / s;
            }
            else
            {
                float s = MathF.Sqrt(1f + m22 - m00 - m11) * 2f;   // 4z
                w = (m10 - m01) / s;
                x = (m02 + m20) / s;
                y = (m12 + m21) / s;
                z = 0.25f * s;
            }

            float lenSq = x * x + y * y + z * z + w * w;
            if (float.IsNaN(lenSq) || float.IsInfinity(lenSq) || lenSq < 1e-12f) return Quat.Identity;
            float inv = 1f / MathF.Sqrt(lenSq);
            return new Quat(x * inv, y * inv, z * inv, w * inv);
        }

        /// <summary>
        /// A gaze direction from where the ray starts and the point it is aimed at (both already in the
        /// same axes). Returns false, and a forward-pointing direction, when the two points coincide.
        /// </summary>
        public static bool TryDirection(Vec3 origin, Vec3 target, out Vec3 direction)
        {
            Vec3 d = target - origin;
            float lenSq = d.X * d.X + d.Y * d.Y + d.Z * d.Z;
            if (float.IsNaN(lenSq) || float.IsInfinity(lenSq) || lenSq < 1e-12f)
            {
                direction = new Vec3(1f, 0f, 0f);
                return false;
            }
            float inv = 1f / MathF.Sqrt(lenSq);
            direction = new Vec3(d.X * inv, d.Y * inv, d.Z * inv);
            return true;
        }
    }
}
