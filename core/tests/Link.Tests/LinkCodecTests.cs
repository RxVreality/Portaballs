using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Xunit;

namespace Portaballs.Core.Tests
{
    internal static class LinkMake
    {
        public static LinkPose Pose(ref Rng rng, int flags)
        {
            LinkPose p = new LinkPose();
            p.Flags = flags;
            p.Position = Near.RandomVec(ref rng, 3f);
            p.Rotation = Near.RandomRotation(ref rng);
            p.Velocity = Near.RandomVec(ref rng, 12f);
            p.AngularVelocity = Near.RandomVec(ref rng, 30f);
            return p;
        }

        public static LinkHand Hand(ref Rng rng, double time)
        {
            LinkHand h = new LinkHand();
            h.Pose = Pose(ref rng, rng.RangeInt(0, 8));
            h.Grip = rng.NextFloat();
            h.GripTime = time - rng.NextFloat() * 0.02;
            h.Trigger = rng.NextFloat();
            h.TriggerTime = time - rng.NextFloat() * 30.0;
            return h;
        }

        public static LinkSample Sample(ref Rng rng, long index, double time)
        {
            LinkSample s = new LinkSample();
            s.Index = index;
            s.Time = time;
            s.Head = Pose(ref rng, rng.RangeInt(0, 4));
            s.Left = Hand(ref rng, time);
            s.Right = Hand(ref rng, time);
            LinkGaze g = new LinkGaze();
            g.Flags = rng.RangeInt(0, 8);
            g.Origin = Near.RandomVec(ref rng, 2f);
            g.Direction = rng.OnUnitSphere();
            s.Gaze = g;
            return s;
        }

        public static string Batch(long sequence, double sendTime, IList<LinkSample> samples)
        {
            StringBuilder sb = new StringBuilder();
            LinkEncoder.AppendBatchHeader(sb, sequence, sendTime, samples.Count);
            for (int i = 0; i < samples.Count; i++) LinkEncoder.AppendSample(sb, samples[i]);
            return sb.ToString();
        }
    }

    internal static class LinkSame
    {
        // Bit-for-bit, so that -0 counts too. "Not a number" only has to come back as "not a number".
        public static void Float(float expected, float actual, string what)
        {
            if (float.IsNaN(expected) && float.IsNaN(actual)) return;
            Assert.True(BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(actual),
                what + ": sent " + expected.ToString("R", CultureInfo.InvariantCulture) + " but read " + actual.ToString("R", CultureInfo.InvariantCulture));
        }

        public static void Double(double expected, double actual, string what)
        {
            Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
                what + ": sent " + expected.ToString("R", CultureInfo.InvariantCulture) + " but read " + actual.ToString("R", CultureInfo.InvariantCulture));
        }

        public static void Vec(Vec3 expected, Vec3 actual, string what)
        {
            Float(expected.X, actual.X, what + ".X");
            Float(expected.Y, actual.Y, what + ".Y");
            Float(expected.Z, actual.Z, what + ".Z");
        }

        public static void Pose(LinkPose expected, LinkPose actual, string what)
        {
            Assert.Equal(expected.Flags, actual.Flags);
            Vec(expected.Position, actual.Position, what + " position");
            Float(expected.Rotation.X, actual.Rotation.X, what + " rotation.X");
            Float(expected.Rotation.Y, actual.Rotation.Y, what + " rotation.Y");
            Float(expected.Rotation.Z, actual.Rotation.Z, what + " rotation.Z");
            Float(expected.Rotation.W, actual.Rotation.W, what + " rotation.W");
            Vec(expected.Velocity, actual.Velocity, what + " velocity");
            Vec(expected.AngularVelocity, actual.AngularVelocity, what + " angular velocity");
        }

        public static void Hand(LinkHand expected, LinkHand actual, string what)
        {
            Pose(expected.Pose, actual.Pose, what);
            Float(expected.Grip, actual.Grip, what + " grip");
            Double(expected.GripTime, actual.GripTime, what + " grip time");
            Float(expected.Trigger, actual.Trigger, what + " trigger");
            Double(expected.TriggerTime, actual.TriggerTime, what + " trigger time");
        }

        public static void Sample(LinkSample expected, LinkSample actual)
        {
            Assert.Equal(expected.Index, actual.Index);
            Double(expected.Time, actual.Time, "time");
            Pose(expected.Head, actual.Head, "head");
            Hand(expected.Left, actual.Left, "left");
            Hand(expected.Right, actual.Right, "right");
            Assert.Equal(expected.Gaze.Flags, actual.Gaze.Flags);
            Vec(expected.Gaze.Origin, actual.Gaze.Origin, "gaze origin");
            Vec(expected.Gaze.Direction, actual.Gaze.Direction, "gaze direction");
        }
    }

    public class LinkCodecTests
    {
        [Fact]
        public void A_thousand_random_samples_come_back_bit_for_bit()
        {
            Rng rng = new Rng(21);
            LinkDecoder decoder = new LinkDecoder();
            List<LinkSample> got = new List<LinkSample>();
            List<LinkSample> sent = new List<LinkSample>();
            double time = 123456.789012345;   // a PC that has been on for a day and a half
            long index = 0;

            for (int batch = 0; batch < 200; batch++)
            {
                List<LinkSample> group = new List<LinkSample>();
                for (int i = 0; i < 5; i++)
                {
                    time += 0.002 + rng.NextFloat() * 1e-4;
                    group.Add(LinkMake.Sample(ref rng, index++, time));
                }
                sent.AddRange(group);
                int added = decoder.Decode(LinkMake.Batch(batch, time, group), got);
                Assert.Equal(5, added);
            }

            Assert.Equal(sent.Count, got.Count);
            for (int i = 0; i < sent.Count; i++) LinkSame.Sample(sent[i], got[i]);

            Assert.Equal(200, decoder.BatchCount);
            Assert.Equal(199, decoder.LastBatchSequence);
            Assert.Equal(1000, decoder.SampleCount);
            Assert.Equal(999, decoder.LastSampleIndex);
            Assert.Equal(0, decoder.MissedBatches);
            Assert.Equal(0, decoder.MissedSamples);
            Assert.Equal(0, decoder.BadLines);
        }

        [Fact]
        public void Awkward_numbers_come_back_bit_for_bit()
        {
            float[] floats =
            {
                0f, -0f, 1f, -1f, 0.1f, 1f / 3f, float.Epsilon, -float.Epsilon, float.MaxValue, float.MinValue,
                1e-7f, 1e-20f, 1e20f, 16777217f, 0.99999994f, MathF.PI, float.NaN, float.PositiveInfinity, float.NegativeInfinity,
            };
            double[] times =
            {
                0.0, 1e-9, 0.1, 1.0 / 3.0, 86400.000000123, 31536000.123456789, 123456789.12345679, double.Epsilon, -5.5,
            };

            LinkDecoder decoder = new LinkDecoder();
            List<LinkSample> got = new List<LinkSample>();
            List<LinkSample> sent = new List<LinkSample>();
            long index = 0;
            foreach (double t in times)
            {
                foreach (float x in floats)
                {
                    LinkSample s = new LinkSample();
                    s.Index = index++;
                    s.Time = t;
                    s.Head.Flags = LinkPoseFlags.Connected | LinkPoseFlags.PoseValid;
                    s.Head.Position = new Vec3(x, -x, x * 0.5f);
                    s.Head.Rotation = new Quat(x, 0f, -0f, 1f);
                    s.Head.Velocity = new Vec3(x, x, x);
                    s.Left.Grip = x;
                    s.Left.GripTime = t - 1e-4;
                    s.Right.Trigger = x;
                    s.Right.TriggerTime = t * 0.999999;
                    s.Right.Pose.AngularVelocity = new Vec3(-x, x, -x);
                    s.Gaze.Flags = LinkGazeFlags.Usable;
                    s.Gaze.Direction = new Vec3(x, 0f, 0f);
                    sent.Add(s);
                }
            }

            decoder.Decode(LinkMake.Batch(0, 1.0, sent), got);

            Assert.Equal(sent.Count, got.Count);
            Assert.Equal(0, decoder.BadLines);
            for (int i = 0; i < sent.Count; i++) LinkSame.Sample(sent[i], got[i]);
        }

        [Fact]
        public void Very_large_counts_survive()
        {
            // A year at 1000 samples a second is 3e10; allow far more than that.
            LinkSample s = new LinkSample();
            s.Index = 9007199254740991L;   // 2^53 - 1, the largest whole number a double holds exactly
            s.Time = 1.0;
            List<LinkSample> got = new List<LinkSample>();
            LinkDecoder decoder = new LinkDecoder();
            decoder.Decode(LinkMake.Batch(9007199254740990L, 2.0, new[] { s }), got);
            Assert.Equal(9007199254740991L, got[0].Index);
            Assert.Equal(9007199254740990L, decoder.LastBatchSequence);
        }

        [Fact]
        public void Hello_and_status_come_back()
        {
            StringBuilder sb = new StringBuilder();
            LinkHello hello = new LinkHello();
            hello.Version = LinkProtocol.Version;
            hello.Source = "openvr";
            hello.PollHz = 500f;
            hello.BatchHz = 100f;
            hello.Time = 4321.123456789;
            LinkEncoder.AppendHello(sb, hello);

            LinkStatus status = new LinkStatus();
            status.Time = 4322.5;
            status.State = LinkState.Streaming;
            status.SampleCount = 123456789012L;
            status.Overruns = 7;
            status.Detail = "streaming at 500 Hz\nfine";
            LinkEncoder.AppendStatus(sb, status);

            LinkDecoder decoder = new LinkDecoder();
            Assert.False(decoder.HasHello);
            Assert.Equal(0, decoder.Decode(sb.ToString(), new List<LinkSample>()));

            Assert.True(decoder.HasHello);
            Assert.False(decoder.VersionMismatch);
            Assert.Equal(LinkProtocol.Version, decoder.Hello.Version);
            Assert.Equal("openvr", decoder.Hello.Source);
            Assert.Equal(500f, decoder.Hello.PollHz);
            Assert.Equal(100f, decoder.Hello.BatchHz);
            Assert.Equal(4321.123456789, decoder.Hello.Time);

            Assert.True(decoder.HasStatus);
            Assert.Equal(4322.5, decoder.Status.Time);
            Assert.Equal(LinkState.Streaming, decoder.Status.State);
            Assert.Equal(123456789012L, decoder.Status.SampleCount);
            Assert.Equal(7, decoder.Status.Overruns);
            Assert.Equal("streaming_at_500_Hz_fine", decoder.Status.Detail);
            Assert.Equal(0, decoder.BadLines);
        }

        [Fact]
        public void Empty_text_fields_still_make_a_readable_line()
        {
            StringBuilder sb = new StringBuilder();
            LinkEncoder.AppendHello(sb, new LinkHello());
            LinkEncoder.AppendStatus(sb, new LinkStatus());
            LinkDecoder decoder = new LinkDecoder();
            decoder.Decode(sb.ToString(), null);
            Assert.True(decoder.HasHello);
            Assert.True(decoder.HasStatus);
            Assert.Equal("-", decoder.Hello.Source);
            Assert.Equal("-", decoder.Status.Detail);
            Assert.Equal(0, decoder.BadLines);
        }

        [Fact]
        public void A_sample_line_has_exactly_the_documented_number_of_fields()
        {
            Rng rng = new Rng(22);
            StringBuilder sb = new StringBuilder();
            LinkEncoder.AppendSample(sb, LinkMake.Sample(ref rng, 5, 10.0));
            string line = sb.ToString();
            Assert.EndsWith("\n", line);
            string[] fields = line.TrimEnd('\n').Split(' ');
            Assert.Equal("S", fields[0]);
            Assert.Equal(59, LinkProtocol.SampleFieldCount);   // the numbers after the "S"
            Assert.Equal(60, fields.Length);                   // the "S" and those numbers
            Assert.DoesNotContain(",", line);
        }

        [Fact]
        public void The_text_does_not_depend_on_the_PCs_language_settings()
        {
            Rng rng = new Rng(23);
            LinkSample sample = LinkMake.Sample(ref rng, 1, 2.5);
            CultureInfo before = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                string plain = LinkMake.Batch(3, 2.5, new[] { sample });

                // German writes one and a half as "1,5"; the link must not.
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                string german = LinkMake.Batch(3, 2.5, new[] { sample });
                Assert.Equal(plain, german);

                List<LinkSample> got = new List<LinkSample>();
                LinkDecoder decoder = new LinkDecoder();
                Assert.Equal(1, decoder.Decode(plain, got));
                LinkSame.Sample(sample, got[0]);
            }
            finally
            {
                CultureInfo.CurrentCulture = before;
            }
        }

        [Fact]
        public void Several_messages_stuck_together_or_one_split_by_lines_read_the_same()
        {
            Rng rng = new Rng(24);
            List<LinkSample> sent = new List<LinkSample>();
            for (int i = 0; i < 6; i++) sent.Add(LinkMake.Sample(ref rng, i, 1.0 + i * 0.002));

            string first = LinkMake.Batch(0, 1.01, sent.GetRange(0, 3));
            string second = LinkMake.Batch(1, 1.02, sent.GetRange(3, 3));

            List<LinkSample> together = new List<LinkSample>();
            LinkDecoder a = new LinkDecoder();
            Assert.Equal(6, a.Decode(first + second, together));
            Assert.Equal(2, a.BatchCount);

            List<LinkSample> apart = new List<LinkSample>();
            LinkDecoder b = new LinkDecoder();
            Assert.Equal(3, b.Decode(first, apart));
            Assert.Equal(3, b.Decode(second, apart));

            for (int i = 0; i < 6; i++)
            {
                LinkSame.Sample(sent[i], together[i]);
                LinkSame.Sample(sent[i], apart[i]);
            }
            Assert.Equal(1.02, a.LastBatchSendTime);
        }

        [Fact]
        public void Lost_batches_and_samples_are_counted()
        {
            Rng rng = new Rng(25);
            LinkDecoder decoder = new LinkDecoder();
            List<LinkSample> got = new List<LinkSample>();

            decoder.Decode(LinkMake.Batch(10, 1.0, new[] { LinkMake.Sample(ref rng, 100, 1.0), LinkMake.Sample(ref rng, 101, 1.002) }), got);
            // Batches 11 and 12 (samples 102..105) never arrive.
            decoder.Decode(LinkMake.Batch(13, 1.03, new[] { LinkMake.Sample(ref rng, 106, 1.012) }), got);

            Assert.Equal(2, decoder.MissedBatches);
            Assert.Equal(4, decoder.MissedSamples);
            Assert.Equal(3, got.Count);

            // Joining part-way through is not a loss.
            LinkDecoder late = new LinkDecoder();
            late.Decode(LinkMake.Batch(500, 9.0, new[] { LinkMake.Sample(ref rng, 2500, 9.0) }), got);
            Assert.Equal(0, late.MissedBatches);
            Assert.Equal(0, late.MissedSamples);
        }

        [Fact]
        public void Damaged_lines_are_skipped_and_counted_and_the_rest_still_reads()
        {
            Rng rng = new Rng(26);
            LinkSample good = LinkMake.Sample(ref rng, 7, 3.0);
            StringBuilder one = new StringBuilder();
            LinkEncoder.AppendSample(one, good);
            string goodLine = one.ToString();

            string cutShort = goodLine.Substring(0, goodLine.Length / 2) + "\n";
            string notANumber = goodLine.Replace(" 7 ", " seven ");
            string tooLong = goodLine.TrimEnd('\n') + " 1 2 3\n";

            string message = "B 0 3 4\n" + cutShort + notANumber + tooLong + "B oops\n" + "H 1\n" + "I 1 2\n" + "\n" + goodLine;

            LinkDecoder decoder = new LinkDecoder();
            List<LinkSample> got = new List<LinkSample>();
            int added = decoder.Decode(message, got);

            Assert.Equal(1, added);
            Assert.Equal(6, decoder.BadLines);
            LinkSame.Sample(good, got[0]);
        }

        [Fact]
        public void Nothing_and_rubbish_do_no_harm()
        {
            LinkDecoder decoder = new LinkDecoder();
            List<LinkSample> got = new List<LinkSample>();
            Assert.Equal(0, decoder.Decode(null, got));
            Assert.Equal(0, decoder.Decode("", got));
            Assert.Equal(0, decoder.Decode("\n\n\n", got));
            Assert.Equal(0, decoder.Decode("hello there", got));
            Assert.Equal(0, decoder.Decode("S", got));
            Assert.Equal(0, decoder.Decode("{\"json\": true}", got));
            Assert.Empty(got);
            Assert.Equal(1, decoder.BadLines);   // only the bare "S" claimed to be a sample
        }

        [Fact]
        public void Lines_from_a_later_version_are_ignored_without_complaint()
        {
            Rng rng = new Rng(27);
            LinkSample sample = LinkMake.Sample(ref rng, 1, 1.0);
            string message = "X something new 1 2 3\n" + LinkMake.Batch(0, 1.0, new[] { sample }) + "Z\n";
            LinkDecoder decoder = new LinkDecoder();
            List<LinkSample> got = new List<LinkSample>();
            Assert.Equal(1, decoder.Decode(message, got));
            Assert.Equal(0, decoder.BadLines);
        }

        [Fact]
        public void A_helper_speaking_another_version_is_flagged_and_its_samples_left_alone()
        {
            Rng rng = new Rng(28);
            StringBuilder sb = new StringBuilder();
            LinkHello hello = new LinkHello();
            hello.Version = LinkProtocol.Version + 1;
            hello.Source = "openvr";
            LinkEncoder.AppendHello(sb, hello);
            sb.Append(LinkMake.Batch(0, 1.0, new[] { LinkMake.Sample(ref rng, 1, 1.0) }));

            LinkDecoder decoder = new LinkDecoder();
            List<LinkSample> got = new List<LinkSample>();
            Assert.Equal(0, decoder.Decode(sb.ToString(), got));
            Assert.True(decoder.VersionMismatch);
            Assert.Empty(got);

            decoder.Reset();
            Assert.False(decoder.VersionMismatch);
            Assert.False(decoder.HasHello);
            Assert.Equal(-1, decoder.LastSampleIndex);
        }

        [Fact]
        public void Flag_helpers_read_the_bits()
        {
            LinkPose pose = new LinkPose();
            Assert.False(pose.IsValid);
            pose.Flags = LinkPoseFlags.Connected | LinkPoseFlags.PoseValid;
            Assert.True(pose.IsValid);

            LinkHand hand = new LinkHand();
            Assert.False(hand.InputActive);
            hand.Pose.Flags = LinkPoseFlags.InputActive;
            Assert.True(hand.InputActive);

            LinkGaze gaze = new LinkGaze();
            gaze.Flags = LinkGazeFlags.Active | LinkGazeFlags.Valid;
            Assert.False(gaze.IsUsable);   // a blink
            gaze.Flags = LinkGazeFlags.Usable;
            Assert.True(gaze.IsUsable);
        }

        [Fact]
        public void Decoded_samples_feed_the_rest_of_the_core_in_time_order()
        {
            // The game appends every message's samples to one list; they must already be oldest first.
            Rng rng = new Rng(29);
            List<LinkSample> sent = new List<LinkSample>();
            for (int i = 0; i < 50; i++) sent.Add(LinkMake.Sample(ref rng, i, 5.0 + i * 0.002));
            LinkDecoder decoder = new LinkDecoder();
            List<LinkSample> got = new List<LinkSample>();
            for (int b = 0; b < 10; b++) decoder.Decode(LinkMake.Batch(b, 5.0 + b * 0.01, sent.GetRange(b * 5, 5)), got);
            for (int i = 1; i < got.Count; i++)
            {
                Assert.True(got[i].Time > got[i - 1].Time);
                Assert.Equal(got[i - 1].Index + 1, got[i].Index);
            }
        }
    }
}
