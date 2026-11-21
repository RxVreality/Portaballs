using System;

namespace Portaballs.Core
{
    // The splat stamp: turns one ball impact into a thrown-paint shape on the grid.
    //
    // A splat is laid in four stages:
    //   1. Build   - choose the splat's parts (body, lobes, spikes, droplets) from its seed.
    //   2. Raster  - draw every part's distance into a scratch grid, part by part, row by row.
    //   3. Convert - turn the distances into coverage (0..1), row by row.
    //   4. Land    - put the coverage on the wall, start the drips, add the paint film.
    // Stages 1 to 3 only write scratch buffers and depend on nothing but the splat's own numbers, so they can be
    // spread over several frames. Stage 4 is the only one that touches the wall, it is small, and it always
    // happens in one go. That is why a splat laid in slices is exactly the splat laid in one call.
    public sealed partial class PaintSurface
    {
        // One building block of a splat shape, in the splat's own unstretched frame
        // (a = along the throw, b = across it, metres, centred on the impact).
        private struct SplatPart
        {
            public bool Ragged;        // true: a wobbly disc; false: a tapering stick from A to B
            public float Ax, Ay, Bx, By;
            public float Ra, Rb;       // half-widths at A and B (a ragged disc uses Ra as its base radius)
            public float Blend;        // how softly it melts into the parts before it (0 = hard edge)
            public int Table;          // first entry of this disc's radius-by-angle table
            public float Cx, Cy, Reach; // bounding circle
        }

        private const int MaxSplatParts = 160;
        private const int RaggedTableSize = 128;
        private const int MaxRaggedParts = 16;

        private readonly SplatPart[] _parts = new SplatPart[MaxSplatParts];
        private readonly float[] _raggedRadius = new float[MaxRaggedParts * RaggedTableSize];
        private int _partCount;
        private int _raggedCount;

        // ------------------------------------------------------------------ the queue of splats still to land

        private struct SplatRequest
        {
            public int Color;
            public Vec2 UV;
            public float Radius;
            public Vec2 WallVelocity;
            public uint Seed;
            public float NormalSpeed;
        }

        /// <summary>Most splats that can wait in the queue. A ninth one makes the oldest land at once.</summary>
        public const int MaxQueuedSplats = 8;

        /// <summary>
        /// Rough number of work units done per millisecond in an optimised build on the development PC (one unit is
        /// about 3 nanoseconds). Only a guide: use <see cref="PaintPacer.AdvanceFor"/> if you would rather give a time.
        /// </summary>
        public const int WorkUnitsPerMillisecond = 300000;

        /// <summary>The same for an unoptimised (Debug) build, where one unit is about 10 nanoseconds.</summary>
        public const int WorkUnitsPerMillisecondUnoptimised = 100000;

        private readonly SplatRequest[] _queue = new SplatRequest[MaxQueuedSplats];
        private int _queueHead, _queueCount;
        private int _splatsLanded;

        // The splat being prepared (always the front of the queue, or the one Splat() is laying right now).
        private const int StageIdle = 0, StageRaster = 1, StageConvert = 2, StageLand = 3;
        private int _stage;
        private SplatRequest _job;
        private Rng _jobRng;
        private float _jobStretch, _jobAcross, _jobCx, _jobCy;
        private Vec2 _jobFwd, _jobSide;
        private int _jobX0, _jobY0, _jobX1, _jobY1;       // the splat's whole box
        private int _jobPart, _jobRow;                    // raster: the part being drawn and its next row (-1 = not begun)
        private int _jobPx0, _jobPy0, _jobPx1, _jobPy1;   // raster: that part's box
        private uint _jobNoiseSeed;
        private float _jobN1, _jobA1, _jobN2, _jobA2, _jobBand;
        private double _jobCovered;
        private int _jobCovY0, _jobCovY1;                 // convert: first and last row with any coverage

        // Scratch the stages share (made once in the constructor; a splat never allocates).
        private readonly float[] _colFwd, _colSide;       // per column: (u - cx) * fwd.X and (u - cx) * side.X
        private readonly int[] _colCell1, _colCell2;      // per column: edge-noise cell, coarse and fine
        private readonly float[] _colMix1, _colMix2;      // per column: blend weight inside that cell
        private readonly int[] _rowLo, _rowHi;            // per row: first and last column with coverage

        private const float Far = 1e9f;

        // Work units charged per step, tuned so a unit costs about the same in every stage.
        private const int WorkBuild = 14000;
        private const int WorkLand = 12000;

        /// <summary>
        /// A paint ball bursts on the wall. The whole splat is laid before this returns, which for a big splat
        /// (radius 0.5 m) costs about 0.8 ms in an optimised build and 3 ms in an unoptimised one, and nearly twice
        /// that for a hard glancing hit; use <see cref="QueueSplat"/> to spread it over frames instead.
        /// Any splats still waiting in the queue land first, so paint always goes on in the order it was thrown.
        /// </summary>
        /// <param name="color">Paint colour. It removes the other colour where it lands.</param>
        /// <param name="uv">Impact point in wall metres.</param>
        /// <param name="radius">Size of the splat body in metres (spikes and droplets reach beyond it).</param>
        /// <param name="wallVelocity">The ball's velocity at impact projected onto the wall (m/s along U and V).
        /// A glancing hit stretches the splat and throws spatter that way.</param>
        /// <param name="seed">Same seed, same splat.</param>
        /// <param name="normalSpeed">Speed into the wall in m/s; negative means Settings.DefaultNormalSpeed. Faster hits spatter more.</param>
        public void Splat(PaintColor color, Vec2 uv, float radius, Vec2 wallVelocity, uint seed, float normalSpeed = -1f)
        {
            if (!(radius > 0f)) return;
            FinishQueuedSplats();
            _job.Color = (int)color;
            _job.UV = uv;
            _job.Radius = radius;
            _job.WallVelocity = wallVelocity;
            _job.Seed = seed;
            _job.NormalSpeed = normalSpeed;
            BeginJob();
            while (_stage != StageIdle) StepJob(int.MaxValue);
        }

        /// <summary>
        /// The same splat as <see cref="Splat"/>, but laid over the next frames instead of all at once. Nothing shows
        /// on the wall until its preparation is finished; then it lands whole, in one cheap step, exactly as Splat
        /// would have laid it at that moment (same shape, same drips, same paint).
        ///
        /// Each frame call <see cref="AdvanceSplats"/> (or <see cref="PaintPacer.Advance"/> /
        /// <see cref="PaintPacer.AdvanceFor"/>, which do it for every wall) to do a slice of the work. Watch
        /// <see cref="SplatsLanded"/> to learn when it has landed. Splats land in the order they were queued.
        /// If the queue is full (<see cref="MaxQueuedSplats"/>) the oldest one is finished on the spot.
        /// </summary>
        public void QueueSplat(PaintColor color, Vec2 uv, float radius, Vec2 wallVelocity, uint seed, float normalSpeed = -1f)
        {
            if (!(radius > 0f)) return;
            if (_queueCount == MaxQueuedSplats) FinishFrontSplat();
            ref SplatRequest q = ref _queue[(_queueHead + _queueCount) % MaxQueuedSplats];
            q.Color = (int)color;
            q.UV = uv;
            q.Radius = radius;
            q.WallVelocity = wallVelocity;
            q.Seed = seed;
            q.NormalSpeed = normalSpeed;
            _queueCount++;
        }

        /// <summary>Splats queued with <see cref="QueueSplat"/> that have not landed yet.</summary>
        public int QueuedSplatCount
        {
            get { return _queueCount; }
        }

        /// <summary>
        /// Goes up by one each time a splat has been dealt with completely, whether through <see cref="Splat"/> or
        /// the queue (also when it missed the wall and left nothing). Compare it with the value you saw last frame
        /// to know that new paint is on the wall, for example to ask for a fresh opening search.
        /// </summary>
        public int SplatsLanded
        {
            get { return _splatsLanded; }
        }

        /// <summary>
        /// Do a slice of the queued splats' work: at most about <paramref name="workUnits"/> units (see
        /// <see cref="WorkUnitsPerMillisecond"/>). It always does at least one small step, and may run over by one
        /// step (a row of texels, or the landing itself, which is never split). Returns the units used; 0 means the
        /// queue is empty. However the work is sliced, the paint that lands is identical.
        /// </summary>
        public int AdvanceSplats(int workUnits)
        {
            int used = 0;
            while (_queueCount > 0)
            {
                if (_stage == StageIdle)
                {
                    _job = _queue[_queueHead];
                    used += BeginJob();
                }
                else
                {
                    used += StepJob(workUnits - used);
                }
                if (_stage == StageIdle)
                {
                    _queueHead = (_queueHead + 1) % MaxQueuedSplats;
                    _queueCount--;
                }
                if (used >= workUnits) break;
            }
            return used;
        }

        /// <summary>
        /// Land every queued splat now, however long it takes. Painting, erasing and <see cref="Splat"/> call this
        /// first, so the order of operations on the wall is always the order of the calls.
        /// </summary>
        public void FinishQueuedSplats()
        {
            while (_queueCount > 0) FinishFrontSplat();
        }

        private void FinishFrontSplat()
        {
            if (_queueCount == 0) return;
            if (_stage == StageIdle)
            {
                _job = _queue[_queueHead];
                BeginJob();
            }
            while (_stage != StageIdle) StepJob(int.MaxValue);
            _queueHead = (_queueHead + 1) % MaxQueuedSplats;
            _queueCount--;
        }

        /// <summary>Forget the queue and any half-prepared splat (the wall is being cleared).</summary>
        private void DropQueuedSplats()
        {
            _queueHead = 0;
            _queueCount = 0;
            _stage = StageIdle;
        }

        private int StepJob(int budget)
        {
            if (_stage == StageRaster) return RasterStep(budget);
            if (_stage == StageConvert) return ConvertStep(budget);
            if (_stage == StageLand)
            {
                Land();
                _stage = StageIdle;
                _splatsLanded++;
                return WorkLand;
            }
            return 0;
        }

        // ------------------------------------------------------------------ stage 1: build

        /// <summary>Choose the parts of the splat in _job and its box on the wall. Leaves _stage at Raster, or Idle when it missed the wall.</summary>
        private int BeginJob()
        {
            Vec2 uv = _job.UV;
            Vec2 wallVelocity = _job.WallVelocity;
            float normalSpeed = _job.NormalSpeed;
            Rng rng = new Rng(_job.Seed * 0x2545F491u + 0x6A09E667u);
            float R = _job.Radius;

            // ---- How the hit was thrown ----
            float vt = wallVelocity.Length();
            float ns = normalSpeed >= 0f ? normalSpeed : _s.DefaultNormalSpeed;
            float speed = MathF.Sqrt(vt * vt + ns * ns);
            float spatter = MathUtil.Saturate(speed / MathF.Max(0.1f, _s.SpeedForFullSpatter));
            Vec2 fwd;
            float randomAngle = rng.Range(0f, 2f * MathF.PI);
            if (vt > 0.3f) fwd = wallVelocity / vt;
            else fwd = new Vec2(MathF.Cos(randomAngle), MathF.Sin(randomAngle));
            Vec2 side = new Vec2(-fwd.Y, fwd.X);
            float glance = vt > 0.3f ? MathUtil.Saturate(vt / (vt + ns + 1e-3f)) : 0f;
            float stretch = MathF.Min(MathF.Max(1f, _s.MaxStretch), 1f + _s.StretchPerSpeed * vt);
            float across = MathF.Pow(stretch, -0.35f);
            float shift = _s.ThrowShift * R * (stretch - 1f);

            // Smallest droplet worth drawing, measured in the splat's own frame (across is the squeezed axis).
            float minPart = 0.9f * _cellMin / across;

            // ---- Build the parts in the splat's own frame (angle 0 = the throw direction) ----
            _partCount = 0;
            _raggedCount = 0;

            // Body: one wobbly disc.
            AddRagged(ref rng, 0f, 0f, 0.8f * R, _s.Raggedness, 0f);

            // Lobes: overlapping rounder blobs of different sizes, melted into the body. Glancing hits push them forward.
            int lobes = rng.RangeInt(Math.Max(0, _s.LobeCountMin), Math.Max(_s.LobeCountMin, _s.LobeCountMax) + 1);
            for (int k = 0; k < lobes; k++)
            {
                float ang = rng.NextFloat() < glance ? rng.Gaussian() * 0.8f : rng.Range(0f, 2f * MathF.PI);
                float dist = R * rng.Range(0.5f, 0.85f);
                float lr = R * rng.Range(0.2f, 0.45f);
                AddRagged(ref rng, MathF.Cos(ang) * dist, MathF.Sin(ang) * dist, lr, _s.Raggedness * 0.8f, 0.22f * R);
            }

            // Spikes: tapering streaks from the body outward, some ending in a bead.
            int spikes = rng.RangeInt(Math.Max(0, _s.SpikeCountMin), Math.Max(_s.SpikeCountMin, _s.SpikeCountMax) + 1)
                         + (int)MathF.Round(_s.SpikesAddedAtFullSpatter * spatter * rng.Range(0.6f, 1.2f));
            for (int k = 0; k < spikes && _partCount < MaxSplatParts - 2; k++)
            {
                bool forward = rng.NextFloat() < glance * 0.85f;
                float ang = forward ? rng.Gaussian() * 0.45f : rng.Range(0f, 2f * MathF.PI);
                float reach = R * _s.SpikeLength * rng.Range(0.72f, 1.3f) * (forward ? 1f + 0.5f * glance : 1f) * (0.85f + 0.3f * spatter);
                float start = R * rng.Range(0.45f, 0.65f);
                float ra = MathF.Max(minPart * 1.4f, R * rng.Range(0.06f, 0.13f));
                float rb = MathF.Max(minPart * 0.8f, R * rng.Range(0.012f, 0.03f));
                float ca = MathF.Cos(ang), sa = MathF.Sin(ang);
                AddStick(ca * start, sa * start, ca * reach, sa * reach, ra, rb, 0.07f * R);
                if (rng.NextFloat() < 0.55f)
                {
                    float br = MathF.Max(minPart, R * rng.Range(0.028f, 0.06f));
                    float bd = reach + rb + br + R * rng.Range(0.02f, 0.09f);
                    AddStick(ca * bd, sa * bd, ca * bd, sa * bd, br, br, 0f);
                }
            }

            // Satellite droplets: thrown clear of the body, small, with a short tail pointing back at the centre.
            float sizeScale = MathF.Sqrt(MathUtil.Saturate(R / 0.2f));
            int sats = (int)MathF.Round(_s.SatelliteCount * (0.15f + 0.85f * spatter) * rng.Range(0.7f, 1.2f) * (0.4f + 0.6f * sizeScale));
            for (int k = 0; k < sats && _partCount < MaxSplatParts; k++)
            {
                bool forward = rng.NextFloat() < glance;
                float ang = forward ? rng.Gaussian() * 0.6f : rng.Range(0f, 2f * MathF.PI);
                float dist = R * rng.Range(1.2f, MathF.Max(1.3f, _s.SatelliteDistance)) * (forward ? 1f + 0.5f * glance : 1f);
                float size = MathF.Max(minPart, R * rng.Range(0.025f, 0.085f));
                float tail = size * rng.Range(0f, 2.2f);
                float ca = MathF.Cos(ang), sa = MathF.Sin(ang);
                AddStick(ca * (dist - tail), sa * (dist - tail), ca * dist, sa * dist, size * 0.7f, size, 0f);
            }

            // ---- Where on the wall the parts land ----
            float cx = uv.X + fwd.X * shift, cy = uv.Y + fwd.Y * shift; // body centre after the throw shift
            float margin = 3f * _halfEdge;
            float bu0 = float.MaxValue, bv0 = float.MaxValue, bu1 = float.MinValue, bv1 = float.MinValue;
            for (int p = 0; p < _partCount; p++)
            {
                ref SplatPart sp = ref _parts[p];
                float wu = cx + fwd.X * sp.Cx * stretch + side.X * sp.Cy * across;
                float wv = cy + fwd.Y * sp.Cx * stretch + side.Y * sp.Cy * across;
                float wr = (sp.Reach + sp.Blend) * stretch + margin;
                if (wu - wr < bu0) bu0 = wu - wr;
                if (wv - wr < bv0) bv0 = wv - wr;
                if (wu + wr > bu1) bu1 = wu + wr;
                if (wv + wr > bv1) bv1 = wv + wr;
            }
            if (!TexelRange(bu0, bv0, bu1, bv1, out int x0, out int y0, out int x1, out int y1))
            {
                // Missed the wall completely: nothing to lay.
                _stage = StageIdle;
                _splatsLanded++;
                return WorkBuild;
            }

            int boxWidth = x1 - x0 + 1;
            float[] sdf = _sdf;
            for (int y = y0; y <= y1; y++) Array.Fill(sdf, Far, y * Width + x0, boxWidth);

            // The wall point of each column, turned into the splat's frame, is the same for every part and row.
            float[] colFwd = _colFwd, colSide = _colSide;
            for (int x = x0; x <= x1; x++)
            {
                float u = (x + 0.5f) * TexelSizeU - cx;
                colFwd[x] = u * fwd.X;
                colSide[x] = u * side.X;
            }

            _jobRng = rng;
            _jobFwd = fwd;
            _jobSide = side;
            _jobStretch = stretch;
            _jobAcross = across;
            _jobCx = cx;
            _jobCy = cy;
            _jobX0 = x0; _jobY0 = y0; _jobX1 = x1; _jobY1 = y1;
            _jobPart = 0;
            _jobRow = -1;
            _stage = StageRaster;
            return WorkBuild + (boxWidth * (y1 - y0 + 1) >> 4);
        }

        // ------------------------------------------------------------------ stage 2: raster

        // A part is skipped at a texel when it is certain to leave the distance there unchanged: the part's nearest
        // possible point is further away than the distance already there plus the part's blend width. This slack
        // keeps that test on the safe side of rounding.
        private const float SkipSlack = 1e-4f;

        /// <summary>Draw each part's distance into the scratch grid, melting it into what is there. Stops when the budget is used.</summary>
        private int RasterStep(int budget)
        {
            int used = 0;
            float cx = _jobCx, cy = _jobCy;
            Vec2 fwd = _jobFwd, side = _jobSide;
            float stretch = _jobStretch, across = _jobAcross;
            float margin = 3f * _halfEdge;
            float invStretch = 1f / stretch, invAcross = 1f / across;
            float[] sdf = _sdf, colFwd = _colFwd, colSide = _colSide, table = _raggedRadius;
            int width = Width;

            while (_jobPart < _partCount)
            {
                ref SplatPart sp = ref _parts[_jobPart];
                if (_jobRow < 0)
                {
                    float wu = cx + fwd.X * sp.Cx * stretch + side.X * sp.Cy * across;
                    float wv = cy + fwd.Y * sp.Cx * stretch + side.Y * sp.Cy * across;
                    float wr = (sp.Reach + sp.Blend) * stretch + margin;
                    if (!TexelRange(wu - wr, wv - wr, wu + wr, wv + wr, out _jobPx0, out _jobPy0, out _jobPx1, out _jobPy1))
                    {
                        _jobPart++;
                        continue;
                    }
                    _jobRow = _jobPy0;
                }

                int px0 = _jobPx0, px1 = _jobPx1, py1 = _jobPy1;
                float blend = sp.Blend;
                float skip = sp.Reach + blend + SkipSlack;
                int rowCost = (px1 - px0 + 1) * 2;

                if (sp.Ragged)
                {
                    float ax = sp.Ax, ay = sp.Ay;
                    int tb = sp.Table;
                    rowCost *= 2;
                    for (int y = _jobRow; y <= py1; y++)
                    {
                        float v = (y + 0.5f) * TexelSizeV - cy;
                        float vf = v * fwd.Y, vs = v * side.Y;
                        int row = y * width;
                        for (int x = px0; x <= px1; x++)
                        {
                            int idx = row + x;
                            float a = sdf[idx];
                            // Wall point into the splat's unstretched frame.
                            float qa = (colFwd[x] + vf) * invStretch;
                            float qb = (colSide[x] + vs) * invAcross;
                            float dx = qa - ax, dy = qb - ay;
                            float len2 = dx * dx + dy * dy;
                            float lim = skip + a;
                            if (lim <= 0f || len2 >= lim * lim) continue;
                            float len = MathF.Sqrt(len2);
                            float t = (MathF.Atan2(dy, dx) * (0.5f / MathF.PI) + 0.5f) * RaggedTableSize;
                            int i0 = (int)t;
                            float ft = t - i0;
                            if (i0 >= RaggedTableSize) i0 -= RaggedTableSize;
                            int i1 = i0 + 1 == RaggedTableSize ? 0 : i0 + 1;
                            float r0 = table[tb + i0];
                            float d = len - (r0 + (table[tb + i1] - r0) * ft);
                            // Smooth minimum (PaintMath.SmoothMin written out).
                            float m = a < d ? a : d;
                            if (blend > 0f)
                            {
                                float h = blend - MathF.Abs(a - d);
                                if (h > 0f)
                                {
                                    h /= blend;
                                    m -= h * h * blend * 0.25f;
                                }
                            }
                            sdf[idx] = m;
                        }
                        used += rowCost;
                        if (used >= budget && y < py1)
                        {
                            _jobRow = y + 1;
                            return used;
                        }
                    }
                }
                else
                {
                    float ax = sp.Ax, ay = sp.Ay, ra = sp.Ra;
                    float bax = sp.Bx - ax, bay = sp.By - ay;
                    float len2 = bax * bax + bay * bay;
                    bool hasLength = len2 > 1e-12f;
                    float taper = sp.Rb - ra;
                    float mx = sp.Cx, my = sp.Cy;
                    for (int y = _jobRow; y <= py1; y++)
                    {
                        float v = (y + 0.5f) * TexelSizeV - cy;
                        float vf = v * fwd.Y, vs = v * side.Y;
                        int row = y * width;
                        for (int x = px0; x <= px1; x++)
                        {
                            int idx = row + x;
                            float a = sdf[idx];
                            float qa = (colFwd[x] + vf) * invStretch;
                            float qb = (colSide[x] + vs) * invAcross;
                            float ex = qa - mx, ey = qb - my;
                            float lim = skip + a;
                            if (lim <= 0f || ex * ex + ey * ey >= lim * lim) continue;
                            // Distance to the tapering stick (PaintMath.Capsule written out).
                            float pax = qa - ax, pay = qb - ay;
                            float t = hasLength ? (pax * bax + pay * bay) / len2 : 0f;
                            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                            float dx = pax - bax * t, dy = pay - bay * t;
                            float d = MathF.Sqrt(dx * dx + dy * dy) - (ra + taper * t);
                            float m = a < d ? a : d;
                            if (blend > 0f)
                            {
                                float h = blend - MathF.Abs(a - d);
                                if (h > 0f)
                                {
                                    h /= blend;
                                    m -= h * h * blend * 0.25f;
                                }
                            }
                            sdf[idx] = m;
                        }
                        used += rowCost;
                        if (used >= budget && y < py1)
                        {
                            _jobRow = y + 1;
                            return used;
                        }
                    }
                }

                _jobPart++;
                _jobRow = -1;
                if (used >= budget && _jobPart < _partCount) return used;
            }

            // ---- All parts drawn: set up the conversion ----
            float R = _job.Radius;
            int x0 = _jobX0, x1 = _jobX1;
            _jobNoiseSeed = _jobRng.NextUInt();
            _jobN1 = MathF.Max(2f * _cellMin, 0.22f * R);
            _jobA1 = _s.EdgeNoise * R;
            _jobN2 = MathF.Max(1.5f * _cellMin, 0.07f * R);
            _jobA2 = _s.EdgeNoise * 0.25f * R;
            _jobBand = 3f * _halfEdge + _jobA1 + _jobA2;
            _jobCovered = 0.0;
            _jobCovY0 = int.MaxValue;
            _jobCovY1 = int.MinValue;
            // Which edge-noise cell each column falls in, and how far across it (the same for every row).
            float n1 = _jobN1, n2 = _jobN2;
            int[] cell1 = _colCell1, cell2 = _colCell2;
            float[] mix1 = _colMix1, mix2 = _colMix2;
            for (int x = x0; x <= x1; x++)
            {
                float u = (x + 0.5f) * TexelSizeU;
                float p1 = u / n1, p2 = u / n2;
                float f1 = MathF.Floor(p1), f2 = MathF.Floor(p2);
                float t1 = p1 - f1, t2 = p2 - f2;
                cell1[x] = (int)f1;
                cell2[x] = (int)f2;
                mix1[x] = t1 * t1 * (3f - 2f * t1);
                mix2[x] = t2 * t2 * (3f - 2f * t2);
            }
            _jobRow = _jobY0;
            _stage = StageConvert;
            return used + (x1 - x0 + 1);
        }

        // ------------------------------------------------------------------ stage 3: convert

        /// <summary>Turn distances into coverage, fixing the stretch so the edge ramp stays the same width everywhere.</summary>
        private int ConvertStep(int budget)
        {
            int used = 0;
            int x0 = _jobX0, y0 = _jobY0, x1 = _jobX1, y1 = _jobY1;
            int width = Width;
            float[] sdf = _sdf, covGrid = _cov;
            int[] cell1 = _colCell1, cell2 = _colCell2;
            float[] mix1 = _colMix1, mix2 = _colMix2;
            uint seed1 = _jobNoiseSeed, seed2 = _jobNoiseSeed + 17u;
            float n1 = _jobN1, a1 = _jobA1, n2 = _jobN2, a2 = _jobA2, band = _jobBand;
            float bandHi = band * 1.5f, bandLo = -band * 1.5f;
            float farHalf = Far * 0.5f;
            float halfEdge = _halfEdge;
            float twoHalfEdge = 2f * halfEdge;
            // Beyond this the edge noise (at most a1 + a2) cannot bring the coverage back between 0 and 1, so the
            // noise need not be worked out: the answer is exactly 0 or exactly 1 either way.
            float sure = halfEdge + (a1 + a2) * 1.01f + 1e-5f;
            float denU1 = 1 * TexelSizeU + 1e-9f, denU2 = 2 * TexelSizeU + 1e-9f;
            float denV0 = 0 * TexelSizeV + 1e-9f, denV1 = 1 * TexelSizeV + 1e-9f, denV2 = 2 * TexelSizeV + 1e-9f;
            float denU0 = 0 * TexelSizeU + 1e-9f;
            double covered = _jobCovered;
            int rowCost = (x1 - x0 + 1) * 3 / 4 + 1;

            for (int y = _jobRow; y <= y1; y++)
            {
                int row = y * width;
                bool hasDown = y > y0, hasUp = y < y1;
                float denV = hasDown && hasUp ? denV2 : (hasDown || hasUp ? denV1 : denV0);
                // This row's place in the two layers of edge noise.
                float v = (y + 0.5f) * TexelSizeV;
                float q1 = v / n1, q2 = v / n2;
                float g1 = MathF.Floor(q1), g2 = MathF.Floor(q2);
                int cy1 = (int)g1, cy2 = (int)g2;
                float ty1 = q1 - g1, ty2 = q2 - g2;
                ty1 = ty1 * ty1 * (3f - 2f * ty1);
                ty2 = ty2 * ty2 * (3f - 2f * ty2);
                // The four corner values of the noise cell last used on this row, per layer.
                int have1 = int.MinValue, have2 = int.MinValue;
                float h1a = 0f, h1b = 0f, h1c = 0f, h1d = 0f, h2a = 0f, h2b = 0f, h2c = 0f, h2d = 0f;
                int lo = int.MaxValue, hi = int.MinValue;
                int inBand = 0;

                for (int x = x0; x <= x1; x++)
                {
                    int idx = row + x;
                    float d = sdf[idx];
                    float cov;
                    if (d > bandHi) cov = 0f;
                    else if (d < bandLo) cov = 1f;
                    else
                    {
                        inBand++;
                        // Slope of the distance on the wall, from its neighbours; dividing by it turns
                        // "distance in the splat's frame" back into true distance on the wall.
                        bool hasLeft = x > x0, hasRight = x < x1;
                        float dl = hasLeft ? sdf[idx - 1] : d, dr = hasRight ? sdf[idx + 1] : d;
                        float dd = hasDown ? sdf[idx - width] : d, du = hasUp ? sdf[idx + width] : d;
                        if (dl >= farHalf) dl = d;
                        if (dr >= farHalf) dr = d;
                        if (dd >= farHalf) dd = d;
                        if (du >= farHalf) du = d;
                        float gx = (dr - dl) / (hasLeft && hasRight ? denU2 : (hasLeft || hasRight ? denU1 : denU0));
                        float gy = (du - dd) / denV;
                        float g = MathF.Sqrt(gx * gx + gy * gy);
                        if (!(g > 0.3f)) g = g > 0f ? 0.3f : 1f;
                        if (g > 3f) g = 3f;
                        float dg = d / g;
                        if (dg >= sure) cov = 0f;
                        else if (dg <= -sure) cov = 1f;
                        else
                        {
                            // Two layers of smooth noise roughen the edge (PaintMath.ValueNoise written out, with
                            // the cell's corner values kept from texel to texel).
                            int c1 = cell1[x];
                            if (c1 != have1)
                            {
                                if (c1 == have1 + 1 && have1 != int.MinValue)
                                {
                                    h1a = h1b;
                                    h1c = h1d;
                                }
                                else
                                {
                                    h1a = PaintMath.Hash01(c1, cy1, seed1);
                                    h1c = PaintMath.Hash01(c1, cy1 + 1, seed1);
                                }
                                h1b = PaintMath.Hash01(c1 + 1, cy1, seed1);
                                h1d = PaintMath.Hash01(c1 + 1, cy1 + 1, seed1);
                                have1 = c1;
                            }
                            int c2 = cell2[x];
                            if (c2 != have2)
                            {
                                if (c2 == have2 + 1 && have2 != int.MinValue)
                                {
                                    h2a = h2b;
                                    h2c = h2d;
                                }
                                else
                                {
                                    h2a = PaintMath.Hash01(c2, cy2, seed2);
                                    h2c = PaintMath.Hash01(c2, cy2 + 1, seed2);
                                }
                                h2b = PaintMath.Hash01(c2 + 1, cy2, seed2);
                                h2d = PaintMath.Hash01(c2 + 1, cy2 + 1, seed2);
                                have2 = c2;
                            }
                            float tx1 = mix1[x], tx2 = mix2[x];
                            float ab1 = h1a + (h1b - h1a) * tx1;
                            float cd1 = h1c + (h1d - h1c) * tx1;
                            float noise1 = ab1 + (cd1 - ab1) * ty1;
                            float ab2 = h2a + (h2b - h2a) * tx2;
                            float cd2 = h2c + (h2d - h2c) * tx2;
                            float noise2 = ab2 + (cd2 - ab2) * ty2;
                            float noise = (noise1 - 0.5f) * 2f * a1 + (noise2 - 0.5f) * 2f * a2;
                            float f = 0.5f - (dg + noise) / twoHalfEdge;
                            cov = f < 0f ? 0f : (f > 1f ? 1f : f);
                        }
                    }
                    covGrid[idx] = cov;
                    if (cov > 0f)
                    {
                        covered += cov;
                        if (x < lo) lo = x;
                        hi = x;
                    }
                }

                _rowLo[y] = lo;
                _rowHi[y] = hi;
                if (hi >= lo)
                {
                    if (y < _jobCovY0) _jobCovY0 = y;
                    _jobCovY1 = y;
                }
                used += rowCost + inBand * 5;
                if (used >= budget && y < y1)
                {
                    _jobRow = y + 1;
                    _jobCovered = covered;
                    return used;
                }
            }

            _jobCovered = covered;
            _stage = StageLand;
            return used;
        }

        // ------------------------------------------------------------------ stage 4: land

        /// <summary>Put the prepared splat on the wall. The only stage that changes the wall, and never split.</summary>
        private void Land()
        {
            double covered = _jobCovered;
            if (covered <= 0.0) return;
            int ci = _job.Color;
            float R = _job.Radius;
            int x0 = _jobX0, y0 = _jobY0, x1 = _jobX1, y1 = _jobY1;

            double coveredArea = covered * _cellArea;
            double splatVolume = _s.SplatThickness * coveredArea;
            // A big splat brings extra paint that slides down as the curtain (none at CurtainStrength 0, and none
            // on a floor or a ceiling, where nothing can slide).
            double curtainVolume = _runs ? CurtainShare(R) * _s.CurtainPaint * coveredArea : 0.0;
            splatVolume += curtainVolume;

            // Running drips of the other colour that this splat lands on are buried by it.
            BuryOtherColourHeads(ci, x0, y0, x1, y1);

            // ---- The paint that moves after landing; it comes out of this splat's budget ----
            //   a wall or a slope: drip heads along the lower rim (sag, curtain, drips);
            //   a floor: the body settles outward a little, like a wet pool;
            //   a ceiling: the paint that would have run gathers and falls as drops.
            Rng rng = _jobRng;
            int firstHead = _headCount;
            double headVolume;
            if (_runs)
                headVolume = SpawnRimHeads(ref rng, ci, R, _jobCx, _jobCy, _jobFwd, _jobSide, _jobStretch, _jobAcross,
                    splatVolume - curtainVolume, curtainVolume, x0, y0, x1, y1);
            else if (_gravOff < 0f)
                headVolume = StartSettling(ref rng, ci, R, _jobCx, _jobCy, _jobFwd, _jobSide, _jobStretch, _jobAcross, splatVolume);
            else
                headVolume = StartHangingDrops(ref rng, ci, R, _jobCx, _jobCy, splatVolume);
            // Paint landing on paint that is still wet runs more (Settings.WetOnWetBoost): measured as it lands.
            bool measureWet = _s.WetOnWetBoost > 0f && _headCount > firstHead;
            double wetUnder = 0.0;

            // ---- The coverage goes onto the wall, and the rest of the paint lies where the splat covers ----
            float perCov = (float)((splatVolume - headVolume) / coveredArea); // metres of film per unit of coverage
            float[] mine = _field[ci], other = _field[1 - ci], wet = _wet, thick = _thick, covGrid = _cov;
            int width = Width, height = Height;
            int mx0 = int.MaxValue, my0 = int.MaxValue, mx1 = int.MinValue, my1 = int.MinValue; // where this colour grew
            int ox0 = int.MaxValue, oy0 = int.MaxValue, ox1 = int.MinValue, oy1 = int.MinValue; // where the other colour shrank
            for (int y = _jobCovY0; y <= _jobCovY1; y++)
            {
                int lo = _rowLo[y], hi = _rowHi[y];
                if (hi < lo) continue;
                int row = y * width;
                int wlo = int.MaxValue, whi = int.MinValue;
                for (int x = lo; x <= hi; x++)
                {
                    int i = row + x;
                    float cov = covGrid[i];
                    if (cov <= 0f) continue;
                    if (measureWet) wetUnder += wet[i] * cov;
                    if (cov > mine[i])
                    {
                        mine[i] = cov;
                        if (x < mx0) mx0 = x;
                        if (x > mx1) mx1 = x;
                        if (y < my0) my0 = y;
                        my1 = y;
                    }
                    float lim = 1f - cov;
                    if (other[i] > lim)
                    {
                        other[i] = lim;
                        if (x < ox0) ox0 = x;
                        if (x > ox1) ox1 = x;
                        if (y < oy0) oy0 = y;
                        oy1 = y;
                    }
                    if (cov > wet[i])
                    {
                        wet[i] = cov;
                        if (x < wlo) wlo = x;
                        whi = x;
                    }
                    thick[i] += perCov * cov;
                }
                if (whi >= wlo)
                {
                    ref TexelRect bandRect = ref _wetBand[y * WetBands / height];
                    bandRect.Include(wlo, y);
                    bandRect.Include(whi, y);
                }
            }
            if (mx1 >= mx0)
            {
                TexelRect grew = new TexelRect(mx0, my0, mx1, my1);
                _shapeDirty[ci].Include(grew);
                _extent[ci].Include(grew);
            }
            if (ox1 >= ox0) _shapeDirty[1 - ci].Include(new TexelRect(ox0, oy0, ox1, oy1));
            _texDirty.Include(new TexelRect(x0, y0, x1, y1));
            _deposited += splatVolume;

            if (measureWet && wetUnder > 0.0)
            {
                // Half the splat on fresh paint already counts as fully wet-on-wet.
                float wetShare = MathUtil.Saturate((float)(2.0 * wetUnder / covered));
                float quicker = 1f + _s.WetOnWetBoost * wetShare;
                for (int k = firstHead; k < _headCount; k++)
                {
                    _heads[k].BaseSpeed *= quicker;
                    _heads[k].Film = 1f / quicker;   // and it leaves a thinner trail, so it goes further too
                }
            }
        }

        private void AddRagged(ref Rng rng, float ax, float ay, float radius, float raggedness, float blend)
        {
            if (_partCount >= MaxSplatParts || _raggedCount >= MaxRaggedParts) return;
            int table = _raggedCount * RaggedTableSize;
            _raggedCount++;

            // Radius by angle: a sum of random waves around the outline (2 to 8 bumps per turn), the slow waves
            // strongest. The table runs from angle -pi to +pi, matching Atan2.
            const int FirstWave = 2, LastWave = 8;
            float amp0 = 0f;
            for (int k = FirstWave; k <= LastWave; k++) amp0 += 1f / MathF.Pow(k, 0.7f);
            float norm = raggedness * 1.6f / amp0;
            for (int i = 0; i < RaggedTableSize; i++) _raggedRadius[table + i] = 0f;
            for (int k = FirstWave; k <= LastWave; k++)
            {
                float amp = rng.Range(0.2f, 1f) / MathF.Pow(k, 0.7f) * norm;
                float phase = rng.Range(0f, 2f * MathF.PI);
                for (int i = 0; i < RaggedTableSize; i++)
                {
                    float th = ((float)i / RaggedTableSize - 0.5f) * 2f * MathF.PI;
                    _raggedRadius[table + i] += amp * MathF.Cos(k * th + phase);
                }
            }
            float maxR = radius;
            for (int i = 0; i < RaggedTableSize; i++)
            {
                float r = radius * (1f + _raggedRadius[table + i]);
                if (r < radius * 0.45f) r = radius * 0.45f;
                if (r > maxR) maxR = r;
                _raggedRadius[table + i] = r;
            }

            ref SplatPart p = ref _parts[_partCount++];
            p.Ragged = true;
            p.Ax = ax; p.Ay = ay; p.Bx = ax; p.By = ay;
            p.Ra = radius; p.Rb = radius;
            p.Blend = blend;
            p.Table = table;
            p.Cx = ax; p.Cy = ay; p.Reach = maxR;
        }

        private void AddStick(float ax, float ay, float bx, float by, float ra, float rb, float blend)
        {
            if (_partCount >= MaxSplatParts) return;
            ref SplatPart p = ref _parts[_partCount++];
            p.Ragged = false;
            p.Ax = ax; p.Ay = ay; p.Bx = bx; p.By = by;
            p.Ra = ra; p.Rb = rb;
            p.Blend = blend;
            p.Table = -1;
            p.Cx = (ax + bx) * 0.5f; p.Cy = (ay + by) * 0.5f;
            float hx = (bx - ax) * 0.5f, hy = (by - ay) * 0.5f;
            p.Reach = MathF.Sqrt(hx * hx + hy * hy) + MathF.Max(ra, rb);
        }
    }
}
