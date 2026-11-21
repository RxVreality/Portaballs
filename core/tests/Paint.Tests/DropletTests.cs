using System;
using System.Collections.Generic;
using Xunit;
using Xunit.Abstractions;

namespace Portaballs.Core.Tests
{
    /// <summary>
    /// The droplets that bounce off a burst (the tester, 2026-11-19, see SplatFeel): how many and how they fly (PaintSpray),
    /// how far a splat can reach (SplatReach), and that a droplet only ever paints bare wall (IsClearForSplat), so it
    /// cannot change a portal patch.
    /// </summary>
    public class DropletTests
    {
        private readonly ITestOutputHelper _out;

        public DropletTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static readonly Vec3 Gravity = new Vec3(0f, 0f, -9.81f);

        /// <summary>The test room the game builds (TestRoom): 8 x 6 x 3.5 m, floor centre at the origin, walls +X, -X, +Y, -Y facing in.</summary>
        internal static WallFrame[] Room(out float floorZ, out float ceilingZ)
        {
            const float L = 8f, W = 6f, H = 3.5f;
            floorZ = 0f;
            ceilingZ = H;
            Vec3 up = new Vec3(0f, 0f, 1f);
            WallFrame Make(Vec3 inward, float distance, float width)
            {
                Vec3 u = Vec3.Cross(up, inward);
                Vec3 foot = -inward * distance;
                return new WallFrame(foot - u * (width * 0.5f), u, up, width, H);
            }
            return new[]
            {
                Make(new Vec3(-1f, 0f, 0f), L * 0.5f, W),
                Make(new Vec3(1f, 0f, 0f), L * 0.5f, W),
                Make(new Vec3(0f, -1f, 0f), W * 0.5f, L),
                Make(new Vec3(0f, 1f, 0f), W * 0.5f, L),
            };
        }

        // ------------------------------------------------------------------ spawning

        [Fact]
        public void ABurstThrowsAFewSmallerSlowerDropletsOffTheWall()
        {
            WallFrame[] room = Room(out _, out _);
            WallFrame wall = room[0];   // +X wall, facing -X
            const float R = 0.42f;
            var buf = new Droplet[PaintSpray.MaxPerBurst];
            var counts = new int[PaintSpray.MaxPerBurst + 1];
            float slowest = float.MaxValue, fastest = 0f, flattest = 1f, biggestSplat = 0f;
            var rng = new Rng(77u);
            for (int k = 0; k < 400; k++)
            {
                Vec3 v = new Vec3(rng.Range(4f, 14f), rng.Range(-4f, 4f), rng.Range(-4f, 2f));
                Vec3 hit = wall.ToWorld(new Vec2(rng.Range(0.5f, 5.5f), rng.Range(0.5f, 3f)));
                int n = PaintSpray.Plan(wall, hit, v, R, PaintColor.Orange, (uint)(k + 1), buf);
                counts[n]++;
                Assert.InRange(n, SplatFeel.DropletsMin, SplatFeel.DropletsMax);
                for (int i = 0; i < n; i++)
                {
                    Droplet d = buf[i];
                    float speed = d.Velocity.Length();
                    float outward = Vec3.Dot(d.Velocity, wall.Normal) / speed;
                    slowest = MathF.Min(slowest, speed);
                    fastest = MathF.Max(fastest, speed);
                    flattest = MathF.Min(flattest, outward);
                    biggestSplat = MathF.Max(biggestSplat, d.SplatRadius);
                    Assert.True(speed < v.Length() * 0.71f, "a droplet is not slower than the ball");
                    Assert.InRange(speed, SplatFeel.DropletMinSpeed - 1e-4f, SplatFeel.DropletMaxSpeed + 1e-4f);
                    Assert.True(outward >= SplatFeel.DropletMinOut - 1e-4f, $"a droplet leaves at {outward} to the normal");
                    Assert.True(d.SplatRadius <= SplatFeel.DropletSplatRadiusMax + 1e-6f && d.SplatRadius <= 0.2f * R);
                    Assert.True(d.SplatRadius < new PaintSettings().DripMinSplatRadius, "a droplet's splat would run");
                    Assert.InRange(d.DrawRadius, SplatFeel.DropletDrawRadiusMin - 1e-6f, SplatFeel.DropletDrawRadiusMax + 1e-6f);
                    Assert.True(wall.SignedDistance(d.Position) > d.DrawRadius, "a droplet starts inside the wall");
                    Vec2 off = wall.ToUV(d.Position) - wall.ToUV(hit);
                    Assert.True(off.Length() <= SplatFeel.DropletStartSpread * R + 1e-4f, "a droplet starts outside the splat");
                    Assert.True(d.Alive && d.Color == PaintColor.Orange);
                }
            }
            _out.WriteLine($"400 bursts: droplets per burst 3:{counts[3]} 4:{counts[4]} 5:{counts[5]} 6:{counts[6]}; speed {slowest:0.00}..{fastest:0.00} m/s; flattest exit {MathF.Acos(flattest) * 57.3f:0} deg from the normal; biggest droplet splat radius {biggestSplat:0.000} m (ball's {R} m)");
            Assert.True(counts[SplatFeel.DropletsMin] > 0 && counts[SplatFeel.DropletsMax] > 0);

            // A slow lob throws none; the same seed throws the same droplets.
            Assert.Equal(0, PaintSpray.Plan(wall, wall.Center, new Vec3(1f, 0f, 0f), R, PaintColor.Blue, 5u, buf));
            var again = new Droplet[PaintSpray.MaxPerBurst];
            int n1 = PaintSpray.Plan(wall, wall.Center, new Vec3(9f, 1f, -1f), R, PaintColor.Blue, 5u, buf);
            int n2 = PaintSpray.Plan(wall, wall.Center, new Vec3(9f, 1f, -1f), R, PaintColor.Blue, 5u, again);
            Assert.Equal(n1, n2);
            for (int i = 0; i < n1; i++)
            {
                Assert.Equal(buf[i].Velocity, again[i].Velocity);
                Assert.Equal(buf[i].Seed, again[i].Seed);
            }
        }

        [Fact]
        public void AGlancingBallsDropletsCarryOnTheWayItWasGoing()
        {
            WallFrame wall = Room(out _, out _)[0];
            var buf = new Droplet[PaintSpray.MaxPerBurst];
            float alongU = 0f, outward = 0f;
            int total = 0;
            for (int k = 0; k < 200; k++)
            {
                Vec3 v = wall.U * 9f - wall.Normal * 4f;   // sliding along +U, into the wall
                int n = PaintSpray.Plan(wall, wall.Center, v, 0.42f, PaintColor.Orange, (uint)(900 + k), buf);
                for (int i = 0; i < n; i++)
                {
                    alongU += Vec3.Dot(buf[i].Velocity, wall.U);
                    outward += Vec3.Dot(buf[i].Velocity, wall.Normal);
                    total++;
                }
            }
            _out.WriteLine($"glancing ball: droplets average {alongU / total:0.00} m/s on along the wall, {outward / total:0.00} m/s out of it");
            Assert.True(alongU / total > 0.5f, "the droplets of a glancing ball do not carry on along the wall");
            Assert.True(outward / total > 0.5f);
        }

        // ------------------------------------------------------------------ flight

        private static void Fly(PaintSpray spray, float fps, List<DropletLanding> landings, Action<PaintSpray> eachFrame = null)
        {
            for (int i = 0; i < 1000 && spray.AliveCount > 0; i++)
            {
                spray.Step(1f / fps, Gravity);
                for (int k = 0; k < spray.LandingCount; k++) landings.Add(spray.GetLanding(k));
                eachFrame?.Invoke(spray);
            }
        }

        [Fact]
        public void DropletsLandOnTheNextWallOrVanishOnTheFloor()
        {
            WallFrame[] room = Room(out float floor, out float ceiling);
            var spray = new PaintSpray();
            spray.SetRoom(room, floor, ceiling);
            var landings = new List<DropletLanding>();
            int burstWallHits = 0, made = 0;

            // Into a corner: the +X wall right beside the -Y wall, a ball sliding towards -Y.
            for (int k = 0; k < 30; k++)
            {
                Vec3 hit = room[0].ToWorld(new Vec2(5.92f, 1.4f));
                made += spray.Burst(room[0], hit, new Vec3(8f, -4f, -0.5f), 0.42f, PaintColor.Blue, (uint)(300 + k));
                Fly(spray, 90f, landings, s =>
                {
                    for (int i = 0; i < s.Capacity; i++)
                    {
                        Droplet d = s.Get(i);
                        if (!d.Alive) continue;
                        Vec3 p = d.Position;
                        Assert.True(p.X <= 4f + 1e-3f && p.X >= -4f - 1e-3f && p.Y <= 3f + 1e-3f && p.Y >= -3f - 1e-3f && p.Z >= -1e-3f && p.Z <= 3.5f + 1e-3f,
                            $"droplet outside the room at {p}");
                    }
                });
            }
            int onSideWall = 0;
            foreach (DropletLanding l in landings)
            {
                WallFrame w = room[l.Wall];
                Assert.True(MathF.Abs(w.SignedDistance(l.Point)) < 1e-3f, "landing point is not on the wall");
                Assert.True(w.Contains(l.UV), "landing point is off the wall");
                Assert.True(l.NormalSpeed > 0f);
                Assert.Equal(PaintColor.Blue, l.Color);
                if (l.Wall == 0) burstWallHits++;
                if (l.Wall == 3) onSideWall++;   // -Y wall
            }
            _out.WriteLine($"corner bursts: {made} droplets, {landings.Count} reached a wall ({onSideWall} the side wall), {spray.TotalOnFloorOrCeiling} the floor or ceiling, {spray.TotalTimedOut} timed out, {spray.TotalLeftRoom} left the room");
            Assert.Equal(0, burstWallHits);
            Assert.True(onSideWall > 0, "no droplet reached the wall beside the corner");
            Assert.Equal(0, spray.TotalLeftRoom);

            // In the middle of a wall at chest height, straight in: they fall to the floor in front of it.
            var spray2 = new PaintSpray();
            spray2.SetRoom(room, floor, ceiling);
            var landings2 = new List<DropletLanding>();
            int made2 = 0;
            for (int k = 0; k < 30; k++)
            {
                made2 += spray2.Burst(room[0], room[0].ToWorld(new Vec2(3f, 1.4f)), new Vec3(9f, 0.5f, -1f), 0.42f, PaintColor.Orange, (uint)(600 + k));
                Fly(spray2, 72f, landings2);
            }
            _out.WriteLine($"middle bursts: {made2} droplets, {landings2.Count} reached a wall, {spray2.TotalOnFloorOrCeiling} the floor or ceiling, {spray2.TotalTimedOut} timed out");
            foreach (DropletLanding l in landings2) Assert.NotEqual(0, l.Wall);
            Assert.True(spray2.TotalOnFloorOrCeiling >= made2 * 3 / 4, "most droplets from the middle of a wall should end on the floor");
            Assert.Equal(0, spray2.TotalLeftRoom);
        }

        [Fact]
        public void NoMoreThanThirtyFlyAndNoneLivesLong()
        {
            WallFrame[] room = Room(out float floor, out float ceiling);
            var spray = new PaintSpray();
            Assert.Equal(SplatFeel.MaxDroplets, spray.Capacity);
            // No gravity, so nothing lands: only the time limit ends them.
            spray.SetRoom(room, floor, ceiling);
            int made = 0;
            for (int k = 0; k < 12; k++)
            {
                made += spray.Burst(room[0], room[0].ToWorld(new Vec2(3f, 1.7f)), new Vec3(5f, 0f, 0f), 0.42f, PaintColor.Orange, (uint)(k + 1));
                Assert.True(spray.AliveCount <= spray.Capacity);
            }
            Assert.Equal(spray.Capacity, spray.AliveCount);
            Assert.Equal(made - spray.Capacity, spray.TotalReplaced);
            float t = 0f;
            while (spray.AliveCount > 0 && t < 10f)
            {
                spray.Step(1f / 90f, Vec3.Zero);
                t += 1f / 90f;
            }
            _out.WriteLine($"{made} made, {spray.TotalReplaced} pushed out by newer ones; the rest were gone after {t:0.00} s ({spray.TotalTimedOut} timed out, {spray.TotalReachedWall} reached a wall)");
            Assert.Equal(0, spray.AliveCount);
            Assert.True(t <= SplatFeel.DropletLifeSeconds + 0.05f);
        }

        // ------------------------------------------------------------------ painting only bare wall

        [Fact]
        public void SplatReachHoldsEveryPaintedTexel()
        {
            var s = new PaintSurface(LookSheets.Wall());
            var rng = new Rng(31u);
            float worstShare = 0f;
            Vec2 uv = new Vec2(2f, 1.5f);
            for (int k = 0; k < 300; k++)
            {
                s.Clear();
                float R = k % 3 == 0 ? rng.Range(0.1f, 0.45f) : rng.Range(SplatFeel.DropletSplatRadiusMin, SplatFeel.DropletSplatRadiusMax);
                Vec2 along = new Vec2(rng.Range(-9f, 9f), rng.Range(-9f, 9f));
                float ns = k % 7 == 0 ? -1f : rng.Range(0.3f, 16f);
                s.Splat(PaintColor.Orange, uv, R, along, rng.NextUInt(), ns);
                float reach = s.SplatReach(R, along, ns);
                float far = 0f;
                for (int y = 0; y < s.Height; y++)
                for (int x = 0; x < s.Width; x++)
                {
                    if (s.GetField(PaintColor.Orange, x, y) <= 0f) continue;
                    far = MathF.Max(far, Vec2.Distance(s.TexelCenter(x, y), uv));
                }
                Assert.True(far <= reach, $"R {R}, along {along}, into {ns}: paint {far} m out, reach says {reach} m");
                worstShare = MathF.Max(worstShare, far / reach);
            }
            _out.WriteLine($"300 splats: paint reached at most {worstShare * 100f:0}% of SplatReach");
        }

        [Fact]
        public void IsClearForSplatSeesPaintDripsAndWaitingSplats()
        {
            var s = new PaintSurface(LookSheets.Wall());
            Vec2 uv = new Vec2(2f, 2.4f);
            const float R = 0.05f;
            Vec2 along = new Vec2(1f, -2f);
            float reach = s.SplatReach(R, along, 2f);
            Assert.True(s.IsClearForSplat(uv, R, along, 2f, 0.03f));

            s.PaintDisc(PaintColor.Blue, uv + new Vec2(reach + 0.03f + 0.08f, 0f), 0.05f);   // just out of reach
            Assert.True(s.IsClearForSplat(uv, R, along, 2f, 0.03f));
            s.PaintDisc(PaintColor.Blue, uv + new Vec2(-(reach + 0.01f), 0f), 0.02f);        // just in reach
            Assert.False(s.IsClearForSplat(uv, R, along, 2f, 0.03f));

            s.Clear();
            s.Splat(PaintColor.Orange, new Vec2(2f, 2.4f), 0.42f, Vec2.Zero, 3u, 9f);
            LookSheets.Run(s, 0.5f);
            // Well below the splat, on bare wall for now, but in the way of its running paint.
            Vec2 under = new Vec2(2f, 1.4f);
            bool anyHeadNear = false;
            for (int i = 0; i < s.ActiveDripCount; i++)
            {
                s.GetDrip(i, out Vec2 p, out float hw, out _, out _);
                if (Vec2.Distance(p, under) < reach + hw + 0.03f) anyHeadNear = true;
            }
            Assert.Equal(!anyHeadNear && !s.IsPainted(PaintColor.Orange, under) && NoPaintNear(s, under, reach + 0.03f), s.IsClearForSplat(under, R, along, 2f, 0.03f));

            s.Clear();
            s.QueueSplat(PaintColor.Blue, new Vec2(1.0f, 1.0f), 0.3f, Vec2.Zero, 4u, 9f);
            Assert.False(s.IsClearForSplat(new Vec2(1.4f, 1.0f), R, along, 2f, 0.03f));   // a splat is on its way there
            Assert.True(s.IsClearForSplat(new Vec2(3.6f, 2.6f), R, along, 2f, 0.03f));
        }

        private static bool NoPaintNear(PaintSurface s, Vec2 uv, float r)
        {
            for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++)
            {
                if (Vec2.Distance(s.TexelCenter(x, y), uv) > r) continue;
                if (s.GetField(PaintColor.Orange, x, y) > 0f || s.GetField(PaintColor.Blue, x, y) > 0f) return false;
            }
            return true;
        }

        /// <summary>
        /// The promise to the portal code: orange droplets that land on a wall holding the blue patch, while the blue
        /// paint is still running, leave the blue patch exactly as it would have been, and its opening too.
        /// (A droplet never lands on the wall holding its OWN colour's patch: PaintWall.SplatDroplet refuses.)
        /// </summary>
        [Fact]
        public void DropletsNeverChangeTheOtherColoursPatch()
        {
            int painted = 0, refused = 0;
            for (int round = 0; round < 3; round++)
            {
                var control = new PaintSurface(LookSheets.Wall());
                var s = new PaintSurface(LookSheets.Wall());
                Vec2 blueHit = new Vec2(1.4f + 0.5f * round, 2.0f);
                foreach (PaintSurface w in new[] { control, s })
                {
                    w.Splat(PaintColor.Blue, blueHit, 0.42f, new Vec2(0.5f, -0.3f), 80u + (uint)round, 9f);
                    w.Splat(PaintColor.Blue, blueHit + new Vec2(0.25f, 0.15f), 0.3f, new Vec2(-0.5f, -0.3f), 90u + (uint)round, 9f);
                }
                var rng = new Rng(123u + (uint)round);
                for (int frame = 0; frame < 10 * 90; frame++)
                {
                    // While the blue curtain is running (first 2 s), a few droplets a frame try to land anywhere.
                    if (frame < 180)
                    {
                        for (int k = 0; k < 2; k++)
                        {
                            Vec2 uv = new Vec2(rng.Range(0f, 4f), rng.Range(0f, 3f));
                            float r = rng.Range(SplatFeel.DropletSplatRadiusMin, SplatFeel.DropletSplatRadiusMax);
                            Vec2 along = new Vec2(rng.Range(-3f, 3f), rng.Range(-6f, 1f));
                            float ns = rng.Range(0.5f, 4f);
                            if (s.IsClearForSplat(uv, r, along, ns, SplatFeel.DropletClearMargin))
                            {
                                s.Splat(PaintColor.Orange, uv, r, along, rng.NextUInt(), ns);
                                painted++;
                            }
                            else refused++;
                        }
                    }
                    control.Step(1f / 90f);
                    s.Step(1f / 90f);
                }
                Assert.True(control.IsSettled && s.IsSettled);
                for (int y = 0; y < s.Height; y++)
                for (int x = 0; x < s.Width; x++)
                    Assert.True(control.GetField(PaintColor.Blue, x, y) == s.GetField(PaintColor.Blue, x, y), $"blue changed at texel {x},{y}");
                PortalOpening a = new OpeningFinder(control).Find(PaintColor.Blue, blueHit);
                PortalOpening b = new OpeningFinder(s).Find(PaintColor.Blue, blueHit);
                Assert.True(a.Valid);
                Assert.Equal(a.Valid, b.Valid);
                Assert.Equal(a.CenterUV, b.CenterUV);
                Assert.Equal(a.InscribedRadius, b.InscribedRadius);
                Assert.Equal(a.Area, b.Area);
                Assert.Equal(a.BoundsMin, b.BoundsMin);
                Assert.Equal(a.BoundsMax, b.BoundsMax);
            }
            _out.WriteLine($"orange droplets thrown at a wall with a running blue patch: {painted} painted bare wall, {refused} soaked in; the blue patch and its opening came out identical to a wall with no droplets");
            Assert.True(painted > 30, "too few droplets painted for the test to mean anything");
        }

        [Fact]
        public void ADropletSplatIsSmallAndDoesNotRun()
        {
            // The biggest droplet splat, landing the way a falling droplet does: fast down the wall, gently into it,
            // stamped at SplatFeel.DropletSplatSpeedScale of that (what PaintSpray hands over).
            var s = new PaintSurface(LookSheets.Wall());
            float k = SplatFeel.DropletSplatSpeedScale;
            Vec2 along = new Vec2(2f, -5f) * k;
            float into = 3f * k;
            s.Splat(PaintColor.Orange, new Vec2(2f, 1.5f), SplatFeel.DropletSplatRadiusMax, along, 7u, into);
            Assert.Equal(0, s.ActiveDripCount);
            Assert.True(s.IsSettled);
            Assert.True(s.IsPainted(PaintColor.Orange, new Vec2(2f, 1.5f)));
            float r = s.SplatReach(SplatFeel.DropletSplatRadiusMax, along, into);
            float far = 0f;
            for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++)
                if (s.GetField(PaintColor.Orange, x, y) > 0f) far = MathF.Max(far, Vec2.Distance(s.TexelCenter(x, y), new Vec2(2f, 1.5f)));
            _out.WriteLine($"largest droplet splat: radius {SplatFeel.DropletSplatRadiusMax} m; its paint reached {far:0.000} m from where it landed, its reach bound is {r:0.000} m");
            Assert.True(r < 0.4f);
        }

        [Fact]
        public void WarmUpRuns()
        {
            PaintSpray.WarmUp();
        }

        /// <summary>What droplets cost a frame: their flight, the bare-wall check, and the small splat itself. Prints; only fails on something absurd.</summary>
        [Fact]
        public void PrintDropletCost()
        {
#if DEBUG
            _out.WriteLine("Build: Debug (slower than the game will be). Run with -c Release for game-like numbers.");
#else
            _out.WriteLine("Build: Release.");
#endif
            var sw = new System.Diagnostics.Stopwatch();
            WallFrame[] room = Room(out float floor, out float ceiling);
            var spray = new PaintSpray();
            spray.SetRoom(room, floor, ceiling);
            for (int w = 0; w < 3; w++) PaintSpray.WarmUp();

            // Flight: 30 droplets in the air, one frame.
            var stepMs = new List<double>();
            for (int k = 0; k < 40; k++)
            {
                for (int b = 0; b < 6; b++)
                    spray.Burst(room[b % 4], room[b % 4].ToWorld(new Vec2(1f + 0.5f * b, 1.6f)), room[b % 4].Normal * -9f, 0.42f, PaintColor.Orange, (uint)(k * 10 + b + 1));
                sw.Restart();
                spray.Step(1f / 90f, Gravity);
                stepMs.Add(sw.Elapsed.TotalMilliseconds);
            }

            // Landing: the check on a wall with a big patch, and the splat where it is clear.
            var s = new PaintSurface(PerfBench.BigWall());
            s.Splat(PaintColor.Blue, new Vec2(4f, 1.6f), 0.42f, new Vec2(0.5f, -0.3f), 5u, 9f);
            LookSheets.Run(s, 0.5f);
            var rng = new Rng(9u);
            var checkMs = new List<double>();
            var splatMs = new List<double>();
            for (int k = 0; k < 400; k++)
            {
                if (k % 100 == 0) { s.Clear(); s.Splat(PaintColor.Blue, new Vec2(4f, 1.6f), 0.42f, new Vec2(0.5f, -0.3f), 5u, 9f); LookSheets.Run(s, 0.5f); }
                Vec2 uv = new Vec2(rng.Range(0.2f, 7.8f), rng.Range(0.2f, 3.3f));
                float r = rng.Range(SplatFeel.DropletSplatRadiusMin, SplatFeel.DropletSplatRadiusMax);
                Vec2 along = new Vec2(rng.Range(-2f, 2f), rng.Range(-4f, 0.5f));
                float ns = rng.Range(0.5f, 2.5f);
                sw.Restart();
                bool clear = s.IsClearForSplat(uv, r, along, ns, SplatFeel.DropletClearMargin);
                checkMs.Add(sw.Elapsed.TotalMilliseconds);
                if (!clear) continue;
                sw.Restart();
                s.Splat(PaintColor.Orange, uv, r, along, rng.NextUInt(), ns);
                splatMs.Add(sw.Elapsed.TotalMilliseconds);
            }
            PerfBench.Stat st = PerfBench.Summarise(stepMs.ToArray(), stepMs.Count);
            PerfBench.Stat ct = PerfBench.Summarise(checkMs.ToArray(), checkMs.Count);
            PerfBench.Stat pt = PerfBench.Summarise(splatMs.ToArray(), splatMs.Count);
            _out.WriteLine($"Flight of {spray.Capacity} droplets, one frame: {st}");
            _out.WriteLine($"Bare-wall check, one droplet: {ct}");
            _out.WriteLine($"Droplet splat (radius {SplatFeel.DropletSplatRadiusMin}..{SplatFeel.DropletSplatRadiusMax} m), one landing ({splatMs.Count} of 400 were clear): {pt}");
            Assert.True(pt.Median < 2.0 && st.Median < 1.0);
        }
    }
}
