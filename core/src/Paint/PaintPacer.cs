using System;

namespace Portaballs.Core
{
    /// <summary>
    /// Spreads the paint's heavy work over frames so no single frame is hurt: the splats waiting to land
    /// (<see cref="PaintSurface.QueueSplat"/>) and the opening searches asked for (<see cref="OpeningFinder.Request"/>),
    /// for every wall of the room.
    ///
    /// Set up once: make one pacer for the room and <see cref="Add"/> each wall's surface and finder.
    /// Every frame, after stepping the walls, call ONE of:
    ///   - <see cref="AdvanceFor"/>( milliseconds ): do work until that much time has gone. The simple choice for the
    ///     game: it means the same thing in an optimised and an unoptimised build and on any PC. About 1.0 is right
    ///     for a headset.
    ///   - <see cref="Advance"/>( workUnits ): do a counted amount of work. The same call always does the same work,
    ///     so tests can repeat it exactly.
    /// Either way the paint that ends up on the wall and the openings found are identical; only the frame they
    /// arrive in differs. A queued splat is prepared off the wall and lands whole, exactly as
    /// <see cref="PaintSurface.Splat"/> would have laid it in the frame it lands (as if the ball had hit a frame
    /// or two later), and the fixed-step drip simulation is not touched by any of this.
    ///
    /// Splats go first (waiting paint is what the player is looking at), then searches. Walls take turns.
    ///
    /// The whole game side, in order:
    /// <code>
    /// // while loading, once:
    /// PaintPacer.WarmUp();
    /// pacer = new PaintPacer();
    /// foreach wall: pacer.Add( wall.Surface, wall.Finder );
    ///
    /// // when a ball hits a wall (instead of Surface.Splat):
    /// wall.Surface.QueueSplat( color, uv, radius, wallVelocity, seed, normalSpeed );
    ///
    /// // every frame, each wall:
    /// wall.Surface.Step( dt );
    /// if ( wall.Surface.SplatsLanded != wall.landedSeen )          // new paint is on the wall: look for its opening
    /// {
    ///     wall.landedSeen = wall.Surface.SplatsLanded;
    ///     wall.Finder.Request( color, seedOfThatColour );
    /// }
    /// // ...and again every 0.1 s while !wall.Surface.IsSettled, as the game does today with Find.
    /// if ( wall.Finder.SearchesFinished( color ) != wall.searchesSeen[color] )   // an answer has arrived
    /// {
    ///     wall.searchesSeen[color] = wall.Finder.SearchesFinished( color );
    ///     opening = wall.Finder.Last( color );
    /// }
    ///
    /// // every frame, once for the whole room, after the walls have stepped:
    /// pacer.AdvanceFor( 1.0 );
    ///
    /// // every frame, each wall: upload the changed texels as before (Surface.CopyDirtyToRgba).
    /// </code>
    /// </summary>
    public sealed class PaintPacer
    {
        private PaintSurface[] _surfaces = new PaintSurface[8];
        private OpeningFinder[] _finders = new OpeningFinder[8];
        private int _count;
        private int _turn;

        // Work done between two looks at the clock in AdvanceFor: about 0.012 ms optimised, 0.04 ms unoptimised.
        private const int ChunkUnits = 4000;

        /// <summary>
        /// Run every kind of paint work once on a small throw-away wall. Call it once while the game loads: the
        /// runtime compiles each method the first time it runs, and this way that pause (a few milliseconds in all)
        /// is spent at load instead of in the frame the first ball lands. Leaves nothing behind.
        /// </summary>
        public static void WarmUp()
        {
            WallFrame wall = new WallFrame(new Vec3(0f, 0f, 0f), new Vec3(0f, -1f, 0f), new Vec3(0f, 0f, 1f), 2f, 2f);
            PaintSurface s = new PaintSurface(wall);
            OpeningFinder f = new OpeningFinder(s);
            PaintPacer pacer = new PaintPacer();
            pacer.Add(s, f);
            byte[] rgba = new byte[s.RgbaByteCount];
            Vec2 hit = new Vec2(1f, 1.2f);
            for (int round = 0; round < 3; round++)
            {
                PaintColor color = round == 1 ? PaintColor.Blue : PaintColor.Orange;
                s.QueueSplat(color, hit, 0.45f, new Vec2(2f, -1f), (uint)(round + 1), 10f);
                f.Request(color, hit);
                while (pacer.HasWork) pacer.Advance(ChunkUnits);
                pacer.AdvanceFor(0.01);
                for (int i = 0; i < 400 && !s.IsSettled; i++)
                {
                    s.Step(1f / 60f);
                    s.CopyDirtyToRgba(rgba);
                }
                f.Find(color, hit);
                f.FindDoor(color, 0.4f, 0.15f);
            }
            s.EraseDisc(hit, 0.1f);
            s.EraseColor(PaintColor.Orange);
            s.Clear();

            // The same for what paint does off the walls (2026-11-19 evening): run-off handed over at the foot of a
            // wall, a puddle poured on a floor, a floor splat settling, drops falling off a ceiling splat.
            s.HandsOverRunOff = true;
            s.Splat(PaintColor.Orange, new Vec2(1f, 0.7f), 0.45f, new Vec2(0.5f, -1f), 4u, 10f);
            PaintSurface floor = new PaintSurface(new WallFrame(new Vec3(0f, -2f, 0f), new Vec3(0f, -1f, 0f), new Vec3(1f, 0f, 0f), 2f, 2f));
            floor.Splat(PaintColor.Blue, new Vec2(1f, 0.8f), 0.3f, new Vec2(1f, 0f), 5u, 8f);
            for (int i = 0; i < 600 && !(s.IsSettled && floor.IsSettled); i++)
            {
                s.Step(1f / 60f);
                for (int k = 0; k < s.RunOffCount; k++)
                {
                    PaintRunOff r = s.GetRunOff(k);
                    floor.Pour(r.Color, new Vec2(r.UV.X, 2f), r.Volume, r.HalfWidth, r.Seed);
                }
                floor.Step(1f / 60f);
                floor.CopyDirtyToRgba(rgba);
            }
            floor.EraseColor(PaintColor.Orange);
            PaintSurface ceiling = new PaintSurface(new WallFrame(new Vec3(0f, 0f, 2f), new Vec3(1f, 0f, 0f), new Vec3(0f, -1f, 0f), 2f, 2f));
            ceiling.Splat(PaintColor.Orange, new Vec2(1f, 1f), 0.3f, new Vec2(0f, 0f), 6u, 8f);
            for (int i = 0; i < 300 && !ceiling.IsSettled; i++) ceiling.Step(1f / 30f);
            s.Clear();
        }

        /// <summary>Put a wall under this pacer's care. The finder may be null for a wall that never needs openings.</summary>
        public void Add(PaintSurface surface, OpeningFinder finder)
        {
            if (surface == null) throw new ArgumentNullException(nameof(surface));
            if (_count == _surfaces.Length)
            {
                Array.Resize(ref _surfaces, _count * 2);
                Array.Resize(ref _finders, _count * 2);
            }
            _surfaces[_count] = surface;
            _finders[_count] = finder;
            _count++;
        }

        /// <summary>Forget every wall (for example when the room is rebuilt).</summary>
        public void Clear()
        {
            for (int i = 0; i < _count; i++)
            {
                _surfaces[i] = null;
                _finders[i] = null;
            }
            _count = 0;
            _turn = 0;
        }

        /// <summary>Walls added so far.</summary>
        public int WallCount
        {
            get { return _count; }
        }

        /// <summary>True while any wall has a splat waiting to land or a search waiting or running.</summary>
        public bool HasWork
        {
            get
            {
                for (int i = 0; i < _count; i++)
                {
                    if (_surfaces[i].QueuedSplatCount > 0) return true;
                    if (_finders[i] != null && _finders[i].HasWork) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Do at most about <paramref name="workUnits"/> units of waiting work across all walls (one unit is roughly
        /// 3 nanoseconds optimised, 10 unoptimised; see <see cref="PaintSurface.WorkUnitsPerMillisecond"/>).
        /// May run over by one small step. Returns the units used; 0 means there was nothing to do.
        /// </summary>
        public int Advance(int workUnits)
        {
            if (_count == 0) return 0;
            if (workUnits < 1) workUnits = 1;
            int used = 0;
            _turn = (_turn + 1) % _count;

            for (int k = 0; k < _count && used < workUnits; k++)
            {
                PaintSurface s = _surfaces[(_turn + k) % _count];
                if (s.QueuedSplatCount > 0) used += s.AdvanceSplats(workUnits - used);
            }
            // Reaching here with budget left means every queued splat has landed, so a search started now sees them.
            for (int k = 0; k < _count && used < workUnits; k++)
            {
                OpeningFinder f = _finders[(_turn + k) % _count];
                if (f != null && f.HasWork) used += f.Advance(workUnits - used);
            }
            return used;
        }

        /// <summary>
        /// Do waiting work across all walls until about <paramref name="milliseconds"/> have gone (or nothing is left).
        /// It works in small pieces and stops when the next piece would probably not fit. It always does at least
        /// one piece, and a piece that holds one of the few steps that cannot be split (a splat's first step, its
        /// landing, a search's last measurement) can carry it over: by about 0.05 ms in an optimised build,
        /// 0.15 ms unoptimised. Returns the milliseconds it took; 0 when there was nothing to do.
        /// </summary>
        public double AdvanceFor(double milliseconds)
        {
            if (!HasWork) return 0.0;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            double ticksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;
            long limit = (long)(milliseconds * ticksPerMs);
            long now = start;
            long lastPiece;
            do
            {
                long before = now;
                int used = Advance(ChunkUnits);
                now = System.Diagnostics.Stopwatch.GetTimestamp();
                lastPiece = now - before;
                if (used == 0) break;
            }
            while (now - start + lastPiece < limit);
            return (now - start) / ticksPerMs;
        }
    }
}
