using System;

namespace Portaballs.Core
{
    /// <summary>
    /// Small deterministic random generator (xorshift32). Same seed gives the same sequence on every
    /// machine, which the tests rely on. It is a mutable struct: pass it by ref.
    /// </summary>
    public struct Rng
    {
        private uint _state;

        public Rng(uint seed)
        {
            _state = seed == 0u ? 0x9E3779B9u : seed;
        }

        public uint NextUInt()
        {
            uint s = _state;
            s ^= s << 13;
            s ^= s >> 17;
            s ^= s << 5;
            _state = s;
            return s;
        }

        /// <summary>Uniform in [0, 1).</summary>
        public float NextFloat()
        {
            return (NextUInt() >> 8) * (1f / 16777216f);
        }

        /// <summary>Uniform in [min, max).</summary>
        public float Range(float min, float max)
        {
            return min + (max - min) * NextFloat();
        }

        /// <summary>Uniform integer in [min, maxExclusive).</summary>
        public int RangeInt(int min, int maxExclusive)
        {
            if (maxExclusive <= min) return min;
            return min + (int)(NextUInt() % (uint)(maxExclusive - min));
        }

        /// <summary>Normal distribution, mean 0, standard deviation 1 (Box-Muller).</summary>
        public float Gaussian()
        {
            float u1 = MathF.Max(NextFloat(), 1e-7f);
            float u2 = NextFloat();
            return MathF.Sqrt(-2f * MathF.Log(u1)) * MathF.Cos(2f * MathF.PI * u2);
        }

        /// <summary>Uniform point inside the unit circle.</summary>
        public Vec2 InsideUnitCircle()
        {
            float angle = NextFloat() * 2f * MathF.PI;
            float radius = MathF.Sqrt(NextFloat());
            return new Vec2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
        }

        /// <summary>Uniform direction on the unit sphere.</summary>
        public Vec3 OnUnitSphere()
        {
            float z = Range(-1f, 1f);
            float angle = NextFloat() * 2f * MathF.PI;
            float r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
            return new Vec3(r * MathF.Cos(angle), r * MathF.Sin(angle), z);
        }
    }
}
