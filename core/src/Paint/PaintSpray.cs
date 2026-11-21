using System;

namespace Portaballs.Core
{
    /// <summary>One paint droplet flying off a burst. World metres and m/s (+X forward, +Y left, +Z up).</summary>
    public struct Droplet
    {
        public bool Alive;
        /// <summary>Centre now.</summary>
        public Vec3 Position;
        public Vec3 Velocity;
        /// <summary>Radius it is drawn with while flying.</summary>
        public float DrawRadius;
        /// <summary>Radius of the small splat it leaves if it hits a wall.</summary>
        public float SplatRadius;
        public PaintColor Color;
        /// <summary>Seconds since it left the wall.</summary>
        public float Age;
        /// <summary>Seed of its splat (same seed, same splat).</summary>
        public uint Seed;
        /// <summary>Counts up with every droplet made, so the oldest can be found.</summary>
        public int Born;
    }

    /// <summary>A droplet reached a wall in the latest step: where and how, so the game can paint its small splat there.</summary>
    public struct DropletLanding
    {
        /// <summary>Index into the walls given to <see cref="PaintSpray.SetRoom"/>.</summary>
        public int Wall;
        /// <summary>Contact point in that wall's coordinates (metres from its lower-left corner).</summary>
        public Vec2 UV;
        /// <summary>Its velocity along the wall at contact (m/s along U and V).</summary>
        public Vec2 WallVelocity;
        /// <summary>Its speed into the wall at contact (m/s, positive).</summary>
        public float NormalSpeed;
        /// <summary>
        /// The two speeds to stamp its splat with: the ones above times SplatFeel.DropletSplatSpeedScale (a light
        /// droplet spatters less than a ball arriving as fast would). Pass these to the splat.
        /// </summary>
        public Vec2 SplatWallVelocity;
        public float SplatNormalSpeed;
        public float SplatRadius;
        public PaintColor Color;
        public uint Seed;
        /// <summary>Contact point on the wall, world metres.</summary>
        public Vec3 Point;
    }

    /// <summary>
    /// The droplets that fly off when a paint ball bursts on a wall (the tester, 2026-11-19: "some splats coming off, bouncing
    /// off the wall, most of it sticking"; every number is in <see cref="SplatFeel"/>). Engine-free: the game gives it
    /// the room's walls, floor and ceiling, calls <see cref="Burst"/> when a ball bursts and <see cref="Step"/> every
    /// frame, draws the live droplets, and paints a small splat for each <see cref="GetLanding"/>.
    ///
    /// A droplet is a point with a radius on a free-flight path under gravity. It is NOT a physics body and knows
    /// nothing about portals: it never goes through one. Where its path first touches a wall it lands (reported, then
    /// gone); on the floor or the ceiling, or after <see cref="SplatFeel.DropletLifeSeconds"/>, it simply vanishes.
    /// "Wall" means any paint surface given to <see cref="SetRoom"/>: when the room's floor (or ceiling) is one of
    /// them, a droplet lands on it like on any wall, and the bare floor and ceiling heights only catch what falls
    /// past them. A droplet off a wall cannot hit that wall again (it leaves moving away from it, and gravity runs
    /// along the wall); one thrown up off a floor comes back down onto it.
    /// At most <see cref="Capacity"/> fly at once; a new one takes the oldest one's place.
    /// </summary>
    public sealed class PaintSpray
    {
        /// <summary>The most droplets one burst can make, whatever SplatFeel says.</summary>
        public const int MaxPerBurst = 16;

        private readonly Droplet[] _d;
        private readonly DropletLanding[] _landings;
        private readonly Droplet[] _plan = new Droplet[MaxPerBurst];
        private int _landingCount;
        private int _born;
        private WallFrame[] _walls = new WallFrame[0];
        private float _floorZ = float.NegativeInfinity;
        private float _ceilingZ = float.PositiveInfinity;

        /// <param name="capacity">Most droplets alive at once; 0 = SplatFeel.MaxDroplets.</param>
        public PaintSpray(int capacity = 0)
        {
            int n = capacity > 0 ? capacity : Math.Max(1, SplatFeel.MaxDroplets);
            _d = new Droplet[n];
            _landings = new DropletLanding[n];
        }

        /// <summary>Most droplets alive at once.</summary>
        public int Capacity
        {
            get { return _d.Length; }
        }

        /// <summary>Droplets flying right now.</summary>
        public int AliveCount { get; private set; }

        /// <summary>
        /// Since creation: droplets made, reached a wall, ended on the floor or ceiling, ran out of time, were pushed
        /// out by newer ones, or were found behind a wall (should stay 0).
        /// </summary>
        public int TotalMade { get; private set; }
        public int TotalReachedWall { get; private set; }
        public int TotalOnFloorOrCeiling { get; private set; }
        public int TotalTimedOut { get; private set; }
        public int TotalReplaced { get; private set; }
        public int TotalLeftRoom { get; private set; }

        /// <summary>
        /// The room the droplets fly in: its walls (a landing names one by its index here), and the heights of the
        /// floor and the ceiling (world Z, metres). Walls should face into the room.
        /// </summary>
        public void SetRoom(WallFrame[] walls, float floorZ, float ceilingZ)
        {
            _walls = walls ?? new WallFrame[0];
            _floorZ = floorZ;
            _ceilingZ = ceilingZ;
        }

        /// <summary>One slot (0 .. Capacity - 1). Check Alive.</summary>
        public Droplet Get(int slot)
        {
            return _d[slot];
        }

        /// <summary>Droplets that reached a wall in the latest <see cref="Step"/>.</summary>
        public int LandingCount
        {
            get { return _landingCount; }
        }

        public DropletLanding GetLanding(int index)
        {
            return _landings[index];
        }

        /// <summary>Forget every droplet in the air.</summary>
        public void Clear()
        {
            for (int i = 0; i < _d.Length; i++) _d[i].Alive = false;
            AliveCount = 0;
            _landingCount = 0;
        }

        /// <summary>
        /// A ball burst on a wall: throw its droplets. Returns how many were made (0 for a slow ball).
        /// </summary>
        /// <param name="wall">The wall it burst on (its Normal points into the room).</param>
        /// <param name="hitPoint">Where, world metres (it is moved onto the wall plane).</param>
        /// <param name="ballVelocity">The ball's velocity as it arrived, m/s.</param>
        /// <param name="splatRadius">Radius of the ball's own splat, metres.</param>
        /// <param name="color">Paint colour.</param>
        /// <param name="seed">The ball's seed: same seed, same droplets.</param>
        /// <param name="extra">More droplets than usual, for a ball that landed in wet paint (SplatFeel.WetSplashExtraDroplets).</param>
        public int Burst(in WallFrame wall, Vec3 hitPoint, Vec3 ballVelocity, float splatRadius, PaintColor color, uint seed, int extra = 0)
        {
            int n = Plan(wall, hitPoint, ballVelocity, splatRadius, color, seed, _plan, extra);
            for (int k = 0; k < n; k++)
            {
                int slot = FreeSlot();
                if (_d[slot].Alive)
                {
                    TotalReplaced++;
                    AliveCount--;
                }
                Droplet d = _plan[k];
                d.Born = _born++;
                _d[slot] = d;
                AliveCount++;
                TotalMade++;
            }
            return n;
        }

        /// <summary>
        /// One drop let go in mid-air: a drop falling off a ceiling (PaintRunOff.Fell). It flies and lands exactly
        /// like a burst's droplets: a small dab where it reaches a paint surface, on bare surface only.
        /// </summary>
        /// <param name="position">Where its centre starts, world metres (just clear of the surface it left).</param>
        /// <param name="velocity">Its starting velocity, m/s (zero for a drop that simply lets go).</param>
        /// <param name="drawRadius">Radius it is drawn with, metres.</param>
        /// <param name="splatRadius">Radius of the dab it leaves, metres (kept within SplatFeel.DropletSplatRadiusMin..Max).</param>
        public void Drop(Vec3 position, Vec3 velocity, float drawRadius, float splatRadius, PaintColor color, uint seed)
        {
            int slot = FreeSlot();
            if (_d[slot].Alive)
            {
                TotalReplaced++;
                AliveCount--;
            }
            Droplet d = new Droplet();
            d.Alive = true;
            d.Position = position;
            d.Velocity = velocity;
            d.DrawRadius = MathF.Max(0.002f, drawRadius);
            d.SplatRadius = MathF.Max(SplatFeel.DropletSplatRadiusMin, MathF.Min(SplatFeel.DropletSplatRadiusMax, splatRadius));
            d.Color = color;
            d.Age = 0f;
            d.Seed = seed;
            d.Born = _born++;
            _d[slot] = d;
            AliveCount++;
            TotalMade++;
        }

        // A free slot, or the oldest droplet's.
        private int FreeSlot()
        {
            int oldest = 0;
            for (int i = 0; i < _d.Length; i++)
            {
                if (!_d[i].Alive) return i;
                if (_d[i].Born < _d[oldest].Born) oldest = i;
            }
            return oldest;
        }

        /// <summary>
        /// The droplets a burst throws, without keeping them: the maths of <see cref="Burst"/>, for tests.
        /// Writes up to output.Length of them and returns how many. Numbers from <see cref="SplatFeel"/>:
        ///   - how many: DropletsMin..DropletsMax (none below DropletMinImpactSpeed);
        ///   - which way: around a direction between the wall's normal and the mirror of the ball's path
        ///     (DropletReflectBias), spread over a cone of DropletSpreadDegrees, never flatter to the wall than
        ///     DropletMinOut;
        ///   - how fast: DropletSpeedShare of the ball's speed (0.7x..1.3x each), within DropletMinSpeed..DropletMaxSpeed
        ///     and always under 0.7 of the ball's speed;
        ///   - how big: drawn DropletDrawRadiusMin..Max, splat DropletSplatShareMin..Max of the ball's splat radius,
        ///     within DropletSplatRadiusMin..Max (the bigger droplets leave the bigger splats);
        ///   - from where: within DropletStartSpread of the splat radius around the hit, just in front of the wall.
        /// </summary>
        public static int Plan(in WallFrame wall, Vec3 hitPoint, Vec3 ballVelocity, float splatRadius, PaintColor color, uint seed, Droplet[] output, int extra = 0)
        {
            if (output == null || output.Length == 0) return 0;
            float speedIn = ballVelocity.Length();
            if (!(speedIn >= SplatFeel.DropletMinImpactSpeed) || !(splatRadius > 0f)) return 0;

            Rng rng = new Rng(seed * 0x9E3779B1u + 0x85EBCA77u);
            int lo = Math.Max(0, SplatFeel.DropletsMin);
            int hi = Math.Max(lo, SplatFeel.DropletsMax);
            int n = rng.RangeInt(lo, hi + 1) + Math.Max(0, extra);
            if (n > output.Length) n = output.Length;
            if (n > MaxPerBurst) n = MaxPerBurst;
            if (n <= 0) return 0;

            Vec3 N = wall.Normal;
            Vec2 hitUV = wall.ToUV(hitPoint);

            // Mirror the ball's path in the wall. A reading that is not into the wall counts as straight in.
            float vn = Vec3.Dot(ballVelocity, N);
            Vec3 mirror = vn < 0f ? ballVelocity - N * (2f * vn) : N * speedIn;
            Vec3 mirrorDir = MathUtil.SafeNormalize(mirror, N);
            float bias = MathUtil.Saturate(SplatFeel.DropletReflectBias);
            Vec3 axis = MathUtil.SafeNormalize(N * (1f - bias) + mirrorDir * bias, N);
            if (Vec3.Dot(axis, N) < 0.05f) axis = N;

            // Two directions square to the axis, for the spread.
            Vec3 helper = MathF.Abs(axis.Z) < 0.9f ? new Vec3(0f, 0f, 1f) : new Vec3(1f, 0f, 0f);
            Vec3 t1 = Vec3.Normalize(Vec3.Cross(axis, helper));
            Vec3 t2 = Vec3.Cross(axis, t1);

            float spread = MathF.Max(0f, MathF.Min(85f, SplatFeel.DropletSpreadDegrees)) * (MathF.PI / 180f);
            float cosSpread = MathF.Cos(spread);
            float minOut = MathUtil.Saturate(SplatFeel.DropletMinOut);
            float slowest = MathF.Max(0.05f, SplatFeel.DropletMinSpeed);
            float fastest = MathF.Max(slowest, SplatFeel.DropletMaxSpeed);
            float underBall = 0.7f * speedIn;
            float startSpread = MathF.Max(0f, SplatFeel.DropletStartSpread) * splatRadius;

            for (int k = 0; k < n; k++)
            {
                // A direction spread evenly over the cone around the axis.
                float cosT = 1f - rng.NextFloat() * (1f - cosSpread);
                float sinT = MathF.Sqrt(MathF.Max(0f, 1f - cosT * cosT));
                float phi = rng.Range(0f, 2f * MathF.PI);
                Vec3 dir = axis * cosT + (t1 * MathF.Cos(phi) + t2 * MathF.Sin(phi)) * sinT;
                float outward = Vec3.Dot(dir, N);
                if (outward < minOut)
                {
                    // Too flat to the wall (or into it): keep its heading along the wall, tip it out to minOut.
                    Vec3 along = dir - N * outward;
                    along = MathUtil.SafeNormalize(along, t1);
                    dir = N * minOut + along * MathF.Sqrt(MathF.Max(0f, 1f - minOut * minOut));
                }

                float speed = speedIn * MathF.Max(0f, SplatFeel.DropletSpeedShare) * rng.Range(0.7f, 1.3f);
                if (speed < slowest) speed = slowest;
                if (speed > fastest) speed = fastest;
                if (speed > underBall) speed = underBall;

                float size = rng.NextFloat();
                float drawR = MathUtil.Lerp(SplatFeel.DropletDrawRadiusMin, SplatFeel.DropletDrawRadiusMax, size);
                float splatR = splatRadius * MathUtil.Lerp(SplatFeel.DropletSplatShareMin, SplatFeel.DropletSplatShareMax, size);
                splatR = MathF.Max(SplatFeel.DropletSplatRadiusMin, MathF.Min(SplatFeel.DropletSplatRadiusMax, splatR));

                // Start on the splat, but never past the wall's own edges (a burst right in a corner would otherwise
                // start a droplet behind the next wall).
                Vec2 start = hitUV + rng.InsideUnitCircle() * startSpread;
                float edge = drawR + 0.006f;
                start.X = MathF.Max(edge, MathF.Min(wall.Width - edge, start.X));
                start.Y = MathF.Max(edge, MathF.Min(wall.Height - edge, start.Y));
                Droplet d = new Droplet();
                d.Alive = true;
                d.Position = wall.ToWorld(start, drawR + 0.005f);
                d.Velocity = dir * speed;
                d.DrawRadius = drawR;
                d.SplatRadius = splatR;
                d.Color = color;
                d.Age = 0f;
                d.Seed = PaintMath.Hash(k, 77, seed);
                output[k] = d;
            }
            return n;
        }

        /// <summary>
        /// Move every droplet on by dt seconds under gravity (m/s^2, world). Droplets that reach a wall this step are
        /// listed in <see cref="GetLanding"/> (until the next Step) and are gone; those that reach the floor or the
        /// ceiling, leave the room, or have flown their time, are simply gone.
        /// </summary>
        public void Step(float dt, Vec3 gravity)
        {
            _landingCount = 0;
            if (!(dt > 0f) || AliveCount == 0) return;
            float life = MathF.Max(0.05f, SplatFeel.DropletLifeSeconds);

            for (int i = 0; i < _d.Length; i++)
            {
                ref Droplet d = ref _d[i];
                if (!d.Alive) continue;
                d.Age += dt;
                if (d.Age > life)
                {
                    Kill(ref d);
                    TotalTimedOut++;
                    continue;
                }

                float r = d.DrawRadius;
                Vec3 p0 = d.Position, v0 = d.Velocity;
                Vec3 p1 = p0 + v0 * dt + gravity * (0.5f * dt * dt);

                // The first thing the path touches this step: a wall (lands), the floor or ceiling (vanishes).
                float bestT = 2f;
                int bestWall = -1;
                bool bestIsWall = false;
                for (int w = 0; w < _walls.Length; w++)
                {
                    ref readonly WallFrame f = ref _walls[w];
                    float a = f.SignedDistance(p0) - r, b = f.SignedDistance(p1) - r;
                    if (!(b <= 0f) || !(b < a)) continue;   // not reaching it, or moving away from it
                    float t = a > 0f ? a / (a - b) : 0f;
                    if (t >= bestT) continue;
                    Vec3 c = p0 + (p1 - p0) * t - f.Normal * r;
                    if (!f.Contains(f.ToUV(c))) continue;   // past this wall's edge: another surface comes first
                    bestT = t;
                    bestWall = w;
                    bestIsWall = true;
                }
                {
                    float a = p0.Z - r - _floorZ, b = p1.Z - r - _floorZ;
                    if (b <= 0f && b < a)
                    {
                        float t = a > 0f ? a / (a - b) : 0f;
                        if (t < bestT) { bestT = t; bestIsWall = false; bestWall = -1; }
                    }
                }
                {
                    float a = _ceilingZ - (p0.Z + r), b = _ceilingZ - (p1.Z + r);
                    if (b <= 0f && b < a)
                    {
                        float t = a > 0f ? a / (a - b) : 0f;
                        if (t < bestT) { bestT = t; bestIsWall = false; bestWall = -1; }
                    }
                }

                if (bestIsWall)
                {
                    ref readonly WallFrame f = ref _walls[bestWall];
                    float tt = bestT * dt;
                    Vec3 c = p0 + v0 * tt + gravity * (0.5f * tt * tt);
                    Vec3 v = v0 + gravity * tt;
                    Vec3 onWall = c - f.Normal * (f.SignedDistance(c));
                    DropletLanding l = new DropletLanding();
                    l.Wall = bestWall;
                    l.Point = onWall;
                    l.UV = f.ToUV(onWall);
                    l.WallVelocity = new Vec2(Vec3.Dot(v, f.U), Vec3.Dot(v, f.V));
                    l.NormalSpeed = MathF.Max(0f, -Vec3.Dot(v, f.Normal));
                    float k = MathF.Max(0f, SplatFeel.DropletSplatSpeedScale);
                    l.SplatWallVelocity = l.WallVelocity * k;
                    l.SplatNormalSpeed = l.NormalSpeed * k;
                    l.SplatRadius = d.SplatRadius;
                    l.Color = d.Color;
                    l.Seed = d.Seed;
                    if (_landingCount < _landings.Length) _landings[_landingCount++] = l;
                    Kill(ref d);
                    TotalReachedWall++;
                    continue;
                }
                if (bestT <= 1f)
                {
                    Kill(ref d);
                    TotalOnFloorOrCeiling++;
                    continue;
                }

                d.Position = p1;
                d.Velocity = v0 + gravity * dt;

                // Safety: a droplet that is somehow behind a wall (it slipped past a corner seam) is gone.
                for (int w = 0; w < _walls.Length; w++)
                {
                    if (_walls[w].SignedDistance(p1) < -0.05f)
                    {
                        Kill(ref d);
                        TotalLeftRoom++;
                        break;
                    }
                }
            }
        }

        private void Kill(ref Droplet d)
        {
            d.Alive = false;
            AliveCount--;
        }

        /// <summary>
        /// Run the droplet maths once on a made-up room, and a droplet's clearance check on a small throw-away wall,
        /// so the runtime's first-use pause is spent while loading and not in the frame of the first burst.
        /// </summary>
        public static void WarmUp()
        {
            WallFrame front = new WallFrame(new Vec3(2f, 1.5f, 0f), new Vec3(0f, -1f, 0f), new Vec3(0f, 0f, 1f), 3f, 2.5f);
            WallFrame side = new WallFrame(new Vec3(-2f, 1.5f, 0f), new Vec3(1f, 0f, 0f), new Vec3(0f, 0f, 1f), 4f, 2.5f);
            PaintSpray spray = new PaintSpray(8);
            spray.SetRoom(new[] { front, side }, 0f, 2.5f);
            spray.Burst(front, front.ToWorld(new Vec2(0.1f, 1.2f)), new Vec3(6f, 1f, -1f), 0.4f, PaintColor.Orange, 3u);
            for (int i = 0; i < 120 && spray.AliveCount > 0; i++) spray.Step(1f / 60f, new Vec3(0f, 0f, -9.81f));

            PaintSurface s = new PaintSurface(new WallFrame(new Vec3(0f, 0f, 0f), new Vec3(0f, -1f, 0f), new Vec3(0f, 0f, 1f), 1f, 1f));
            Vec2 uv = new Vec2(0.5f, 0.5f);
            if (s.IsClearForSplat(uv, 0.05f, new Vec2(1f, -2f), 2f, SplatFeel.DropletClearMargin))
                s.Splat(PaintColor.Blue, uv, 0.05f, new Vec2(1f, -2f), 9u, 2f);
            s.IsClearForSplat(uv, 0.05f, new Vec2(1f, -2f), 2f, SplatFeel.DropletClearMargin);
        }
    }
}
