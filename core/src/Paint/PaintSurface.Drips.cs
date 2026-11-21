using System;

namespace Portaballs.Core
{
    // Sag and drips: the paint along a splat's lower rim runs down the wall.
    //
    // Each splat hands part of its paint to a row of wide "sag heads" along its lower rim. They overlap, so as they
    // creep down together the rim sags as one sheet whose lower edge ends up scalloped (each head sags a different
    // distance). When a sag head has used its paint it stops, and the extra paint it was carrying runs on as one or
    // two narrow drips. A drip paints a rounded trail and leaves paint on it, so it slowly empties: it gets a little
    // thinner and slower, wanders slightly sideways, merges with a drip it runs into, and stops when it has too
    // little paint left or the paint has dried, leaving a small round bead. Paint is never made: what the heads carry
    // plus what is on the wall always equals what the splats put down.
    //
    // "Down" is wherever gravity pulls along THIS surface (PaintSurface.GravityAlong; _runX/_runY is that direction,
    // _sideX/_sideY across it). On an upright wall that is straight down V, and every formula below then gives exactly
    // what it gave when "down" was written as minus V. On a slope the paint runs the same way, more slowly. On a
    // floor or a ceiling there are no heads at all (see PaintSurface.Pools.cs for what happens there instead).
    //
    // Since the evening of 2026-11-19 (the tester: "more than three seconds for it to drip") a head starts as fast as
    // before but its paint stays wet for Settings.DryTime real seconds and it slows the way drying paint does: fast
    // at first, then a long creep (Settings.SlowDownCurve), leaving a thinning trail (Settings.TrailThinEnd).
    public sealed partial class PaintSurface
    {
        private struct DripHead
        {
            public float X, Y;              // where the head is (wall metres)
            public float LastX, LastY, LastR; // where its trail was last painted, and the trail half-width there
            public float Px, Py;            // where it would be had it run dead straight; sideways wander is measured from here
            public float V;                 // paint it is using up as it moves (cubic metres)
            public float V0, R0;            // paint and half-width it started with
            public float Carry;             // sag heads only: paint that runs on as drips once the sag stops
            public float DripR;             // sag heads only: half-width of those drips
            public float Lane;              // sag heads only: width of wall it covers per metre run (uses Lane x film of paint)
            public float BaseSpeed;         // full running speed (m/s)
            public float Drift;             // sideways drift per metre run (curtain edges creep inward)
            public float Wander;            // size of the sideways wiggle (m)
            public float Shrink;            // sag heads only: width left at the end of the run, as a fraction (curtains stay wide)
            public float Pending;           // paint used up on the trail since the last stroke, not yet laid down
            public float Dist;              // how far it has run (m)
            public float Age;               // seconds since its splat landed
            public float SpeedMul;          // its own small speed difference, so heads don't move in lockstep
            public float Film;              // share of the usual trail film it leaves (1; less for paint that landed on wet paint)
            public float Phase;             // where its wander pattern starts
            public uint Seed;               // its wander pattern
            public int Color;
            public bool Sheet;              // true for a wide sag head, false for a narrow drip
            public bool Alive;
        }

        private readonly DripHead[] _heads;
        private int _headCount;

        // Small fixed scratch for choosing rim heads (no allocation per splat).
        private const int MaxRimHeads = 40;
        private readonly float[] _rimU = new float[MaxRimHeads];
        private readonly float[] _rimV = new float[MaxRimHeads];
        private readonly float[] _rimW = new float[MaxRimHeads];
        private readonly float[] _rimSag = new float[MaxRimHeads];
        private readonly float[] _rimP = new float[MaxRimHeads];
        private readonly float[] _rimOff = new float[MaxRimHeads];   // how far across the splat's centre line (metres)

        /// <summary>Sag heads and drips moving right now.</summary>
        public int ActiveDripCount
        {
            get { return _headCount; }
        }

        /// <summary>Read one moving head (0 .. ActiveDripCount - 1), for debug drawing.</summary>
        public void GetDrip(int index, out Vec2 position, out float halfWidth, out PaintColor color, out float paintVolume)
        {
            ref DripHead h = ref _heads[index];
            position = new Vec2(h.X, h.Y);
            halfWidth = HeadRadius(ref h);
            color = (PaintColor)h.Color;
            paintVolume = h.V + h.Carry;
        }

        private float MinDripRadius
        {
            get { return MathF.Max(_s.MinDripRadius, 0.75f * _cellMin); }
        }

        /// <summary>Current half-width: a sag head narrows a little as it ends (rounding its scallop); a drip thins slowly as it empties.</summary>
        private float HeadRadius(ref DripHead h)
        {
            float frac = h.V0 > 0f ? h.V / h.V0 : 0f;
            if (frac < 0f) frac = 0f;
            float r = h.Sheet ? h.R0 * (h.Shrink + (1f - h.Shrink) * frac) : h.R0 * MathF.Pow(frac, 0.2f);
            float rmin = MinDripRadius;
            return r < rmin ? rmin : r;
        }

        /// <summary>How much of CurtainPaint a splat of radius R brings (0 for small splats or CurtainStrength 0).</summary>
        private float CurtainShare(float R)
        {
            float span = _s.CurtainFullRadius - _s.CurtainMinRadius;
            float f = span > 1e-6f ? MathUtil.Saturate((R - _s.CurtainMinRadius) / span) : (R >= _s.CurtainFullRadius ? 1f : 0f);
            return MathUtil.Saturate(_s.CurtainStrength) * f;
        }

        /// <summary>
        /// Choose sag heads along the splat's lower rim and give them their paint: a short sag for each, the curtain
        /// paint (more in the middle than at the sides), and the paint that later runs on as drips.
        /// Returns the paint handed out.
        /// </summary>
        private double SpawnRimHeads(ref Rng rng, int ci, float R, float cx, float cy, Vec2 fwd, Vec2 side,
            float stretch, float across, double splatVolume, double curtainVolume, int x0, int y0, int x1, int y1)
        {
            float span = _s.DripFullSplatRadius - _s.DripMinSplatRadius;
            float f = span > 1e-6f ? MathUtil.Saturate((R - _s.DripMinSplatRadius) / span) : (R >= _s.DripFullSplatRadius ? 1f : 0f);
            float cs = curtainVolume > 0.0 ? MathUtil.Saturate(_s.CurtainStrength) : 0f;
            // With a curtain, the fringe drips start much lower down, so they get less paint (else they all hit the floor).
            double want = _s.DripShareMax * (1f - 0.75f * cs) * f * splatVolume;
            if (want <= 0.0 && curtainVolume <= 0.0) return 0.0;

            float rmin = MinDripRadius;
            float rs = MathF.Max(rmin, _s.HeadWidth * R);
            float bodyR = 0.86f * R;
            // The splat's half-width ACROSS the way paint runs (on a wall: along U).
            float ex = stretch * (fwd.X * _sideX + fwd.Y * _sideY), sx = across * (side.X * _sideX + side.Y * _sideY);
            bool upright = _runX == 0f && _runY == -1f;
            float halfW = bodyR * MathF.Sqrt(ex * ex + sx * sx) * (0.9f + 0.08f * MathUtil.Saturate(_s.CurtainStrength));
            float spacing = MathF.Max(_s.HeadSpacing * R, 1.2f * rmin);
            int n = (int)MathF.Round(2f * halfW / spacing);
            if (n < 1) n = 1;
            if (n > MaxRimHeads) n = MaxRimHeads;
            float step = 2f * halfW / n;
            // Width of wall each head is responsible for covering: a lone sag uses its own width, a curtain only the
            // gap to its neighbours (they overlap), so the curtain's paint really covers the curtain's area.
            float lane = (2f * rs * 0.85f) * (1f - cs) + step * cs;
            float sheetSpeed = _s.SagSpeed * (1f - cs) + _s.CurtainSpeed * cs;

            int startRow = (int)MathF.Floor(cy / TexelSizeV - 0.5f);
            if (startRow < y0) startRow = y0;
            if (startRow > y1) startRow = y1;

            // Find the rim under each column, how far that part sags, its share of the curtain, and of the drips.
            float sumW = 0f, sumP = 0f;
            double sheetTotal = 0.0;
            float sagMax = R * _s.SagDistance * MathF.Sqrt(f);
            for (int k = 0; k < n; k++)
            {
                float jitter = rng.Range(-0.25f, 0.25f);
                float u = cx - halfW + (k + 0.5f) * step + jitter * step;
                float w = MathF.Pow(rng.Range(0.03f, 1f), MathF.Max(0f, _s.DripUnevenness));
                float sag = sagMax * rng.Range(0.15f, 1f);
                float xn = halfW > 0f ? (u - cx) / halfW : 0f;
                float prof = MathF.Max(0.05f, 1f - _s.CurtainEdgeFalloff * xn * xn) * rng.Range(0.82f, 1.12f);
                _rimW[k] = -1f;
                _rimOff[k] = u - cx;
                float depth;
                if (upright)
                {
                    // Straight down a wall: the rim is the lowest covered texel of this column under the centre.
                    int col = (int)MathF.Floor(u / TexelSizeU - 0.5f);
                    if (col < x0 || col > x1) continue;
                    if (_cov[col + startRow * Width] < 0.5f) continue;
                    int j = startRow;
                    while (j - 1 >= y0 && _cov[col + (j - 1) * Width] >= 0.5f) j--;
                    float rim = j * TexelSizeV;
                    depth = MathF.Max(0.05f, (cy - rim) / R);
                    _rimU[k] = u;
                    _rimV[k] = rim;
                }
                else
                {
                    // Any other direction: walk from the centre line along the way paint runs until the splat ends.
                    float off = u - cx;
                    float su = cx + _sideX * off, sv = cy + _sideY * off;
                    if (!CoveredAt(su, sv, x0, y0, x1, y1)) continue;
                    float t = 0f;
                    while (CoveredAt(su + _runX * (t + _cellMin), sv + _runY * (t + _cellMin), x0, y0, x1, y1)) t += _cellMin;
                    depth = MathF.Max(0.05f, t / R);
                    _rimU[k] = su + _runX * t;
                    _rimV[k] = sv + _runY * t;
                }
                // With a curtain, drip paint goes mostly to the outer columns: they stop early, so their drips hang
                // off the curtain's sides instead of being swallowed by the floor pool.
                _rimW[k] = depth * w * (1f - cs + cs * (0.15f + xn * xn));
                _rimP[k] = prof;
                _rimSag[k] = sag * lane * _s.TrailThickness; // paint needed to sag that far
                sumW += _rimW[k];
                sumP += prof;
                sheetTotal += _rimSag[k];
            }
            if (sumW <= 0f) return 0.0;

            // The sheet may use at most 60% of the running paint; the rest runs on as drips.
            float sheetScale = want > 0.0 && sheetTotal > want * 0.6 ? (float)(want * 0.6 / sheetTotal) : (want > 0.0 ? 1f : 0f);
            double carryTotal = Math.Max(0.0, want - sheetTotal * sheetScale);

            double given = 0.0;
            for (int k = 0; k < n; k++)
            {
                if (_rimW[k] < 0f) continue;
                float sheetV = _rimSag[k] * sheetScale + (float)(curtainVolume * _rimP[k] / sumP);
                float carry = (float)(carryTotal * _rimW[k] / sumW);
                float rd = MathF.Min(MathF.Max(_s.DripWidth * R * rng.Range(0.8f, 1.25f), rmin), MathF.Max(rmin, _s.MaxDripRadius));
                float xn = halfW > 0f ? _rimOff[k] / halfW : 0f;
                float drift = -_s.CurtainTaper * cs * xn * rng.Range(0.4f, 1.6f);
                if (carry / (MathF.PI * rd * rd) < _s.StallThickness * 1.5f) carry = 0f; // too little to run: stays in the splat
                if (sheetV < 0.5f * _cellMin * lane * _s.TrailThickness && carry <= 0f) continue;
                if (_headCount >= _heads.Length) break;
                ref DripHead h = ref _heads[_headCount++];
                // It starts a little way back from the rim, inside the splat.
                h.X = _rimU[k] - _runX * (rs * 0.9f);
                h.Y = _rimV[k] - _runY * (rs * 0.9f);
                h.LastX = h.X;
                h.LastY = h.Y;
                h.LastR = rs;
                h.Px = h.X;
                h.Py = h.Y;
                h.Film = 1f;
                h.V = sheetV;
                h.V0 = MathF.Max(sheetV, 1e-12f);
                h.R0 = rs;
                h.Carry = carry;
                h.DripR = rd;
                h.Lane = lane;
                h.BaseSpeed = sheetSpeed;
                h.Drift = drift;
                // Curtain edges wobble and lag more than its middle, so its outline is uneven instead of ruled.
                h.Wander = _s.WanderAmplitude * (0.3f + cs * (0.2f + 1.6f * xn * xn));
                h.Shrink = 0.7f + 0.2f * cs;
                h.Pending = 0f;
                h.Dist = 0f;
                h.Age = 0f;
                h.SpeedMul = rng.Range(0.8f, 1.2f) * (1f - cs * 0.35f * MathF.Min(1f, xn * xn));
                h.Phase = rng.Range(0f, 64f);
                h.Seed = rng.NextUInt();
                h.Color = ci;
                h.Sheet = true;
                h.Alive = true;
                given += sheetV + carry;
            }
            return given;
        }

        /// <summary>True when the splat being landed covers the texel under this point (inside its box).</summary>
        private bool CoveredAt(float u, float v, int x0, int y0, int x1, int y1)
        {
            int x = (int)MathF.Floor(u / TexelSizeU), y = (int)MathF.Floor(v / TexelSizeV);
            if (x < x0 || x > x1 || y < y0 || y > y1) return false;
            return _cov[x + y * Width] >= 0.5f;
        }

        /// <summary>Running heads of the other colour under fresh paint stop there; their paint stays on the wall.</summary>
        private void BuryOtherColourHeads(int ci, int x0, int y0, int x1, int y1)
        {
            bool any = false;
            for (int k = 0; k < _headCount; k++)
            {
                ref DripHead h = ref _heads[k];
                if (h.Color == ci) continue;
                int x = (int)(h.X / TexelSizeU), y = (int)(h.Y / TexelSizeV);
                if (x < x0 || x > x1 || y < y0 || y > y1) continue;
                if (_cov[x + y * Width] < 0.5f) continue;
                DepositAt(h.X, h.Y, (double)h.V + h.Pending + h.Carry);
                h.V = 0f;
                h.Pending = 0f;
                h.Carry = 0f;
                h.Alive = false;
                any = true;
            }
            if (any) CompactHeads();
        }

        /// <summary>One inner time step of every moving head.</summary>
        private void DripSubstep(float dt)
        {
            float stall = _s.StallThickness;
            float full = MathF.Max(stall * 1.01f, _s.FullSpeedThickness);
            float paintStep = 0.5f * _cellMin;
            float wanderLen = MathF.Max(0.01f, _s.WanderWavelength);
            float delay = MathF.Max(0f, _s.SagDelay);
            float dry = MathF.Max(0.1f, _s.DryTime);
            // Everything runs RunSpeedUp times faster (the tester, 2026-11-19: drips run quicker), and its start-up (the delay
            // and the ramp to full speed) goes by a clock that fast too, so a fresh drip starts exactly as it did.
            // How long the paint stays wet does NOT go by that clock any more: DryTime is real seconds (the tester, that
            // evening: "more than three seconds for it to drip").
            float runUp = MathF.Max(0.01f, _s.RunSpeedUp);
            float dtRun = dt * runUp;
            float curve = MathF.Max(0.05f, _s.SlowDownCurve);
            float sheetEnd = MathUtil.Saturate(_s.SheetEndSpeed);
            float sheetRest = sheetEnd == 0.45f ? 0.55f : 1f - sheetEnd;   // 0.45 + 0.55 is the old pair, kept to the bit
            float thinEnd = MathF.Min(1f, MathF.Max(0.05f, _s.TrailThinEnd)), thinRest = 1f - thinEnd;

            // Heads born during this step (drips leaving a finished sag) start moving next step.
            int count = _headCount;
            for (int k = 0; k < count; k++)
            {
                ref DripHead h = ref _heads[k];
                if (!h.Alive) continue;
                h.Age += dt;
                float r = HeadRadius(ref h);
                float wet = 1f - h.Age / dry;
                float startAge = h.Age * runUp;
                float ramp = startAge <= delay ? 0f : MathUtil.SmoothStep(0f, 0.25f, startAge - delay);
                float frac = h.V0 > 0f ? h.V / h.V0 : 0f;
                // Drying paint slows: with a curve of 2 it loses speed quickly at first and then creeps for a long time.
                float drying = wet <= 0f ? 0f : (curve == 0.5f ? MathF.Sqrt(wet) : MathF.Pow(wet, curve));
                float speed;
                if (h.Sheet)
                {
                    if (wet <= 0f || (h.V <= 0f && ramp > 0f))
                    {
                        EndSag(ref h, r);
                        continue;
                    }
                    speed = h.BaseSpeed * h.SpeedMul * ramp * drying * (sheetEnd + sheetRest * frac);
                }
                else
                {
                    float thick = h.V / (MathF.PI * r * r);
                    if (thick <= stall || wet <= 0f)
                    {
                        StopDrip(ref h, r);
                        continue;
                    }
                    float run = (thick - stall) / (full - stall);
                    run = MathF.Pow(run > 1f ? 1f : run, 0.7f);
                    speed = h.BaseSpeed * h.SpeedMul * ramp * drying * (0.35f + 0.65f * MathF.Sqrt(frac)) * run;
                }

                float ds = speed * dtRun;
                if (_runPull != 1f) ds *= _runPull;   // a slope pulls less than a wall
                if (ds > 0f)
                {
                    h.Dist += ds;
                    h.Px += _runX * ds;
                    h.Py += _runY * ds;
                    float wiggle = h.Wander *
                          (PaintMath.Wiggle(h.Dist / wanderLen + h.Phase, h.Seed) - PaintMath.Wiggle(h.Phase, h.Seed));
                    h.X = h.Px + _sideX * (h.Drift * h.Dist) + _sideX * wiggle;
                    h.Y = h.Py + _sideY * (h.Drift * h.Dist) + _sideY * wiggle;
                    // The film it leaves behind thins as the paint dries (and is thinner over wet paint: h.Film).
                    float film = _s.TrailThickness * (thinEnd + thinRest * wet) * h.Film;
                    float used = ds * (h.Sheet ? h.Lane : 2f * r) * film;
                    if (used > h.V) used = h.V;
                    h.V -= used;
                    h.Pending += used;
                    r = HeadRadius(ref h);
                }

                // Has its front reached the edge it is running toward (on a wall: the bottom)?
                float fx = h.X + _runX * (0.6f * r), fy = h.Y + _runY * (0.6f * r);
                int edge = _runY < 0f && fy <= 0f ? EdgeBottom
                         : _runY > 0f && fy >= _wall.Height ? EdgeTop
                         : _runX < 0f && fx <= 0f ? EdgeLeft
                         : _runX > 0f && fx >= _wall.Width ? EdgeRight : EdgeNone;
                if (edge != EdgeNone)
                {
                    // Finish the trail. Then most of what is left pours onto the surface below, if the game has
                    // said there is one; the rest (or all of it) pools along the edge.
                    PaintStroke(ref h, r);
                    if (HandsOverRunOff) RunOffAtEdge(ref h, r, edge);
                    PoolOnFloor(ref h, r, edge);
                    continue;
                }
                // A stroke covers everything between its two ends, so wide heads can stroke less often (cheaper).
                float stepLen = MathF.Max(paintStep, 0.4f * r);
                float mx = h.X - h.LastX, my = h.Y - h.LastY;
                if (mx * mx + my * my >= stepLen * stepLen) PaintStroke(ref h, r);
            }

            MergeDrips();
            CompactHeads();
        }

        /// <summary>
        /// End of a frame (Settings.StrokeEveryFrame): every head that has moved at least Settings.StrokeMinTexels since
        /// its trail was last painted paints it up to where it is now, so the texture sent this frame shows the paint
        /// where it really is instead of up to 0.4 of a head's width behind.
        /// </summary>
        private void StrokeMovedHeads()
        {
            float min = MathF.Max(0.01f, _s.StrokeMinTexels) * _cellMin;
            float min2 = min * min;
            for (int k = 0; k < _headCount; k++)
            {
                ref DripHead h = ref _heads[k];
                if (!h.Alive) continue;
                float mx = h.X - h.LastX, my = h.Y - h.LastY;
                if (mx * mx + my * my < min2) continue;
                PaintStroke(ref h, HeadRadius(ref h));
            }
        }

        /// <summary>
        /// How wet the paint a head lays is, 1..0 (Settings.TrailDriesWithPaint): as wet as the head's own paint still
        /// is. Fresh paint goes down fully wet; what a nearly dry head lays in its last creep is nearly dry already,
        /// so the whole splat finishes drying together instead of the last trail shining on for seconds more.
        /// </summary>
        private float HeadWetness(ref DripHead h)
        {
            if (!_s.TrailDriesWithPaint) return 1f;
            float w = 1f - h.Age / MathF.Max(0.1f, _s.DryTime);
            return w < 0f ? 0f : w;
        }

        /// <summary>Paint the trail from where it was last painted to where the head is now, and lay the used paint on it.</summary>
        private void PaintStroke(ref DripHead h, float r)
        {
            PaintCapsule(h.Color, h.LastX, h.LastY, h.LastR, h.X, h.Y, r, h.Pending, false, HeadWetness(ref h));
            h.Pending = 0f;
            h.LastX = h.X;
            h.LastY = h.Y;
            h.LastR = r;
        }

        /// <summary>A sag head has stopped: finish its patch of sheet, then let the paint it carried run on as drips.</summary>
        private void EndSag(ref DripHead h, float r)
        {
            float mx = h.X - h.LastX, my = h.Y - h.LastY;
            if (mx * mx + my * my > 1e-12f || h.Pending > 0f) PaintStroke(ref h, r);
            // Paint it had left (a curtain that dried before using it all) thickens its rounded lower lip.
            if (h.V > 0f) PaintCapsule(h.Color, h.X, h.Y, r, h.X, h.Y, r, h.V, false, HeadWetness(ref h));
            h.V = 0f;
            float carry = h.Carry;
            h.Carry = 0f;
            h.Alive = false;
            if (carry <= 0f) return;

            // One drip, or two from a wide sag with plenty of paint.
            float rd = h.DripR;
            bool two = r > 3f * rd && carry / (MathF.PI * rd * rd) > 8f * _s.StallThickness
                       && PaintMath.Hash01(1, 0, h.Seed) < 0.4f;
            int drips = two ? 2 : 1;
            for (int d = 0; d < drips; d++)
            {
                float share = drips == 1 ? 1f : (d == 0 ? 0.62f : 0.38f);
                float vol = carry * share;
                float off = drips == 1 ? (PaintMath.Hash01(2, 0, h.Seed) - 0.5f) * 0.8f * r
                                       : (d == 0 ? -0.45f : 0.45f) * r;
                if (_headCount >= _heads.Length)
                {
                    DepositAt(h.X, h.Y, vol);
                    continue;
                }
                ref DripHead c = ref _heads[_headCount++];
                c.X = h.X + _sideX * off + _runX * (r * 0.55f);
                c.Y = h.Y + _sideY * off + _runY * (r * 0.55f);
                c.LastX = c.X;
                c.LastY = c.Y;
                c.LastR = rd;
                c.Px = c.X;
                c.Py = c.Y;
                c.Film = h.Film;
                c.V = vol;
                c.V0 = vol;
                c.R0 = rd;
                c.Carry = 0f;
                c.DripR = rd;
                c.Lane = 0f;
                c.BaseSpeed = _s.DripSpeed * (h.Film < 1f ? 1f / h.Film : 1f);   // paint on wet paint stays quicker
                c.Drift = 0f;
                c.Wander = _s.WanderAmplitude;
                c.Pending = 0f;
                c.Dist = 0f;
                c.Age = h.Age;
                c.SpeedMul = 0.8f + 0.4f * PaintMath.Hash01(3, d, h.Seed);
                c.Phase = 64f * PaintMath.Hash01(4, d, h.Seed);
                c.Seed = PaintMath.Hash(5, d, h.Seed);
                c.Color = h.Color;
                c.Sheet = false;
                c.Alive = true;
            }
        }

        // The edge of the surface a head ran into.
        private const int EdgeNone = 0, EdgeBottom = 1, EdgeTop = 2, EdgeLeft = 3, EdgeRight = 4;

        /// <summary>
        /// A head reached the edge it was running toward (on a wall: the floor line): whatever paint it still carries
        /// (including drip paint it had not let go of yet) spreads sideways a little along that edge as a low pool.
        /// </summary>
        private void PoolOnFloor(ref DripHead h, float r, int edge)
        {
            double left = (double)h.V + h.Carry;
            h.V = 0f;
            h.Carry = 0f;
            h.Alive = false;
            if (left <= 0.0) return;
            float pr = MathF.Min(MathF.Max(0.5f * r, MinDripRadius), 0.04f);
            float spread = (float)(left / (2f * pr * 2f * MathF.Max(1e-5f, _s.TrailThickness)));
            float maxSpread = MathF.Max(0f, _s.FloorPoolMaxSpread);
            if (spread > maxSpread) spread = maxSpread;
            float inset = pr * 0.6f;
            if (edge == EdgeLeft || edge == EdgeRight)
            {
                float x = edge == EdgeLeft ? inset : _wall.Width - inset;
                PaintCapsule(h.Color, x, h.Y - spread, pr, x, h.Y + spread, pr, left, false, HeadWetness(ref h));
            }
            else
            {
                float y = edge == EdgeTop ? _wall.Height - inset : inset;
                PaintCapsule(h.Color, h.X - spread, y, pr, h.X + spread, y, pr, left, false, HeadWetness(ref h));
            }
        }

        /// <summary>
        /// A head reached the edge and a surface lies below (HandsOverRunOff): Settings.RunOffShare of the paint it
        /// still carries leaves this surface and is reported (see RunOffCount / GetRunOff); the head keeps the rest,
        /// which PoolOnFloor then lays along the edge.
        /// </summary>
        private void RunOffAtEdge(ref DripHead h, float r, int edge)
        {
            float share = MathUtil.Saturate(_s.RunOffShare);
            double left = (double)h.V + h.Carry;
            double gone = left * share;
            if (!(gone > 0.0)) return;
            h.V = (float)(h.V * (1.0 - share));
            h.Carry = (float)(h.Carry * (1.0 - share));
            gone = left - ((double)h.V + h.Carry);   // exactly what the head no longer has
            float u = MathF.Min(MathF.Max(h.X, 0f), _wall.Width), v = MathF.Min(MathF.Max(h.Y, 0f), _wall.Height);
            if (edge == EdgeBottom) v = 0f;
            else if (edge == EdgeTop) v = _wall.Height;
            else if (edge == EdgeLeft) u = 0f;
            else u = _wall.Width;
            ReportRunOff(u, v, h.Color, gone, r, false, h.Seed);
        }

        /// <summary>A drip is out of paint or dry: finish its trail, leave a bead, and lay its last paint down.</summary>
        private void StopDrip(ref DripHead h, float r)
        {
            float mx = h.X - h.LastX, my = h.Y - h.LastY;
            if (mx * mx + my * my > 1e-12f || h.Pending > 0f) PaintStroke(ref h, r);
            if (h.Dist > r * 0.5f && _s.BulbScale > 1f)
            {
                float br = r * _s.BulbScale;
                float bx = h.X + _runX * (br - r), by = h.Y + _runY * (br - r);
                PaintCapsule(h.Color, bx, by, br, bx, by, br, h.V, false, HeadWetness(ref h));
            }
            else
            {
                DepositAt(h.X, h.Y, h.V);
            }
            h.V = 0f;
            h.Alive = false;
        }

        /// <summary>Two drips of one colour that run into each other become one, carrying both their paint.</summary>
        private void MergeDrips()
        {
            float mf = _s.MergeFactor;
            float joinBoost = _s.JoinSpeedBoost;
            float joinCap = _s.DripSpeed * 1.6f;
            for (int i = 0; i < _headCount; i++)
            {
                if (!_heads[i].Alive || _heads[i].Sheet) continue;
                for (int j = i + 1; j < _headCount; j++)
                {
                    if (!_heads[j].Alive || _heads[j].Sheet || _heads[j].Color != _heads[i].Color) continue;
                    ref DripHead a = ref _heads[i];
                    ref DripHead b = ref _heads[j];
                    float ra = HeadRadius(ref a), rb = HeadRadius(ref b);
                    float dx = a.X - b.X, dy = a.Y - b.Y;
                    float lim = mf * (ra + rb);
                    if (dx * dx + dy * dy >= lim * lim) continue;
                    // The lower one (the one further along the run) carries on; the upper one finishes its trail into it.
                    bool aLower = a.X * _runX + a.Y * _runY >= b.X * _runX + b.Y * _runY;
                    ref DripHead keep = ref (aLower ? ref a : ref b);
                    ref DripHead gone = ref (aLower ? ref b : ref a);
                    float rg = aLower ? rb : ra;
                    gone.X = keep.X; gone.Y = keep.Y;
                    PaintStroke(ref gone, rg);
                    keep.V += gone.V;
                    keep.V0 += gone.V0;
                    keep.R0 = MathF.Min(MathF.Sqrt(keep.R0 * keep.R0 + gone.R0 * gone.R0), MathF.Max(MinDripRadius, _s.MaxDripRadius) * 1.3f);
                    // Two drips that join run faster as one (once: a third joining later adds nothing more).
                    if (joinBoost > 1f && keep.BaseSpeed < joinCap) keep.BaseSpeed = MathF.Min(keep.BaseSpeed * joinBoost, joinCap);
                    gone.V = 0f;
                    gone.Alive = false;
                    if (!aLower) break; // head i is gone; move on to the next i
                }
            }
        }

        /// <summary>Drop finished heads, keeping the others in their order (so results stay repeatable).</summary>
        private void CompactHeads()
        {
            int w = 0;
            for (int r = 0; r < _headCount; r++)
            {
                if (!_heads[r].Alive) continue;
                if (w != r) _heads[w] = _heads[r];
                w++;
            }
            _headCount = w;
        }

        /// <summary>
        /// Paint a rounded stick from a (half-width ra) to b (half-width rb) in colour c, and spread the given volume of
        /// paint over it in proportion to coverage. Paint falling outside the wall counts as spilled.
        /// </summary>
        /// <param name="bareOnly">For paint that merely arrived (a puddle of run-off): it never covers the other
        /// colour, only bare surface and its own colour, so it can never cut into a portal patch.</param>
        /// <param name="wetness">How wet this paint is as it goes down, 0..1 (the wetness channel gets coverage times this).</param>
        private void PaintCapsule(int c, float ax, float ay, float ra, float bx, float by, float rb, double volume, bool bareOnly = false, float wetness = 1f)
        {
            // Coverage is zero once the distance is a soft edge's half-width past the outline, so the box need reach
            // no further (it used to reach twice as far: the same paint, 40% more texels to look at).
            float m = _halfEdge * 1.001f + 1e-6f;
            float rr = MathF.Max(ra, rb) + m;
            if (!TexelRange(MathF.Min(ax, bx) - rr, MathF.Min(ay, by) - rr, MathF.Max(ax, bx) + rr, MathF.Max(ay, by) + rr,
                out int x0, out int y0, out int x1, out int y1))
            {
                _spilled += volume;
                return;
            }
            _extent[c].Include(new TexelRect(x0, y0, x1, y1));

            // Each texel's coverage is worked out once, kept, and summed (the sum shares the paint out); then laid.
            // Since 2026-11-19 heads paint every frame (SplatFeel), so this is the paint's busiest loop.
            int count = (x1 - x0 + 1) * (y1 - y0 + 1);
            if (_capsuleCov.Length < count) _capsuleCov = new float[Math.Max(count, _capsuleCov.Length * 2)];
            float[] cv = _capsuleCov;
            float[] otherField = _field[1 - c];
            double sum = 0.0;
            int k = 0;
            for (int y = y0; y <= y1; y++)
            {
                float v = (y + 0.5f) * TexelSizeV;
                for (int x = x0; x <= x1; x++, k++)
                {
                    float u = (x + 0.5f) * TexelSizeU;
                    float cov = PaintMath.FieldFromDistance(PaintMath.Capsule(u, v, ax, ay, bx, by, ra, rb), _halfEdge);
                    if (bareOnly && cov > 0f)
                    {
                        // Only as far as the other colour leaves room: its field is not touched below.
                        float room = 1f - otherField[x + y * Width];
                        if (cov > room) cov = room;
                    }
                    cv[k] = cov;
                    sum += cov;
                }
            }
            if (volume > 0.0 && sum <= 0.0)
            {
                _spilled += volume;
                volume = 0.0;
            }
            float perCov = sum > 0.0 && volume > 0.0 ? (float)(volume / (sum * _cellArea)) : 0f;

            // Lay it: Cover() and the film written out, with the changed texels gathered per row (the same rectangles
            // Cover() would mark, for a fraction of the work).
            float[] f = _field[c], g = _field[1 - c], wet = _wet, thick = _thick;
            k = 0;
            for (int y = y0; y <= y1; y++)
            {
                int row = y * Width;
                int slo = int.MaxValue, shi = int.MinValue;   // this colour grew
                int olo = int.MaxValue, ohi = int.MinValue;   // the other colour shrank
                int wlo = int.MaxValue, whi = int.MinValue;   // got wetter
                int tlo = int.MaxValue, thi = int.MinValue;   // texture bytes changed
                for (int x = x0; x <= x1; x++, k++)
                {
                    float cov = cv[k];
                    if (cov <= 0f) continue;
                    int i = row + x;
                    bool changed = false;
                    if (cov > f[i])
                    {
                        f[i] = cov;
                        if (x < slo) slo = x;
                        shi = x;
                        changed = true;
                    }
                    float lim = 1f - cov;
                    if (g[i] > lim && !bareOnly)   // loose paint never takes anything off the other colour, not even a rounding error
                    {
                        g[i] = lim;
                        if (x < olo) olo = x;
                        ohi = x;
                        changed = true;
                    }
                    float wetNow = cov * wetness;
                    if (wetNow > wet[i])
                    {
                        wet[i] = wetNow;
                        if (x < wlo) wlo = x;
                        whi = x;
                        changed = true;
                    }
                    if (perCov > 0f)
                    {
                        thick[i] += perCov * cov;
                        changed = true;
                    }
                    if (changed)
                    {
                        if (x < tlo) tlo = x;
                        thi = x;
                    }
                }
                if (shi >= slo) { _shapeDirty[c].Include(slo, y); _shapeDirty[c].Include(shi, y); }
                if (ohi >= olo) { _shapeDirty[1 - c].Include(olo, y); _shapeDirty[1 - c].Include(ohi, y); }
                if (whi >= wlo)
                {
                    ref TexelRect band = ref _wetBand[y * WetBands / Height];
                    band.Include(wlo, y);
                    band.Include(whi, y);
                }
                if (thi >= tlo) { _texDirty.Include(tlo, y); _texDirty.Include(thi, y); }
            }
        }

        // Scratch for PaintCapsule: one stroke's coverage. Grows (rarely) to the biggest stroke seen.
        private float[] _capsuleCov = new float[1024];
    }
}
