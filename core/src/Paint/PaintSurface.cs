using System;

namespace Portaballs.Core
{
    /// <summary>How much paint is where, for checking that paint is never made out of nothing. Volumes in cubic metres.</summary>
    public struct PaintBudget
    {
        /// <summary>Everything splats and paint helpers have ever put on this wall since the last Clear.</summary>
        public double Deposited;
        /// <summary>Paint lying on the wall now.</summary>
        public double OnWall;
        /// <summary>Paint still carried by running drip heads.</summary>
        public double InDrips;
        /// <summary>Paint that ran off the bottom edge.</summary>
        public double Spilled;
        /// <summary>Paint removed by erasing or covered over by the other colour's heads being cancelled.</summary>
        public double Erased;
        /// <summary>Paint that left for another surface: run-off reported at the bottom edge, and drops off a ceiling.</summary>
        public double HandedOver;

        /// <summary>Deposited minus everything accounted for. Should stay at (almost) zero.</summary>
        public double Unaccounted
        {
            get { return Deposited - OnWall - InDrips - Spilled - Erased - HandedOver; }
        }
    }

    /// <summary>
    /// The paint on one wall: a grid of texels laid over the wall rectangle. Each texel holds
    ///   - an orange field and a blue field (0..1). The paint edge is where a field crosses 0.5. Near an edge the
    ///     field is a straight ramp a few texels wide, so a bilinear sample plus a smoothstep in a shader draws a
    ///     clean, anti-aliased outline at any zoom;
    ///   - a wetness value (1 just painted, fading to 0 as it dries) for the shader's shine;
    ///   - a paint thickness (metres of film), which is the paint budget that drips use up.
    /// Texel (0, 0) is the lower-left corner of the wall; x runs along U (right), y along V (up).
    ///
    /// The surface need not be a wall. It works out which way gravity pulls along itself from its own frame
    /// (<see cref="GravityAlong"/>): paint runs down a wall or a slope; on a floor it settles and poured paint grows a
    /// puddle; on a ceiling it gathers and falls as drops (PaintSurface.Pools.cs).
    ///
    /// Use: <see cref="Splat"/> when a ball hits, <see cref="Step"/> once per frame, <see cref="CopyDirtyToRgba"/>
    /// to update the texture, and an <see cref="OpeningFinder"/> to find portal openings.
    /// </summary>
    public sealed partial class PaintSurface : IPaintMask
    {
        private readonly WallFrame _wall;
        private readonly PaintSettings _s;

        /// <summary>Grid size in texels.</summary>
        public readonly int Width;
        public readonly int Height;

        /// <summary>Size of one texel along U and V in metres (almost exactly 1 / TexelsPerMeter).</summary>
        public readonly float TexelSizeU;
        public readonly float TexelSizeV;

        private readonly float _cellMin;
        private readonly float _cellArea;
        private readonly float _halfEdge;       // half-width of the edge ramp, metres

        private readonly float[][] _field;      // [colour][x + y * Width]
        private readonly float[] _wet;
        private readonly float[] _thick;        // metres of paint film

        // Scratch space reused by every splat so stamping never allocates.
        private readonly float[] _sdf;
        private readonly float[] _cov;

        private TexelRect _texDirty = TexelRect.Empty;   // texels whose texture bytes may have changed
        private readonly TexelRect[] _shapeDirty = { TexelRect.Empty, TexelRect.Empty }; // per colour, for the opening finder
        // Per colour: a rectangle that holds every texel where that colour's field may be above zero. It only grows
        // (by whole stamp boxes, so it costs nothing per texel) until the colour is erased or the wall is cleared.
        private readonly TexelRect[] _extent = { TexelRect.Empty, TexelRect.Empty };

        private double _accum;
        private double _time;

        private double _deposited;
        private double _spilled;
        private double _erased;
        private double _handedOver;

        // Which way paint runs on this surface, worked out once from the frame (see GravityAlong).
        private readonly float _gravU, _gravV;   // gravity along the surface, per unit of gravity
        private readonly float _gravOff;         // gravity along the normal: +1 a ceiling (pulls paint off), -1 a floor
        private readonly float _runX, _runY;     // unit direction paint runs in (0, -1 on a wall); 0, 0 when it cannot run
        private readonly float _sideX, _sideY;   // across that, to the right of it as you look at the surface (1, 0 on a wall)
        private readonly float _runPull;         // how hard gravity pulls along the surface, 0..1 (1 on a wall)
        private readonly bool _runs;             // paint can run along this surface at all

        /// <summary>A surface tilted less than this from level (gravity along it under this share) holds its paint: nothing runs.</summary>
        public const float MinRunPull = 0.08f;

        /// <param name="wall">The wall this paint covers.</param>
        /// <param name="settings">Tuning; a private copy is kept. Null uses the defaults.</param>
        public PaintSurface(WallFrame wall, PaintSettings settings = null)
        {
            _wall = wall;
            _s = settings != null ? settings.Clone() : new PaintSettings();
            Width = Math.Max(1, (int)MathF.Round(wall.Width * _s.TexelsPerMeter));
            Height = Math.Max(1, (int)MathF.Round(wall.Height * _s.TexelsPerMeter));
            TexelSizeU = wall.Width / Width;
            TexelSizeV = wall.Height / Height;
            _cellMin = MathF.Min(TexelSizeU, TexelSizeV);
            _cellArea = TexelSizeU * TexelSizeV;
            _halfEdge = MathF.Max(0.5f, _s.EdgeSoftnessTexels) * _cellMin;

            int n = Width * Height;
            _field = new[] { new float[n], new float[n] };
            _wet = new float[n];
            _thick = new float[n];
            _sdf = new float[n];
            _cov = new float[n];
            _colFwd = new float[Width];
            _colSide = new float[Width];
            _colCell1 = new int[Width];
            _colCell2 = new int[Width];
            _colMix1 = new float[Width];
            _colMix2 = new float[Width];
            _rowLo = new int[Height];
            _rowHi = new int[Height];
            _heads = new DripHead[Math.Max(1, _s.MaxDrips)];

            // Gravity along this surface: (dot(down, U), dot(down, V)), down = (0, 0, -1). A wall gets (0, -1), a floor
            // or a ceiling (0, 0). Values a rounding error away from 0 or 1 are snapped, so an upright wall runs
            // exactly straight down.
            Vec3 down = new Vec3(0f, 0f, -1f);
            _gravU = Snap(Vec3.Dot(down, wall.U));
            _gravV = Snap(Vec3.Dot(down, wall.V));
            _gravOff = Snap(Vec3.Dot(down, wall.Normal));
            _runPull = MathF.Sqrt(_gravU * _gravU + _gravV * _gravV);
            if (_runPull > 1f) _runPull = 1f;
            _runs = _runPull >= MinRunPull;
            if (_runs)
            {
                float len = MathF.Sqrt(_gravU * _gravU + _gravV * _gravV);
                _runX = Snap(_gravU / len);
                _runY = Snap(_gravV / len);
                _sideX = Snap(-_runY);
                _sideY = Snap(_runX);
            }

            // Write to every big buffer once now. Fresh memory is only really handed over by the system when it is
            // first written, and that would otherwise add a fraction of a millisecond to the first splat on this wall.
            Array.Clear(_field[0], 0, n);
            Array.Clear(_field[1], 0, n);
            Array.Clear(_wet, 0, n);
            Array.Clear(_thick, 0, n);
            Array.Clear(_sdf, 0, n);
            Array.Clear(_cov, 0, n);
        }

        /// <summary>The wall this paint lives on.</summary>
        public WallFrame Wall
        {
            get { return _wall; }
        }

        /// <summary>The settings this surface runs with (its own copy; changing it after creation is not supported).</summary>
        public PaintSettings Settings
        {
            get { return _s; }
        }

        /// <summary>Simulated time in seconds (whole inner steps only).</summary>
        public double Time
        {
            get { return _time; }
        }

        /// <summary>True when no drips are running, no puddle is growing, no drop is waiting to fall and no queued splat is still to land, so the painted shape will not change by itself any more.</summary>
        public bool IsSettled
        {
            get { return _headCount == 0 && _queueCount == 0 && _poolsActive == 0 && _hangCount == 0; }
        }

        /// <summary>True when settled and every texel has dried, so the texture will not change by itself any more.</summary>
        public bool IsDry
        {
            get { return IsSettled && !AnyWet; }
        }

        // ------------------------------------------------------------------ gravity along the surface

        // Values a rounding error away from 0, 1 or -1 become exactly that (and never minus zero).
        private static float Snap(float v)
        {
            if (MathF.Abs(v) < 1e-4f) return 0f;
            if (MathF.Abs(v - 1f) < 1e-4f) return 1f;
            if (MathF.Abs(v + 1f) < 1e-4f) return -1f;
            return v;
        }

        /// <summary>
        /// Which way gravity pulls along this surface, in its own coordinates and per unit of gravity:
        /// (dot(down, U), dot(down, V)) with down = (0, 0, -1). An upright wall gives (0, -1): paint runs down V.
        /// A floor or a ceiling gives (0, 0): nothing runs along it. A slope gives something in between, and paint
        /// runs along it more slowly. Worked out from the frame when the surface is made.
        /// </summary>
        public Vec2 GravityAlong
        {
            get { return new Vec2(_gravU, _gravV); }
        }

        /// <summary>
        /// How hard gravity pulls paint OFF this surface, per unit of gravity: dot(down, Normal), the normal pointing
        /// into the room. 0 on a wall, +1 on a ceiling (paint hangs and drops fall), -1 on a floor (paint lies on it).
        /// </summary>
        public float GravityOff
        {
            get { return _gravOff; }
        }

        /// <summary>True when paint can run along this surface (a wall or a slope steeper than about 5 degrees).</summary>
        public bool PaintRuns
        {
            get { return _runs; }
        }

        /// <summary>The unit direction paint runs in on this surface (0, -1 on a wall), or (0, 0) when it cannot run.</summary>
        public Vec2 RunDirection
        {
            get { return new Vec2(_runX, _runY); }
        }

        /// <summary>True for a surface paint lies on without running: a floor (or anything nearly level, facing up).</summary>
        public bool IsFloorLike
        {
            get { return !_runs && _gravOff < 0f; }
        }

        /// <summary>True for a surface paint hangs under without running: a ceiling (or anything nearly level, facing down).</summary>
        public bool IsCeilingLike
        {
            get { return !_runs && _gravOff > 0f; }
        }

        // ------------------------------------------------------------------ reading

        /// <summary>Raw field value of one texel (0..1, edge at 0.5).</summary>
        public float GetField(PaintColor color, int x, int y)
        {
            return _field[(int)color][x + y * Width];
        }

        /// <summary>Wetness of one texel (1 fresh .. 0 dry).</summary>
        public float GetWetness(int x, int y)
        {
            return _wet[x + y * Width];
        }

        /// <summary>Paint film thickness of one texel in metres.</summary>
        public float GetThickness(int x, int y)
        {
            return _thick[x + y * Width];
        }

        /// <summary>Wall coordinates (metres) of a texel's centre.</summary>
        public Vec2 TexelCenter(int x, int y)
        {
            return new Vec2((x + 0.5f) * TexelSizeU, (y + 0.5f) * TexelSizeV);
        }

        /// <summary>Bilinear sample of a colour's field at wall coordinates, the same way a GPU samples the texture.</summary>
        public float SampleField(PaintColor color, Vec2 uv)
        {
            return Bilinear(_field[(int)color], uv.X, uv.Y);
        }

        /// <summary>True when uv (metres) is covered by solid paint of that colour: the bilinear field is at least 0.5.</summary>
        public bool IsPainted(PaintColor color, Vec2 uv)
        {
            if (!(uv.X >= 0f && uv.X <= _wall.Width && uv.Y >= 0f && uv.Y <= _wall.Height)) return false;
            return Bilinear(_field[(int)color], uv.X, uv.Y) >= 0.5f;
        }

        private float Bilinear(float[] f, float u, float v)
        {
            float fx = u / TexelSizeU - 0.5f;
            float fy = v / TexelSizeV - 0.5f;
            if (fx < 0f) fx = 0f; else if (fx > Width - 1) fx = Width - 1;
            if (fy < 0f) fy = 0f; else if (fy > Height - 1) fy = Height - 1;
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = x0 + 1 < Width ? x0 + 1 : x0;
            int y1 = y0 + 1 < Height ? y0 + 1 : y0;
            float tx = fx - x0, ty = fy - y0;
            float a = f[x0 + y0 * Width], b = f[x1 + y0 * Width];
            float c = f[x0 + y1 * Width], d = f[x1 + y1 * Width];
            float ab = a + (b - a) * tx;
            float cd = c + (d - c) * tx;
            return ab + (cd - ab) * ty;
        }

        /// <summary>Where the paint is and has gone. Walks the whole grid, so it is for tests and debugging.</summary>
        public PaintBudget ComputeBudget()
        {
            double onWall = 0.0;
            for (int i = 0; i < _thick.Length; i++) onWall += _thick[i];
            onWall *= _cellArea;
            double inDrips = 0.0;
            for (int i = 0; i < _headCount; i++) inDrips += (double)_heads[i].V + _heads[i].Pending + _heads[i].Carry;
            inDrips += PaintWaitingInPools();
            return new PaintBudget
            {
                Deposited = _deposited,
                OnWall = onWall,
                InDrips = inDrips,
                Spilled = _spilled,
                Erased = _erased,
                HandedOver = _handedOver,
            };
        }

        // ------------------------------------------------------------------ time

        /// <summary>
        /// Advance the sagging, drips and drying by dt seconds (one call per frame).
        /// With Settings.EvenSubsteps (the default) the frame is cut into equal inner steps no longer than
        /// Settings.SimStep, so the paint moves by exactly this frame's time and looks smooth at any frame rate;
        /// 144 fps and 30 fps give nearly the same picture (a handful of texels differ). With it off, whole fixed steps
        /// of SimStep run and the remainder is carried, which gives exactly the same picture at any frame rate.
        /// With Settings.StrokeEveryFrame (the default) every moving head then paints its trail up to where it is, so
        /// each frame's texture shows the paint where it really is.
        /// </summary>
        public void Step(float dt)
        {
            _runOffCount = 0;   // what left this surface is reported for one Step only
            if (!(dt > 0f)) return;
            if (dt > 0.25f) dt = 0.25f;
            double h = _s.SimStep;
            int max = Math.Max(1, _s.MaxSubstepsPerFrame);
            if (_s.EvenSubsteps)
            {
                int n = (int)Math.Ceiling(dt / h - 1e-6);
                if (n < 1) n = 1;
                if (n > max)
                {
                    n = max;
                    dt = (float)(max * h); // fell behind (a long hitch): drop the rest instead of snowballing
                }
                float sub = dt / n;
                for (int i = 0; i < n && _headCount > 0; i++) DripSubstep(sub);
                _time += dt;
                DecayWetness(dt);
            }
            else
            {
                _accum += dt;
                int n = 0;
                while (_accum >= h - 1e-9 && n < max)
                {
                    if (_headCount > 0) DripSubstep((float)h);
                    _accum -= h;
                    n++;
                }
                if (_accum >= h) _accum = 0.0; // fell behind (a long hitch): drop the backlog instead of snowballing
                if (n > 0)
                {
                    _time += n * h;
                    DecayWetness((float)(n * h));
                }
            }
            if (_s.StrokeEveryFrame && _headCount > 0) StrokeMovedHeads();
            // Puddles and hanging drops are slow and smooth: one step a frame is plenty, at any frame rate.
            if (_poolsActive > 0) StepPools(dt);
            if (_hangCount > 0) StepHangingDrops(dt);
        }

        // Drying is spread over horizontal bands of the wall, one band per frame, so each frame only touches (and
        // re-uploads) a quarter of the wet area. Each band remembers how much drying time it still owes.
        private const int WetBands = 4;
        private readonly TexelRect[] _wetBand = { TexelRect.Empty, TexelRect.Empty, TexelRect.Empty, TexelRect.Empty };
        private readonly float[] _wetOwed = new float[WetBands];
        private int _wetPhase;

        private bool AnyWet
        {
            get
            {
                for (int k = 0; k < WetBands; k++) if (!_wetBand[k].IsEmpty) return true;
                return false;
            }
        }

        private void MarkWet(int x, int y)
        {
            _wetBand[y * WetBands / Height].Include(x, y);
        }

        private void DecayWetness(float seconds)
        {
            for (int k = 0; k < WetBands; k++) _wetOwed[k] += seconds;
            int band = _wetPhase;
            _wetPhase = (_wetPhase + 1) % WetBands;
            float owed = _wetOwed[band];
            // Paint now takes 7 s to dry instead of 3, so one visit changes the wetness by less than the texture
            // can show. A band waits its next turn until it owes a change worth sending (Settings.WetStepLevels
            // steps of the texture's 255): the same drying, half to a third of the work and of the upload.
            if (owed * 255f < _s.WetStepLevels * MathF.Max(0.01f, _s.WetFadeTime) && !_wetBand[band].IsEmpty) return;
            _wetOwed[band] = 0f;
            TexelRect r = _wetBand[band];
            if (r.IsEmpty) return;
            float drop = owed / MathF.Max(0.01f, _s.WetFadeTime);
            TexelRect still = TexelRect.Empty;
            for (int y = r.Y0; y <= r.Y1; y++)
            {
                int row = y * Width;
                for (int x = r.X0; x <= r.X1; x++)
                {
                    int i = row + x;
                    float w = _wet[i];
                    if (w <= 0f) continue;
                    w -= drop;
                    if (w <= 0f) w = 0f; else still.Include(x, y);
                    _wet[i] = w;
                }
            }
            _texDirty.Include(r);
            _wetBand[band] = still;
        }
        // ------------------------------------------------------------------ writing helpers

        /// <summary>Lay paint of colour c with coverage cov (0..1) on texel i, and remove the other colour under it.</summary>
        private void Cover(int i, int x, int y, int c, float cov)
        {
            if (cov <= 0f) return;
            float[] f = _field[c];
            float[] g = _field[1 - c];
            bool changed = false;
            if (cov > f[i]) { f[i] = cov; _shapeDirty[c].Include(x, y); changed = true; }
            float lim = 1f - cov;
            if (g[i] > lim) { g[i] = lim; _shapeDirty[1 - c].Include(x, y); changed = true; }
            if (cov > _wet[i]) { _wet[i] = cov; MarkWet(x, y); changed = true; }
            if (changed) _texDirty.Include(x, y);
        }

        /// <summary>Texel index range covering wall coordinates [u0, u1] x [v0, v1], clipped to the grid. False when off the wall.</summary>
        private bool TexelRange(float u0, float v0, float u1, float v1, out int x0, out int y0, out int x1, out int y1)
        {
            x0 = (int)MathF.Floor(u0 / TexelSizeU - 0.5f);
            y0 = (int)MathF.Floor(v0 / TexelSizeV - 0.5f);
            x1 = (int)MathF.Ceiling(u1 / TexelSizeU - 0.5f);
            y1 = (int)MathF.Ceiling(v1 / TexelSizeV - 0.5f);
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            if (x1 > Width - 1) x1 = Width - 1;
            if (y1 > Height - 1) y1 = Height - 1;
            return x0 <= x1 && y0 <= y1;
        }

        /// <summary>Add a volume of paint at one point, spread bilinearly over the four nearest texels.</summary>
        private void DepositAt(float u, float v, double volume)
        {
            if (volume <= 0.0) return;
            float fx = u / TexelSizeU - 0.5f;
            float fy = v / TexelSizeV - 0.5f;
            if (fx < 0f) fx = 0f; else if (fx > Width - 1) fx = Width - 1;
            if (fy < 0f) fy = 0f; else if (fy > Height - 1) fy = Height - 1;
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = x0 + 1 < Width ? x0 + 1 : x0;
            int y1 = y0 + 1 < Height ? y0 + 1 : y0;
            float tx = fx - x0, ty = fy - y0;
            float t = (float)(volume / _cellArea);
            _thick[x0 + y0 * Width] += t * (1f - tx) * (1f - ty);
            _thick[x1 + y0 * Width] += t * tx * (1f - ty);
            _thick[x0 + y1 * Width] += t * (1f - tx) * ty;
            _thick[x1 + y1 * Width] += t * tx * ty;
            _texDirty.Include(x0, y0);
            _texDirty.Include(x1, y1);
        }

        // ------------------------------------------------------------------ simple shapes, clearing, erasing

        /// <summary>
        /// Paint an exact disc (no splat shape, no drips). For level set-up, debugging and tests.
        /// Leaves Settings.SplatThickness of paint, which counts towards the budget.
        /// </summary>
        public void PaintDisc(PaintColor color, Vec2 center, float radius)
        {
            PaintShape(color, 0, center, new Vec2(radius, radius));
        }

        /// <summary>Paint an exact axis-aligned rectangle from min to max (wall metres). No drips.</summary>
        public void PaintBox(PaintColor color, Vec2 min, Vec2 max)
        {
            PaintShape(color, 1, (min + max) * 0.5f, (max - min) * 0.5f);
        }

        private void PaintShape(PaintColor color, int kind, Vec2 c, Vec2 half)
        {
            FinishQueuedSplats();
            float m = 2f * _halfEdge;
            if (!TexelRange(c.X - half.X - m, c.Y - half.Y - m, c.X + half.X + m, c.Y + half.Y + m,
                out int x0, out int y0, out int x1, out int y1)) return;
            int ci = (int)color;
            _extent[ci].Include(new TexelRect(x0, y0, x1, y1));
            double added = 0.0;
            for (int y = y0; y <= y1; y++)
            {
                float v = (y + 0.5f) * TexelSizeV;
                for (int x = x0; x <= x1; x++)
                {
                    float u = (x + 0.5f) * TexelSizeU;
                    float d;
                    if (kind == 0)
                    {
                        float dx = u - c.X, dy = v - c.Y;
                        d = MathF.Sqrt(dx * dx + dy * dy) - half.X;
                    }
                    else
                    {
                        float qx = MathF.Abs(u - c.X) - half.X;
                        float qy = MathF.Abs(v - c.Y) - half.Y;
                        float ox = MathF.Max(qx, 0f), oy = MathF.Max(qy, 0f);
                        d = MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(MathF.Max(qx, qy), 0f);
                    }
                    float cov = PaintMath.FieldFromDistance(d, _halfEdge);
                    if (cov <= 0f) continue;
                    int i = x + y * Width;
                    Cover(i, x, y, ci, cov);
                    float t = _s.SplatThickness * cov;
                    _thick[i] += t;
                    added += t;
                }
            }
            _deposited += added * _cellArea;
        }

        /// <summary>Remove all paint and drips and reset the paint budget. The whole texture becomes dirty.</summary>
        public void Clear()
        {
            DropQueuedSplats();
            Array.Clear(_field[0], 0, _field[0].Length);
            Array.Clear(_field[1], 0, _field[1].Length);
            Array.Clear(_wet, 0, _wet.Length);
            Array.Clear(_thick, 0, _thick.Length);
            _headCount = 0;
            ClearPoolsAndDrops();
            _deposited = 0.0;
            _spilled = 0.0;
            _erased = 0.0;
            _handedOver = 0.0;
            for (int k = 0; k < WetBands; k++) { _wetBand[k] = TexelRect.Empty; _wetOwed[k] = 0f; }
            TexelRect all = new TexelRect(0, 0, Width - 1, Height - 1);
            _texDirty = all;
            _shapeDirty[0] = all;
            _shapeDirty[1] = all;
            _extent[0] = TexelRect.Empty;
            _extent[1] = TexelRect.Empty;
        }

        /// <summary>
        /// Wipe paint inside a disc (wall metres), with the same soft edge as painted paint.
        /// Pass a colour to wipe only that colour, or null for both. Drips of a wiped colour inside the disc go too.
        /// </summary>
        public void EraseDisc(Vec2 center, float radius, PaintColor? color = null)
        {
            FinishQueuedSplats();
            float m = 2f * _halfEdge;
            bool all = !color.HasValue;
            int only = all ? -1 : (int)color.Value;
            if (TexelRange(center.X - radius - m, center.Y - radius - m, center.X + radius + m, center.Y + radius + m,
                out int x0, out int y0, out int x1, out int y1))
            {
                double removed = 0.0;
                for (int y = y0; y <= y1; y++)
                {
                    float v = (y + 0.5f) * TexelSizeV;
                    for (int x = x0; x <= x1; x++)
                    {
                        float u = (x + 0.5f) * TexelSizeU;
                        float dx = u - center.X, dy = v - center.Y;
                        float cov = PaintMath.FieldFromDistance(MathF.Sqrt(dx * dx + dy * dy) - radius, _halfEdge);
                        if (cov <= 0f) continue;
                        int i = x + y * Width;
                        float keep = 1f - cov;
                        float fo = _field[0][i], fb = _field[1][i];
                        float share = 1f;
                        if (!all)
                        {
                            float sum = fo + fb;
                            share = sum > 1e-6f ? _field[only][i] / sum : 0f;
                        }
                        for (int c = 0; c < 2; c++)
                        {
                            if (!all && c != only) continue;
                            if (_field[c][i] > keep) { _field[c][i] = keep; _shapeDirty[c].Include(x, y); }
                        }
                        float t = _thick[i];
                        float take = t * cov * share;
                        _thick[i] = t - take;
                        removed += take;
                        if (all && _wet[i] > keep) _wet[i] = keep;
                        _texDirty.Include(x, y);
                    }
                }
                _erased += removed * _cellArea;
            }

            // Drips of the wiped colour inside the disc disappear with their paint.
            for (int k = 0; k < _headCount; k++)
            {
                ref DripHead h = ref _heads[k];
                if (!all && h.Color != only) continue;
                float dx = h.X - center.X, dy = h.Y - center.Y;
                if (dx * dx + dy * dy <= radius * radius)
                {
                    _erased += (double)h.V + h.Pending + h.Carry;
                    h.V = 0f;
                    h.Pending = 0f;
                    h.Carry = 0f;
                    h.Alive = false;
                }
            }
            CompactHeads();
            ErasePoolsAndDrops(only, center, radius);
        }

        /// <summary>
        /// Take one colour off the whole wall: its field, its share of the paint film and its drips. The other colour
        /// is not touched. The result is the same as EraseDisc with that colour and a disc bigger than the wall, but
        /// it does no distance maths and only marks the texels that really held the colour as changed, so both this
        /// call and the texture upload after it cost far less. For "this colour's patch has moved to another wall".
        /// </summary>
        public void EraseColor(PaintColor color)
        {
            FinishQueuedSplats();
            int c = (int)color;
            float[] f = _field[c];
            float[] g = _field[1 - c];
            double removed = 0.0;
            // Only the rectangle the colour has ever reached needs a look; everything outside it is already zero.
            TexelRect ext = _extent[c];
            _extent[c] = TexelRect.Empty;
            float[] thick = _thick;
            int cx0 = int.MaxValue, cy0 = int.MaxValue, cx1 = int.MinValue, cy1 = int.MinValue;
            for (int y = ext.Y0; y <= ext.Y1; y++)
            {
                int row = y * Width;
                int lo = int.MaxValue, hi = int.MinValue;
                for (int x = ext.X0; x <= ext.X1; x++)
                {
                    int i = row + x;
                    float fc = f[i];
                    if (fc <= 0f) continue;
                    float sum = fc + g[i];
                    float share = sum > 1e-6f ? fc / sum : 0f;
                    float take = thick[i] * share;
                    thick[i] -= take;
                    removed += take;
                    f[i] = 0f;
                    if (x < lo) lo = x;
                    hi = x;
                }
                if (hi >= lo)
                {
                    if (lo < cx0) cx0 = lo;
                    if (hi > cx1) cx1 = hi;
                    if (y < cy0) cy0 = y;
                    cy1 = y;
                }
            }
            if (cx1 >= cx0)
            {
                TexelRect changed = new TexelRect(cx0, cy0, cx1, cy1);
                _shapeDirty[c].Include(changed);
                _texDirty.Include(changed);
            }
            _erased += removed * _cellArea;

            // The colour's drips go with it, wherever they are.
            for (int k = 0; k < _headCount; k++)
            {
                ref DripHead h = ref _heads[k];
                if (h.Color != c) continue;
                _erased += (double)h.V + h.Pending + h.Carry;
                h.V = 0f;
                h.Pending = 0f;
                h.Carry = 0f;
                h.Alive = false;
            }
            CompactHeads();
            ErasePoolsAndDrops(c, default, float.MaxValue);
        }

        // ------------------------------------------------------------------ texture output

        /// <summary>The texels whose texture bytes may have changed since the last copy (empty when nothing did).</summary>
        public TexelRect DirtyRect
        {
            get { return _texDirty; }
        }

        /// <summary>Bytes needed for the full RGBA texture: Width * Height * 4.</summary>
        public int RgbaByteCount
        {
            get { return Width * Height * 4; }
        }

        /// <summary>
        /// Write the changed texels into a full-size RGBA8 buffer (Width * Height * 4 bytes, row 0 = bottom of the
        /// wall, so flip V when mapping it if your engine puts row 0 at the top) and forget the changes.
        /// Returns the rectangle written, which is the only part the game needs to upload. Channels:
        /// R = orange field, G = blue field, B = wetness, A = thickness (Settings.ThicknessForFullAlpha = 255).
        /// </summary>
        public TexelRect CopyDirtyToRgba(byte[] rgba)
        {
            TexelRect r = _texDirty;
            if (!r.IsEmpty) CopyRectToRgba(r, rgba);
            _texDirty = TexelRect.Empty;
            return r;
        }

        /// <summary>Write every texel into a full-size RGBA8 buffer (same layout as CopyDirtyToRgba). Does not touch the dirty rectangle.</summary>
        public void CopyAllToRgba(byte[] rgba)
        {
            CopyRectToRgba(new TexelRect(0, 0, Width - 1, Height - 1), rgba);
        }

        /// <summary>Write one rectangle of texels into a full-size RGBA8 buffer.</summary>
        public void CopyRectToRgba(TexelRect r, byte[] rgba)
        {
            if (rgba == null || rgba.Length < Width * Height * 4) throw new ArgumentException("RGBA buffer must hold Width * Height * 4 bytes.");
            if (r.X0 < 0) r.X0 = 0;
            if (r.Y0 < 0) r.Y0 = 0;
            if (r.X1 > Width - 1) r.X1 = Width - 1;
            if (r.Y1 > Height - 1) r.Y1 = Height - 1;
            float aScale = 255f / MathF.Max(1e-6f, _s.ThicknessForFullAlpha);
            float[] fo = _field[0], fb = _field[1], wet = _wet, thick = _thick;
            for (int y = r.Y0; y <= r.Y1; y++)
            {
                int row = y * Width;
                for (int x = r.X0; x <= r.X1; x++)
                {
                    int i = row + x;
                    int o = i * 4;
                    // The same rounding as ToByte, written out here: this loop runs for every changed texel, and a
                    // call per channel is most of its cost in an unoptimised build. Empty texels are the common case.
                    float a = fo[i], b = fb[i], c = wet[i], d = thick[i];
                    if (a <= 0f && b <= 0f && c <= 0f && d <= 0f)
                    {
                        rgba[o] = 0;
                        rgba[o + 1] = 0;
                        rgba[o + 2] = 0;
                        rgba[o + 3] = 0;
                        continue;
                    }
                    a = a * 255f + 0.5f;
                    b = b * 255f + 0.5f;
                    c = c * 255f + 0.5f;
                    d = d * aScale + 0.5f;
                    rgba[o] = a <= 0f ? (byte)0 : (a >= 255f ? (byte)255 : (byte)a);
                    rgba[o + 1] = b <= 0f ? (byte)0 : (b >= 255f ? (byte)255 : (byte)b);
                    rgba[o + 2] = c <= 0f ? (byte)0 : (c >= 255f ? (byte)255 : (byte)c);
                    rgba[o + 3] = d <= 0f ? (byte)0 : (d >= 255f ? (byte)255 : (byte)d);
                }
            }
        }

        private static byte ToByte(float v)
        {
            v += 0.5f;
            if (v <= 0f) return 0;
            if (v >= 255f) return 255;
            return (byte)v;
        }

        // ------------------------------------------------------------------ for the opening finder

        /// <summary>
        /// The texels where this colour's field changed since the last call, then forgets them.
        /// The <see cref="OpeningFinder"/> calls this; use one finder per surface.
        /// </summary>
        public TexelRect TakeShapeChanges(PaintColor color)
        {
            TexelRect r = _shapeDirty[(int)color];
            _shapeDirty[(int)color] = TexelRect.Empty;
            return r;
        }

        internal float[] FieldArray(PaintColor color)
        {
            return _field[(int)color];
        }
    }
}
