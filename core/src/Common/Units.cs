namespace Portaballs.Core
{
    /// <summary>
    /// Conventions for everything under core/src:
    ///   Units: metres, seconds, radians. Timestamps are double seconds on one monotonic clock.
    ///   Axes:  Source 2 layout, right-handed: +X forward, +Y left, +Z up.
    ///   Rules: no engine types, no System.IO, no threads, no reflection, no unsafe code, so the
    ///          same files compile inside s&box's sandbox and in the plain .NET test projects.
    ///          Randomness only through <see cref="Rng"/> with an explicit seed.
    /// </summary>
    public static class Units
    {
        /// <summary>Source 2 world units (inches) per metre. Multiply metres by this at the s&box boundary.</summary>
        public const float SourceUnitsPerMeter = 39.3701f;

        /// <summary>Metres per Source 2 world unit.</summary>
        public const float MetersPerSourceUnit = 1f / SourceUnitsPerMeter;

        /// <summary>World up.</summary>
        public static readonly Vec3 Up = new Vec3(0f, 0f, 1f);

        /// <summary>Default gravity in m/s^2 (points down, -Z).</summary>
        public static readonly Vec3 Gravity = new Vec3(0f, 0f, -9.81f);
    }
}
