using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Portaballs.Core;

namespace Portaballs.Helper
{
    /// <summary>What a listener saw while it was connected.</summary>
    internal sealed class ProbeResult
    {
        public bool Connected;
        public string ConnectError;
        public bool HasHello;
        public LinkHello Hello;
        public bool VersionMismatch;
        public bool HasStatus;
        public LinkStatus Status;

        public double ListenSeconds;
        public long Messages;
        public long Bytes;
        public long Batches;
        public long Samples;
        public long MissedBatches;
        public long MissedSamples;
        public long BadLines;
        public long OutOfOrder;

        /// <summary>Gaps between consecutive sample timestamps.</summary>
        public StatsSummary Interval;

        /// <summary>From the helper stamping a batch as sent to this listener having it decoded.</summary>
        public StatsSummary Delivery;

        /// <summary>From a sample being taken to this listener having it decoded (includes waiting for its batch to fill).</summary>
        public StatsSummary SampleAge;

        /// <summary>Samples per second by their own timestamps.</summary>
        public double SampleRate;

        /// <summary>Samples per second by when they arrived here.</summary>
        public double ArrivalRate;

        public double BatchRate;

        /// <summary>The helper's clock at hello minus ours on receiving it. Should be a small negative number (the trip time).</summary>
        public double HelloClockDifference;

        /// <summary>Fake source only: samples compared with the made-up throw, and how many differed.</summary>
        public long FakeChecked;
        public long FakeMismatches;
        public string FirstMismatch;

        /// <summary>Fake source only: right-hand releases seen, and the worst error in placing when the fingers began to open.</summary>
        public int ReleasesSeen;
        public double WorstReleaseErrorFromGripTime;
        public double WorstReleaseErrorFromSampleTime;

        /// <summary>Every sample received, when asked to keep them.</summary>
        public List<LinkSample> Kept;

        public string Describe()
        {
            StringBuilder sb = new StringBuilder();
            if (!Connected)
            {
                sb.AppendLine("Could not connect: " + ConnectError);
                return sb.ToString();
            }
            CultureInfo inv = CultureInfo.InvariantCulture;
            sb.AppendLine(HasHello
                ? string.Format(inv, "Hello: protocol {0}, source '{1}', poll {2} Hz, batch {3} Hz, clock difference {4:0.000} ms{5}",
                    Hello.Version, Hello.Source, Hello.PollHz, Hello.BatchHz, HelloClockDifference * 1e3, VersionMismatch ? "  ** VERSION MISMATCH **" : "")
                : "No hello received.");
            if (HasStatus) sb.AppendLine(string.Format(inv, "Status: state {0}, detail {1}, helper has taken {2} samples, skipped {3} slots", Status.State, Status.Detail, Status.SampleCount, Status.Overruns));
            sb.AppendLine(string.Format(inv, "Listened {0:0.00} s: {1} messages, {2} bytes ({3:0.0} kB/s), {4} batches ({5:0.0}/s), {6} samples",
                ListenSeconds, Messages, Bytes, ListenSeconds > 0 ? Bytes / 1024.0 / ListenSeconds : 0.0, Batches, BatchRate, Samples));
            sb.AppendLine(string.Format(inv, "Sample rate by timestamps {0:0.00}/s, by arrival {1:0.00}/s", SampleRate, ArrivalRate));
            sb.AppendLine("Gaps between samples: " + Interval.Describe());
            sb.AppendLine("Batch delivery delay: " + Delivery.DescribeDuration());
            sb.AppendLine("Sample age on arrival: " + SampleAge.DescribeDuration());
            sb.AppendLine(string.Format(inv, "Missed batches {0}, missed samples {1}, bad lines {2}, out of order {3}", MissedBatches, MissedSamples, BadLines, OutOfOrder));
            if (FakeChecked > 0)
            {
                sb.AppendLine(string.Format(inv, "Fake check: {0} samples compared bit for bit with the made-up throw, {1} differed{2}",
                    FakeChecked, FakeMismatches, FirstMismatch != null ? " (first: " + FirstMismatch + ")" : ""));
                sb.AppendLine(string.Format(inv, "Releases seen: {0}. Worst error placing the start of the release: {1:0.000} ms using GripTime, {2:0.000} ms using sample time",
                    ReleasesSeen, WorstReleaseErrorFromGripTime * 1e3, WorstReleaseErrorFromSampleTime * 1e3));
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// A stand-in for the game: connects to the helper with a plain WebSocket client, decodes what
    /// arrives with the same LinkDecoder the game uses, and measures it. Used by --probe and the tests.
    /// </summary>
    internal static class Probe
    {
        public static async Task<ProbeResult> RunAsync(int port, double seconds, bool keepSamples, CancellationToken cancel, string origin = null)
        {
            ProbeResult r = new ProbeResult();
            if (keepSamples) r.Kept = new List<LinkSample>();

            LinkDecoder decoder = new LinkDecoder();
            List<LinkSample> fresh = new List<LinkSample>(64);
            LoopStats interval = new LoopStats();
            LoopStats delivery = new LoopStats();
            LoopStats age = new LoopStats();
            ReleaseWatch watch = new ReleaseWatch();

            double firstSampleTime = 0.0, lastSampleTime = 0.0;
            double firstArrival = 0.0, lastArrival = 0.0;
            bool haveSample = false;

            using (ClientWebSocket socket = new ClientWebSocket())
            {
                if (origin != null) socket.Options.SetRequestHeader("Origin", origin);
                try
                {
                    using (CancellationTokenSource connectLimit = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                    {
                        connectLimit.CancelAfter(5000);
                        await socket.ConnectAsync(new Uri("ws://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/"), connectLimit.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception e)
                {
                    r.ConnectError = e.InnerException != null ? e.InnerException.Message : e.Message;
                    return r;
                }
                r.Connected = true;

                byte[] buffer = new byte[64 * 1024];
                byte[] whole = new byte[256 * 1024];
                double started = Clock.Now;
                using (CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    limit.CancelAfter(TimeSpan.FromSeconds(seconds));
                    try
                    {
                        while (socket.State == WebSocketState.Open)
                        {
                            int length = 0;
                            WebSocketReceiveResult got;
                            do
                            {
                                got = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), limit.Token).ConfigureAwait(false);
                                if (got.MessageType == WebSocketMessageType.Close) break;
                                if (length + got.Count > whole.Length) Array.Resize(ref whole, (length + got.Count) * 2);
                                Buffer.BlockCopy(buffer, 0, whole, length, got.Count);
                                length += got.Count;
                            }
                            while (!got.EndOfMessage);
                            if (got.MessageType == WebSocketMessageType.Close) break;

                            string message = Encoding.UTF8.GetString(whole, 0, length);
                            bool hadHello = decoder.HasHello;
                            long batchesBefore = decoder.BatchCount;
                            fresh.Clear();
                            decoder.Decode(message, fresh);
                            double now = Clock.Now;

                            r.Messages++;
                            r.Bytes += length;
                            if (!hadHello && decoder.HasHello) r.HelloClockDifference = decoder.Hello.Time - now;
                            if (decoder.BatchCount > batchesBefore) delivery.Add(Math.Max(0.0, now - decoder.LastBatchSendTime));

                            for (int i = 0; i < fresh.Count; i++)
                            {
                                LinkSample s = fresh[i];
                                if (haveSample)
                                {
                                    if (s.Time <= lastSampleTime) r.OutOfOrder++;
                                    else interval.Add(s.Time - lastSampleTime);
                                }
                                else
                                {
                                    haveSample = true;
                                    firstSampleTime = s.Time;
                                    firstArrival = now;
                                }
                                lastSampleTime = s.Time;
                                lastArrival = now;
                                age.Add(Math.Max(0.0, now - s.Time));

                                if (decoder.HasHello && decoder.Hello.Source == "fake")
                                {
                                    CheckAgainstFake(s, r);
                                    watch.Feed(s, r);
                                }
                                if (r.Kept != null) r.Kept.Add(s);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (WebSocketException)
                    {
                        // The helper went away while we were listening; report what we have.
                    }
                }
                r.ListenSeconds = Clock.Now - started;
                try
                {
                    using (CancellationTokenSource brief = new CancellationTokenSource(500))
                    {
                        if (socket.State == WebSocketState.Open) await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", brief.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception)
                {
                }
            }

            r.HasHello = decoder.HasHello;
            r.Hello = decoder.Hello;
            r.VersionMismatch = decoder.VersionMismatch;
            r.HasStatus = decoder.HasStatus;
            r.Status = decoder.Status;
            r.Batches = decoder.BatchCount;
            r.Samples = decoder.SampleCount;
            r.MissedBatches = decoder.MissedBatches;
            r.MissedSamples = decoder.MissedSamples;
            r.BadLines = decoder.BadLines;
            r.Interval = interval.Summarize();
            r.Delivery = delivery.Summarize();
            r.SampleAge = age.Summarize();
            if (r.Samples > 1 && lastSampleTime > firstSampleTime) r.SampleRate = (r.Samples - 1) / (lastSampleTime - firstSampleTime);
            if (r.Samples > 1 && lastArrival > firstArrival) r.ArrivalRate = (r.Samples - 1) / (lastArrival - firstArrival);
            if (r.ListenSeconds > 0.0) r.BatchRate = r.Batches / r.ListenSeconds;
            return r;
        }

        /// <summary>The fake throw is a pure function of time, so every sample can be recomputed here and compared exactly.</summary>
        private static void CheckAgainstFake(LinkSample got, ProbeResult r)
        {
            LinkSample expected = FakeThrow.SampleAt(got.Time);
            expected.Index = got.Index;
            r.FakeChecked++;
            string difference = FirstDifference(expected, got);
            if (difference != null)
            {
                r.FakeMismatches++;
                if (r.FirstMismatch == null) r.FirstMismatch = "sample " + got.Index.ToString(CultureInfo.InvariantCulture) + " " + difference;
            }
        }

        /// <summary>Null when two samples are the same bit for bit; otherwise the name of the first field that differs.</summary>
        public static string FirstDifference(LinkSample a, LinkSample b)
        {
            if (a.Index != b.Index) return "index";
            if (!Same(a.Time, b.Time)) return "time";
            string d = PoseDifference(a.Head, b.Head);
            if (d != null) return "head " + d;
            d = HandDifference(a.Left, b.Left);
            if (d != null) return "left " + d;
            d = HandDifference(a.Right, b.Right);
            if (d != null) return "right " + d;
            if (a.Gaze.Flags != b.Gaze.Flags) return "gaze flags";
            if (!Same(a.Gaze.Origin, b.Gaze.Origin)) return "gaze origin";
            if (!Same(a.Gaze.Direction, b.Gaze.Direction)) return "gaze direction";
            return null;
        }

        private static string HandDifference(LinkHand a, LinkHand b)
        {
            string d = PoseDifference(a.Pose, b.Pose);
            if (d != null) return d;
            if (!Same(a.Grip, b.Grip)) return "grip";
            if (!Same(a.GripTime, b.GripTime)) return "grip time";
            if (!Same(a.Trigger, b.Trigger)) return "trigger";
            if (!Same(a.TriggerTime, b.TriggerTime)) return "trigger time";
            return null;
        }

        private static string PoseDifference(LinkPose a, LinkPose b)
        {
            if (a.Flags != b.Flags) return "flags";
            if (!Same(a.Position, b.Position)) return "position";
            if (!Same(a.Rotation.X, b.Rotation.X) || !Same(a.Rotation.Y, b.Rotation.Y) || !Same(a.Rotation.Z, b.Rotation.Z) || !Same(a.Rotation.W, b.Rotation.W)) return "rotation";
            if (!Same(a.Velocity, b.Velocity)) return "velocity";
            if (!Same(a.AngularVelocity, b.AngularVelocity)) return "angular velocity";
            return null;
        }

        private static bool Same(Vec3 a, Vec3 b)
        {
            return Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);
        }

        private static bool Same(float a, float b)
        {
            return BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
        }

        private static bool Same(double a, double b)
        {
            return BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
        }
    }

    /// <summary>
    /// Watches the fake right hand's grip the way the game will: it looks at the first two readings of
    /// the falling grip and runs the straight line through them back up to "fully closed". That lands on
    /// the moment the fingers began to open. Done twice: once trusting GripTime (when the controller
    /// reported each value), once trusting the sample's own time (when the helper happened to poll).
    /// </summary>
    internal sealed class ReleaseWatch
    {
        private bool _holding;
        private bool _haveFirstFall;
        private double _firstGripTime, _firstSampleTime;
        private float _firstValue;
        private double _lastGripTime = double.NaN;

        public void Feed(LinkSample s, ProbeResult r)
        {
            float grip = s.Right.Grip;
            if (!_holding)
            {
                _holding = grip >= 1f;
                _haveFirstFall = false;
                _lastGripTime = s.Right.GripTime;
                return;
            }
            if (grip >= 1f)
            {
                _lastGripTime = s.Right.GripTime;
                return;
            }
            if (s.Right.GripTime == _lastGripTime) return;   // the same controller report seen again
            _lastGripTime = s.Right.GripTime;

            if (!_haveFirstFall)
            {
                _haveFirstFall = true;
                _firstGripTime = s.Right.GripTime;
                _firstSampleTime = s.Time;
                _firstValue = grip;
                return;
            }
            if (grip >= _firstValue) return;

            // Two points on the falling line: extend it back to grip = 1.
            double truth = FakeThrow.ReleaseOnset(s.Time);
            double byGripTime = Onset(_firstGripTime, _firstValue, s.Right.GripTime, grip);
            double bySampleTime = Onset(_firstSampleTime, _firstValue, s.Time, grip);
            r.ReleasesSeen++;
            r.WorstReleaseErrorFromGripTime = Math.Max(r.WorstReleaseErrorFromGripTime, Math.Abs(byGripTime - truth));
            r.WorstReleaseErrorFromSampleTime = Math.Max(r.WorstReleaseErrorFromSampleTime, Math.Abs(bySampleTime - truth));
            _holding = false;
        }

        private static double Onset(double t1, float v1, double t2, float v2)
        {
            double slope = (v2 - v1) / (t2 - t1);   // negative
            return t1 + (1.0 - v1) / slope;
        }
    }
}
