namespace Portaballs.Core
{
    /// <summary>
    /// Every knob of the paint simulation in one place. Make one, change what you need, and hand it to
    /// <see cref="PaintSurface"/>. Change these before creating the surface; the grid size is fixed at creation.
    /// Distances are metres, times seconds, paint amounts are a film thickness in metres (1 mm = 0.001).
    /// The comment on each field gives the default's purpose and a sensible range.
    /// </summary>
    public sealed class PaintSettings
    {
        // ---------------- Grid ----------------

        /// <summary>Paint texels per metre of wall. 64 makes a 4 x 3 m wall 256 x 192. Range 32..128.</summary>
        public float TexelsPerMeter = 64f;

        /// <summary>
        /// Half the width, in texels, of the soft band at a paint edge. The field ramps from 1 to 0 across
        /// twice this, which is what lets a shader draw a smooth anti-aliased outline. Range 1..3.
        /// </summary>
        public float EdgeSoftnessTexels = 1.5f;

        // ---------------- Splat shape ----------------

        /// <summary>Film of paint a splat leaves over its area (this is its paint budget). Range 0.0005..0.004.</summary>
        public float SplatThickness = 0.0015f;

        /// <summary>Fewest and most extra round lobes that make the body lumpy. Range 1..10.</summary>
        public int LobeCountMin = 3;
        public int LobeCountMax = 7;

        /// <summary>How wobbly the body's outline is, as a fraction of the radius. 0 = round. Range 0..0.3.</summary>
        public float Raggedness = 0.2f;

        /// <summary>Fine roughness added to every edge, as a fraction of the radius. Range 0..0.06.</summary>
        public float EdgeNoise = 0.03f;

        /// <summary>Radial spikes on a slow hit, and how many more a hit at full spatter adds. Range 0..10 / 0..16.</summary>
        public int SpikeCountMin = 3;
        public int SpikeCountMax = 6;
        public int SpikesAddedAtFullSpatter = 8;

        /// <summary>Typical spike reach measured from the centre, in splat radii. Range 1.1..2.5.</summary>
        public float SpikeLength = 1.55f;

        /// <summary>Satellite droplets thrown off at full spatter (a gentle hit gets a few). Range 0..40.</summary>
        public int SatelliteCount = 18;

        /// <summary>Farthest a satellite droplet lands from the centre, in splat radii. Range 1.5..4.</summary>
        public float SatelliteDistance = 2.6f;

        /// <summary>Impact speed (m/s) that gives the most spikes and spatter. Range 6..30.</summary>
        public float SpeedForFullSpatter = 14f;

        /// <summary>Speed into the wall assumed when the caller doesn't give one (m/s). Range 2..20.</summary>
        public float DefaultNormalSpeed = 8f;

        /// <summary>Extra splat length per m/s of speed along the wall. 0.07 makes a 10 m/s glancing hit 1.7x long. Range 0..0.15.</summary>
        public float StretchPerSpeed = 0.07f;

        /// <summary>Longest a glancing hit can stretch a splat. Range 1..3.5.</summary>
        public float MaxStretch = 2.6f;

        /// <summary>How far the stretched body slides forward along the throw, in radii per unit of stretch. Range 0..0.8.</summary>
        public float ThrowShift = 0.35f;

        // ---------------- Sag and drips ----------------

        /// <summary>Longest inner time step (s). With EvenSubsteps off, Step(dt) runs whole steps of exactly this size and
        /// carries the remainder, which keeps the result identical at any frame rate. Range 1/240..1/60.</summary>
        public float SimStep = 1f / 120f;

        /// <summary>Most inner steps one Step call may run; extra time is dropped so a hitch can't snowball. Range 4..20.</summary>
        public int MaxSubstepsPerFrame = 8;

        /// <summary>
        /// On (the tester, 2026-11-19, see SplatFeel): each Step(dt) is cut into equal inner steps no longer than SimStep, so the
        /// paint moves by exactly the frame's time every frame. Off = whole SimStep steps with the remainder carried,
        /// which at 90 fps moves the paint 1, 1, then 2 steps' worth in turn (a slight judder). On gives nearly the same
        /// result at any frame rate (a few texels differ); off gives exactly the same.
        /// </summary>
        public bool EvenSubsteps = true;

        /// <summary>
        /// On (the tester, 2026-11-19, see SplatFeel): at the end of every Step each moving head paints its trail up to where it
        /// is, once it has moved StrokeMinTexels. Off = only every 0.4 of its width, which made wide curtains creep
        /// down in jumps of about 2.5 cm.
        /// </summary>
        public bool StrokeEveryFrame = true;

        /// <summary>Smallest move (texels) worth repainting a head's trail for at the end of a frame. Default from SplatFeel. Range 0.1..1.</summary>
        public float StrokeMinTexels = SplatFeel.StrokeMinTexels;

        /// <summary>
        /// Everything that runs (sag, curtain, drips) moves this many times faster than the speeds below, and starts
        /// that much sooner (SagDelay). 1 = the speeds below as they are. It no longer changes how long the paint
        /// stays wet: that is DryTime. Default from SplatFeel (the tester, 2026-11-19: "dripping down a little bit quicker").
        /// Range 1..2.5.
        /// </summary>
        public float RunSpeedUp = SplatFeel.RunSpeedUp;

        /// <summary>How running paint slows as it dries: speed goes with (wetness left) to this power. 0.5 = full speed
        /// nearly to the end then a sudden stop; 2 = fast at first, then a long creep. Default from SplatFeel. Range 0.5..3.</summary>
        public float SlowDownCurve = SplatFeel.SlowDownCurve;

        /// <summary>Share of its full speed a sag or curtain still has when its paint is nearly used up. Default from SplatFeel. Range 0.1..0.6.</summary>
        public float SheetEndSpeed = SplatFeel.SheetEndSpeed;

        /// <summary>Share of TrailThickness a head leaves behind by the time its paint has dried (full when fresh). 1 = no
        /// thinning. Default from SplatFeel. Range 0.3..1.</summary>
        public float TrailThinEnd = SplatFeel.TrailThinEnd;

        /// <summary>On: the paint a head lays is only as wet as the head's own paint still is, so a splat and all its
        /// drips finish drying together. Off: every stroke goes down fully wet, as before (the last trail then shines
        /// on for WetFadeTime after everything has stopped).</summary>
        public bool TrailDriesWithPaint = true;

        /// <summary>A splat landing on wet paint: its running paint starts up to this much faster and leaves a thinner
        /// trail. 0 = off. Default from SplatFeel. Range 0..1.</summary>
        public float WetOnWetBoost = SplatFeel.WetOnWetBoost;

        /// <summary>Two drips that join run this many times faster as one. Default from SplatFeel. Range 1..1.6.</summary>
        public float JoinSpeedBoost = SplatFeel.JoinSpeedBoost;

        /// <summary>Most drip heads alive at once on one wall. Range 16..256.</summary>
        public int MaxDrips = 128;

        // ---------------- Curtain (the doorway) ----------------

        /// <summary>
        /// THE dial for the doorway. 0 = the splat only sags a little and hangs thin drips (the first version).
        /// 1 = a big splat's lower body slides down the wall as a wide curtain of paint, giving a doorway you can step
        /// through (a 0.45 m splat thrown 1.5 m up reaches the floor). In between scales the extra paint and speed.
        /// Range 0..1.
        /// </summary>
        public float CurtainStrength = 1f;

        /// <summary>Extra paint film (m) a splat brings at full strength, all of which runs as the curtain. This is what
        /// sets how far a curtain can reach: it scales with splat area, so bigger splats reach further. Range 0..0.004.</summary>
        public float CurtainPaint = 0.0018f;

        /// <summary>Splats at or below this radius get no curtain; at CurtainFullRadius they get all of CurtainPaint.
        /// Ranges 0.05..0.2 / 0.3..0.8.</summary>
        public float CurtainMinRadius = 0.13f;
        public float CurtainFullRadius = 0.45f;

        /// <summary>Speed the curtain starts sliding at (m/s), before RunSpeedUp; it slows as it thins and dries (SlowDownCurve). Range 0.2..1.</summary>
        public float CurtainSpeed = 0.65f;

        /// <summary>How much less paint the outer parts of the curtain get than the middle (0 = a straight-edged band,
        /// 1 = a pointed tongue). Makes the curtain narrow as it goes. Range 0..1.</summary>
        public float CurtainEdgeFalloff = 0.95f;

        /// <summary>How far (m) the outermost curtain edge drifts inward per metre it slides, with some randomness. Range 0..0.3.</summary>
        public float CurtainTaper = 0.08f;

        /// <summary>When a curtain or drip reaches the floor, the paint it still carries pools along the floor line.
        /// This caps how wide (m, each side) that pool spreads. Range 0..0.4.</summary>
        public float FloorPoolMaxSpread = 0.15f;

        /// <summary>Largest share of a splat's paint that is allowed to run as drips (the sag sheet comes out of it too). Range 0..0.6.</summary>
        public float DripShareMax = 0.55f;

        /// <summary>Splats smaller than this radius do not run at all; at DripFullSplatRadius they run the full share. Ranges 0.02..0.15 / 0.2..0.8.</summary>
        public float DripMinSplatRadius = 0.06f;
        public float DripFullSplatRadius = 0.5f;

        /// <summary>Gap between sag heads along the splat's lower rim, in splat radii. Range 0.1..0.4.</summary>
        public float HeadSpacing = 0.17f;

        /// <summary>Half-width of a sag head, in splat radii. Wider than half the spacing makes neighbours overlap, so the
        /// lower rim sags down as one sheet with a scalloped edge. Range 0.08..0.3.</summary>
        public float HeadWidth = 0.15f;

        /// <summary>Farthest the sheet sags below the rim, in splat radii (each sag head picks a random share of it). Range 0..1.5.</summary>
        public float SagDistance = 1.1f;

        /// <summary>Speed of the sagging sheet (m/s), before RunSpeedUp. Range 0.05..0.6.</summary>
        public float SagSpeed = 0.35f;

        /// <summary>Half-width of a drip that runs out of the sheet, in splat radii (clamped to MinDripRadius..MaxDripRadius). Range 0.02..0.1.</summary>
        public float DripWidth = 0.05f;

        /// <summary>Thinnest and widest a running drip may be (half-width, m). The minimum is kept to at least 0.75 texel so it
        /// still draws. Ranges 0.008..0.03 / 0.02..0.06.</summary>
        public float MinDripRadius = 0.012f;
        public float MaxDripRadius = 0.03f;

        /// <summary>Unevenness of the paint shared out to drips: 0 = all equal, 2 = a few long drips and many short ones. Range 0..3.</summary>
        public float DripUnevenness = 1.8f;

        /// <summary>Running speed of a full, fresh drip (m/s), before RunSpeedUp. It slows as it empties. Range 0.1..1.5.</summary>
        public float DripSpeed = 0.6f;

        /// <summary>Paint film (m) a drip needs to keep running; thinner than this it stops. Range 0.0003..0.003.</summary>
        public float StallThickness = 0.001f;

        /// <summary>Paint film (m) at which a drip runs at full speed. Must be above StallThickness. Range 0.002..0.01.</summary>
        public float FullSpeedThickness = 0.004f;

        /// <summary>Film (m) a running head leaves behind on its trail; this is what uses its paint up. Range 0.0003..0.003.</summary>
        public float TrailThickness = 0.001f;

        /// <summary>Time after impact before the paint starts to sag (s), before RunSpeedUp. Range 0..0.5.</summary>
        public float SagDelay = 0.12f;

        /// <summary>Sideways wander of a running drip (m) and the run length over which it changes (m). Ranges 0..0.05 / 0.1..0.6.</summary>
        public float WanderAmplitude = 0.018f;
        public float WanderWavelength = 0.22f;

        /// <summary>Heads merge when closer than this fraction of their summed half-widths. Range 0.2..1.</summary>
        public float MergeFactor = 0.5f;

        /// <summary>A head's paint is dry and stops this long after it was thrown, in real seconds (RunSpeedUp no longer
        /// shortens it). Default from SplatFeel.WetSeconds. Range 2..12.</summary>
        public float DryTime = SplatFeel.WetSeconds;

        /// <summary>Time for the wetness shown by the shader to fade from fully wet to dry (s). Default from
        /// SplatFeel.WetShineSeconds. Range 1..12.</summary>
        public float WetFadeTime = SplatFeel.WetShineSeconds;

        /// <summary>The drying wetness is only written out (and the texture re-sent) once it has changed by this many of
        /// the texture's 255 steps. 0 = at every turn, as before. 4 cannot be seen and saves most of the drying work
        /// now that paint dries over 7 s. Range 0..8.</summary>
        public float WetStepLevels = 4f;

        // ---------------- Run-off, puddles, floors and ceilings (defaults from SplatFeel) ----------------

        /// <summary>Share of the paint a head still carries at the bottom edge that leaves the surface as run-off, when
        /// the game has said a surface lies below (PaintSurface.HandsOverRunOff). The rest pools at the edge. Range 0..1.</summary>
        public float RunOffShare = SplatFeel.RunOffShare;

        /// <summary>Film (m) a puddle of poured paint lies at; the paint that arrived divided by this is its area. Range 0.0004..0.003.</summary>
        public float PuddleFilm = SplatFeel.PuddleFilm;

        /// <summary>A puddle covers about two thirds of the way left to its full size in this many seconds. Range 0.3..4.</summary>
        public float PuddleGrowSeconds = SplatFeel.PuddleGrowSeconds;

        /// <summary>Farthest one stream's puddle reaches from where it arrived (m). Range 0.1..1.5.</summary>
        public float PuddleMaxReach = SplatFeel.PuddleMaxReach;

        /// <summary>Poured paint within this distance (m) of an earlier stream of its colour feeds that puddle. Range 0.02..0.2.</summary>
        public float PuddleStreamSpacing = SplatFeel.PuddleStreamSpacing;

        /// <summary>On a floor: a splat's body creeps outward by this share of its radius as it settles. 0 = off. Range 0..0.3.</summary>
        public float FloorSettleSpread = SplatFeel.FloorSettleSpread;

        /// <summary>On a floor: the settling covers about two thirds of the way left in this many seconds. Range 0.3..4.</summary>
        public float FloorSettleSeconds = SplatFeel.FloorSettleSeconds;

        /// <summary>On a floor: share of a splat's paint that moves as it settles. Range 0..0.5.</summary>
        public float FloorSettleShare = SplatFeel.FloorSettleShare;

        /// <summary>On a ceiling: fewest and most drops that fall off one splat. Range 0..10.</summary>
        public int CeilingDropsMin = SplatFeel.CeilingDropsMin;
        public int CeilingDropsMax = SplatFeel.CeilingDropsMax;

        /// <summary>On a ceiling: wait before the first drop (s), and how much longer each next wait is. Ranges 0.2..2 / 1..2.5.</summary>
        public float CeilingFirstDropSeconds = SplatFeel.CeilingFirstDropSeconds;
        public float CeilingDropGapGrowth = SplatFeel.CeilingDropGapGrowth;

        /// <summary>Size of the round bead a drip leaves where it stops, relative to its width. Range 1..1.8.</summary>
        public float BulbScale = 1.3f;

        // ---------------- Texture ----------------

        /// <summary>Paint film (m) that shows as full alpha in the texture's thickness channel. Range 0.001..0.01.</summary>
        public float ThicknessForFullAlpha = 0.004f;

        // ---------------- Portal openings ----------------

        /// <summary>Radius (m) of the largest circle that must fit in a patch for it to count as an opening. Range 0.2..0.6.</summary>
        public float OpeningMinRadius = 0.30f;

        /// <summary>When the seed point is just off the paint, how far (texels) to look for the nearest painted texel. Range 0..16.</summary>
        public int SeedSearchTexels = 6;

        /// <summary>
        /// A copy, so one tweak doesn't change another surface's settings.
        /// Written out field by field because s&box's code whitelist forbids MemberwiseClone.
        /// When you add a field above, add it here too: PaintSettingsCloneTests fails if one is missed.
        /// </summary>
        public PaintSettings Clone()
        {
            return new PaintSettings
            {
                TexelsPerMeter = TexelsPerMeter,
                EdgeSoftnessTexels = EdgeSoftnessTexels,
                SplatThickness = SplatThickness,
                LobeCountMin = LobeCountMin,
                LobeCountMax = LobeCountMax,
                Raggedness = Raggedness,
                EdgeNoise = EdgeNoise,
                SpikeCountMin = SpikeCountMin,
                SpikeCountMax = SpikeCountMax,
                SpikesAddedAtFullSpatter = SpikesAddedAtFullSpatter,
                SpikeLength = SpikeLength,
                SatelliteCount = SatelliteCount,
                SatelliteDistance = SatelliteDistance,
                SpeedForFullSpatter = SpeedForFullSpatter,
                DefaultNormalSpeed = DefaultNormalSpeed,
                StretchPerSpeed = StretchPerSpeed,
                MaxStretch = MaxStretch,
                ThrowShift = ThrowShift,
                SimStep = SimStep,
                MaxSubstepsPerFrame = MaxSubstepsPerFrame,
                EvenSubsteps = EvenSubsteps,
                StrokeEveryFrame = StrokeEveryFrame,
                StrokeMinTexels = StrokeMinTexels,
                RunSpeedUp = RunSpeedUp,
                SlowDownCurve = SlowDownCurve,
                SheetEndSpeed = SheetEndSpeed,
                TrailThinEnd = TrailThinEnd,
                TrailDriesWithPaint = TrailDriesWithPaint,
                WetOnWetBoost = WetOnWetBoost,
                JoinSpeedBoost = JoinSpeedBoost,
                RunOffShare = RunOffShare,
                PuddleFilm = PuddleFilm,
                PuddleGrowSeconds = PuddleGrowSeconds,
                PuddleMaxReach = PuddleMaxReach,
                PuddleStreamSpacing = PuddleStreamSpacing,
                FloorSettleSpread = FloorSettleSpread,
                FloorSettleSeconds = FloorSettleSeconds,
                FloorSettleShare = FloorSettleShare,
                CeilingDropsMin = CeilingDropsMin,
                CeilingDropsMax = CeilingDropsMax,
                CeilingFirstDropSeconds = CeilingFirstDropSeconds,
                CeilingDropGapGrowth = CeilingDropGapGrowth,
                MaxDrips = MaxDrips,
                CurtainStrength = CurtainStrength,
                CurtainPaint = CurtainPaint,
                CurtainMinRadius = CurtainMinRadius,
                CurtainFullRadius = CurtainFullRadius,
                CurtainSpeed = CurtainSpeed,
                CurtainEdgeFalloff = CurtainEdgeFalloff,
                CurtainTaper = CurtainTaper,
                FloorPoolMaxSpread = FloorPoolMaxSpread,
                DripShareMax = DripShareMax,
                DripMinSplatRadius = DripMinSplatRadius,
                DripFullSplatRadius = DripFullSplatRadius,
                HeadSpacing = HeadSpacing,
                HeadWidth = HeadWidth,
                SagDistance = SagDistance,
                SagSpeed = SagSpeed,
                DripWidth = DripWidth,
                MinDripRadius = MinDripRadius,
                MaxDripRadius = MaxDripRadius,
                DripUnevenness = DripUnevenness,
                DripSpeed = DripSpeed,
                StallThickness = StallThickness,
                FullSpeedThickness = FullSpeedThickness,
                TrailThickness = TrailThickness,
                SagDelay = SagDelay,
                WanderAmplitude = WanderAmplitude,
                WanderWavelength = WanderWavelength,
                MergeFactor = MergeFactor,
                DryTime = DryTime,
                WetFadeTime = WetFadeTime,
                WetStepLevels = WetStepLevels,
                BulbScale = BulbScale,
                ThicknessForFullAlpha = ThicknessForFullAlpha,
                OpeningMinRadius = OpeningMinRadius,
                SeedSearchTexels = SeedSearchTexels,
            };
        }
    }
}
