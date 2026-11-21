namespace Portaballs.Core
{
    /// <summary>
    /// The tester's request on 2026-11-19, after their first headset session with paint:
    ///   "I'd also like the splat to be a little bit more animated in terms of frame rate and physics, you know,
    ///    like some splats coming off, bouncing off the wall, most of it sticking, and then dripping down a little
    ///    bit quicker."
    /// EVERY NUMBER THAT REQUEST ADDED OR CHANGED IS HERE, so it is easy to tune. Change a value and start play
    /// again (paint surfaces copy theirs when the room is built; the droplets read theirs at every burst).
    ///
    /// Where each part lives:
    ///   - smoother running paint: PaintSettings.EvenSubsteps / StrokeEveryFrame, used by PaintSurface.Step;
    ///   - quicker drips: PaintSettings.RunSpeedUp, used by the drip heads (PaintSurface.Drips.cs);
    ///   - droplets: core PaintSpray (flight and spawning) and the game's PaintDroplets / PaintWall.SplatDroplet.
    ///
    /// The same evening, after the tester watched a splat in the headset ("more than three seconds for it to drip and also
    /// pour across the ground as well as it puddles at the bottom ... explore other options that may also happen"):
    ///   - longer, naturally slowing drips: WetSeconds, SlowDownCurve, SheetEndSpeed, TrailThinEnd (drip heads);
    ///   - paint follows gravity along its own surface (PaintSurface.GravityAlong): it runs down a wall or a slope,
    ///     not along a floor or a ceiling;
    ///   - run-off: paint reaching the bottom of a wall pours onto the floor and grows a puddle
    ///     (PaintSurface.Pools.cs; the game's PaintWall passes it from one surface to the next);
    ///   - a floor splat settles, a ceiling splat lets drops fall, wet-on-wet runs more, joined drips run faster.
    /// </summary>
    public static class SplatFeel
    {
        // ---------------- Dripping down quicker, and for longer ----------------

        /// <summary>
        /// Everything that runs down the wall (the sagging rim, the curtain and the thin drips) STARTS this many times
        /// faster than before 2026-11-19. Since that evening it is the speed only: how long the paint stays wet is
        /// <see cref="WetSeconds"/>. 1 = the old slow start. Range 1..2.5.
        /// </summary>
        public static float RunSpeedUp = 1.75f;

        /// <summary>
        /// The tester, 2026-11-19 evening, after watching a splat for five seconds: "I love it, however, I would suggest more
        /// than three seconds for it to drip". How long thrown paint stays wet enough to run, in real seconds. Paint
        /// keeps creeping until then (unless it runs out first), so this is roughly how long a big splat keeps moving.
        /// Was 2.9 (5 s on a clock running 1.75 times fast). Range 3..12.
        /// </summary>
        public static float WetSeconds = 7f;

        /// <summary>
        /// The shape of the slowing down as paint dries: speed goes with (wetness left) to this power. 0.5 = as
        /// before: nearly full speed almost to the end, then a sudden stop. 1.5 = as fast as before for the first
        /// second, then slowing all the way into a long creep: half speed after 37% of WetSeconds, a tenth after 78%.
        /// Higher = slows sooner and creeps longer. Range 0.5..3.
        /// </summary>
        public static float SlowDownCurve = 1.5f;

        /// <summary>
        /// A curtain or sag that has nearly used its paint up slides at this share of its full speed (it slows
        /// evenly from 1 as the paint goes). Lower = a softer stop. Was 0.45. Range 0.1..0.6.
        /// </summary>
        public static float SheetEndSpeed = 0.3f;

        /// <summary>
        /// Thinning trail: running paint leaves a full film behind when fresh and this share of it by the time it has
        /// dried, so the same paint runs further and its trail thins toward the end. 1 = the same film all the way
        /// (as before). Range 0.3..1.
        /// </summary>
        public static float TrailThinEnd = 0.55f;

        /// <summary>
        /// How long the wet shine the shader shows takes to fade (s, real time). Kept at WetSeconds so paint looks wet
        /// for as long as it can still run. Was 3. Range 1..12.
        /// </summary>
        public static float WetShineSeconds = 7f;

        // ---------------- Other things wet paint does (the tester: "explore other options that may also happen") ----------------

        /// <summary>
        /// Paint thrown onto paint that is still wet runs more than on dry wall: its sag, curtain and drips start up
        /// to this much faster (0.4 = 40% faster on fully wet paint, nothing on dry wall or dry paint) and leave a
        /// thinner trail, so they also go further. 0 = off. Range 0..1.
        /// </summary>
        public static float WetOnWetBoost = 0.4f;

        /// <summary>Two drips that run into each other become one that runs this many times faster (it also carries both their paint). 1 = no faster. Range 1..1.6.</summary>
        public static float JoinSpeedBoost = 1.25f;

        // ---------------- Reaching the bottom: puddles that pour across the floor ----------------

        /// <summary>
        /// The tester, same message: "and also pour across the ground as well as it puddles at the bottom". When running paint
        /// reaches the bottom edge of its wall and a paint surface lies under it (the floor), this share of the paint
        /// it still carries pours onto that surface; the rest pools along the foot of the wall as before. Range 0..1.
        /// </summary>
        public static float RunOffShare = 0.85f;

        /// <summary>
        /// How thick a puddle lies (film, m). The paint that arrived divided by this is the area the puddle grows to,
        /// so thinner = a wider puddle from the same paint. At 0.0016 a chest-height 0.45 m splat's curtain leaves
        /// a puddle about 0.7 m wide reaching 0.4 m out from the wall. Range 0.0005..0.004.
        /// </summary>
        public static float PuddleFilm = 0.0016f;

        /// <summary>How quickly a puddle creeps out to its full size: it covers about two thirds of what is left in this many seconds. Range 0.3..4.</summary>
        public static float PuddleGrowSeconds = 1.1f;

        /// <summary>Farthest one stream's puddle can reach from where it arrived (m), however much paint came. Range 0.1..1.5.</summary>
        public static float PuddleMaxReach = 0.6f;

        /// <summary>Paint arriving within this distance (m) of an earlier stream of the same colour feeds that one's puddle; further away it starts its own. Range 0.02..0.2.</summary>
        public static float PuddleStreamSpacing = 0.18f;

        // ---------------- A splat on the floor: settles like a wet pool ----------------

        /// <summary>
        /// Nothing runs along a floor. A ball's splat there settles instead: bulges creep out of its body by about
        /// this share of its radius (each a different amount) and it rounds off: a 0.42 m splat grows about 5 cm all
        /// round. 0 = off. Range 0..0.3.
        /// </summary>
        public static float FloorSettleSpread = 0.12f;

        /// <summary>How long that settling takes (s): about two thirds of it in this time, like a puddle. Range 0.3..4.</summary>
        public static float FloorSettleSeconds = 0.9f;

        /// <summary>Share of a floor splat's paint that moves as it settles. Range 0..0.5.</summary>
        public static float FloorSettleShare = 0.25f;

        // ---------------- A splat on the ceiling: drops fall off it ----------------

        /// <summary>
        /// Nothing runs along a ceiling either. The paint that would have run gathers and falls as this many drops
        /// (a small splat gets the fewest, a 0.5 m one the most; none below PaintSettings.DripMinSplatRadius). Range 0..10.
        /// </summary>
        public static int CeilingDropsMin = 2;
        public static int CeilingDropsMax = 6;

        /// <summary>The first drop falls about this long after the hit (s); each wait after that is CeilingDropGapGrowth times the one before, so they come quickly at first and then slow. Ranges 0.2..2 / 1..2.5.</summary>
        public static float CeilingFirstDropSeconds = 0.5f;
        public static float CeilingDropGapGrowth = 1.5f;

        /// <summary>Radius a falling ceiling drop is drawn with (m). A burst's droplets are 0.007 to 0.013. Range 0.008..0.03.</summary>
        public static float CeilingDropDrawRadius = 0.016f;

        // ---------------- A ball landing in a puddle splashes ----------------

        /// <summary>A ball that bursts on wet paint ON A FLOOR (a puddle, a fresh floor splat) throws up to this many more droplets than usual: all of them when the paint there is fresh, fewer as it dries. A burst on a wall is not changed. 0 = off. Range 0..6.</summary>
        public static int WetSplashExtraDroplets = 3;

        // ---------------- Smoother running paint (frame rate) ----------------

        /// <summary>
        /// Before: a wide sag or curtain head only repainted its trail every 0.4 of its own width (about 2.5 cm on a
        /// thrown splat), so the curtain crept down in visible jumps 8 to 20 times a second. Now every moving head
        /// repaints its trail at the end of every frame once it has moved at least this many texels (0.35 of a
        /// 1.6 cm texel is under 6 mm, smoothed further by the soft paint edge). Range 0.1..1.
        /// </summary>
        public static float StrokeMinTexels = 0.35f;

        // ---------------- Droplets bouncing off the wall ----------------

        /// <summary>Droplets thrown off by one ball bursting on a wall (most of the paint still sticks). Range 0..10.</summary>
        public static int DropletsMin = 3;
        public static int DropletsMax = 6;

        /// <summary>A ball arriving slower than this (m/s) throws no droplets at all. Range 0..4.</summary>
        public static float DropletMinImpactSpeed = 1.5f;

        /// <summary>Droplet speed as a share of the ball's speed (each droplet varies by 0.7x to 1.3x). Range 0.1..0.6.</summary>
        public static float DropletSpeedShare = 0.35f;

        /// <summary>Slowest and fastest a droplet leaves the wall (m/s). Always slower than the ball. Ranges 0.3..2 / 2..8.</summary>
        public static float DropletMinSpeed = 0.9f;
        public static float DropletMaxSpeed = 4.5f;

        /// <summary>
        /// Which way they fly: 0 = straight out of the wall along its normal, 1 = the mirror image of the ball's path
        /// (a glancing ball's droplets skim on along the wall). Range 0..1.
        /// </summary>
        public static float DropletReflectBias = 0.45f;

        /// <summary>How widely they spread around that direction, degrees (half the cone). Range 0..70.</summary>
        public static float DropletSpreadDegrees = 38f;

        /// <summary>Every droplet leaves the wall at least this steeply: the cosine of its angle to the normal. Range 0.1..0.8.</summary>
        public static float DropletMinOut = 0.3f;

        /// <summary>Droplets start from anywhere within this share of the splat's radius from the hit. Range 0..0.8.</summary>
        public static float DropletStartSpread = 0.45f;

        /// <summary>Drawn radius of a flying droplet (m). The ball is 0.035. Ranges 0.004..0.01 / 0.008..0.02.</summary>
        public static float DropletDrawRadiusMin = 0.007f;
        public static float DropletDrawRadiusMax = 0.013f;

        /// <summary>
        /// Radius of the small splat a droplet leaves on a wall, as a share of the ball's splat radius (a 0.42 m splat
        /// gives 0.04 to 0.055 m droplet splats). Range 0.05..0.2.
        /// </summary>
        public static float DropletSplatShareMin = 0.10f;
        public static float DropletSplatShareMax = 0.13f;

        /// <summary>
        /// Largest droplet splat radius (m). Kept below PaintSettings.DripMinSplatRadius (0.06) on purpose: a droplet's
        /// paint never runs, so it can never run into a portal patch afterwards. Range 0.02..0.059.
        /// </summary>
        public static float DropletSplatRadiusMax = 0.055f;

        /// <summary>Smallest droplet splat radius (m). Range 0.015..0.04.</summary>
        public static float DropletSplatRadiusMin = 0.025f;

        /// <summary>
        /// A droplet's splat is stamped as if it arrived this much slower than it really did: a light droplet spatters
        /// and smears less than a ball would at that speed, so its splat stays a compact dab (about 0.35 m across at
        /// most, spatter included) instead of throwing specks 30 cm away. 1 = its real speed. Range 0.2..1.
        /// </summary>
        public static float DropletSplatSpeedScale = 0.5f;

        /// <summary>
        /// Most droplet splats laid in one frame in the whole room. One costs about 0.2 ms (measured in an optimised
        /// build, DropletTests.PrintDropletCost), so the rest wait a frame or two each, which nobody can see. Range 1..6.
        /// </summary>
        public static int DropletSplatsPerFrame = 1;

        /// <summary>A droplet that has not hit anything after this long disappears (s). Range 0.5..3.</summary>
        public static float DropletLifeSeconds = 1.5f;

        /// <summary>Most droplets in the air at once in the whole room; a new one takes the oldest one's place. Range 6..60.</summary>
        public static int MaxDroplets = 30;

        /// <summary>A flying droplet is drawn stretched along its path by this much travel time (s), so it reads as fast. Range 0..0.03.</summary>
        public static float DropletStretchSeconds = 0.015f;

        /// <summary>
        /// A droplet only paints bare wall: no paint of either colour, no running drip and no splat on its way within
        /// its splat's reach plus this margin (m). Otherwise it soaks in and leaves nothing, so it can never cut into
        /// or add to a portal patch. Range 0..0.1.
        /// </summary>
        public static float DropletClearMargin = 0.03f;
    }
}
