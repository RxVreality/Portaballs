using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Portaballs.Core.Tests
{
    /// <summary>
    /// The headline measurement: many simulated throws, scored against the known truth, for the naive
    /// method, the pipeline at default settings, and the pipeline after the delay calibration has learned
    /// the delay. Prints tables (also saved as throw_accuracy.txt beside the test binaries, because
    /// "dotnet test -v q" hides test output unless a test fails).
    /// </summary>
    public class ThrowAccuracyTests
    {
        private readonly ITestOutputHelper _out;

        public ThrowAccuracyTests(ITestOutputHelper output)
        {
            _out = output;
        }

        internal sealed class Stats
        {
            public readonly List<double> Direction = new List<double>();
            public readonly List<double> SpeedAbs = new List<double>();
            public readonly List<double> SpeedSigned = new List<double>();
            public readonly List<double> Landing = new List<double>();
            public int Misses;

            public void Add(ThrowError e)
            {
                Direction.Add(e.DirectionDeg);
                SpeedAbs.Add(Math.Abs(e.SpeedFraction) * 100.0);
                SpeedSigned.Add(e.SpeedFraction * 100.0);
                Landing.Add(e.LandingMeters);
                if (e.Missed) Misses++;
            }

            public int Count { get { return Direction.Count; } }

            public static double Percentile(List<double> values, double p)
            {
                if (values.Count == 0) return double.NaN;
                List<double> sorted = values.OrderBy(x => x).ToList();
                double pos = p * (sorted.Count - 1);
                int lo = (int)Math.Floor(pos);
                int hi = Math.Min(sorted.Count - 1, lo + 1);
                return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
            }

            public double DirMedian { get { return Percentile(Direction, 0.5); } }
            public double DirP90 { get { return Percentile(Direction, 0.9); } }
            public double SpeedAbsMedian { get { return Percentile(SpeedAbs, 0.5); } }
            public double SpeedAbsP90 { get { return Percentile(SpeedAbs, 0.9); } }
            public double SpeedBias { get { return SpeedSigned.Count == 0 ? double.NaN : SpeedSigned.Average(); } }
            public double LandMedian { get { return Percentile(Landing, 0.5); } }
            public double LandP90 { get { return Percentile(Landing, 0.9); } }
        }

        internal sealed class Experiment
        {
            public readonly Dictionary<string, Stats> Rows = new Dictionary<string, Stats>();
            public readonly List<string> Order = new List<string>();
            public int Throws;
            public int ReleasesMissed;
            public int CalibratedThrows;        // measured throws that ran with a trusted learned delay
            public readonly List<double> LearnedMinusIdeal = new List<double>();   // seconds, one per session

            public Stats Row(string name)
            {
                Stats s;
                if (!Rows.TryGetValue(name, out s))
                {
                    s = new Stats();
                    Rows[name] = s;
                    Order.Add(name);
                }
                return s;
            }
        }

        private static readonly ThrowKind[] AllKinds =
        {
            ThrowKind.Overarm, ThrowKind.Underarm, ThrowKind.SidearmFlick, ThrowKind.GentleToss,
        };

        private static readonly ThrowKind[] WarmUpKinds =
        {
            ThrowKind.Overarm, ThrowKind.Underarm, ThrowKind.SidearmFlick,
        };

        private const string Naive = "naive (2-frame velocity at the frame of notice)";
        private const string Default = "pipeline, default settings";
        private const string Calibrated = "pipeline, delay calibrated";
        private const string InfoExactBoth = "  info: exact release time given (best possible)";
        private const string InfoExactBefore = "  info: exact time, nothing after release used";
        private const string InfoOwnPeak = "  info: launch from this throw's own fastest moment";
        private const string InfoCalLine = "  info: learned delay but still the Line fit";
        private const string InfoCalBefore = "  info: learned delay, Curve, nothing after release";
        private const string InfoDefaultCurve = "  info: default delay with the Curve fit";
        private const string InfoNoPeak = "  info: default with peak-speed check off";

        /// <summary>
        /// Run sessions of throws. Each session is one headset setup (its own delays and noise). The
        /// calibrated pipeline starts each session knowing nothing, watches some warm-up throws, and
        /// keeps learning during the measured ones.
        /// </summary>
        /// <param name="intentMean">How long after the ball's fastest moment the simulated player truly lets go, on average.</param>
        /// <param name="peakToRelease">The calibrator's "feel" setting (how much before the fastest moment to release).</param>
        /// <param name="useOnsetTime">Feed the detector's "fingers began to open" time as the event, instead of its crossing time.</param>
        /// <param name="breakdown">Add the for-information rows and the per-kind, per-rate and per-delay rows.</param>
        /// <param name="fingerJitter">Throw-to-throw scatter of when the fingers start to open, relative to the true release.</param>
        internal static Experiment Run(double[] rates, int sessionsPerRate, int warmUpThrows, int measuredThrows,
            uint seed, double intentMean = -0.004, double intentSd = 0.005, double peakToRelease = 0.0,
            bool useOnsetTime = false, bool breakdown = false, double fingerJitter = 0.003)
        {
            Experiment ex = new Experiment();
            // Fix the row order so the three headline rows come first.
            ex.Row(Naive); ex.Row(Default); ex.Row(Calibrated);

            foreach (double rate in rates)
            {
                for (int session = 0; session < sessionsPerRate; session++)
                {
                    Rng rng = new Rng(seed + (uint)(rate * 1000) + (uint)session * 7919u);
                    Rig rig = Rig.Random(rate, ref rng);
                    rig.FingerTimingJitter = fingerJitter;
                    double poseLag = rig.PoseLatency * (1.0 - rig.PredictionFraction);
                    ReleaseDelayCalibrator calibrator = new ReleaseDelayCalibrator();
                    calibrator.Settings.PeakToReleaseSeconds = peakToRelease;
                    ThrowSettings[] none = new ThrowSettings[0];

                    for (int i = 0; i < warmUpThrows; i++)
                    {
                        Swing swing = Swing.Random(WarmUpKinds[i % WarmUpKinds.Length], ref rng);
                        SimThrow th = ThrowSim.Generate(swing, rig, ref rng, intentMean, intentSd);
                        Player.Play(th, none, calibrator, useOnsetTime);
                    }

                    List<double> trueDelays = new List<double>();
                    for (int i = 0; i < measuredThrows; i++)
                    {
                        ThrowKind kind = AllKinds[i % AllKinds.Length];
                        Swing swing = Swing.Random(kind, ref rng);
                        SimThrow th = ThrowSim.Generate(swing, rig, ref rng, intentMean, intentSd);
                        Vec3 offset = swing.BallOffsetLocal.ToVec3();

                        ThrowSettings defaults = new ThrowSettings();
                        ThrowSettings calibrated = new ThrowSettings();
                        bool trusted = calibrator.ApplyTo(calibrated);

                        // For information only: other ways of using the same data.
                        ThrowSettings calLine = new ThrowSettings();           // learned delay, but still the Line fit
                        calLine.ReleaseTimeOffset = calibrator.DelayOr(calLine.ReleaseTimeOffset);
                        ThrowSettings calBefore = new ThrowSettings();         // learned delay, Curve fit, nothing after release
                        if (calibrator.ApplyTo(calBefore)) calBefore.FitWindowAfterSeconds = 0.0;
                        ThrowSettings defaultCurve = new ThrowSettings();      // Curve fit without knowing the delay
                        defaultCurve.Fit = VelocityFit.Curve;
                        ThrowSettings noPeak = new ThrowSettings();            // default with the peak-speed cross-check off
                        noPeak.UsePeakSpeed = false;
                        ThrowSettings[] variants = { defaults, calibrated, calLine, calBefore, defaultCurve, noPeak };

                        // Cheating on purpose: tell the estimator the exact release moment, to see how much
                        // error is left when timing is perfect (the floor set by noise and by the fit itself).
                        ThrowResult exactBefore = default(ThrowResult), exactBoth = default(ThrowResult);
                        Playback pb = Player.Play(th, variants, calibrator, useOnsetTime, atRelease: p =>
                        {
                            ThrowSettings exact = new ThrowSettings();
                            exact.BallLocalOffset = offset;
                            exact.UseMeasuredDelay(p.EventTime - (th.TrueReleaseTime + poseLag));
                            exactBoth = ThrowEstimator.Estimate(p.History, p.EventTime, p.NoticedTime, exact);
                            exact.FitWindowAfterSeconds = 0.0;
                            exactBefore = ThrowEstimator.Estimate(p.History, p.EventTime, p.NoticedTime, exact);
                        });
                        ex.Throws++;
                        if (!pb.Released)
                        {
                            ex.ReleasesMissed++;
                            continue;
                        }
                        if (trusted) ex.CalibratedThrows++;
                        // The real gap between the release event and the true release, on the clock the poses
                        // are stamped with (poses that are not fully predicted show the hand slightly late).
                        trueDelays.Add(pb.EventTime - (th.TrueReleaseTime + poseLag));

                        ThrowError naive = Scoring.Score(th, pb.NaivePosition, pb.NaiveVelocity);
                        ThrowError def = Score(th, pb.Results[0]);
                        ThrowError cal = Score(th, pb.Results[1]);
                        ex.Row(Naive).Add(naive);
                        ex.Row(Default).Add(def);
                        ex.Row(Calibrated).Add(cal);
                        if (!breakdown) continue;

                        ex.Row(InfoExactBoth).Add(Score(th, exactBoth));
                        ex.Row(InfoExactBefore).Add(Score(th, exactBefore));
                        ThrowDiagnostics dd = pb.Results[0].Diagnostics;
                        if (dd.PeakFound)
                        {
                            // Launch straight from the fastest moment of this one throw, ignoring the event time.
                            PoseSample atPeak;
                            pb.History.TrySample(dd.PeakTime, out atPeak);
                            Vec3 ballAtPeak = atPeak.Position + Vec3.Transform(offset, atPeak.Rotation);
                            ex.Row(InfoOwnPeak).Add(Scoring.Score(th, ballAtPeak, dd.PeakVelocity));
                        }
                        ex.Row(InfoCalLine).Add(Score(th, pb.Results[2]));
                        ex.Row(InfoCalBefore).Add(Score(th, pb.Results[3]));
                        ex.Row(InfoDefaultCurve).Add(Score(th, pb.Results[4]));
                        ex.Row(InfoNoPeak).Add(Score(th, pb.Results[5]));

                        string k = "  " + kind + " | ";
                        ex.Row(k + "naive").Add(naive);
                        ex.Row(k + "default").Add(def);
                        ex.Row(k + "calibrated").Add(cal);

                        string r = "  " + ((int)rate).ToString(CultureInfo.InvariantCulture) + " Hz | ";
                        ex.Row(r + "naive").Add(naive);
                        ex.Row(r + "default").Add(def);
                        ex.Row(r + "calibrated").Add(cal);

                        string d = "  grip signal delay "
                            + (rig.ButtonDelay < 0.013 ? "0-13" : (rig.ButtonDelay < 0.027 ? "13-27" : "27-40")) + " ms | ";
                        ex.Row(d + "naive").Add(naive);
                        ex.Row(d + "default").Add(def);
                        ex.Row(d + "calibrated").Add(cal);
                    }

                    if (calibrator.IsConfident && trueDelays.Count > 0)
                    {
                        ex.LearnedMinusIdeal.Add(calibrator.DelaySeconds - trueDelays.Average());
                    }
                }
            }
            return ex;
        }

        private static ThrowError Score(SimThrow th, ThrowResult r)
        {
            return Scoring.Score(th, r.ReleasePosition, r.ReleaseVelocity);
        }

        internal static string Format(Experiment ex, string title)
        {
            const string head = "{0,-52} {1,5} | {2,8} {3,8} | {4,8} {5,8} {6,8} | {7,8} {8,8} {9,5}";
            const string line = "{0,-52} {1,5} | {2,8:F2} {3,8:F2} | {4,8:F1} {5,8:F1} {6,8:F1} | {7,8:F3} {8,8:F3} {9,5}";
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(title);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, head,
                "method", "n", "dir med", "dir p90", "spd med", "spd p90", "spd bias", "land med", "land p90", "miss"));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, head,
                "", "", "deg", "deg", "%", "%", "%", "m", "m", ""));
            foreach (string name in ex.Order)
            {
                Stats s = ex.Rows[name];
                if (s.Count == 0) continue;
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, line,
                    name, s.Count, s.DirMedian, s.DirP90, s.SpeedAbsMedian, s.SpeedAbsP90, s.SpeedBias,
                    s.LandMedian, s.LandP90, s.Misses));
            }
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "throws: {0}; releases not detected: {1}; measured throws that ran with a trusted learned delay: {2}",
                ex.Throws, ex.ReleasesMissed, ex.CalibratedThrows));
            if (ex.LearnedMinusIdeal.Count > 0)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "learned delay minus the real average delay, per session: mean {0:F1} ms, worst {1:F1} ms ({2} sessions)",
                    ex.LearnedMinusIdeal.Average() * 1000.0,
                    ex.LearnedMinusIdeal.Select(Math.Abs).Max() * 1000.0,
                    ex.LearnedMinusIdeal.Count));
            }
            return sb.ToString();
        }

        private void Publish(string text, string fileName)
        {
            _out.WriteLine(text);
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, fileName), text);
            }
            catch (IOException)
            {
                // The table is still in the test output; a locked file must not fail the test.
            }
        }

        private const string Legend =
            "dir = error in launch direction; spd = error in speed (bias: negative means throws come out weak);\n" +
            "land = distance between where the ball lands and where the true throw lands, on a wall 3-6 m away\n" +
            "(or on the floor when the true throw drops short); med = middle value; p90 = 9 throws in 10 are better than this.";

        private static readonly double[] Rates = { 90.0, 120.0, 250.0 };

        [Fact]
        public void PipelineBeatsNaiveAndMeetsTargets()
        {
            Experiment ex = Run(Rates, sessionsPerRate: 8, warmUpThrows: 12, measuredThrows: 40,
                seed: 20261119u, breakdown: true);
            Publish(Format(ex, "THROW ACCURACY: simulated throws scored against the known truth\n" + Legend), "throw_accuracy.txt");

            Stats naive = ex.Rows[Naive];
            Stats def = ex.Rows[Default];
            Stats cal = ex.Rows[Calibrated];

            Assert.Equal(0, ex.ReleasesMissed);
            Assert.Equal(960, naive.Count);
            Assert.True(ex.CalibratedThrows > 0.9 * ex.Throws, "the calibration should be trusted for nearly all measured throws");
            Assert.Equal(0, def.Misses);
            Assert.Equal(0, cal.Misses);

            // The pipeline must clearly beat the naive method, even before calibration.
            Assert.True(def.DirMedian < 0.5 * naive.DirMedian, "default direction should be at least twice as good as naive");
            Assert.True(def.LandMedian < 0.5 * naive.LandMedian, "default landing should be at least twice as good as naive");
            Assert.True(def.SpeedAbsMedian < 0.5 * naive.SpeedAbsMedian, "default speed should be at least twice as good as naive");
            Assert.True(def.LandP90 < 0.5 * naive.LandP90, "default bad-case landing should be at least twice as good as naive");

            // Calibration must be a clear further step.
            Assert.True(cal.DirMedian < 0.25 * naive.DirMedian, "calibrated direction should be at least four times as good as naive");
            Assert.True(cal.LandMedian < 0.25 * naive.LandMedian, "calibrated landing should be at least four times as good as naive");
            Assert.True(cal.DirMedian < 0.7 * def.DirMedian, "calibrated direction should clearly beat default");
            Assert.True(cal.LandMedian < 0.7 * def.LandMedian, "calibrated landing should clearly beat default");
            Assert.True(cal.LandP90 < 0.7 * def.LandP90, "calibrated bad-case landing should clearly beat default");

            // Absolute targets (see the report for why these are the realistic ones).
            Assert.True(def.DirMedian < 9.0, "default direction median");
            Assert.True(def.SpeedAbsMedian < 3.0, "default speed median");
            Assert.True(Math.Abs(def.SpeedBias) < 3.0, "default throws should be neither weak nor strong on average");
            Assert.True(def.LandMedian < 0.50, "default landing median");
            Assert.True(cal.DirMedian < 4.5, "calibrated direction median");
            Assert.True(cal.DirP90 < 13.0, "calibrated direction 90th percentile");
            Assert.True(cal.SpeedAbsMedian < 2.5, "calibrated speed median");
            Assert.True(Math.Abs(cal.SpeedBias) < 3.0, "calibrated throws should be neither weak nor strong on average");
            Assert.True(cal.LandMedian < 0.25, "calibrated landing median");
            Assert.True(cal.LandP90 < 1.0, "calibrated landing 90th percentile");

            // The naive method throws weak; the pipeline must not.
            Assert.True(naive.SpeedBias < -5.0, "the simulator should reproduce the 'weak throw' problem for the naive method");

            // The calibration must find each session's real delay. It cannot see the 4 ms by which this
            // simulated player lets go before the fastest moment, so that much is expected to be missing.
            Assert.Equal(24, ex.LearnedMinusIdeal.Count);
            Assert.InRange(ex.LearnedMinusIdeal.Average(), -0.0065, -0.0015);
            foreach (double miss in ex.LearnedMinusIdeal) Assert.InRange(miss, -0.004 - 0.006, -0.004 + 0.006);

            // With perfect timing the maths itself must be nearly exact: what is left is timing.
            Stats exact = ex.Rows[InfoExactBoth];
            Assert.True(exact.DirMedian < 1.0, "direction with exact timing");
            Assert.True(exact.LandMedian < 0.10, "landing with exact timing");

            // Every kind of throw and every sample rate must benefit, not just the average.
            foreach (ThrowKind kind in AllKinds)
            {
                Stats n = ex.Rows["  " + kind + " | naive"];
                Stats d = ex.Rows["  " + kind + " | default"];
                Stats c = ex.Rows["  " + kind + " | calibrated"];
                Assert.True(d.LandMedian < 0.6 * n.LandMedian, kind + ": default landing vs naive");
                Assert.True(c.LandMedian < 0.3 * n.LandMedian, kind + ": calibrated landing vs naive");
                Assert.True(c.DirMedian < 0.3 * n.DirMedian, kind + ": calibrated direction vs naive");
            }
            foreach (double rate in Rates)
            {
                string r = "  " + ((int)rate).ToString(CultureInfo.InvariantCulture) + " Hz | ";
                Assert.True(ex.Rows[r + "default"].LandMedian < 0.6 * ex.Rows[r + "naive"].LandMedian, r + "default landing vs naive");
                Assert.True(ex.Rows[r + "calibrated"].LandMedian < 0.35 * ex.Rows[r + "naive"].LandMedian, r + "calibrated landing vs naive");
            }
        }

        [Fact]
        public void WhatIfTables()
        {
            // Smaller runs that answer "what if" questions. They print; only loose sanity checks are asserted.
            StringBuilder sb = new StringBuilder();

            Experiment onset = Run(Rates, 6, 12, 40, seed: 5150u, useOnsetTime: true);
            sb.AppendLine(Format(onset, "WHAT IF the event time fed in is the detector's 'fingers began to open' time instead of its crossing time?"));

            Experiment crossing = Run(Rates, 6, 12, 40, seed: 5150u);
            sb.AppendLine(Format(crossing, "...the same throws with the crossing time (the normal way), for comparison"));

            Experiment tuned = Run(Rates, 6, 12, 40, seed: 5150u, peakToRelease: 0.004);
            sb.AppendLine(Format(tuned, "WHAT IF the 'peak to release' feel setting is tuned to match the player (4 ms here)?"));

            Experiment early = Run(Rates, 6, 12, 40, seed: 5150u, intentMean: -0.015);
            sb.AppendLine(Format(early, "WHAT IF the player really lets go 15 ms BEFORE the fastest moment, and nothing is tuned for it?"));

            Experiment earlyTuned = Run(Rates, 6, 12, 40, seed: 5150u, intentMean: -0.015, peakToRelease: 0.015);
            sb.AppendLine(Format(earlyTuned, "...and the same player with the 'peak to release' feel setting at 15 ms"));

            Experiment sloppy = Run(Rates, 6, 12, 40, seed: 5150u, fingerJitter: 0.010);
            sb.AppendLine(Format(sloppy, "WHAT IF the fingers start to open less consistently (10 ms of throw-to-throw scatter instead of 3 ms)?"));

            Publish(sb.ToString(), "throw_whatif.txt");

            Assert.True(tuned.Rows[Calibrated].DirMedian < crossing.Rows[Calibrated].DirMedian,
                "tuning the feel setting to the player should help");
            Assert.True(earlyTuned.Rows[Calibrated].DirMedian < 0.6 * early.Rows[Calibrated].DirMedian,
                "the feel setting should fix a player who lets go early");
            foreach (Experiment e in new[] { onset, crossing, tuned, earlyTuned, sloppy })
            {
                Assert.True(e.Rows[Calibrated].LandMedian < 0.4 * e.Rows[Naive].LandMedian, "calibrated should beat naive in every scenario");
            }
        }
    }
}
