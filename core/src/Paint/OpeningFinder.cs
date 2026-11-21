using System;

namespace Portaballs.Core
{
    /// <summary>
    /// The tallest upright rectangle that is fully painted inside an opening's patch and at least a given width:
    /// roughly "the doorway you could walk through". All in wall metres. Edges are texel edges, so they can be up to
    /// half a texel generous.
    /// </summary>
    public struct DoorBand
    {
        /// <summary>False when no fully painted band that wide exists in the patch (or there is no patch).</summary>
        public bool Found;

        public float LeftU;
        public float RightU;
        public float BottomV;
        public float TopV;

        /// <summary>True when the band's bottom is within the asked distance of the floor (v = 0).</summary>
        public bool ReachesFloor;

        /// <summary>The opening Revision this was measured on.</summary>
        public int Revision;

        public float Width
        {
            get { return RightU - LeftU; }
        }

        public float Height
        {
            get { return TopV - BottomV; }
        }
    }

    /// <summary>
    /// Finds the portal opening in a colour's paint: starting from a seed point (normally the latest impact) it
    /// collects the connected painted patch, then finds the largest circle that fits inside it with a distance
    /// transform, plus the patch's bounding box and area.
    ///
    /// Two ways to use it, with the same answers:
    ///   - <see cref="Find"/> does the whole search before it returns (about 0.4 ms on a big patch in an
    ///     optimised build, 1.6 ms in an unoptimised one).
    ///   - <see cref="Request"/> asks for a search, and <see cref="Advance"/> (or <see cref="PaintPacer"/>, which
    ///     calls it for every wall) does a slice of it each frame. When <see cref="SearchesFinished"/> goes up,
    ///     read the answer with <see cref="Last"/>. The search works on a copy of the paint taken when it starts,
    ///     so its answer is exactly what Find would have returned at that moment, however it is sliced and
    ///     whatever the paint does meanwhile.
    ///
    /// Cheap to ask whenever the paint is dirty: if nothing changed next to the last patch and the seed is still
    /// on it, the last answer is returned without any work; otherwise only the patch and its box are visited.
    /// Make one finder per <see cref="PaintSurface"/> (it consumes the surface's change rectangles). Allocates only
    /// when created.
    /// </summary>
    public sealed class OpeningFinder
    {
        private readonly PaintSurface _surface;
        private readonly int _w, _h;
        private readonly float _su, _sv, _wallW, _wallH;

        // Search id of the patch each texel last belonged to. _label[colour] is the finished search that Last,
        // InLastPatch and FindDoor describe; the search in progress writes _spare, and the two swap when it finishes.
        private readonly int[][] _label;
        private int[] _spare;

        private readonly int[] _stack;
        private readonly float[] _snap;        // the colour's field as it was when the search started
        private readonly float[] _dist;        // squared distance (m^2) to the nearest unpainted texel, over the patch box
        private readonly int[] _colCount;      // per box column: painted texels in a row so far
        private readonly int[] _rowLo, _rowHi; // per box row: first and last column that is in the patch
        private readonly float[] _rowMax;      // per box row: the most headroom any of its texels has (squared, m^2)
        private readonly float[] _lineZ;
        private readonly int[] _lineV;
        private readonly float[] _dirX48, _dirY48, _dirX256, _dirY256;

        private int _searchId;
        private readonly int[] _lastId = new int[2];
        private readonly TexelRect[] _lastBox = { TexelRect.Empty, TexelRect.Empty };
        private readonly TexelRect[] _pendingChanges = { TexelRect.Empty, TexelRect.Empty };
        private readonly PortalOpening[] _last = new PortalOpening[2];
        private readonly ulong[] _lastShapeKey = new ulong[2];
        private readonly bool[] _haveLast = new bool[2];

        private readonly int[] _run;           // per texel: painted texels in a row ending here (door search)
        private readonly DoorBand[] _door = new DoorBand[2];
        private readonly float[] _doorWidth = { -1f, -1f };
        private readonly float[] _doorFloor = { -1f, -1f };
        private readonly int[] _doorId = new int[2];

        // ---- searches asked for with Request, and the one in progress ----
        private readonly bool[] _want = new bool[2];
        private readonly Vec2[] _wantSeed = new Vec2[2];
        private readonly int[] _finished = new int[2];
        private int _nextColor;

        private const int PhaseIdle = 0, PhaseFlood = 1, PhaseColumns = 2, PhaseRows = 3, PhaseRefine = 4;
        private int _phase;
        private int _jobColor;
        private Vec2 _jobSeed;
        private TexelRect _jobChanges;         // the changes this search set out to cover (put back if it is abandoned)
        private int _jobId;
        private int _sp, _count;               // flood: stack height, texels in the patch
        private int _fx0, _fy0, _fx1, _fy1;    // flood: the patch's box
        private ulong _keySum, _keyXor;
        private int _bx0, _by0, _bw, _bh;      // the box plus a one-texel border that is "outside"
        private int _sweep, _sweepRow;         // columns: 0 = counting up, 1 = counting down, and the next row
        private int _rowNext;                  // rows: next box row to look at
        private float _bound;                  // rows: a headroom some texel is already known to reach
        private float _best, _bestMid;
        private int _bestX, _bestY;
        private int _refine;                   // refine: 0 = first measure, 1 = nudging, 2 = final measure
        private Vec2 _center;
        private float _radius, _stepU, _stepV;
        private int _round, _dir;
        private bool _moved;

        // Work units charged per step, tuned so a unit costs about the same in every phase (and the same as a
        // splat's unit: see PaintSurface.WorkUnitsPerMillisecond).
        private const int WorkStart = 4000;
        private const int WorkPerFloodTexel = 3;
        private const int WorkPerRay = 36;

        /// <summary>How many texels the last search visited (0 when it reused the previous answer). For profiling.</summary>
        public int LastTexelsVisited { get; private set; }

        /// <summary>True when the last call reused the previous answer because nothing near the patch had changed.</summary>
        public bool LastWasCached { get; private set; }

        public OpeningFinder(PaintSurface surface)
        {
            _surface = surface ?? throw new ArgumentNullException(nameof(surface));
            _w = surface.Width;
            _h = surface.Height;
            _su = surface.TexelSizeU;
            _sv = surface.TexelSizeV;
            _wallW = surface.Wall.Width;
            _wallH = surface.Wall.Height;
            int n = _w * _h;
            _label = new[] { new int[n], new int[n] };
            _spare = new int[n];
            _stack = new int[n];
            _run = new int[n];
            _snap = new float[n];
            _dist = new float[(_w + 2) * (_h + 2)];
            _colCount = new int[_w + 2];
            _rowLo = new int[_h + 2];
            _rowHi = new int[_h + 2];
            _rowMax = new float[_h + 2];
            int m = Math.Max(_w, _h) + 3;
            _lineZ = new float[m + 1];
            _lineV = new int[m];
            _dirX48 = new float[48];
            _dirY48 = new float[48];
            _dirX256 = new float[256];
            _dirY256 = new float[256];
            FillDirections(_dirX48, _dirY48, 48);
            FillDirections(_dirX256, _dirY256, 256);
            // Write to every big buffer once now, so the first search does not pay for the system handing over
            // fresh memory (see the same note in PaintSurface).
            Array.Clear(_label[0], 0, n);
            Array.Clear(_label[1], 0, n);
            Array.Clear(_spare, 0, n);
            Array.Clear(_stack, 0, n);
            Array.Clear(_run, 0, n);
            Array.Clear(_snap, 0, n);
            Array.Clear(_dist, 0, _dist.Length);
            _last[0].Color = PaintColor.Orange;
            _last[1].Color = PaintColor.Blue;
        }

        private static void FillDirections(float[] dx, float[] dy, int rays)
        {
            for (int k = 0; k < rays; k++)
            {
                float a = k * (2f * MathF.PI / rays);
                dx[k] = MathF.Cos(a);
                dy[k] = MathF.Sin(a);
            }
        }

        /// <summary>The last opening found for a colour (without searching again).</summary>
        public PortalOpening Last(PaintColor color)
        {
            return _last[(int)color];
        }

        /// <summary>True when texel (x, y) belongs to the patch found by the latest finished search for this colour. For debug drawing.</summary>
        public bool InLastPatch(PaintColor color, int x, int y)
        {
            int c = (int)color;
            return _haveLast[c] && _lastId[c] != 0 && _label[c][x + y * _w] == _lastId[c];
        }

        /// <summary>
        /// Find (or refresh) the opening of one colour around a seed point in wall metres, all in this call.
        /// Valid is true when the largest circle inside the patch reaches Settings.OpeningMinRadius.
        /// Revision goes up whenever the patch's shape (or validity) changes.
        /// If a sliced search (see <see cref="Request"/>) is part-way through, it is put back in the queue and
        /// starts again later.
        /// </summary>
        public PortalOpening Find(PaintColor color, Vec2 seedUV)
        {
            int c = (int)color;
            if (_phase != PhaseIdle) AbandonJob();
            StartJob(c, seedUV);
            while (_phase != PhaseIdle) StepJob(int.MaxValue);
            return _last[c];
        }

        // ------------------------------------------------------------------ searching a slice at a time

        /// <summary>
        /// Ask for a fresh search of one colour around a seed point (wall metres), to be done in slices by
        /// <see cref="Advance"/>. Costs nothing by itself. Asking again before it has started just replaces the seed;
        /// asking while it is running lets that search finish and then runs one more.
        /// </summary>
        public void Request(PaintColor color, Vec2 seedUV)
        {
            int c = (int)color;
            _want[c] = true;
            _wantSeed[c] = seedUV;
        }

        /// <summary>True while a search asked for with <see cref="Request"/> for this colour has not finished.</summary>
        public bool IsPending(PaintColor color)
        {
            int c = (int)color;
            return _want[c] || (_phase != PhaseIdle && _jobColor == c);
        }

        /// <summary>True while any requested search is waiting or running.</summary>
        public bool HasWork
        {
            get { return _want[0] || _want[1] || _phase != PhaseIdle; }
        }

        /// <summary>True while a requested search is part-way through (it has started and not finished).</summary>
        public bool IsSearching
        {
            get { return _phase != PhaseIdle; }
        }

        /// <summary>
        /// Goes up by one each time a search for this colour finishes, whether through <see cref="Find"/> or in slices.
        /// Compare it with the value you saw last frame; when it has changed, <see cref="Last"/> holds a new answer
        /// (its Revision tells you whether the shape really changed).
        /// </summary>
        public int SearchesFinished(PaintColor color)
        {
            return _finished[(int)color];
        }

        /// <summary>
        /// Do a slice of the requested searches: at most about <paramref name="workUnits"/> units (see
        /// <see cref="PaintSurface.WorkUnitsPerMillisecond"/>). It always does at least one small step and may run
        /// over by one step. Returns the units used; 0 means nothing is waiting. The two colours take turns.
        /// </summary>
        public int Advance(int workUnits)
        {
            int used = 0;
            while (true)
            {
                if (_phase == PhaseIdle)
                {
                    int c;
                    if (_want[_nextColor]) c = _nextColor;
                    else if (_want[1 - _nextColor]) c = 1 - _nextColor;
                    else break;
                    _nextColor = 1 - c;
                    _want[c] = false;
                    used += StartJob(c, _wantSeed[c]);
                }
                else
                {
                    used += StepJob(workUnits - used);
                }
                if (used >= workUnits) break;
            }
            return used;
        }

        /// <summary>Give up the search in progress; it goes back in the queue and will start again from fresh paint.</summary>
        private void AbandonJob()
        {
            int c = _jobColor;
            _pendingChanges[c].Include(_jobChanges);
            if (!_want[c])
            {
                _want[c] = true;
                _wantSeed[c] = _jobSeed;
            }
            _phase = PhaseIdle;
        }

        /// <summary>
        /// Begin a search: look at what changed, answer at once if nothing near the last patch did (or there is no
        /// paint at the seed), otherwise copy the paint and get the flood fill ready. Returns the work used.
        /// </summary>
        private int StartJob(int c, Vec2 seedUV)
        {
            PaintColor color = (PaintColor)c;
            float[] field = _surface.FieldArray(color);
            _pendingChanges[c].Include(_surface.TakeShapeChanges(color));
            LastWasCached = false;
            LastTexelsVisited = 0;

            int seed = FindSeedTexel(field, seedUV);

            // Nothing could have changed: same patch, no edits touching it (a change has to touch the patch or its
            // one-texel border to alter it).
            if (_haveLast[c] && seed >= 0 && _lastId[c] != 0 && _label[c][seed] == _lastId[c]
                && !_pendingChanges[c].Intersects(_lastBox[c].Expanded(1)))
            {
                _pendingChanges[c] = TexelRect.Empty;
                LastWasCached = true;
                _finished[c]++;
                return 50;
            }
            _jobChanges = _pendingChanges[c];
            _pendingChanges[c] = TexelRect.Empty;

            if (seed < 0)
            {
                PortalOpening none = _last[c];
                none.Color = color;
                bool changed = none.Valid || none.Area != 0f;
                none.Valid = false;
                none.Area = 0f;
                none.InscribedRadius = 0f;
                if (changed || !_haveLast[c]) { none.Revision++; }
                _lastId[c] = 0;
                _lastBox[c] = TexelRect.Empty;
                _lastShapeKey[c] = 0;
                _haveLast[c] = true;
                _last[c] = none;
                _finished[c]++;
                return 200;
            }

            // The search runs on this copy, so paint that moves while it is being sliced cannot change its answer.
            Array.Copy(field, _snap, field.Length);

            // ---- 1. Flood fill the connected patch (4-neighbour) ----
            _searchId++;
            if (_searchId == int.MaxValue)
            {
                Array.Clear(_label[0], 0, _label[0].Length);
                Array.Clear(_label[1], 0, _label[1].Length);
                Array.Clear(_spare, 0, _spare.Length);
                _lastId[0] = 0;
                _lastId[1] = 0;
                _searchId = 1;
            }
            _jobId = _searchId;
            _jobColor = c;
            _jobSeed = seedUV;
            _spare[seed] = _jobId;
            _stack[0] = seed;
            _sp = 1;
            _count = 0;
            _fx0 = int.MaxValue; _fy0 = int.MaxValue; _fx1 = int.MinValue; _fy1 = int.MinValue;
            _keySum = 0;
            _keyXor = 0;
            _phase = PhaseFlood;
            return WorkStart;
        }

        private int StepJob(int budget)
        {
            if (_phase == PhaseFlood) return FloodStep(budget);
            if (_phase == PhaseColumns) return ColumnsStep(budget);
            if (_phase == PhaseRows) return RowsStep(budget);
            if (_phase == PhaseRefine) return RefineStep();
            return 0;
        }

        private int FloodStep(int budget)
        {
            int[] label = _spare, stack = _stack;
            float[] snap = _snap;
            int id = _jobId, w = _w, hLast = _h - 1, wLast = _w - 1;
            int sp = _sp, count = _count;
            int x0 = _fx0, y0 = _fy0, x1 = _fx1, y1 = _fy1;
            ulong keySum = _keySum, keyXor = _keyXor;
            int most = budget / WorkPerFloodTexel;
            if (most < 64) most = 64;
            int done = 0;
            while (sp > 0 && done < most)
            {
                int i = stack[--sp];
                int y = i / w;
                int x = i - y * w;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
                done++;
                // A fingerprint of the patch's shape (the Mix function written out twice).
                ulong hh = (ulong)i;
                hh ^= hh >> 33;
                hh *= 0xFF51AFD7ED558CCDUL;
                hh ^= hh >> 33;
                hh *= 0xC4CEB9FE1A85EC53UL;
                hh ^= hh >> 33;
                keySum += hh;
                ulong h2 = hh ^ 0x9E3779B97F4A7C15UL;
                h2 ^= h2 >> 33;
                h2 *= 0xFF51AFD7ED558CCDUL;
                h2 ^= h2 >> 33;
                h2 *= 0xC4CEB9FE1A85EC53UL;
                h2 ^= h2 >> 33;
                keyXor ^= h2;
                if (x > 0 && label[i - 1] != id && snap[i - 1] >= 0.5f) { label[i - 1] = id; stack[sp++] = i - 1; }
                if (x < wLast && label[i + 1] != id && snap[i + 1] >= 0.5f) { label[i + 1] = id; stack[sp++] = i + 1; }
                if (y > 0 && label[i - w] != id && snap[i - w] >= 0.5f) { label[i - w] = id; stack[sp++] = i - w; }
                if (y < hLast && label[i + w] != id && snap[i + w] >= 0.5f) { label[i + w] = id; stack[sp++] = i + w; }
            }
            _sp = sp;
            _count = count + done;
            _fx0 = x0; _fy0 = y0; _fx1 = x1; _fy1 = y1;
            _keySum = keySum;
            _keyXor = keyXor;
            if (sp == 0)
            {
                // ---- 2. Distance transform over the patch box plus a one-texel border that is "outside" ----
                _bx0 = x0 - 1;
                _by0 = y0 - 1;
                _bw = x1 - x0 + 3;
                _bh = y1 - y0 + 3;
                Array.Clear(_colCount, 0, _bw);
                _sweep = 0;
                _sweepRow = 0;
                _phase = PhaseColumns;
            }
            return done * WorkPerFloodTexel;
        }

        /// <summary>
        /// The up-and-down half of the distance transform: for every texel of the patch, how many patch texels lie
        /// between it and the nearest gap straight above or below. Two sweeps of the box, row by row.
        /// </summary>
        private int ColumnsStep(int budget)
        {
            int[] label = _spare, cnt = _colCount;
            float[] dist = _dist;
            int id = _jobId, w = _w, bw = _bw, bh = _bh, bx0 = _bx0, by0 = _by0;
            int used = 0;
            int rowCost = bw / 2 + 1;

            if (_sweep == 0)
            {
                // Counting up from the bottom; the count is parked in _dist until the sweep back down.
                for (int ly = _sweepRow; ly < bh; ly++)
                {
                    int row = ly * bw;
                    int lo = int.MaxValue, hi = int.MinValue;
                    if (ly == 0 || ly == bh - 1)
                    {
                        Array.Clear(dist, row, bw);
                    }
                    else
                    {
                        int g = (by0 + ly) * w + bx0;
                        dist[row] = 0f;
                        dist[row + bw - 1] = 0f;
                        for (int lx = 1; lx < bw - 1; lx++)
                        {
                            if (label[g + lx] == id)
                            {
                                int n = cnt[lx] + 1;
                                cnt[lx] = n;
                                dist[row + lx] = n;
                                if (lx < lo) lo = lx;
                                hi = lx;
                            }
                            else
                            {
                                cnt[lx] = 0;
                                dist[row + lx] = 0f;
                            }
                        }
                    }
                    _rowLo[ly] = lo;
                    _rowHi[ly] = hi;
                    used += rowCost;
                    if (used >= budget && ly < bh - 1)
                    {
                        _sweepRow = ly + 1;
                        return used;
                    }
                }
                Array.Clear(cnt, 0, bw);
                _sweep = 1;
                _sweepRow = bh - 1;
                if (used >= budget) return used;
            }

            // Counting down from the top: the smaller of the two counts is the distance to the nearest gap in the
            // column, and its square (in metres) is what the row pass needs.
            float w2 = _sv * _sv;
            for (int ly = _sweepRow; ly >= 0; ly--)
            {
                float rowMax = 0f;
                if (_rowHi[ly] >= _rowLo[ly])
                {
                    int row = ly * bw;
                    for (int lx = 1; lx < bw - 1; lx++)
                    {
                        int up = (int)dist[row + lx];
                        if (up > 0)
                        {
                            int down = cnt[lx] + 1;
                            cnt[lx] = down;
                            float dq = up < down ? up : down;
                            float f = w2 * dq * dq;
                            dist[row + lx] = f;
                            if (f > rowMax) rowMax = f;
                        }
                        else
                        {
                            cnt[lx] = 0;
                        }
                    }
                }
                else
                {
                    Array.Clear(cnt, 0, bw);   // a row with no patch texels at all
                }
                _rowMax[ly] = rowMax;
                used += rowCost;
                if (used >= budget && ly > 0)
                {
                    _sweepRow = ly - 1;
                    return used;
                }
            }

            // ---- Ready for the rows: first find a headroom that some texel certainly reaches ----
            // The row holding the tallest column is worked out first. Its best value is a floor under the final
            // answer, so every row whose tallest column falls short of it cannot hold the answer and is skipped.
            int tallest = 0;
            float most = -1f;
            for (int ly = 0; ly < bh; ly++)
            {
                if (_rowMax[ly] > most) { most = _rowMax[ly]; tallest = ly; }
            }
            _bound = ScanRow(tallest, false);
            _best = -1f;
            _bestX = 0;
            _bestY = 0;
            _bestMid = float.MaxValue;
            _rowNext = 0;
            _phase = PhaseRows;
            return used + bw * 3;
        }

        /// <summary>
        /// The sideways half of the distance transform (exact squared Euclidean distance, Felzenszwalb and
        /// Huttenlocher), keeping only what the search needs: the texel with the most room around it.
        /// </summary>
        private int RowsStep(int budget)
        {
            int used = 0;
            int bh = _bh;
            float[] rowMax = _rowMax;
            float bound = _bound;
            for (int ly = _rowNext; ly < bh; ly++)
            {
                used += 1;
                if (_rowHi[ly] < _rowLo[ly]) continue;                 // no patch texels in this row
                if (rowMax[ly] * 1.0001f < bound) continue;           // cannot reach what another row already has
                ScanRow(ly, true);
                used += (_rowHi[ly] - _rowLo[ly] + 3) * 3;
                if (used >= budget && ly < bh - 1)
                {
                    _rowNext = ly + 1;
                    return used;
                }
            }

            // ---- 3. Refine: from the best centre, find the true painted edge (bilinear 0.5 crossing) in many directions ----
            LastTexelsVisited = _count + _bw * _bh;
            _center = new Vec2((_bx0 + _bestX + 0.5f) * _su, (_by0 + _bestY + 0.5f) * _sv);
            _refine = 0;
            _phase = PhaseRefine;
            return used;
        }

        /// <summary>
        /// One row of the distance transform, from the gap just left of its first patch texel to the gap just right of
        /// its last. Returns the largest value in the row; with <paramref name="keep"/> it also offers every texel
        /// to the running "most room" record (ties go to the texel nearest the middle of the box).
        /// </summary>
        private float ScanRow(int ly, bool keep)
        {
            float[] dist = _dist, z = _lineZ;
            int[] v = _lineV;
            int rowBase = ly * _bw;
            int lo = _rowLo[ly], hi = _rowHi[ly];
            int start = lo - 1, end = hi + 1;
            float w = _su * _su;

            // Lower envelope of the parabolas rooted at each texel.
            int k = 0;
            v[0] = start;
            z[0] = float.NegativeInfinity;
            z[1] = float.PositiveInfinity;
            for (int q = start + 1; q <= end; q++)
            {
                float fq = dist[rowBase + q] + w * q * q;
                int p = v[k];
                float s = (fq - (dist[rowBase + p] + w * p * p)) / (2f * w * (q - p));
                while (k > 0 && s <= z[k])
                {
                    k--;
                    p = v[k];
                    s = (fq - (dist[rowBase + p] + w * p * p)) / (2f * w * (q - p));
                }
                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = float.PositiveInfinity;
            }

            float rowBest = 0f;
            float best = _best, bestMid = _bestMid;
            int bestX = _bestX, bestY = _bestY;
            float midX = (_bw - 1) * 0.5f, midY = (_bh - 1) * 0.5f;
            float my = ly - midY;
            k = 0;
            for (int q = lo; q <= hi; q++)
            {
                while (z[k + 1] < q) k++;
                int p = v[k];
                float dq = q - p;
                float d = w * dq * dq + dist[rowBase + p];
                if (d > rowBest) rowBest = d;
                if (!keep) continue;
                if (d < best - 1e-9f) continue;
                float mx = q - midX;
                float mid = mx * mx + my * my;
                if (d > best + 1e-9f || mid < bestMid)
                {
                    best = d;
                    bestX = q;
                    bestY = ly;
                    bestMid = mid;
                }
            }
            if (keep)
            {
                _best = best;
                _bestMid = bestMid;
                _bestX = bestX;
                _bestY = bestY;
            }
            return rowBest;
        }

        /// <summary>One measuring pass of the refinement per call; the last one publishes the answer.</summary>
        private int RefineStep()
        {
            float su = _su, sv = _sv;
            if (_refine == 0)
            {
                float coarse = MathF.Sqrt(MathF.Max(0f, _best));
                _radius = EdgeDistance(_center, coarse, 48);
                // The true centre is rarely exactly on a texel centre: nudge it around by fractions of a texel and keep
                // any move that lets a bigger circle fit (a few rounds, each with half the step).
                _stepU = 0.35f * su;
                _stepV = 0.35f * sv;
                _round = 0;
                _dir = 0;
                _moved = false;
                _refine = 1;
                return 48 * WorkPerRay;
            }
            if (_refine == 1)
            {
                int dir = _dir;
                Vec2 trial = _center + new Vec2(dir == 0 ? _stepU : dir == 1 ? -_stepU : 0f, dir == 2 ? _stepV : dir == 3 ? -_stepV : 0f);
                float r = EdgeDistance(trial, _radius, 48);
                if (r > _radius + 1e-5f) { _radius = r; _center = trial; _moved = true; }
                _dir++;
                if (_dir == 4)
                {
                    if (!_moved) { _stepU *= 0.5f; _stepV *= 0.5f; }
                    _dir = 0;
                    _moved = false;
                    _round++;
                    if (_round == 3) _refine = 2;
                }
                return 48 * WorkPerRay;
            }

            // The climb used fewer directions; measure the final centre carefully, and keep a sliver of margin so a
            // ragged edge poking in between two measuring directions still stays outside the circle.
            float radius = EdgeDistance(_center, _radius, 256) - 0.15f * MathF.Min(su, sv);
            if (radius < 0f) radius = 0f;

            int c = _jobColor;
            PortalOpening result = _last[c];
            result.Color = (PaintColor)c;
            result.Valid = radius >= _surface.Settings.OpeningMinRadius;
            result.CenterUV = _center;
            result.InscribedRadius = radius;
            result.BoundsMin = new Vec2(_fx0 * su, _fy0 * sv);
            result.BoundsMax = new Vec2((_fx1 + 1) * su, (_fy1 + 1) * sv);
            result.Area = _count * su * sv;

            ulong key = _keySum ^ (_keyXor * 31UL) ^ ((ulong)_count << 40);
            bool shapeChanged = !_haveLast[c] || key != _lastShapeKey[c] || result.Valid != _last[c].Valid;
            if (shapeChanged) result.Revision = _last[c].Revision + 1;

            // The labels this search wrote become the colour's patch; its old ones become the next search's scratch.
            int[] old = _label[c];
            _label[c] = _spare;
            _spare = old;

            _lastShapeKey[c] = key;
            _lastId[c] = _jobId;
            _lastBox[c] = new TexelRect(_fx0, _fy0, _fx1, _fy1);
            _haveLast[c] = true;
            _last[c] = result;
            _finished[c]++;
            _phase = PhaseIdle;
            return 256 * WorkPerRay;
        }

        /// <summary>
        /// The tallest fully painted upright band at least minWidth wide inside the patch from the latest finished
        /// search for this colour (search first). The band is widened sideways as far as it stays fully
        /// painted over its whole height. ReachesFloor is true when its bottom is within floorReach of the floor.
        /// The answer is kept until the patch changes, so asking again is free.
        /// </summary>
        public DoorBand FindDoor(PaintColor color, float minWidth, float floorReach)
        {
            int c = (int)color;
            if (!_haveLast[c] || _lastId[c] == 0) return new DoorBand { Revision = _last[c].Revision };
            if (_doorId[c] == _lastId[c] && _doorWidth[c] == minWidth && _doorFloor[c] == floorReach
                && _door[c].Revision == _last[c].Revision)
            {
                LastWasCached = true;
                return _door[c];
            }
            LastWasCached = false;

            float su = _su, sv = _sv;
            int need = Math.Max(1, (int)MathF.Ceiling(minWidth / su - 1e-3f));
            int[] label = _label[c];
            int id = _lastId[c];
            TexelRect box = _lastBox[c];

            // How many painted texels in a row end at each texel.
            for (int y = box.Y0; y <= box.Y1; y++)
            {
                int row = y * _w;
                int run = 0;
                for (int x = box.X0; x <= box.X1; x++)
                {
                    run = label[row + x] == id ? run + 1 : 0;
                    _run[row + x] = run;
                }
            }

            // For each window of `need` columns (named by its right column), the longest stack of rows that are
            // painted all across it. Ties go to the band that reaches lower.
            int bestH = 0, bestX = -1, bestY0 = 0;
            for (int x = box.X0 + need - 1; x <= box.X1; x++)
            {
                int h = 0;
                for (int y = box.Y0; y <= box.Y1; y++)
                {
                    if (_run[x + y * _w] >= need)
                    {
                        h++;
                        int y0 = y - h + 1;
                        if (h > bestH || (h == bestH && y0 < bestY0)) { bestH = h; bestX = x; bestY0 = y0; }
                    }
                    else h = 0;
                }
            }

            var band = new DoorBand { Revision = _last[c].Revision };
            if (bestX >= 0)
            {
                int y0 = bestY0, y1 = bestY0 + bestH - 1;
                int xl = bestX - need + 1, xr = bestX;
                while (xl - 1 >= box.X0 && ColumnFull(label, id, xl - 1, y0, y1)) xl--;
                while (xr + 1 <= box.X1 && ColumnFull(label, id, xr + 1, y0, y1)) xr++;
                band.Found = true;
                band.LeftU = xl * su;
                band.RightU = (xr + 1) * su;
                band.BottomV = y0 * sv;
                band.TopV = (y1 + 1) * sv;
                band.ReachesFloor = band.BottomV <= floorReach;
            }
            _door[c] = band;
            _doorId[c] = id;
            _doorWidth[c] = minWidth;
            _doorFloor[c] = floorReach;
            return band;
        }

        private bool ColumnFull(int[] label, int id, int x, int y0, int y1)
        {
            for (int y = y0; y <= y1; y++) if (label[x + y * _w] != id) return false;
            return true;
        }

        private int FindSeedTexel(float[] field, Vec2 uv)
        {
            int sx = (int)MathF.Floor(uv.X / _surface.TexelSizeU);
            int sy = (int)MathF.Floor(uv.Y / _surface.TexelSizeV);
            if (sx < 0) sx = 0; else if (sx > _w - 1) sx = _w - 1;
            if (sy < 0) sy = 0; else if (sy > _h - 1) sy = _h - 1;
            if (field[sx + sy * _w] >= 0.5f) return sx + sy * _w;
            int reach = _surface.Settings.SeedSearchTexels;
            int bestI = -1, bestD = int.MaxValue;
            for (int dy = -reach; dy <= reach; dy++)
            {
                int y = sy + dy;
                if (y < 0 || y >= _h) continue;
                for (int dx = -reach; dx <= reach; dx++)
                {
                    int x = sx + dx;
                    if (x < 0 || x >= _w) continue;
                    int d = dx * dx + dy * dy;
                    if (d >= bestD || d > reach * reach) continue;
                    if (field[x + y * _w] >= 0.5f) { bestD = d; bestI = x + y * _w; }
                }
            }
            return bestI;
        }

        /// <summary>
        /// Distance from centre to the nearest point where the bilinear paint field drops below 0.5, checked along
        /// many directions (48 or 256) near the expected radius. This turns the texel-grid answer into a sub-texel one.
        /// </summary>
        private float EdgeDistance(Vec2 center, float coarse, int rays)
        {
            float[] dirX = rays == 48 ? _dirX48 : _dirX256;
            float[] dirY = rays == 48 ? _dirY48 : _dirY256;
            float cx = center.X, cy = center.Y;
            float cell = MathF.Min(_su, _sv);
            float lo0 = MathF.Max(0f, coarse - 2.5f * cell);
            float hi0 = coarse + 0.5f * cell;
            float stepLen = 0.25f * cell;
            float best = hi0;
            for (int k = 0; k < rays; k++)
            {
                float dx = dirX[k], dy = dirY[k];
                // March outward until the paint ends (or we pass the best found so far), then bisect.
                float prev = lo0;
                if (!Inside(cx + dx * prev, cy + dy * prev)) { best = MathF.Min(best, Bisect(cx, cy, dx, dy, 0f, prev)); continue; }
                float t = prev;
                bool found = false;
                while (t < best)
                {
                    t = MathF.Min(t + stepLen, best);
                    if (!Inside(cx + dx * t, cy + dy * t)) { found = true; break; }
                    prev = t;
                }
                if (found) best = MathF.Min(best, Bisect(cx, cy, dx, dy, prev, t));
            }
            return best;
        }

        private float Bisect(float cx, float cy, float dx, float dy, float inside, float outside)
        {
            for (int i = 0; i < 9; i++)
            {
                float m = (inside + outside) * 0.5f;
                if (Inside(cx + dx * m, cy + dy * m)) inside = m; else outside = m;
            }
            return inside;
        }

        /// <summary>
        /// True when the point (wall metres) is on solid paint in the copy this search works on: the same test as
        /// PaintSurface.IsPainted (bilinear field at least 0.5), on the copied field.
        /// </summary>
        private bool Inside(float u, float v)
        {
            if (!(u >= 0f && u <= _wallW && v >= 0f && v <= _wallH)) return false;
            int w = _w;
            float[] f = _snap;
            float fx = u / _su - 0.5f;
            float fy = v / _sv - 0.5f;
            if (fx < 0f) fx = 0f; else if (fx > w - 1) fx = w - 1;
            if (fy < 0f) fy = 0f; else if (fy > _h - 1) fy = _h - 1;
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = x0 + 1 < w ? x0 + 1 : x0;
            int y1 = y0 + 1 < _h ? y0 + 1 : y0;
            float tx = fx - x0, ty = fy - y0;
            float a = f[x0 + y0 * w], b = f[x1 + y0 * w];
            float c = f[x0 + y1 * w], d = f[x1 + y1 * w];
            float ab = a + (b - a) * tx;
            float cd = c + (d - c) * tx;
            return ab + (cd - ab) * ty >= 0.5f;
        }
    }
}
