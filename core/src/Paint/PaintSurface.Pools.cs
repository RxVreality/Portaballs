using System;

namespace Portaballs.Core
{
    /// <summary>
    /// Paint that left a surface during its latest <see cref="PaintSurface.Step"/>: it ran over the edge it was
    /// running toward (the bottom of a wall), or it fell off a ceiling as a drop. The game reads these after every
    /// Step and passes each one on: run-off to whichever paint surface lies under that point
    /// (<see cref="PaintSurface.Pour"/>), a ceiling drop to the flying droplets.
    /// </summary>
    public struct PaintRunOff
    {
        /// <summary>Where it left, in this surface's own coordinates (metres): on the edge it ran over, or the point it fell from.</summary>
        public Vec2 UV;
        public PaintColor Color;
        /// <summary>How much paint left, cubic metres.</summary>
        public float Volume;
        /// <summary>Half-width of the stream that carried it (metres). For a ceiling drop: 0.</summary>
        public float HalfWidth;
        /// <summary>False: it ran over the edge at UV. True: it fell off the surface at UV as a drop (a ceiling).</summary>
        public bool Fell;
        /// <summary>A repeatable seed for whatever it becomes.</summary>
        public uint Seed;
    }

    // What paint does where it cannot run, and how paint gets from one surface to the next.
    // The tester, 2026-11-19 evening: "... and also pour across the ground as well as it puddles at the bottom. That's
    // something naturally that would happen in physics."
    //
    //   Run-off   A drip head that reaches the bottom edge of its wall hands most of its remaining paint over
    //             (PaintRunOff), when the game has said a surface lies below (HandsOverRunOff).
    //   Puddles   Pour() puts paint that arrived from elsewhere onto this surface. On a floor it grows a puddle:
    //             a round pool that creeps outward from where the paint arrived until its area matches the paint
    //             it was given, and stops. Several streams side by side give a row of pools that merge into one
    //             puddle with an uneven front. POURED PAINT IS LOOSE PAINT: it only ever covers bare surface and
    //             its own colour, never the other colour, so it can never cut into a portal patch (the game also
    //             refuses it on a surface that holds its own colour's patch: see PaintWall.TakeRunOff).
    //   Settling  A ball's splat on a floor does not drip. Its body creeps outward a little and rounds off, like a
    //             wet pool. That is the splat's own paint (part of its patch), so it covers like any thrown paint.
    //   Ceiling   A splat on a ceiling does not drip either. The paint that would have run gathers and falls as a
    //             few drops, quickly at first and then more slowly (PaintRunOff.Fell).
    //
    // Paint is still never made: a pool lays exactly what it was given, and what leaves is counted
    // (PaintBudget.HandedOver).
    public sealed partial class PaintSurface
    {
        // ------------------------------------------------------------------ what left this surface

        /// <summary>
        /// Set by the game when a paint surface lies under this one's lower edge (a wall above a floor). While it is
        /// true, a head reaching that edge reports Settings.RunOffShare of its paint as run-off instead of pooling
        /// all of it at the edge. Off (the default): everything pools at the edge, as it always did.
        /// </summary>
        public bool HandsOverRunOff { get; set; }

        /// <summary>Most run-off reports one Step keeps apart; any more are added to the nearest one of their colour.</summary>
        public const int MaxRunOffPerStep = 24;

        private readonly PaintRunOff[] _runOff = new PaintRunOff[MaxRunOffPerStep];
        private int _runOffCount;

        /// <summary>Paint that left this surface in the latest <see cref="Step"/> (forgotten at the next one).</summary>
        public int RunOffCount
        {
            get { return _runOffCount; }
        }

        public PaintRunOff GetRunOff(int index)
        {
            return _runOff[index];
        }

        private void ReportRunOff(float u, float v, int color, double volume, float halfWidth, bool fell, uint seed)
        {
            if (!(volume > 0.0)) return;
            _handedOver += volume;
            if (_runOffCount == _runOff.Length)
            {
                // Full (it never should be): add it to the nearest report of its kind, so no paint is lost.
                int best = -1;
                float bestD = float.MaxValue;
                for (int k = 0; k < _runOffCount; k++)
                {
                    if ((int)_runOff[k].Color != color || _runOff[k].Fell != fell) continue;
                    float dx = _runOff[k].UV.X - u, dy = _runOff[k].UV.Y - v;
                    float d = dx * dx + dy * dy;
                    if (d < bestD) { bestD = d; best = k; }
                }
                if (best < 0) best = _runOffCount - 1;
                _runOff[best].Volume += (float)volume;
                return;
            }
            ref PaintRunOff r = ref _runOff[_runOffCount++];
            r.UV = new Vec2(u, v);
            r.Color = (PaintColor)color;
            r.Volume = (float)volume;
            r.HalfWidth = halfWidth;
            r.Fell = fell;
            r.Seed = seed;
        }

        // ------------------------------------------------------------------ pools: puddles and settling splats

        private struct Pool
        {
            public float X, Y;          // centre (surface metres)
            public float R, LastR;      // radius now, and as last painted
            public float Target;        // radius it is creeping toward
            public float Tau;           // it covers about two thirds of the way left in this many seconds
            public float Inside;        // share of its disc that lies on the surface (0.5 on an edge, 0.25 in a corner)
            // A puddle poured at an edge is three tongues that run out across the surface: a main one from its
            // centre and a smaller one from either side of it. Each: direction (unit), length in radii, tip radius
            // as a share of its root radius. All zero = a plain round pool.
            public bool Tongues;
            public float Ex, Ey;        // along the edge it arrived on (unit)
            public float D0x, D0y, S0, T0;
            public float D1x, D1y, S1, T1;
            public float D2x, D2y, S2, T2;
            public float Shape;         // its area on the surface is about Shape * R * R
            public double Pending;      // paint it has been given and not laid yet (cubic metres)
            public double Total;        // all the paint it has ever been given
            public float Age;           // seconds since it was started
            public int Color;
            public bool Loose;          // poured paint: covers bare surface only
            public bool Active;         // still growing or still holding paint to lay
            public int Born;
        }

        /// <summary>Most pools one surface remembers. A new stream beyond that feeds the nearest pool of its colour.</summary>
        public const int MaxPools = 48;

        private readonly Pool[] _pools = new Pool[MaxPools];
        private int _poolCount, _poolsActive, _poolBorn;

        /// <summary>Puddles and settling splats still creeping outward right now.</summary>
        public int ActivePoolCount
        {
            get { return _poolsActive; }
        }

        /// <summary>
        /// Paint arrives on this surface from elsewhere (run-off from the wall above). Returns true when it was taken.
        ///
        /// On a floor it feeds a puddle that starts at <paramref name="uv"/> (clamped onto the surface): the puddle
        /// creeps outward over the next seconds until its area times Settings.PuddleFilm equals the paint it has been
        /// given, and never beyond Settings.PuddleMaxReach. A stream arriving on an edge (the foot of a wall) makes a
        /// puddle of three tongues that run out across the floor from there, each puddle a little different (from
        /// the seed), so its front is uneven like spilt paint, not a ruled half-circle. Paint arriving within
        /// Settings.PuddleStreamSpacing of an earlier stream of its colour feeds that one. On a surface where paint
        /// runs (a wall, a slope) it is simply laid where it arrived as a small dab.
        ///
        /// Poured paint is loose paint: it covers bare surface and its own colour only. Where the other colour lies
        /// it leaves that alone, so it can never cut into the other colour's portal patch. (Whether a surface that
        /// holds its OWN colour's patch should take it at all is the game's rule: PaintWall.TakeRunOff refuses.)
        /// </summary>
        /// <param name="color">Paint colour.</param>
        /// <param name="uv">Where it arrives, this surface's metres. Usually on an edge: the foot of the wall it ran down.</param>
        /// <param name="volume">How much, cubic metres (PaintRunOff.Volume).</param>
        /// <param name="halfWidth">Half-width of the stream that brought it, metres: the puddle starts that wide.</param>
        /// <param name="seed">Same seed, same puddle shape (PaintRunOff.Seed).</param>
        public bool Pour(PaintColor color, Vec2 uv, float volume, float halfWidth, uint seed = 0u)
        {
            if (!(volume > 0f)) return false;
            float u = MathF.Min(MathF.Max(uv.X, 0f), _wall.Width), v = MathF.Min(MathF.Max(uv.Y, 0f), _wall.Height);
            int c = (int)color;
            float r0 = MathF.Max(halfWidth, MinDripRadius);
            _deposited += volume;

            if (_runs)
            {
                PaintCapsule(c, u, v, r0, u, v, r0, volume, true);
                return true;
            }

            // The stream this belongs to: the nearest pool of loose paint of its colour, if one is close enough.
            float reach = MathF.Max(0.5f * _cellMin, _s.PuddleStreamSpacing);
            int found = -1, nearest = -1;
            float foundD = reach * reach, nearestD = float.MaxValue;
            for (int k = 0; k < _poolCount; k++)
            {
                ref Pool q = ref _pools[k];
                if (!q.Loose || q.Color != c) continue;
                float dx = q.X - u, dy = q.Y - v;
                float d = dx * dx + dy * dy;
                if (d <= foundD) { foundD = d; found = k; }
                if (d < nearestD) { nearestD = d; nearest = k; }
            }
            if (found < 0)
            {
                bool fresh = true;
                found = NewPool();
                if (found < 0 && nearest >= 0)
                {
                    found = nearest;                   // no room for another: the nearest one of its colour takes it
                    fresh = false;
                }
                if (found < 0) found = OldestPool();   // not one of its colour: the oldest pool makes way
                if (fresh)
                {
                    ref Pool n = ref _pools[found];
                    FinishPool(ref n);
                    n = default;
                    n.X = u;
                    n.Y = v;
                    n.R = r0 * 0.5f;
                    n.LastR = 0f;
                    n.Tau = MathF.Max(0.05f, _s.PuddleGrowSeconds);
                    // How much of a disc centred here lies on the surface: half on an edge, a quarter in a corner.
                    float edge = 2f * _cellMin;
                    float inU = u < edge ? 1f : (u > _wall.Width - edge ? -1f : 0f);    // which way is onto the surface
                    float inV = v < edge ? 1f : (v > _wall.Height - edge ? -1f : 0f);
                    n.Inside = (inU != 0f ? 0.5f : 1f) * (inV != 0f ? 0.5f : 1f);
                    n.Shape = MathF.PI * n.Inside;
                    if (inU != 0f || inV != 0f)
                    {
                        // Arrived on an edge: tongues running out across the surface. The main one is turned up to
                        // about 25 degrees either way and is 0.7 to 1.4 radii long; the two side ones lean outward
                        // 20 to 50 degrees and are shorter. Each ends in a smaller round tip.
                        float len = MathF.Sqrt(inU * inU + inV * inV);
                        float ix = inU / len, iy = inV / len;
                        uint sd = seed * 0x9E3779B1u + (uint)found * 0x85EBCA77u + 0x27D4EB2Fu;
                        n.Tongues = true;
                        n.Ex = -iy;
                        n.Ey = ix;
                        Turned(ix, iy, (PaintMath.Hash01(1, 0, sd) - 0.5f) * 0.9f, out n.D0x, out n.D0y);
                        n.S0 = 0.7f + 0.7f * PaintMath.Hash01(2, 0, sd);
                        n.T0 = 0.5f + 0.3f * PaintMath.Hash01(3, 0, sd);
                        Turned(ix, iy, 0.35f + 0.55f * PaintMath.Hash01(4, 0, sd), out n.D1x, out n.D1y);
                        n.S1 = 0.3f + 0.9f * PaintMath.Hash01(5, 0, sd);
                        n.T1 = 0.5f + 0.3f * PaintMath.Hash01(6, 0, sd);
                        Turned(ix, iy, -(0.35f + 0.55f * PaintMath.Hash01(7, 0, sd)), out n.D2x, out n.D2y);
                        n.S2 = 0.3f + 0.9f * PaintMath.Hash01(8, 0, sd);
                        n.T2 = 0.5f + 0.3f * PaintMath.Hash01(9, 0, sd);
                        // Each tongue: a root half-disc, its two sides, its tip's half-disc. The side ones are
                        // SideTongue of the size and half hidden under the main one.
                        float a0 = 0.5f * MathF.PI + n.S0 * (1f + n.T0) + 0.5f * MathF.PI * n.T0 * n.T0;
                        float a1 = 0.5f * MathF.PI + n.S1 * (1f + n.T1) + 0.5f * MathF.PI * n.T1 * n.T1;
                        float a2 = 0.5f * MathF.PI + n.S2 * (1f + n.T2) + 0.5f * MathF.PI * n.T2 * n.T2;
                        // Measured: the three overlap (and lose a little off the edge) so that the puddle really
                        // covers about 0.55 of that sum.
                        n.Shape = 0.55f * (a0 + 0.5f * SideTongue * SideTongue * (a1 + a2)) * (n.Inside < 0.5f ? 0.6f : 1f);
                    }
                    n.Color = c;
                    n.Loose = true;
                    n.Born = _poolBorn++;
                }
            }

            ref Pool p = ref _pools[found];
            p.Pending += volume;
            p.Total += volume;
            float film = MathF.Max(1e-5f, _s.PuddleFilm);
            float full = MathF.Sqrt((float)(p.Total / (film * p.Shape)));
            float most = MathF.Max(r0, p.Tongues ? _s.PuddleMaxReach / (p.S0 + p.T0) : _s.PuddleMaxReach);   // a tongue's far end is S + T radii out
            if (full > most) full = most;
            if (full < r0) full = r0;
            if (full > p.Target) p.Target = full;
            if (!p.Active)
            {
                p.Active = true;
                _poolsActive++;
            }
            return true;
        }

        /// <summary>
        /// A ball's splat has landed on a floor: part of its paint settles outward. A handful of round bulges start
        /// inside the body near its rim and creep out past it, each a different amount, so the notches in the outline
        /// fill, the body rounds off and ends up a little bigger, and it still looks like thrown paint (not a ruled
        /// disc). Returns the paint set aside for that (it comes out of the splat's film).
        /// </summary>
        private double StartSettling(ref Rng rng, int ci, float R, float cx, float cy, Vec2 fwd, Vec2 side, float stretch, float across, double splatVolume)
        {
            float span = _s.DripFullSplatRadius - _s.DripMinSplatRadius;
            float f = span > 1e-6f ? MathUtil.Saturate((R - _s.DripMinSplatRadius) / span) : (R >= _s.DripFullSplatRadius ? 1f : 0f);
            float spread = MathF.Max(0f, _s.FloorSettleSpread);
            // Small splats (a droplet's dab among them) stay exactly as they land.
            if (f <= 0f || spread <= 0f) return 0.0;
            double volume = MathUtil.Saturate(_s.FloorSettleShare) * MathF.Sqrt(f) * splatVolume;
            const int Bulges = 6;
            double given = 0.0;
            float turn = rng.Range(0f, 2f * MathF.PI);
            for (int b = 0; b < Bulges; b++)
            {
                int k = NewPool();
                if (k < 0) break;   // every pool is busy: this splat settles with fewer bulges
                // Round the body, evenly spaced with some jitter, on a ring inside it (the body is about 0.8 R,
                // drawn out to `stretch` along the throw and squeezed to `across` over it by a glancing hit).
                float ang = turn + (b + rng.Range(-0.3f, 0.3f)) * (2f * MathF.PI / Bulges);
                float a = MathF.Cos(ang) * 0.5f * R * stretch, o = MathF.Sin(ang) * 0.5f * R * across;
                ref Pool p = ref _pools[k];
                FinishPool(ref p);
                p = default;
                p.X = cx + fwd.X * a + side.X * o;
                p.Y = cy + fwd.Y * a + side.Y * o;
                p.R = 0.28f * R * across;
                p.LastR = p.R;
                p.Target = R * across * (0.36f + spread * MathF.Sqrt(f) * rng.Range(0.5f, 1.5f));
                p.Tau = MathF.Max(0.05f, _s.FloorSettleSeconds) * rng.Range(0.8f, 1.3f);
                p.Inside = 1f;
                p.Shape = MathF.PI;
                p.Pending = volume / Bulges;
                p.Total = p.Pending;
                p.Color = ci;
                p.Loose = false;
                p.Born = _poolBorn++;
                p.Active = true;
                _poolsActive++;
                given += p.Pending;
            }
            return given;
        }

        private int NewPool()
        {
            if (_poolCount < _pools.Length)
            {
                _pools[_poolCount] = default;   // whatever an earlier wipe left in the slot
                return _poolCount++;
            }
            // Reuse one that has finished, the oldest first.
            int best = -1;
            for (int k = 0; k < _poolCount; k++)
            {
                if (_pools[k].Active) continue;
                if (best < 0 || _pools[k].Born < _pools[best].Born) best = k;
            }
            return best;
        }

        private int OldestPool()
        {
            int best = 0;
            for (int k = 1; k < _poolCount; k++)
            {
                if (_pools[k].Born < _pools[best].Born) best = k;
            }
            return best;
        }

        /// <summary>Lay whatever a pool still holds where it is now, and stop it (its slot is about to be reused).</summary>
        private void FinishPool(ref Pool p)
        {
            if (p.Active)
            {
                if (p.Pending > 0.0) PaintPool(ref p, p.Pending);
                _poolsActive--;
            }
            p.Pending = 0.0;
            p.Active = false;
        }

        // A puddle's two side tongues: their size as a share of the main one, and how far along the edge they start (in radii).
        private const float SideTongue = 0.75f, SideOffset = 0.55f;

        private static void Turned(float x, float y, float angle, out float ox, out float oy)
        {
            float c = MathF.Cos(angle), s = MathF.Sin(angle);
            ox = x * c - y * s;
            oy = x * s + y * c;
        }

        /// <summary>Paint a pool at its present size (a disc, or the three tongues of a poured puddle) and lay this much paint on it.</summary>
        private void PaintPool(ref Pool p, double volume)
        {
            float wetness = PoolWetness(ref p);
            float R = p.R;
            if (!p.Tongues)
            {
                PaintCapsule(p.Color, p.X, p.Y, R, p.X, p.Y, R, volume, p.Loose, wetness);
                return;
            }
            float r1 = R * SideTongue;
            float x1 = p.X + p.Ex * (R * SideOffset), y1 = p.Y + p.Ey * (R * SideOffset);
            float x2 = p.X - p.Ex * (R * SideOffset), y2 = p.Y - p.Ey * (R * SideOffset);
            PaintCapsule(p.Color, p.X, p.Y, R, p.X + p.D0x * (R * p.S0), p.Y + p.D0y * (R * p.S0), R * p.T0, volume * 0.6, p.Loose, wetness);
            PaintCapsule(p.Color, x1, y1, r1, x1 + p.D1x * (r1 * p.S1), y1 + p.D1y * (r1 * p.S1), r1 * p.T1, volume * 0.2, p.Loose, wetness);
            PaintCapsule(p.Color, x2, y2, r1, x2 + p.D2x * (r1 * p.S2), y2 + p.D2y * (r1 * p.S2), r1 * p.T2, volume * 0.2, p.Loose, wetness);
        }

        // How wet the paint a pool lays is: it dries along with the paint that made it, but poured paint is never
        // laid less than half wet (it only just arrived).
        private float PoolWetness(ref Pool p)
        {
            if (!_s.TrailDriesWithPaint) return 1f;
            float w = 1f - p.Age / MathF.Max(0.1f, _s.DryTime);
            float least = p.Loose ? 0.5f : 0f;
            return w < least ? least : w;
        }

        /// <summary>One frame of every growing pool: creep outward, repaint when it has grown enough to see.</summary>
        private void StepPools(float dt)
        {
            float minStroke = MathF.Max(0.01f, _s.StrokeMinTexels) * _cellMin;
            const float SlowestCreep = 0.02f;   // m/s: the last stretch is finished at this speed instead of never
            for (int k = 0; k < _poolCount; k++)
            {
                ref Pool p = ref _pools[k];
                if (!p.Active) continue;
                p.Age += dt;
                float gap = p.Target - p.R;
                float step = gap * (1f - MathF.Exp(-dt / p.Tau));
                float least = SlowestCreep * dt;
                if (step < least) step = least;
                bool done = step >= gap;
                p.R = done ? p.Target : p.R + step;
                if (!done && p.R - p.LastR < minStroke) continue;

                // Lay the paint in step with the area gained, so the film is even and all of it is down at the end.
                double share = 1.0;
                if (!done)
                {
                    double whole = (double)p.Target * p.Target - (double)p.LastR * p.LastR;
                    double gained = (double)p.R * p.R - (double)p.LastR * p.LastR;
                    share = whole > 1e-12 ? Math.Min(1.0, gained / whole) : 1.0;
                }
                double lay = p.Pending * share;
                PaintPool(ref p, lay);
                p.Pending -= lay;
                p.LastR = p.R;
                if (done)
                {
                    p.Pending = 0.0;
                    p.Active = false;
                    _poolsActive--;
                }
            }
        }

        // ------------------------------------------------------------------ ceilings: drops that gather and fall

        private struct HangingDrop
        {
            public float X, Y;
            public float Wait;      // seconds until it falls
            public float Volume;
            public int Color;
            public uint Seed;
        }

        /// <summary>Most drops waiting to fall from one surface.</summary>
        public const int MaxHangingDrops = 32;

        private readonly HangingDrop[] _hang = new HangingDrop[MaxHangingDrops];
        private int _hangCount;

        /// <summary>Drops gathered on this ceiling that have not fallen yet.</summary>
        public int HangingDropCount
        {
            get { return _hangCount; }
        }

        /// <summary>
        /// A splat has landed on a ceiling: the paint that would have run on a wall gathers at a few points inside the
        /// body and falls from them one by one, the first soon and each later one after a longer wait. Only drops due
        /// while the paint is still wet (Settings.DryTime) are made. Returns the paint set aside for them.
        /// </summary>
        private double StartHangingDrops(ref Rng rng, int ci, float R, float cx, float cy, double splatVolume)
        {
            float span = _s.DripFullSplatRadius - _s.DripMinSplatRadius;
            float f = span > 1e-6f ? MathUtil.Saturate((R - _s.DripMinSplatRadius) / span) : (R >= _s.DripFullSplatRadius ? 1f : 0f);
            if (f <= 0f) return 0.0;
            int lo = Math.Max(0, _s.CeilingDropsMin), hi = Math.Max(lo, _s.CeilingDropsMax);
            int n = lo + (int)MathF.Round((hi - lo) * f);
            if (n <= 0) return 0.0;
            double each = _s.DripShareMax * f * splatVolume / n;
            float wait = MathF.Max(0.05f, _s.CeilingFirstDropSeconds) * rng.Range(0.7f, 1.3f);
            float gap = MathF.Max(0.05f, _s.CeilingFirstDropSeconds) * MathF.Max(1f, _s.CeilingDropGapGrowth);
            float latest = 0.95f * MathF.Max(0.1f, _s.DryTime);
            double given = 0.0;
            for (int k = 0; k < n && wait <= latest && _hangCount < _hang.Length; k++)
            {
                Vec2 at = new Vec2(cx, cy) + rng.InsideUnitCircle() * (0.6f * R);
                ref HangingDrop d = ref _hang[_hangCount++];
                d.X = MathF.Min(MathF.Max(at.X, 0f), _wall.Width);
                d.Y = MathF.Min(MathF.Max(at.Y, 0f), _wall.Height);
                d.Wait = wait;
                d.Volume = (float)each;
                d.Color = ci;
                d.Seed = rng.NextUInt();
                given += d.Volume;
                wait += gap * rng.Range(0.8f, 1.2f);
                gap *= MathF.Max(1f, _s.CeilingDropGapGrowth);
            }
            return given;
        }

        private void StepHangingDrops(float dt)
        {
            int w = 0;
            for (int k = 0; k < _hangCount; k++)
            {
                ref HangingDrop d = ref _hang[k];
                d.Wait -= dt;
                if (d.Wait <= 0f)
                {
                    ReportRunOff(d.X, d.Y, d.Color, d.Volume, 0f, true, d.Seed);
                    continue;
                }
                if (w != k) _hang[w] = d;
                w++;
            }
            _hangCount = w;
        }

        // ------------------------------------------------------------------ housekeeping

        /// <summary>Paint held by pools and hanging drops and not laid or fallen yet (it counts with the drips' paint).</summary>
        private double PaintWaitingInPools()
        {
            double sum = 0.0;
            for (int k = 0; k < _poolCount; k++) sum += _pools[k].Pending;
            for (int k = 0; k < _hangCount; k++) sum += _hang[k].Volume;
            return sum;
        }

        private void ClearPoolsAndDrops()
        {
            _poolCount = 0;
            _poolsActive = 0;
            _hangCount = 0;
            _runOffCount = 0;
        }

        /// <summary>
        /// Erasing: pools and hanging drops of that colour (-1 = both) whose centre is within the disc go, with the
        /// paint they had not laid yet. A radius of float.MaxValue means the whole surface.
        /// </summary>
        private void ErasePoolsAndDrops(int color, Vec2 center, float radius)
        {
            bool everywhere = radius >= float.MaxValue * 0.5f;
            float r2 = everywhere ? 0f : radius * radius;
            int w = 0;
            for (int k = 0; k < _poolCount; k++)
            {
                ref Pool p = ref _pools[k];
                bool hit = color < 0 || p.Color == color;
                if (hit && !everywhere)
                {
                    float dx = p.X - center.X, dy = p.Y - center.Y;
                    hit = dx * dx + dy * dy <= r2;
                }
                if (hit)
                {
                    _erased += p.Pending;
                    if (p.Active) _poolsActive--;
                    continue;
                }
                if (w != k) _pools[w] = p;
                w++;
            }
            _poolCount = w;

            w = 0;
            for (int k = 0; k < _hangCount; k++)
            {
                ref HangingDrop d = ref _hang[k];
                bool hit = color < 0 || d.Color == color;
                if (hit && !everywhere)
                {
                    float dx = d.X - center.X, dy = d.Y - center.Y;
                    hit = dx * dx + dy * dy <= r2;
                }
                if (hit)
                {
                    _erased += d.Volume;
                    continue;
                }
                if (w != k) _hang[w] = d;
                w++;
            }
            _hangCount = w;
        }
    }
}
