using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Portaballs.Core;
using Xunit;
using Xunit.Abstractions;

namespace Portaballs.Helper.Tests
{
    public class FakeThrowTests
    {
        private static readonly double[] CycleStarts = { 0.0, 2.0, 1000.0, 86400.0, 2000000.0 };

        [Fact]
        public void The_same_moment_always_gives_the_same_sample()
        {
            for (int i = 0; i < 2000; i++)
            {
                double t = 54321.0 + i * 0.00137;
                Assert.Null(Probe.FirstDifference(FakeThrow.SampleAt(t), FakeThrow.SampleAt(t)));
            }
        }

        [Fact]
        public void Reported_velocity_matches_how_the_position_actually_changes()
        {
            // Converted velocity must still be the rate of change of converted position, for head and both hands.
            const double h = 1e-3;
            foreach (double start in CycleStarts)
            {
                for (double p = 0.01; p < 1.99; p += 0.0173)
                {
                    double t = start + p;
                    LinkSample before = FakeThrow.SampleAt(t - h), at = FakeThrow.SampleAt(t), after = FakeThrow.SampleAt(t + h);
                    CheckVelocity(before.Head, at.Head, after.Head, h, "head at " + p);
                    CheckVelocity(before.Left.Pose, at.Left.Pose, after.Left.Pose, h, "left at " + p);
                    CheckVelocity(before.Right.Pose, at.Right.Pose, after.Right.Pose, h, "right at " + p);
                }
            }
        }

        private static void CheckVelocity(LinkPose before, LinkPose at, LinkPose after, double h, string what)
        {
            Vec3 measured = (after.Position - before.Position) / (float)(2.0 * h);
            float error = (measured - at.Velocity).Length();
            // Central differences on float positions: allow a few cm/s plus 2 percent.
            Assert.True(error <= 0.05f + 0.02f * at.Velocity.Length(), what + ": velocity " + at.Velocity + " but position moves at " + measured);
        }

        [Fact]
        public void Reported_spin_matches_how_the_rotation_actually_changes()
        {
            const double h = 1e-3;
            for (double p = 0.01; p < 1.99; p += 0.0173)
            {
                double t = 4000.0 + p;
                CheckSpin(FakeThrow.SampleAt(t - h).Right.Pose, FakeThrow.SampleAt(t).Right.Pose, FakeThrow.SampleAt(t + h).Right.Pose, h, "right at " + p);
                CheckSpin(FakeThrow.SampleAt(t - h).Head, FakeThrow.SampleAt(t).Head, FakeThrow.SampleAt(t + h).Head, h, "head at " + p);
            }
        }

        private static void CheckSpin(LinkPose before, LinkPose at, LinkPose after, double h, string what)
        {
            // The turn from 'before' to 'after', as an axis scaled by angle, divided by the time between.
            Quat delta = after.Rotation * Quat.Conjugate(before.Rotation);
            if (delta.W < 0f) delta = new Quat(-delta.X, -delta.Y, -delta.Z, -delta.W);
            Vec3 axisTimesSin = new Vec3(delta.X, delta.Y, delta.Z);
            float sinHalf = axisTimesSin.Length();
            Vec3 measured = sinHalf < 1e-9f ? new Vec3(0f, 0f, 0f)
                : axisTimesSin / sinHalf * (2f * MathF.Atan2(sinHalf, delta.W) / (float)(2.0 * h));
            float error = (measured - at.AngularVelocity).Length();
            Assert.True(error <= 0.15f + 0.03f * at.AngularVelocity.Length(), what + ": spin " + at.AngularVelocity + " but rotation turns at " + measured);
        }

        [Fact]
        public void It_is_a_believable_throw_forward_and_upward()
        {
            double release = 6000.0 + FakeThrow.ReleaseStart;
            LinkSample atRelease = FakeThrow.SampleAt(release);

            // In core axes: +X forward, +Z up. A good underarm throw leaves going forward and up.
            Vec3 v = atRelease.Right.Pose.Velocity;
            Assert.InRange(v.Length(), 5f, 12f);
            Assert.True(v.X > 3f, "forward speed " + v.X);
            Assert.True(v.Z > 1f, "upward speed " + v.Z);
            Assert.InRange(MathF.Abs(v.Y), 0f, 0.01f);

            // Peak hand speed over the whole cycle is human.
            float peak = 0f;
            for (double p = 0.0; p < 2.0; p += 0.001) peak = MathF.Max(peak, FakeThrow.SampleAt(6000.0 + p).Right.Pose.Velocity.Length());
            Assert.InRange(peak, 6f, 14f);

            // Hands and head are at sensible heights, the right hand on the right (-Y), the left on the left (+Y).
            LinkSample rest = FakeThrow.SampleAt(6000.1);
            Assert.InRange(rest.Head.Position.Z, 1.5f, 1.8f);
            Assert.InRange(rest.Right.Pose.Position.Z, 0.6f, 1.2f);
            Assert.True(rest.Right.Pose.Position.Y < -0.1f);
            Assert.True(rest.Left.Pose.Position.Y > 0.1f);

            // The swing carries the hand from behind the shoulder to in front of it.
            Assert.True(FakeThrow.SampleAt(6001.0).Right.Pose.Position.X < -0.3f);
            Assert.True(FakeThrow.SampleAt(6001.3).Right.Pose.Position.X > 0.3f);
        }

        [Fact]
        public void Grip_closes_holds_through_the_swing_and_opens_over_forty_milliseconds()
        {
            double c = 8000.0;
            Assert.Equal(0f, FakeThrow.SampleAt(c + 0.10).Right.Grip);
            Assert.Equal(1f, FakeThrow.SampleAt(c + 0.40).Right.Grip);
            Assert.Equal(1f, FakeThrow.SampleAt(c + FakeThrow.ReleaseStart - 0.001).Right.Grip);
            Assert.InRange(FakeThrow.SampleAt(c + FakeThrow.ReleaseStart + 0.020).Right.Grip, 0.3f, 0.7f);
            Assert.Equal(0f, FakeThrow.SampleAt(c + FakeThrow.ReleaseStart + 0.050).Right.Grip);
            Assert.Equal(0f, FakeThrow.SampleAt(c + 1.9).Right.Grip);
            Assert.Equal(c + FakeThrow.ReleaseStart, FakeThrow.ReleaseOnset(c + 1.5), 9);

            // The release does not sit on a 2 ms polling tick, so polling alone cannot land on it.
            double ticks = FakeThrow.ReleaseStart / 0.002;
            Assert.True(Math.Abs(ticks - Math.Round(ticks)) > 0.1);
        }

        [Fact]
        public void Grip_time_is_when_the_value_last_changed_never_later_than_the_sample()
        {
            double c = 8000.0;
            double lastGripTime = double.NegativeInfinity;
            float lastGrip = float.NaN;
            for (double p = 0.0; p < 4.0; p += 0.0005)
            {
                LinkSample s = FakeThrow.SampleAt(c + p);
                Assert.True(s.Right.GripTime <= s.Time + 1e-12, "grip time after sample time at " + p);
                Assert.True(s.Right.GripTime >= lastGripTime - 1e-12, "grip time went backwards at " + p);
                if (s.Right.GripTime == lastGripTime) Assert.Equal(lastGrip, s.Right.Grip);   // same report, same value
                else if (!float.IsNaN(lastGrip) && p > 0.01) Assert.NotEqual(lastGrip, s.Right.Grip);   // new report time means a new value
                lastGripTime = s.Right.GripTime;
                lastGrip = s.Right.Grip;
            }

            // While holding steady, the value was last changed when the ramp up ended, long before the sample.
            LinkSample holding = FakeThrow.SampleAt(c + 0.9);
            Assert.InRange(holding.Time - holding.Right.GripTime, 0.59, 0.61);
        }

        [Fact]
        public void Eyes_go_from_the_hand_to_the_target_and_blink()
        {
            double c = 10000.0;
            LinkSample early = FakeThrow.SampleAt(c + 0.3), fixed_ = FakeThrow.SampleAt(c + 1.2), blink = FakeThrow.SampleAt(c + 1.9), back = FakeThrow.SampleAt(c + 1.97);

            Assert.True(early.Gaze.IsUsable);
            Assert.True(fixed_.Gaze.IsUsable);
            Assert.False(blink.Gaze.IsUsable);
            Assert.True((blink.Gaze.Flags & LinkGazeFlags.Active) != 0);
            Assert.True(back.Gaze.IsUsable);

            Assert.Equal(1f, early.Gaze.Direction.Length(), 5);
            Assert.Equal(1f, fixed_.Gaze.Direction.Length(), 5);
            // Looking down at the hand first, then nearly level at the far target ahead.
            Assert.True(early.Gaze.Direction.Z < -0.5f, "early gaze " + early.Gaze.Direction);
            Assert.True(fixed_.Gaze.Direction.X > 0.99f, "fixed gaze " + fixed_.Gaze.Direction);
            // The gaze starts at the head.
            Assert.Equal(fixed_.Head.Position, fixed_.Gaze.Origin);

            // Fixation: the direction holds still between 0.8 and 1.5 s apart from the head's own sway.
            float spread = MathUtil.AngleBetween(FakeThrow.SampleAt(c + 0.8).Gaze.Direction, FakeThrow.SampleAt(c + 1.5).Gaze.Direction);
            Assert.True(spread < 0.02f, "gaze moved " + spread + " rad during the fixation");
        }
    }

    public class ReleaseTimingTests
    {
        private readonly ITestOutputHelper _out;

        public ReleaseTimingTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>
        /// The reason GripTime exists. Poll the fake at 500 Hz with realistic wobble, give the game's own
        /// release detector the readings two ways, and see how well each finds the true let-go moment.
        /// </summary>
        [Fact]
        public void The_games_release_detector_places_the_release_far_better_with_GripTime_than_with_poll_time()
        {
            Rng rng = new Rng(77);
            double worstWithGripTime = 0.0, worstWithPollTime = 0.0, sumGrip = 0.0, sumPoll = 0.0;
            int releases = 0;

            for (int trial = 0; trial < 200; trial++)
            {
                double cycle = 2000.0 + trial * 2.0;
                GripSettings settings = new GripSettings();
                GripReleaseDetector byGripTime = new GripReleaseDetector(settings);
                GripReleaseDetector byPollTime = new GripReleaseDetector(settings);

                // The truth: the squeeze passes the detector's let-go level (75 percent of full) on a straight 40 ms ramp.
                double truth = cycle + FakeThrow.ReleaseStart + (1.0 - settings.ReleaseFraction) * FakeThrow.ReleaseRampSeconds;

                double t = cycle + rng.NextFloat() * 0.002;   // polling starts at a random phase
                double lastGripTime = double.NaN;
                float lastGripValue = float.NaN;
                bool gotGrip = false, gotPoll = false;
                while (t < cycle + 1.9)
                {
                    LinkSample s = FakeThrow.SampleAt(t);

                    if (s.Right.GripTime != lastGripTime)
                    {
                        lastGripTime = s.Right.GripTime;
                        if (byGripTime.Update(s.Right.GripTime, s.Right.Grip) == GripEvent.Released)
                        {
                            double error = Math.Abs(byGripTime.ReleaseTime - truth);
                            worstWithGripTime = Math.Max(worstWithGripTime, error);
                            sumGrip += error;
                            gotGrip = true;
                        }
                    }
                    if (s.Right.Grip != lastGripValue)
                    {
                        lastGripValue = s.Right.Grip;
                        if (byPollTime.Update(s.Time, s.Right.Grip) == GripEvent.Released)
                        {
                            double error = Math.Abs(byPollTime.ReleaseTime - truth);
                            worstWithPollTime = Math.Max(worstWithPollTime, error);
                            sumPoll += error;
                            gotPoll = true;
                        }
                    }
                    t += 0.002 + (rng.NextFloat() - 0.5) * 0.0004;   // 2 ms give or take 0.2 ms
                }
                Assert.True(gotGrip && gotPoll, "no release found in trial " + trial);
                releases++;
            }

            _out.WriteLine("Releases: " + releases);
            _out.WriteLine("Using GripTime : mean error " + (sumGrip / releases * 1e6).ToString("0.0") + " us, worst " + (worstWithGripTime * 1e6).ToString("0.0") + " us");
            _out.WriteLine("Using poll time: mean error " + (sumPoll / releases * 1e6).ToString("0.0") + " us, worst " + (worstWithPollTime * 1e6).ToString("0.0") + " us");

            Assert.True(worstWithGripTime < 5e-6, "worst error with GripTime " + worstWithGripTime);
            Assert.True(worstWithPollTime > 0.5e-3, "poll time was unexpectedly good: " + worstWithPollTime);
            Assert.True(sumPoll > sumGrip * 50.0);
        }
    }

    public class PollLoopTests
    {
        private readonly ITestOutputHelper _out;

        public PollLoopTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private sealed class CountingSink : ISampleSink
        {
            public long Samples, Batches, Statuses;
            public int LargestBatch;
            public long LastIndex = -1;
            public bool Gap;

            public void PostBatch(LinkSample[] samples, int count)
            {
                Batches++;
                Samples += count;
                if (count > LargestBatch) LargestBatch = count;
                for (int i = 0; i < count; i++)
                {
                    if (samples[i].Index != LastIndex + 1) Gap = true;
                    LastIndex = samples[i].Index;
                }
            }

            public void PostStatus(LinkStatus status)
            {
                Statuses++;
            }
        }

        [Theory]
        [InlineData(250.0)]
        [InlineData(500.0)]
        [InlineData(1000.0)]
        public void The_loop_holds_its_rate(double hz)
        {
            Options options = new Options();
            options.PollHz = hz;
            options.BatchHz = 100.0;
            CountingSink sink = new CountingSink();
            using (FileLog log = new FileLog(null, false))
            using (FakeSource source = new FakeSource())
            {
                SamplePump pump = new SamplePump(source, sink, options, log);
                pump.Start();
                Thread.Sleep(3000);
                pump.Stop();
                PumpTotals totals = pump.GetTotals();

                _out.WriteLine(hz + " Hz asked. Gaps: " + totals.Interval.Describe());
                _out.WriteLine("Late by: " + totals.Lateness.DescribeDuration() + " | skipped slots: " + totals.Overruns);

                Assert.InRange(totals.Interval.Rate, hz * 0.98, hz * 1.02);
                Assert.InRange(totals.Interval.P50, 0.75 / hz, 1.25 / hz);
                Assert.True(totals.Overruns <= totals.Samples / 50, "skipped " + totals.Overruns + " of " + totals.Samples);
                Assert.Equal(totals.Samples, sink.Samples);
                Assert.False(sink.Gap, "sample indexes were not consecutive");
                Assert.True(sink.LargestBatch <= options.SamplesPerBatch);
                Assert.True(sink.Statuses >= 2);
            }
        }

        [Fact]
        public void Idle_batches_are_full_and_moving_inputs_flush_early()
        {
            Options options = new Options();
            CountingSink withFlush = new CountingSink(), withoutFlush = new CountingSink();
            using (FileLog log = new FileLog(null, false))
            using (FakeSource source = new FakeSource())
            {
                SamplePump a = new SamplePump(source, withFlush, options, log);
                a.Start();
                Thread.Sleep(2100);
                a.Stop();

                Options plain = new Options();
                plain.FlushOnInputChange = false;
                SamplePump b = new SamplePump(source, withoutFlush, plain, log);
                b.Start();
                Thread.Sleep(2100);
                b.Stop();
            }
            _out.WriteLine("With early sending: " + withFlush.Batches + " batches for " + withFlush.Samples + " samples");
            _out.WriteLine("Without           : " + withoutFlush.Batches + " batches for " + withoutFlush.Samples + " samples");

            // Without: 5 samples per batch, give or take the last partial one.
            Assert.InRange(withoutFlush.Batches, withoutFlush.Samples / 5, withoutFlush.Samples / 5 + 1);
            // With: more batches, because one 2-second cycle contains about 0.19 s of moving grip or trigger.
            Assert.True(withFlush.Batches > withFlush.Samples / 5 + 10);
        }
    }

    public class OptionsAndStatsTests
    {
        [Fact]
        public void Defaults()
        {
            string problem;
            Options o = Options.Load(new string[0], Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid()), out problem);
            Assert.Null(problem);
            Assert.Equal(LinkProtocol.DefaultPort, o.Port);
            Assert.Equal(500.0, o.PollHz);
            Assert.Equal(100.0, o.BatchHz);
            Assert.Equal(5, o.SamplesPerBatch);
            Assert.Equal("overlay", o.AppType);
            Assert.False(o.Fake);
            Assert.False(o.ShowConsole);
            Assert.False(o.AllowAnyOrigin);
            Assert.True(o.FlushOnInputChange);
        }

        [Fact]
        public void Switches_override_the_file_which_overrides_the_defaults()
        {
            string folder = Path.Combine(AppContext.BaseDirectory, "options-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllText(Path.Combine(folder, "helper.json"), "{ \"port\": 30001, \"pollHz\": 250, // comment\n \"appType\": \"background\", }");
                string problem;
                Options fromFile = Options.Load(new string[0], folder, out problem);
                Assert.Null(problem);
                Assert.Equal(30001, fromFile.Port);
                Assert.Equal(250.0, fromFile.PollHz);
                Assert.Equal("background", fromFile.AppType);

                Options both = Options.Load(new[] { "--port", "30002", "--fake", "--console", "--batch-hz", "50", "--seconds", "2.5" }, folder, out problem);
                Assert.Null(problem);
                Assert.Equal(30002, both.Port);
                Assert.Equal(250.0, both.PollHz);
                Assert.Equal(50.0, both.BatchHz);
                Assert.Equal(5, both.SamplesPerBatch);
                Assert.True(both.Fake);
                Assert.True(both.ShowConsole);
                Assert.Equal(2.5, both.Seconds);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void Silly_values_are_brought_back_into_range_and_mistakes_are_reported()
        {
            string none = Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid());
            string problem;
            Options o = Options.Load(new[] { "--poll-hz", "999999", "--batch-hz", "0", "--port", "70000", "--app-type", "scene", "--spin-us", "-5" }, none, out problem);
            Assert.Null(problem);
            Assert.Equal(2000.0, o.PollHz);
            Assert.Equal(1.0, o.BatchHz);
            Assert.Equal(LinkProtocol.DefaultPort, o.Port);
            Assert.Equal("overlay", o.AppType);
            Assert.Equal(0, o.SpinMicros);

            Options.Load(new[] { "--wibble" }, none, out problem);
            Assert.StartsWith("Unknown switch", problem);
            Options.Load(new[] { "--port" }, none, out problem);
            Assert.Contains("needs a value", problem);
        }

        [Fact]
        public void Stats_summarise_gaps_correctly()
        {
            LoopStats stats = new LoopStats();
            for (int i = 0; i < 990; i++) stats.Add(0.002);
            for (int i = 0; i < 10; i++) stats.Add(0.004);
            StatsSummary s = stats.Summarize();
            Assert.Equal(1000, s.Count);
            Assert.Equal(0.00202, s.Mean, 9);
            Assert.InRange(s.Rate, 494.0, 496.0);
            Assert.InRange(s.P50, 0.002, 0.002001);
            Assert.InRange(s.P99, 0.002, 0.002001);
            Assert.InRange(s.P999, 0.004, 0.004001);
            Assert.Equal(0.004, s.Max, 9);
            Assert.Equal(0.002, s.Min, 9);
            Assert.InRange(s.StdDev, 0.000198, 0.0002);

            stats.Reset();
            Assert.Equal(0, stats.Summarize().Count);
            Assert.Equal("n=0", stats.Summarize().Describe());
        }

        [Fact]
        public void The_clock_is_the_shared_stopwatch_in_seconds()
        {
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
            double now = Clock.Now;
            double expected = (double)ticks / System.Diagnostics.Stopwatch.Frequency;
            Assert.InRange(now - expected, 0.0, 0.05);
            Assert.Equal(1.0, Clock.ToSeconds(System.Diagnostics.Stopwatch.Frequency));
        }
    }
}
