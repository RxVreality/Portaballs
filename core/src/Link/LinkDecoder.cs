using System;
using System.Collections.Generic;
using System.Globalization;

namespace Portaballs.Core
{
    /// <summary>
    /// Reads the text the helper sends (the format is described on <see cref="LinkEncoder"/>).
    ///
    /// What it is for: the game hands every WebSocket message it receives to <see cref="Decode"/> and
    /// gets back the samples inside, ready to feed to PoseHistory, the release detector and the gaze filter.
    ///
    /// It is written to pass s&box's code checker: only strings, string.Split, float.Parse and
    /// double.Parse with the invariant culture. No files, sockets, threads, reflection or JSON.
    /// Whole numbers are read with double.Parse too, which is exact for every count this link can reach.
    ///
    /// One decoder per connection. Make a new one (or call <see cref="Reset"/>) after reconnecting.
    /// </summary>
    public sealed class LinkDecoder
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        /// <summary>True once the helper's hello has arrived.</summary>
        public bool HasHello;
        public LinkHello Hello;

        /// <summary>True once a status line has arrived.</summary>
        public bool HasStatus;
        public LinkStatus Status;

        /// <summary>True when the helper speaks a different protocol version. Its samples are ignored.</summary>
        public bool VersionMismatch;

        /// <summary>Batches read so far.</summary>
        public long BatchCount;

        /// <summary>Number on the newest batch, or -1 before the first.</summary>
        public long LastBatchSequence;

        /// <summary>When the helper sent the newest batch (shared clock). "Now" minus this is the delivery delay.</summary>
        public double LastBatchSendTime;

        /// <summary>Batches the helper sent that never arrived (it drops the oldest when a listener falls behind).</summary>
        public long MissedBatches;

        /// <summary>Samples read so far.</summary>
        public long SampleCount;

        /// <summary>Index of the newest sample, or -1 before the first.</summary>
        public long LastSampleIndex;

        /// <summary>Samples that should have come between the ones received, judged by their indexes.</summary>
        public long MissedSamples;

        /// <summary>Lines that could not be understood and were skipped.</summary>
        public long BadLines;

        public LinkDecoder()
        {
            Reset();
        }

        public void Reset()
        {
            HasHello = false;
            Hello = default(LinkHello);
            HasStatus = false;
            Status = default(LinkStatus);
            VersionMismatch = false;
            BatchCount = 0;
            LastBatchSequence = -1;
            LastBatchSendTime = 0.0;
            MissedBatches = 0;
            SampleCount = 0;
            LastSampleIndex = -1;
            MissedSamples = 0;
            BadLines = 0;
        }

        /// <summary>
        /// Read one message. Samples found are added to the end of <paramref name="samples"/> in time order.
        /// Returns how many were added. Never throws on bad input: a damaged line is counted in
        /// <see cref="BadLines"/> and skipped.
        /// </summary>
        public int Decode(string message, List<LinkSample> samples)
        {
            if (string.IsNullOrEmpty(message)) return 0;
            int added = 0;
            string[] lines = message.Split(LinkProtocol.LineSeparator);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;
                try
                {
                    string[] f = line.Split(LinkProtocol.FieldSeparator);
                    string kind = f[0];
                    if (kind == "S")
                    {
                        if (VersionMismatch) continue;
                        LinkSample sample;
                        if (!TryReadSample(f, out sample))
                        {
                            BadLines++;
                            continue;
                        }
                        if (LastSampleIndex >= 0 && sample.Index > LastSampleIndex + 1)
                        {
                            MissedSamples += sample.Index - LastSampleIndex - 1;
                        }
                        LastSampleIndex = sample.Index;
                        SampleCount++;
                        if (samples != null) samples.Add(sample);
                        added++;
                    }
                    else if (kind == "B")
                    {
                        if (f.Length < 4)
                        {
                            BadLines++;
                            continue;
                        }
                        long sequence = Whole(f[1]);
                        double sendTime = Number(f[2]);
                        if (LastBatchSequence >= 0 && sequence > LastBatchSequence + 1)
                        {
                            MissedBatches += sequence - LastBatchSequence - 1;
                        }
                        LastBatchSequence = sequence;
                        LastBatchSendTime = sendTime;
                        BatchCount++;
                    }
                    else if (kind == "H")
                    {
                        if (f.Length < 6)
                        {
                            BadLines++;
                            continue;
                        }
                        LinkHello hello = new LinkHello();
                        hello.Version = (int)Whole(f[1]);
                        hello.Source = f[2];
                        hello.PollHz = Single(f[3]);
                        hello.BatchHz = Single(f[4]);
                        hello.Time = Number(f[5]);
                        Hello = hello;
                        HasHello = true;
                        VersionMismatch = hello.Version != LinkProtocol.Version;
                    }
                    else if (kind == "I")
                    {
                        if (f.Length < 6)
                        {
                            BadLines++;
                            continue;
                        }
                        LinkStatus status = new LinkStatus();
                        status.Time = Number(f[1]);
                        status.State = (int)Whole(f[2]);
                        status.SampleCount = Whole(f[3]);
                        status.Overruns = Whole(f[4]);
                        status.Detail = f[5];
                        Status = status;
                        HasStatus = true;
                    }
                    // Anything else belongs to a later version: skip it.
                }
                catch (Exception)
                {
                    BadLines++;
                }
            }
            return added;
        }

        /// <summary>Read one "S" line that has already been split into fields.</summary>
        public static bool TryReadSample(string[] f, out LinkSample sample)
        {
            sample = new LinkSample();
            if (f == null || f.Length != 1 + LinkProtocol.SampleFieldCount) return false;
            int at = 1;
            sample.Index = Whole(f[at++]);
            sample.Time = Number(f[at++]);
            sample.Head = ReadPose(f, ref at);
            sample.Left = ReadHand(f, ref at);
            sample.Right = ReadHand(f, ref at);
            LinkGaze gaze = new LinkGaze();
            gaze.Flags = (int)Whole(f[at++]);
            gaze.Origin = ReadVector(f, ref at);
            gaze.Direction = ReadVector(f, ref at);
            sample.Gaze = gaze;
            return true;
        }

        private static LinkHand ReadHand(string[] f, ref int at)
        {
            LinkHand hand = new LinkHand();
            hand.Pose = ReadPose(f, ref at);
            hand.Grip = Single(f[at++]);
            hand.GripTime = Number(f[at++]);
            hand.Trigger = Single(f[at++]);
            hand.TriggerTime = Number(f[at++]);
            return hand;
        }

        private static LinkPose ReadPose(string[] f, ref int at)
        {
            LinkPose pose = new LinkPose();
            pose.Flags = (int)Whole(f[at++]);
            pose.Position = ReadVector(f, ref at);
            float x = Single(f[at++]);
            float y = Single(f[at++]);
            float z = Single(f[at++]);
            float w = Single(f[at++]);
            pose.Rotation = new Quat(x, y, z, w);
            pose.Velocity = ReadVector(f, ref at);
            pose.AngularVelocity = ReadVector(f, ref at);
            return pose;
        }

        private static Vec3 ReadVector(string[] f, ref int at)
        {
            float x = Single(f[at++]);
            float y = Single(f[at++]);
            float z = Single(f[at++]);
            return new Vec3(x, y, z);
        }

        private static float Single(string text)
        {
            return float.Parse(text, Invariant);
        }

        private static double Number(string text)
        {
            return double.Parse(text, Invariant);
        }

        private static long Whole(string text)
        {
            return (long)double.Parse(text, Invariant);
        }
    }
}
