using System;
using Xunit;
using Xunit.Abstractions;

namespace Portaballs.Core.Tests
{
    // ---------------------------------------------------------------------------------------------
    // Lining up the helper's tracking space with the engine's, the grip offset, carrying a throw
    // between spaces, and feeding the release detector from "time of last change" readings.
    // ---------------------------------------------------------------------------------------------

    /// <summary>A pretend head (or hand) wandering about a room, seen in two spaces at once.</summary>
    internal sealed class TwoSpaceDevice
    {
        public RigidTransform SourceToTarget;   // the truth the solver has to find
        public RigidTransform LocalOffset = RigidTransform.Identity;   // target-side pose = alignment * source pose * this
        public double PredictAhead = 0.030;     // the target space's pose is guessed this far ahead
        public float PositionNoise = 0.0007f;
        public float RotationNoise = 0.15f * MathF.PI / 180f;

        // Where the device really is, in source space, at a time: it stands still for a while, then moves, and so on.
        public void TruePose(double t, out Vec3 position, out Quat rotation, out Vec3 velocity, out Vec3 spin)
        {
            double phase = t % 2.0;                // 2 s cycle: 1.2 s still, 0.8 s moving
            int leg = (int)Math.Floor(t / 2.0);
            Vec3 from = Spot(leg), to = Spot(leg + 1);
            float yawFrom = Heading(leg), yawTo = Heading(leg + 1);
            float u = 0f, du = 0f;
            if (phase > 1.2)
            {
                float x = (float)((phase - 1.2) / 0.8);
                u = x * x * (3f - 2f * x);
                du = 6f * x * (1f - x) / 0.8f;
            }
            position = from + (to - from) * u;
            velocity = (to - from) * du;
            float yaw = yawFrom + (yawTo - yawFrom) * u;
            // The head is never perfectly level: a small fixed nod and lean that differ from spot to spot.
            Quat nod = Quat.CreateFromAxisAngle(new Vec3(0f, 1f, 0f), 0.10f * MathF.Sin(leg * 1.7f))
                     * Quat.CreateFromAxisAngle(new Vec3(1f, 0f, 0f), 0.06f * MathF.Cos(leg * 2.3f));
            rotation = Quat.CreateFromAxisAngle(new Vec3(0f, 0f, 1f), yaw) * nod;
            spin = new Vec3(0f, 0f, (yawTo - yawFrom) * du);
        }

        private static Vec3 Spot(int leg)
        {
            return new Vec3(0.6f * MathF.Sin(leg * 1.3f), 0.5f * MathF.Cos(leg * 0.9f), 1.62f + 0.03f * MathF.Sin(leg * 2.1f));
        }

        private static float Heading(int leg)
        {
            return 0.8f * MathF.Sin(leg * 0.7f);
        }

        /// <summary>What the two programs report at time t: the helper's "now" pose and the engine's guessed-ahead pose.</summary>
        public void Observe(double t, ref Rng rng, out Vec3 sourcePos, out Quat sourceRot, out Vec3 targetPos, out Quat targetRot,
            out float linearSpeed, out float angularSpeed)
        {
            Vec3 v, w;
            TruePose(t, out sourcePos, out sourceRot, out v, out w);
            linearSpeed = v.Length();
            angularSpeed = w.Length();

            Vec3 aheadPos;
            Quat aheadRot;
            Vec3 v2, w2;
            TruePose(t + PredictAhead, out aheadPos, out aheadRot, out v2, out w2);
            targetPos = SourceToTarget.TransformPoint(aheadPos + Vec3.Transform(LocalOffset.Translation, aheadRot));
            targetRot = SourceToTarget.TransformRotation(aheadRot * LocalOffset.Rotation);

            sourcePos += Noise(ref rng, PositionNoise);
            targetPos += Noise(ref rng, PositionNoise);
            sourceRot = Wobble(ref rng, RotationNoise) * sourceRot;
            targetRot = Wobble(ref rng, RotationNoise) * targetRot;
        }

        private static Vec3 Noise(ref Rng rng, float sd)
        {
            return new Vec3(rng.Gaussian(), rng.Gaussian(), rng.Gaussian()) * sd;
        }

        private static Quat Wobble(ref Rng rng, float sd)
        {
            Vec3 axis = new Vec3(rng.Gaussian(), rng.Gaussian(), rng.Gaussian()) * sd;
            float angle = axis.Length();
            return angle < 1e-9f ? Quat.Identity : Quat.CreateFromAxisAngle(axis / angle, angle);
        }
    }

    public class SpaceAlignmentTests
    {
        private readonly ITestOutputHelper _out;

        public SpaceAlignmentTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static RigidTransform Truth(float yawDegrees, Vec3 shift)
        {
            return new RigidTransform(AlignmentMath.YawRotation(Check.Deg(yawDegrees)), shift);
        }

        [Fact]
        public void One_pose_seen_in_both_spaces_gives_the_transform_exactly()
        {
            RigidTransform truth = Truth(37f, new Vec3(1.2f, -0.7f, 0.03f));
            Vec3 p = new Vec3(0.3f, -0.2f, 1.6f);
            Quat q = Quat.CreateFromAxisAngle(Vec3.Normalize(new Vec3(0.2f, 0.9f, -0.3f)), 0.8f);

            RigidTransform solved = AlignmentMath.FromPosePair(p, q, truth.TransformPoint(p), truth.TransformRotation(q));

            Vec3 probe = new Vec3(-2f, 3f, 0.5f);
            Check.Near(truth.TransformPoint(probe), solved.TransformPoint(probe), 1e-4f, "far point");
            Assert.True(AlignmentMath.AngleBetween(truth.Rotation, solved.Rotation) < 1e-3f);
        }

        [Fact]
        public void Yaw_and_tilt_are_split_correctly()
        {
            float yaw, tilt;
            AlignmentMath.SplitYawTilt(AlignmentMath.YawRotation(Check.Deg(123f)), out yaw, out tilt);
            Assert.InRange(yaw, Check.Deg(122.99f), Check.Deg(123.01f));
            Assert.InRange(tilt, 0f, 1e-3f);

            AlignmentMath.SplitYawTilt(AlignmentMath.YawRotation(Check.Deg(-170f)), out yaw, out tilt);
            Assert.InRange(yaw, Check.Deg(-170.01f), Check.Deg(-169.99f));

            // A level turn followed by tipping 8 degrees over: the tilt is 8 degrees whichever way it tips.
            Quat tipped = Quat.CreateFromAxisAngle(new Vec3(1f, 0f, 0f), Check.Deg(8f)) * AlignmentMath.YawRotation(Check.Deg(40f));
            AlignmentMath.SplitYawTilt(tipped, out yaw, out tilt);
            Assert.InRange(tilt, Check.Deg(7.99f), Check.Deg(8.01f));
            Assert.InRange(yaw, Check.Deg(39f), Check.Deg(41f));

            Assert.InRange(AlignmentMath.WrapAngle(Check.Deg(350f)), Check.Deg(-10.01f), Check.Deg(-9.99f));
        }

        [Fact]
        public void The_aligner_finds_the_transform_from_a_head_that_moves_and_pauses_and_ignores_it_while_moving()
        {
            Rng rng = new Rng(4242);
            TwoSpaceDevice head = new TwoSpaceDevice();
            head.SourceToTarget = Truth(-63f, new Vec3(0.85f, 1.4f, -0.02f));
            SpaceAligner aligner = new SpaceAligner();

            int acceptedWhileMoving = 0, moving = 0, accepted = 0;
            float firstError = -1f;
            for (double t = 0.0; t < 20.0; t += 1.0 / 90.0)
            {
                Vec3 sp, tp;
                Quat sq, tq;
                float lin, ang;
                head.Observe(t, ref rng, out sp, out sq, out tp, out tq, out lin, out ang);
                AlignerOutcome outcome = aligner.Add(t, sp, sq, tp, tq, lin, ang);
                bool fast = lin > aligner.Settings.MaxLinearSpeed || ang > aligner.Settings.MaxAngularSpeed;
                if (fast) moving++;
                if (outcome == AlignerOutcome.Accepted)
                {
                    accepted++;
                    if (fast) acceptedWhileMoving++;
                    if (firstError < 0f) firstError = ErrorAt(aligner.Current, head.SourceToTarget, new Vec3(0f, 0f, 1.2f));
                }
            }

            Assert.True(aligner.HasEstimate);
            Assert.True(moving > 100, "the test head never moved");
            Assert.Equal(0, acceptedWhileMoving);
            Assert.True(accepted > 50, "accepted only " + accepted);
            Assert.Equal(0, aligner.Reseeds);

            // Judge it where a hand would be, a metre from the head, and at the far wall four metres off.
            float nearError = ErrorAt(aligner.Current, head.SourceToTarget, new Vec3(0.5f, -0.3f, 1.0f));
            float farError = ErrorAt(aligner.Current, head.SourceToTarget, new Vec3(4f, 1f, 1.5f));
            float yaw, tilt;
            AlignmentMath.SplitYawTilt(aligner.Current.Rotation * Quat.Conjugate(head.SourceToTarget.Rotation), out yaw, out tilt);
            _out.WriteLine("first reading error at 1.2 m up: " + (firstError * 1000f).ToString("0.0") + " mm");
            _out.WriteLine("after 20 s: " + accepted + " readings, hand-distance error " + (nearError * 1000f).ToString("0.00")
                + " mm, far-wall error " + (farError * 1000f).ToString("0.00") + " mm, yaw error " + (yaw * 180f / MathF.PI).ToString("0.000")
                + " deg, residual " + (aligner.ResidualDistance * 1000f).ToString("0.00") + " mm");

            Assert.True(nearError < 0.003f, "hand-distance error " + nearError);
            Assert.True(farError < 0.012f, "far-wall error " + farError);
            Assert.True(tilt < 1e-4f, "a level answer was asked for but it is tipped by " + tilt);
            Assert.True(MathF.Abs(yaw) < Check.Deg(0.15f), "yaw error " + yaw);
        }

        [Fact]
        public void A_guess_ahead_would_spoil_the_answer_if_moving_readings_were_used()
        {
            // The reason for the stillness gate, shown by switching it off.
            Rng rng = new Rng(99);
            TwoSpaceDevice head = new TwoSpaceDevice();
            head.SourceToTarget = Truth(20f, new Vec3(0.2f, 0.1f, 0f));
            head.PositionNoise = 0f;
            head.RotationNoise = 0f;

            float worstGated = 0f, worstUngated = 0f;
            SpaceAligner gated = new SpaceAligner();
            for (double t = 0.0; t < 12.0; t += 1.0 / 90.0)
            {
                Vec3 sp, tp;
                Quat sq, tq;
                float lin, ang;
                head.Observe(t, ref rng, out sp, out sq, out tp, out tq, out lin, out ang);
                RigidTransform single = AlignmentMath.FromPosePair(sp, sq, tp, tq);
                worstUngated = MathF.Max(worstUngated, (single.TransformPoint(sp) - head.SourceToTarget.TransformPoint(sp)).Length());
                if (gated.Add(t, sp, sq, tp, tq, lin, ang) == AlignerOutcome.Accepted)
                {
                    worstGated = MathF.Max(worstGated, (tp - head.SourceToTarget.TransformPoint(sp)).Length());
                }
            }
            _out.WriteLine("worst single-reading error: any time " + (worstUngated * 1000f).ToString("0.0") + " mm, only when still "
                + (worstGated * 1000f).ToString("0.00") + " mm");
            Assert.True(worstUngated > 0.02f, "the moving head should have been centimetres off: " + worstUngated);
            Assert.True(worstGated < 0.002f, "still readings should agree to about a millimetre: " + worstGated);
        }

        [Fact]
        public void The_aligner_starts_again_when_the_spaces_really_move_against_each_other()
        {
            Rng rng = new Rng(7);
            TwoSpaceDevice head = new TwoSpaceDevice();
            head.SourceToTarget = Truth(10f, new Vec3(0f, 0f, 0f));
            SpaceAligner aligner = new SpaceAligner();

            double t = 0.0;
            for (; t < 8.0; t += 1.0 / 90.0) Step(aligner, head, t, ref rng);
            Assert.True(ErrorAt(aligner.Current, head.SourceToTarget, new Vec3(1f, 0f, 1f)) < 0.004f);

            // SteamVR is re-centred: the helper's space turns a quarter turn and shifts.
            RigidTransform after = Truth(100f, new Vec3(0.5f, -0.4f, 0f));
            head.SourceToTarget = after;
            int outliers = aligner.Outliers;
            for (; t < 16.0; t += 1.0 / 90.0) Step(aligner, head, t, ref rng);

            Assert.True(aligner.Outliers > outliers, "the jump should first have looked like outliers");
            Assert.Equal(1, aligner.Reseeds);
            Assert.True(ErrorAt(aligner.Current, after, new Vec3(1f, 0f, 1f)) < 0.004f);
        }

        [Fact]
        public void A_single_bad_reading_does_not_move_the_answer()
        {
            Rng rng = new Rng(11);
            TwoSpaceDevice head = new TwoSpaceDevice();
            head.SourceToTarget = Truth(-25f, new Vec3(0.3f, 0.3f, 0f));
            SpaceAligner aligner = new SpaceAligner();
            double t = 0.0;
            for (; t < 1.0; t += 1.0 / 90.0) Step(aligner, head, t, ref rng);   // inside the first still spell
            RigidTransform before = aligner.Current;

            // One frame where tracking glitches by 30 cm.
            Vec3 sp, tp;
            Quat sq, tq;
            float lin, ang;
            head.Observe(t, ref rng, out sp, out sq, out tp, out tq, out lin, out ang);
            AlignerOutcome outcome = aligner.Add(t + 0.06, sp, sq, tp + new Vec3(0.3f, 0f, 0f), tq, 0f, 0f);

            Assert.Equal(AlignerOutcome.Outlier, outcome);
            Check.Near(before.Translation, aligner.Current.Translation, 1e-6f, "answer after one outlier");
        }

        [Fact]
        public void A_head_whose_axes_are_labelled_differently_is_refused_not_averaged_in()
        {
            // If the two programs disagreed about which way a device's own axes point, every reading
            // would look like the whole room tipped over. That must show up as "too tilted", not as an answer.
            Rng rng = new Rng(5);
            TwoSpaceDevice head = new TwoSpaceDevice();
            head.SourceToTarget = Truth(15f, new Vec3(0f, 0f, 0f));
            head.LocalOffset = new RigidTransform(Quat.CreateFromAxisAngle(new Vec3(1f, 0f, 0f), MathF.PI / 2f), Vec3.Zero);
            SpaceAligner aligner = new SpaceAligner();
            for (double t = 0.0; t < 6.0; t += 1.0 / 90.0) Step(aligner, head, t, ref rng);

            Assert.False(aligner.HasEstimate);
            Assert.True(aligner.RejectedTilt > 20);
            Assert.True(aligner.LastTilt > Check.Deg(45f));
        }

        [Fact]
        public void Positions_alone_give_the_same_transform_once_the_head_has_been_to_a_few_places()
        {
            Rng rng = new Rng(31);
            RigidTransform truth = Truth(142f, new Vec3(-1.1f, 0.6f, 0.05f));
            Vec3[] source = new Vec3[12];
            Vec3[] target = new Vec3[12];
            for (int i = 0; i < source.Length; i++)
            {
                source[i] = new Vec3(rng.Range(-0.8f, 0.8f), rng.Range(-0.8f, 0.8f), rng.Range(1.5f, 1.7f));
                target[i] = truth.TransformPoint(source[i]) + new Vec3(rng.Gaussian(), rng.Gaussian(), rng.Gaussian()) * 0.001f;
            }

            RigidTransform solved;
            float rms, spread;
            Assert.True(AlignmentMath.SolveYawFromPoints(source, target, source.Length, out solved, out rms, out spread));
            Assert.True(spread > 0.3f);
            Assert.True(rms < 0.003f, "rms " + rms);
            Assert.True(ErrorAt(solved, truth, new Vec3(0.5f, -0.3f, 1.0f)) < 0.003f);

            // All on one spot: the turn cannot be known, and it says so.
            for (int i = 0; i < source.Length; i++) { source[i] = new Vec3(0.1f, 0.2f, 1.6f); target[i] = truth.TransformPoint(source[i]); }
            Assert.False(AlignmentMath.SolveYawFromPoints(source, target, source.Length, out solved, out rms, out spread));
        }

        [Fact]
        public void The_grip_offset_is_recovered_and_puts_the_ball_in_the_same_place_either_way()
        {
            Rng rng = new Rng(77);
            RigidTransform alignment = Truth(55f, new Vec3(0.4f, -0.9f, 0.01f));
            // SteamVR's raw controller pose and the engine's grip pose: 6 cm apart and turned 40 degrees about the handle.
            RigidTransform gripOffset = new RigidTransform(
                Quat.CreateFromAxisAngle(Vec3.Normalize(new Vec3(0.1f, 1f, 0.05f)), Check.Deg(40f)), new Vec3(-0.045f, 0.012f, -0.038f));

            TwoSpaceDevice hand = new TwoSpaceDevice();
            hand.SourceToTarget = alignment;
            hand.LocalOffset = gripOffset;

            RigidOffsetAverager averager = new RigidOffsetAverager();
            StillnessGate gate = new StillnessGate();
            for (double t = 0.0; t < 10.0; t += 1.0 / 90.0)
            {
                Vec3 sp, tp;
                Quat sq, tq;
                float lin, ang;
                hand.Observe(t, ref rng, out sp, out sq, out tp, out tq, out lin, out ang);
                if (!gate.Update(t, lin, ang, 0.05f, 0.3f, 0.15)) continue;
                averager.Add(AlignmentMath.LocalOffset(alignment, sp, sq, tp, tq));
            }

            Assert.True(averager.HasEstimate);
            Assert.True(averager.Accepted > 100);
            RigidTransform g = averager.Current;
            _out.WriteLine("offset error " + ((g.Translation - gripOffset.Translation).Length() * 1000f).ToString("0.00") + " mm, "
                + (AlignmentMath.AngleBetween(g.Rotation, gripOffset.Rotation) * 180f / MathF.PI).ToString("0.00") + " deg");
            Assert.True((g.Translation - gripOffset.Translation).Length() < 0.001f);
            Assert.True(AlignmentMath.AngleBetween(g.Rotation, gripOffset.Rotation) < Check.Deg(0.3f));

            // The held ball, 8 cm out from the grip point: worked out from the engine's grip pose, and from
            // the helper's raw pose plus the offset, it must be the same point in the room.
            Vec3 ballInGrip = new Vec3(0.08f, 0f, 0.02f);
            Vec3 rawPos = new Vec3(0.3f, -0.25f, 1.1f);
            Quat rawRot = Quat.CreateFromAxisAngle(Vec3.Normalize(new Vec3(0.4f, -0.2f, 0.7f)), 1.3f);
            Vec3 gripPos = alignment.TransformPoint(rawPos + Vec3.Transform(gripOffset.Translation, rawRot));
            Quat gripRot = alignment.TransformRotation(rawRot * gripOffset.Rotation);
            Vec3 viaEngine = gripPos + Vec3.Transform(ballInGrip, gripRot);
            Vec3 ballInRaw = g.TransformPoint(ballInGrip);
            Vec3 viaHelper = alignment.TransformPoint(rawPos + Vec3.Transform(ballInRaw, rawRot));
            Check.Near(viaEngine, viaHelper, 0.0015f, "held ball position");
        }

        [Fact]
        public void An_offset_between_two_different_devices_is_refused()
        {
            RigidOffsetAverager averager = new RigidOffsetAverager();
            // Left raw pose against the right hand's grip pose: half a metre apart.
            RigidTransform silly = AlignmentMath.LocalOffset(RigidTransform.Identity, new Vec3(0f, 0.25f, 1f), Quat.Identity,
                new Vec3(0f, -0.25f, 1f), Quat.Identity);
            Assert.False(averager.Add(silly));
            Assert.False(averager.HasEstimate);
            Assert.Equal(1, averager.RejectedTooFar);
        }

        private static void Step(SpaceAligner aligner, TwoSpaceDevice head, double t, ref Rng rng)
        {
            Vec3 sp, tp;
            Quat sq, tq;
            float lin, ang;
            head.Observe(t, ref rng, out sp, out sq, out tp, out tq, out lin, out ang);
            aligner.Add(t, sp, sq, tp, tq, lin, ang);
        }

        private static float ErrorAt(RigidTransform solved, RigidTransform truth, Vec3 point)
        {
            return (solved.TransformPoint(point) - truth.TransformPoint(point)).Length();
        }
    }

    public class ThrowSpaceTests
    {
        private readonly ITestOutputHelper _out;

        public ThrowSpaceTests(ITestOutputHelper output)
        {
            _out = output;
        }

        [Fact]
        public void A_throw_worked_out_in_the_play_areas_own_space_lands_where_the_same_throw_worked_out_in_the_world_does()
        {
            Rng rng = new Rng(2024);
            Vec3 worldGravity = new Vec3(0f, 0f, -9.81f);
            float worst = 0f;
            int throws = 0;

            for (int i = 0; i < 40; i++)
            {
                ThrowKind kind = (ThrowKind)(i % 3);
                SimThrow th = ThrowSim.Generate(Swing.Random(kind, ref rng), Rig.Random(90, ref rng), ref rng);

                // Where the play area sits in the world: turned and moved, differently each time.
                RigidTransform localToWorld = new RigidTransform(
                    AlignmentMath.YawRotation(rng.Range(-3f, 3f)), new Vec3(rng.Range(-5f, 5f), rng.Range(-5f, 5f), rng.Range(0f, 2f)));
                RigidTransform worldToLocal = localToWorld.Inverse();

                PoseHistory worldHistory = new PoseHistory();
                PoseHistory localHistory = new PoseHistory();
                GripReleaseDetector detector = new GripReleaseDetector();
                ThrowSettings worldSettings = new ThrowSettings();
                ThrowSettings localSettings = new ThrowSettings();
                worldSettings.BallLocalOffset = localSettings.BallLocalOffset = th.Swing.BallOffsetLocal.ToVec3();
                worldSettings.Gravity = worldGravity;
                localSettings.Gravity = ThrowSpace.GravityInLocal(localToWorld, worldGravity);

                foreach (SimFrame f in th.Frames)
                {
                    // The simulator's frames are the "local" ones; the world sees them through the play area's placement.
                    localHistory.Add(f.Stamp, f.Position, f.Rotation);
                    worldHistory.Add(f.Stamp, localToWorld.TransformPoint(f.Position), localToWorld.TransformRotation(f.Rotation));
                    if (detector.Update(f.Stamp, f.Squeeze) != GripEvent.Released) continue;

                    ThrowResult inWorld = ThrowEstimator.Estimate(worldHistory, detector.ReleaseTime, f.Stamp, worldSettings);
                    ThrowResult carried = ThrowSpace.ToWorld(ThrowEstimator.Estimate(localHistory, detector.ReleaseTime, f.Stamp, localSettings), localToWorld);

                    Assert.True(inWorld.Valid && carried.Valid);
                    Assert.Equal(inWorld.IsDrop, carried.IsDrop);
                    Assert.Equal(inWorld.ReleaseTime, carried.ReleaseTime);
                    worst = MathF.Max(worst, (inWorld.NowPosition - carried.NowPosition).Length());
                    Check.Near(inWorld.ReleasePosition, carried.ReleasePosition, 2e-4f, "release position");
                    Check.Near(inWorld.ReleaseVelocity, carried.ReleaseVelocity, 5e-3f, "release velocity");
                    Check.Near(inWorld.NowPosition, carried.NowPosition, 5e-4f, "spawn position (gravity catch-up included)");
                    Check.Near(inWorld.NowVelocity, carried.NowVelocity, 5e-3f, "spawn velocity (gravity catch-up included)");
                    Check.Near(inWorld.AngularVelocity, carried.AngularVelocity, 2e-2f, "spin");
                    throws++;
                    break;
                }
            }
            _out.WriteLine(throws + " throws, worst spawn-point difference " + (worst * 1000f).ToString("0.000") + " mm");
            Assert.True(throws >= 35, "only " + throws + " of 40 simulated throws released");
        }

        [Fact]
        public void Gravity_seen_from_inside_a_tipped_space_still_pulls_toward_the_world_floor()
        {
            RigidTransform tipped = new RigidTransform(Quat.CreateFromAxisAngle(new Vec3(1f, 0f, 0f), Check.Deg(30f)), new Vec3(1f, 2f, 3f));
            Vec3 local = ThrowSpace.GravityInLocal(tipped, new Vec3(0f, 0f, -9.81f));
            Check.Near(new Vec3(0f, 0f, -9.81f), tipped.TransformDirection(local), 1e-4f, "gravity back in the world");
            Assert.True(MathF.Abs(local.Y) > 4f, "inside the tipped space gravity must lean sideways");
        }

        [Fact]
        public void Moving_the_play_area_mid_swing_does_not_disturb_a_throw_kept_in_its_own_space()
        {
            // The player steps through a portal 30 ms before letting go: the play area jumps 6 m and turns
            // half way round. A world-space history would see the hand cross the room in one frame.
            Rng rng = new Rng(808);
            SimThrow th = ThrowSim.Generate(Swing.Random(ThrowKind.Overarm, ref rng), Rig.Perfect(90), ref rng);
            RigidTransform before = RigidTransform.Identity;
            RigidTransform after = new RigidTransform(AlignmentMath.YawRotation(MathF.PI), new Vec3(6f, 1f, 0f));
            Vec3 gravity = new Vec3(0f, 0f, -9.81f);

            PoseHistory local = new PoseHistory();
            PoseHistory world = new PoseHistory();
            GripReleaseDetector detector = new GripReleaseDetector();
            ThrowSettings settings = new ThrowSettings();
            settings.BallLocalOffset = th.Swing.BallOffsetLocal.ToVec3();
            double jumpAt = th.TrueReleaseTime - 0.030;

            foreach (SimFrame f in th.Frames)
            {
                RigidTransform placement = f.Stamp < jumpAt ? before : after;
                local.Add(f.Stamp, f.Position, f.Rotation);
                world.Add(f.Stamp, placement.TransformPoint(f.Position), placement.TransformRotation(f.Rotation));
                if (detector.Update(f.Stamp, f.Squeeze) != GripEvent.Released) continue;

                settings.Gravity = ThrowSpace.GravityInLocal(after, gravity);
                ThrowResult good = ThrowSpace.ToWorld(ThrowEstimator.Estimate(local, detector.ReleaseTime, f.Stamp, settings), after);
                settings.Gravity = gravity;
                ThrowResult bad = ThrowEstimator.Estimate(world, detector.ReleaseTime, f.Stamp, settings);

                // What the very same throw gives with no portal at all, then carried to where the player now stands.
                ThrowResult noPortal = ThrowEstimator.Estimate(local, detector.ReleaseTime, f.Stamp, settings);
                Vec3 wanted = after.TransformDirection(noPortal.ReleaseVelocity);

                float goodAngle = MathUtil.AngleBetween(good.ReleaseVelocity, wanted) * 180f / MathF.PI;
                float badAngle = MathUtil.AngleBetween(bad.ReleaseVelocity, wanted) * 180f / MathF.PI;
                _out.WriteLine("no portal: " + noPortal.ReleaseVelocity.Length().ToString("0.0") + " m/s. Through the portal, kept in its own space: "
                    + goodAngle.ToString("0.00") + " deg off, " + good.ReleaseVelocity.Length().ToString("0.0")
                    + " m/s. Kept in the world: " + badAngle.ToString("0.0") + " deg off, " + bad.ReleaseVelocity.Length().ToString("0.0") + " m/s.");
                Assert.True(goodAngle < 0.05f, "own-space throw is " + goodAngle + " deg off the no-portal throw");
                Check.Near(wanted, good.ReleaseVelocity, 2e-3f, "release velocity");
                Check.Near(after.TransformPoint(noPortal.ReleasePosition), good.ReleasePosition, 1e-4f, "release position");
                Check.Near(after.TransformPoint(noPortal.NowPosition), good.NowPosition, 1e-3f, "spawn position");
                Assert.True(badAngle > 20f || bad.ReleaseVelocity.Length() > 2f * noPortal.ReleaseVelocity.Length(),
                    "the world-space history should have been thrown off by the jump");
                return;
            }
            Assert.Fail("the simulated throw never released");
        }
    }

    public class ChangeTimeGripFeedTests
    {
        // A controller that reports every 4 ms. Returns the value in the newest report at or before 'time',
        // and when the reported value last changed, as SteamVR would.
        private static void Report(Func<double, double> squeeze, double time, out double changeTime, out float value)
        {
            const double interval = 0.004, offset = 0.0007;
            double report = Math.Floor((time - offset) / interval) * interval + offset;
            double v = squeeze(report);
            double r = report;
            while (r - interval > -1.0 && squeeze(r - interval) == v) r -= interval;   // the controller was switched on at -1 s
            changeTime = r;
            value = (float)v;
        }

        private static GripEvent Run(Func<double, double> squeeze, double from, double to, GripReleaseDetector detector, ChangeTimeGripFeed feed,
            double jitter, ref Rng rng, out int releases)
        {
            releases = 0;
            GripEvent last = GripEvent.None;
            for (double t = from; t < to; t += 0.002)
            {
                double changeTime;
                float value;
                Report(squeeze, t, out changeTime, out value);
                if (jitter > 0.0) changeTime += jitter * (rng.NextFloat() - 0.5);
                GripEvent e = feed.Feed(detector, changeTime, value);
                if (e == GripEvent.Released) releases++;
                if (e != GripEvent.None) last = e;
            }
            return last;
        }

        [Fact]
        public void A_gradual_let_go_is_placed_on_the_ramp_to_within_microseconds()
        {
            // Grab at 0.25-0.30, hold, open over 40 ms from 1.1837 (the helper's fake throw).
            Func<double, double> squeeze = t =>
                t <= 0.25 ? 0.0 : t < 0.30 ? (t - 0.25) / 0.05 : t <= 1.1837 ? 1.0 : t < 1.2237 ? 1.0 - (t - 1.1837) / 0.040 : 0.0;
            GripReleaseDetector detector = new GripReleaseDetector();
            ChangeTimeGripFeed feed = new ChangeTimeGripFeed();
            Rng rng = new Rng(1);
            int releases;
            Run(squeeze, 0.0, 1.5, detector, feed, 0.0, ref rng, out releases);

            Assert.Equal(1, releases);
            double truth = 1.1837 + 0.25 * 0.040;   // passes 75 percent of full
            Assert.InRange(detector.ReleaseTime, truth - 5e-6, truth + 5e-6);
            Assert.InRange(detector.ReleaseOnsetTime, 1.1837 - 0.004, 1.1837 + 0.004);
            Assert.True(feed.Fed < 60, "repeats should have been skipped, but " + feed.Fed + " readings were fed");
        }

        [Fact]
        public void A_sudden_let_go_after_a_long_hold_is_not_placed_back_in_the_hold()
        {
            // An on/off grip: full squeeze from 0.3 s, straight to zero at 2.0 s. Without the bridging
            // reading the detector would draw its line from 0.3 s and place the release near 0.7 s.
            Func<double, double> squeeze = t => t > 0.3 && t <= 2.0 ? 1.0 : 0.0;
            Rng rng = new Rng(2);

            GripReleaseDetector naive = new GripReleaseDetector();
            double lastChange = double.NaN;
            for (double t = 0.0; t < 2.5; t += 0.002)
            {
                double changeTime;
                float value;
                Report(squeeze, t, out changeTime, out value);
                if (changeTime != lastChange) { lastChange = changeTime; naive.Update(changeTime, value); }
            }
            Assert.True(naive.ReleaseTime < 1.0, "expected the naive feed to misplace the release, got " + naive.ReleaseTime);

            GripReleaseDetector detector = new GripReleaseDetector();
            ChangeTimeGripFeed feed = new ChangeTimeGripFeed();
            int releases;
            Run(squeeze, 0.0, 2.5, detector, feed, 0.0, ref rng, out releases);
            Assert.Equal(1, releases);
            // The first report after 2.0 s is at 2.0007. The release may be placed up to one assumed report interval before it.
            Assert.InRange(detector.ReleaseTime, 2.0007 - feed.MaxReportIntervalSeconds, 2.0007 + 1e-9);
        }

        [Fact]
        public void A_change_time_that_wobbles_from_poll_to_poll_causes_no_false_events_and_no_loss_of_accuracy()
        {
            Func<double, double> squeeze = t =>
                t <= 0.25 ? 0.0 : t < 0.30 ? (t - 0.25) / 0.05 : t <= 1.1837 ? 1.0 : t < 1.2237 ? 1.0 - (t - 1.1837) / 0.040 : 0.0;
            GripReleaseDetector detector = new GripReleaseDetector();
            ChangeTimeGripFeed feed = new ChangeTimeGripFeed();
            Rng rng = new Rng(3);
            int releases;
            Run(squeeze, 0.0, 1.5, detector, feed, 0.0002, ref rng, out releases);   // +-0.1 ms of wobble

            Assert.Equal(1, releases);
            Assert.False(detector.IsHeld);
            double truth = 1.1837 + 0.25 * 0.040;
            Assert.InRange(detector.ReleaseTime, truth - 0.0005, truth + 0.0005);
        }
    }
}
