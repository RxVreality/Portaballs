using System;
using Xunit;

namespace Portaballs.Core.Tests
{
    internal static class Near
    {
        public static void Vec(Vec3 expected, Vec3 actual, float tolerance, string what)
        {
            Assert.True((expected - actual).Length() <= tolerance,
                what + ": expected " + expected + " but got " + actual + " (allowed error " + tolerance + ")");
        }

        /// <summary>Two rotations are the same turn (q and -q are the same turn).</summary>
        public static void Rotation(Quat expected, Quat actual, float tolerance, string what)
        {
            float dot = MathF.Abs(Quat.Dot(Quat.Normalize(expected), Quat.Normalize(actual)));
            Assert.True(dot >= 1f - tolerance, what + ": expected " + expected + " but got " + actual + " (dot " + dot + ")");
        }

        public static Vec3 RandomVec(ref Rng rng, float size)
        {
            return new Vec3(rng.Range(-size, size), rng.Range(-size, size), rng.Range(-size, size));
        }

        public static Quat RandomRotation(ref Rng rng)
        {
            Vec3 axis = RandomVec(ref rng, 1f);
            if (axis.Length() < 1e-3f) axis = new Vec3(0f, 1f, 0f);
            return Quat.CreateFromAxisAngle(Vec3.Normalize(axis), rng.Range(-MathF.PI, MathF.PI));
        }
    }

    public class OpenVrSpaceTests
    {
        // SteamVR tracking space: +X right, +Y up, -Z forward.
        private static readonly Vec3 VrRight = new Vec3(1f, 0f, 0f);
        private static readonly Vec3 VrUp = new Vec3(0f, 1f, 0f);
        private static readonly Vec3 VrForward = new Vec3(0f, 0f, -1f);

        // Core (Units.cs): +X forward, +Y left, +Z up.
        private static readonly Vec3 CoreForward = new Vec3(1f, 0f, 0f);
        private static readonly Vec3 CoreLeft = new Vec3(0f, 1f, 0f);
        private static readonly Vec3 CoreUp = new Vec3(0f, 0f, 1f);

        [Fact]
        public void Named_directions_land_on_the_core_axes()
        {
            Assert.Equal(CoreForward, OpenVrSpace.ToCore(VrForward));
            Assert.Equal(-CoreLeft, OpenVrSpace.ToCore(VrRight));
            Assert.Equal(CoreUp, OpenVrSpace.ToCore(VrUp));
            Assert.Equal(Units.Up, OpenVrSpace.ToCore(VrUp));
        }

        [Fact]
        public void A_point_two_metres_ahead_one_right_and_head_high_converts_as_expected()
        {
            // SteamVR: 1 m right, 1.6 m up, 2 m forward.
            Vec3 core = OpenVrSpace.ToCore(new Vec3(1f, 1.6f, -2f));
            Assert.Equal(new Vec3(2f, -1f, 1.6f), core);
        }

        [Fact]
        public void Gravity_points_down_in_both()
        {
            Vec3 vrDown = new Vec3(0f, -9.81f, 0f);
            Assert.Equal(Units.Gravity, OpenVrSpace.ToCore(vrDown));
        }

        [Fact]
        public void There_and_back_is_exact()
        {
            Rng rng = new Rng(11);
            for (int i = 0; i < 500; i++)
            {
                Vec3 v = Near.RandomVec(ref rng, 5f);
                Quat q = Near.RandomRotation(ref rng);
                Assert.Equal(v, OpenVrSpace.ToOpenVr(OpenVrSpace.ToCore(v)));
                Assert.Equal(v, OpenVrSpace.ToCore(OpenVrSpace.ToOpenVr(v)));
                Assert.Equal(q, OpenVrSpace.ToOpenVr(OpenVrSpace.ToCore(q)));
                Assert.Equal(q, OpenVrSpace.ToCore(OpenVrSpace.ToOpenVr(q)));
            }
        }

        [Fact]
        public void Lengths_angles_and_handedness_survive()
        {
            Rng rng = new Rng(12);
            for (int i = 0; i < 500; i++)
            {
                Vec3 a = Near.RandomVec(ref rng, 3f);
                Vec3 b = Near.RandomVec(ref rng, 3f);
                Vec3 ca = OpenVrSpace.ToCore(a);
                Vec3 cb = OpenVrSpace.ToCore(b);
                Assert.True(MathF.Abs(a.Length() - ca.Length()) <= 1e-5f, "length changed");
                Assert.True(MathF.Abs(Vec3.Dot(a, b) - Vec3.Dot(ca, cb)) <= 1e-4f, "angle changed");
                // No mirror: the cross product converts like any other vector.
                Near.Vec(OpenVrSpace.ToCore(Vec3.Cross(a, b)), Vec3.Cross(ca, cb), 1e-4f, "cross product");
            }
            // Right-handed in SteamVR (right x up = back) and right-handed in core (forward x left = up).
            Assert.Equal(-VrForward, Vec3.Cross(VrRight, VrUp));
            Assert.Equal(CoreUp, Vec3.Cross(CoreForward, CoreLeft));
        }

        [Fact]
        public void Rotation_converted_then_applied_equals_vector_rotated_then_converted()
        {
            Rng rng = new Rng(13);
            for (int i = 0; i < 2000; i++)
            {
                Quat q = Near.RandomRotation(ref rng);
                Vec3 v = Near.RandomVec(ref rng, 4f);

                Vec3 rotatedThenConverted = OpenVrSpace.ToCore(Vec3.Transform(v, q));
                Vec3 convertedThenApplied = Vec3.Transform(OpenVrSpace.ToCore(v), OpenVrSpace.ToCore(q));

                Near.Vec(rotatedThenConverted, convertedThenApplied, 2e-5f, "rotation " + i);
            }
        }

        [Fact]
        public void The_same_holds_going_back_to_SteamVR_axes()
        {
            Rng rng = new Rng(14);
            for (int i = 0; i < 500; i++)
            {
                Quat q = Near.RandomRotation(ref rng);
                Vec3 v = Near.RandomVec(ref rng, 4f);
                Near.Vec(OpenVrSpace.ToOpenVr(Vec3.Transform(v, q)),
                         Vec3.Transform(OpenVrSpace.ToOpenVr(v), OpenVrSpace.ToOpenVr(q)), 2e-5f, "rotation " + i);
            }
        }

        [Fact]
        public void One_rotation_after_another_converts_to_the_same_two_in_the_same_order()
        {
            Rng rng = new Rng(15);
            for (int i = 0; i < 500; i++)
            {
                Quat a = Near.RandomRotation(ref rng);
                Quat b = Near.RandomRotation(ref rng);
                Near.Rotation(OpenVrSpace.ToCore(a * b), OpenVrSpace.ToCore(a) * OpenVrSpace.ToCore(b), 1e-5f, "product " + i);
            }
            Assert.Equal(Quat.Identity, OpenVrSpace.ToCore(Quat.Identity));
        }

        [Fact]
        public void A_device_pointing_along_its_own_minus_Z_points_along_its_own_plus_X_after_conversion()
        {
            Rng rng = new Rng(16);
            for (int i = 0; i < 500; i++)
            {
                Quat vr = Near.RandomRotation(ref rng);
                Quat core = OpenVrSpace.ToCore(vr);

                Vec3 forwardInWorld = OpenVrSpace.ToCore(Vec3.Transform(VrForward, vr));
                Vec3 leftInWorld = OpenVrSpace.ToCore(Vec3.Transform(-VrRight, vr));
                Vec3 upInWorld = OpenVrSpace.ToCore(Vec3.Transform(VrUp, vr));

                Near.Vec(forwardInWorld, Vec3.Transform(CoreForward, core), 1e-5f, "forward");
                Near.Vec(leftInWorld, Vec3.Transform(CoreLeft, core), 1e-5f, "left");
                Near.Vec(upInWorld, Vec3.Transform(CoreUp, core), 1e-5f, "up");
            }
        }

        [Fact]
        public void Turning_the_head_left_is_a_turn_about_up_in_both()
        {
            // SteamVR: a quarter turn about +Y (up) swings forward (-Z) round to the left (-X).
            Quat vrTurnLeft = Quat.CreateFromAxisAngle(VrUp, MathF.PI / 2f);
            Near.Vec(-VrRight, Vec3.Transform(VrForward, vrTurnLeft), 1e-6f, "SteamVR turn left");

            // Core: the same turn is a quarter turn about +Z (up), swinging +X round to +Y (left).
            Quat expected = Quat.CreateFromAxisAngle(CoreUp, MathF.PI / 2f);
            Near.Rotation(expected, OpenVrSpace.ToCore(vrTurnLeft), 1e-6f, "turn left");
            Near.Vec(CoreLeft, Vec3.Transform(CoreForward, OpenVrSpace.ToCore(vrTurnLeft)), 1e-6f, "core turn left");
        }

        [Fact]
        public void Looking_up_is_a_turn_about_the_right_hand_side_in_both()
        {
            // SteamVR: a positive turn about +X (right) tips forward (-Z) up toward +Y.
            Quat vrLookUp = Quat.CreateFromAxisAngle(VrRight, MathF.PI / 6f);
            Assert.True(Vec3.Transform(VrForward, vrLookUp).Y > 0.49f);

            Vec3 coreForward = Vec3.Transform(CoreForward, OpenVrSpace.ToCore(vrLookUp));
            Near.Vec(new Vec3(MathF.Cos(MathF.PI / 6f), 0f, MathF.Sin(MathF.PI / 6f)), coreForward, 1e-6f, "look up");
        }

        [Fact]
        public void Spin_converts_like_a_plain_vector()
        {
            // A point at r on a body spinning at w moves at w x r. That must still be true after converting all three.
            Rng rng = new Rng(17);
            for (int i = 0; i < 500; i++)
            {
                Vec3 w = Near.RandomVec(ref rng, 20f);
                Vec3 r = Near.RandomVec(ref rng, 0.5f);
                Vec3 v = Vec3.Cross(w, r);
                Near.Vec(OpenVrSpace.ToCore(v), Vec3.Cross(OpenVrSpace.ToCore(w), OpenVrSpace.ToCore(r)), 1e-4f, "spin " + i);
            }

            // And the spin axis agrees with the rotation it produces: spin about SteamVR up for a short
            // time turns forward toward the left, in core axes too.
            Vec3 coreSpin = OpenVrSpace.ToCore(new Vec3(0f, 2f, 0f));
            Assert.Equal(new Vec3(0f, 0f, 2f), coreSpin);
            Quat step = Quat.CreateFromAxisAngle(Vec3.Normalize(coreSpin), coreSpin.Length() * 0.01f);
            Assert.True(Vec3.Transform(CoreForward, step).Y > 0f);
        }

        [Fact]
        public void A_pose_matrix_gives_back_the_rotation_that_made_it()
        {
            Rng rng = new Rng(18);
            for (int i = 0; i < 2000; i++)
            {
                Quat q = Near.RandomRotation(ref rng);
                Quat back = FromMatrixOf(q);
                Near.Rotation(q, back, 1e-5f, "matrix " + i);
                Assert.Equal(1f, back.Length(), 4);
            }
        }

        [Fact]
        public void Pose_matrices_for_half_turns_and_no_turn_work_too()
        {
            Quat[] cases =
            {
                Quat.Identity,
                Quat.CreateFromAxisAngle(VrRight, MathF.PI),
                Quat.CreateFromAxisAngle(VrUp, MathF.PI),
                Quat.CreateFromAxisAngle(VrForward, MathF.PI),
                Quat.CreateFromAxisAngle(Vec3.Normalize(new Vec3(1f, 1f, 0f)), MathF.PI),
                Quat.CreateFromAxisAngle(Vec3.Normalize(new Vec3(1f, 1f, 1f)), MathF.PI * 0.999f),
                Quat.CreateFromAxisAngle(VrUp, 1e-4f),
            };
            for (int i = 0; i < cases.Length; i++)
            {
                Quat back = FromMatrixOf(cases[i]);
                Vec3 probe = new Vec3(0.3f, -0.7f, 0.2f);
                Near.Vec(Vec3.Transform(probe, cases[i]), Vec3.Transform(probe, back), 1e-5f, "case " + i);
            }
        }

        [Fact]
        public void A_broken_pose_matrix_gives_no_rotation_rather_than_rubbish()
        {
            Assert.Equal(Quat.Identity, OpenVrSpace.RotationFromMatrix(
                float.NaN, 0f, 0f, 0f, float.NaN, 0f, 0f, 0f, float.NaN));
        }

        [Fact]
        public void A_whole_pose_matrix_converts_to_the_right_core_pose()
        {
            // A controller held 0.3 m right, 1.2 m up, 0.4 m forward, turned a quarter turn to the left.
            Quat vr = Quat.CreateFromAxisAngle(VrUp, MathF.PI / 2f);
            Quat core = OpenVrSpace.ToCore(FromMatrixOf(vr));
            Vec3 position = OpenVrSpace.ToCore(new Vec3(0.3f, 1.2f, -0.4f));

            Assert.Equal(new Vec3(0.4f, -0.3f, 1.2f), position);
            // Its nose now points to the player's left.
            Near.Vec(CoreLeft, Vec3.Transform(CoreForward, core), 1e-6f, "controller forward");
        }

        [Fact]
        public void Gaze_direction_comes_from_origin_and_target()
        {
            Vec3 direction;
            Assert.True(OpenVrSpace.TryDirection(new Vec3(0f, 0f, 1.6f), new Vec3(3f, 0f, 1.6f), out direction));
            Assert.Equal(CoreForward, direction);

            Assert.False(OpenVrSpace.TryDirection(new Vec3(1f, 2f, 3f), new Vec3(1f, 2f, 3f), out direction));
            Assert.Equal(CoreForward, direction);

            Assert.False(OpenVrSpace.TryDirection(new Vec3(float.NaN, 0f, 0f), new Vec3(1f, 0f, 0f), out direction));
        }

        /// <summary>Build the 3x3 a SteamVR pose matrix would hold for this rotation (its columns are where the axes end up).</summary>
        private static Quat FromMatrixOf(Quat q)
        {
            Vec3 cx = Vec3.Transform(new Vec3(1f, 0f, 0f), q);
            Vec3 cy = Vec3.Transform(new Vec3(0f, 1f, 0f), q);
            Vec3 cz = Vec3.Transform(new Vec3(0f, 0f, 1f), q);
            return OpenVrSpace.RotationFromMatrix(
                cx.X, cy.X, cz.X,
                cx.Y, cy.Y, cz.Y,
                cx.Z, cy.Z, cz.Z);
        }
    }
}
