using System;
using System.Globalization;
using System.Runtime;
using System.Threading;
using Portaballs.Core;

namespace Portaballs.Helper
{
    /// <summary>Where the poll loop hands its samples. The WebSocket server is one; tests use others.</summary>
    internal interface ISampleSink
    {
        /// <summary>Take ownership of this array; only the first <paramref name="count"/> entries are filled.</summary>
        void PostBatch(LinkSample[] samples, int count);

        void PostStatus(LinkStatus status);
    }

    /// <summary>What the loop measured about itself.</summary>
    internal struct PumpTotals
    {
        public long Samples;
        public long Overruns;
        public long Batches;

        /// <summary>Gaps between one sample's timestamp and the next.</summary>
        public StatsSummary Interval;

        /// <summary>How long after its due moment each poll actually began.</summary>
        public StatsSummary Lateness;

        /// <summary>How long one read of the source took.</summary>
        public StatsSummary ReadCost;
    }

    /// <summary>
    /// The heartbeat: a dedicated thread that reads the source at a fixed rate and hands the samples on
    /// in small batches.
    ///
    /// Each poll is due at start + n * period, worked out from the start rather than from the previous
    /// poll, so small delays never add up into drift. If the thread is held up for longer than a whole
    /// period, the missed slots are skipped (and counted as overruns) instead of fired in a burst.
    ///
    /// The loop itself does no text work and no network work; it only fills arrays. Timing wobble in
    /// the loop is harmless to accuracy anyway, because every sample carries the time it was really taken.
    /// </summary>
    internal sealed class SamplePump
    {
        private readonly ISampleSource _source;
        private readonly ISampleSink _sink;
        private readonly Options _options;
        private readonly FileLog _log;

        private readonly object _statsGate = new object();
        private readonly LoopStats _interval = new LoopStats();
        private readonly LoopStats _lateness = new LoopStats();
        private readonly LoopStats _readCost = new LoopStats();
        private readonly LoopStats _windowInterval = new LoopStats();
        private readonly LoopStats _windowLateness = new LoopStats();
        private readonly LoopStats _windowReadCost = new LoopStats();

        private long _samples;
        private long _overruns;
        private long _batches;
        private Thread _thread;
        private volatile bool _stop;
        private volatile bool _streaming;

        /// <summary>Seconds between summary lines in the log.</summary>
        public double LogEverySeconds = 10.0;

        /// <summary>Called on the poll thread with every sample taken, before it is batched. For tests.</summary>
        public Action<LinkSample> Tap;

        public SamplePump(ISampleSource source, ISampleSink sink, Options options, FileLog log)
        {
            _source = source;
            _sink = sink;
            _options = options;
            _log = log;
        }

        public bool IsStreaming { get { return _streaming; } }

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(Run);
            _thread.Name = "Portaballs poll";
            _thread.IsBackground = true;
            _thread.Priority = ThreadPriority.Highest;
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            Thread t = _thread;
            if (t != null && t != Thread.CurrentThread) t.Join(3000);
            _thread = null;
        }

        public PumpTotals GetTotals()
        {
            lock (_statsGate)
            {
                PumpTotals t = new PumpTotals();
                t.Samples = _samples;
                t.Overruns = _overruns;
                t.Batches = _batches;
                t.Interval = _interval.Summarize();
                t.Lateness = _lateness.Summarize();
                t.ReadCost = _readCost.Summarize();
                return t;
            }
        }

        private void Run()
        {
            try
            {
                // Ask the garbage collector to avoid long pauses while this is running.
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            }
            catch (Exception)
            {
            }

            // Ask Windows to schedule this one thread like an audio thread, so that a busy game does not
            // hold it up. This affects only this thread and ends with it; it changes no system setting.
            IntPtr boost = PreciseTimer.BoostThisThread();
            using (PreciseTimer timer = new PreciseTimer())
            {
                if (!timer.HighResolution) _log.Warn("High-resolution timer not available; polling will be less steady.");
                if (boost == IntPtr.Zero) _log.Info("Windows would not give the poll thread audio-style scheduling; using an ordinary high-priority thread.");
                try
                {
                    Loop(timer);
                }
                catch (Exception e)
                {
                    _log.Error("Poll loop stopped by an error: " + e);
                }
            }
            PreciseTimer.EndBoost(boost);
            _streaming = false;
        }

        private void Loop(PreciseTimer timer)
        {
            double periodTicks = Clock.TicksPerSecond / _options.PollHz;
            long spinTicks = (long)(_options.SpinMicros * 1e-6 * Clock.TicksPerSecond);
            int perBatch = _options.SamplesPerBatch;
            bool flushOnInput = _options.FlushOnInputChange;

            LinkSample[] batch = new LinkSample[perBatch];
            int filled = 0;

            LinkSample sample = new LinkSample();
            bool havePrevious = false;
            double previousTime = 0.0;
            float previousLeftGrip = 0f, previousLeftTrigger = 0f, previousRightGrip = 0f, previousRightTrigger = 0f;

            // One read and a few timed waits that do not count, so that first-use start-up costs (several
            // milliseconds the first time the timer is used) are paid before the beat starts.
            _source.TryRead(ref sample);
            for (int i = 0; i < 5 && !_stop; i++) timer.WaitUntil(Clock.Ticks + Clock.TicksPerSecond / 1000, spinTicks);

            long origin = Clock.Ticks;
            long slot = 1;
            long nextStatus = origin;
            long nextLog = origin + Clock.ToTicks(LogEverySeconds);
            long statusEvery = Clock.TicksPerSecond;
            bool wasStreaming = false;

            while (!_stop)
            {
                long due = origin + (long)(slot * periodTicks);
                timer.WaitUntil(due, spinTicks);
                long began = Clock.Ticks;

                bool ok = _source.TryRead(ref sample);
                long ended = Clock.Ticks;

                if (ok)
                {
                    sample.Index = _samples;
                    lock (_statsGate)
                    {
                        _samples++;
                        double late = Clock.ToSeconds(began - due);
                        double cost = Clock.ToSeconds(ended - began);
                        _lateness.Add(late);
                        _windowLateness.Add(late);
                        _readCost.Add(cost);
                        _windowReadCost.Add(cost);
                        if (havePrevious)
                        {
                            double gap = sample.Time - previousTime;
                            _interval.Add(gap);
                            _windowInterval.Add(gap);
                        }
                    }

                    Action<LinkSample> tap = Tap;
                    if (tap != null) tap(sample);

                    bool inputMoved = havePrevious && (
                        sample.Left.Grip != previousLeftGrip || sample.Left.Trigger != previousLeftTrigger ||
                        sample.Right.Grip != previousRightGrip || sample.Right.Trigger != previousRightTrigger);
                    havePrevious = true;
                    previousTime = sample.Time;
                    previousLeftGrip = sample.Left.Grip;
                    previousLeftTrigger = sample.Left.Trigger;
                    previousRightGrip = sample.Right.Grip;
                    previousRightTrigger = sample.Right.Trigger;

                    batch[filled++] = sample;
                    if (filled >= perBatch || (flushOnInput && inputMoved))
                    {
                        _sink.PostBatch(batch, filled);
                        Interlocked.Increment(ref _batches);
                        batch = new LinkSample[perBatch];
                        filled = 0;
                    }

                    if (!wasStreaming)
                    {
                        wasStreaming = true;
                        _streaming = true;
                        _log.Info("Streaming from " + _source.Name + " (" + _source.Detail + ").");
                        nextStatus = ended;
                    }
                }
                else
                {
                    // Nothing to read (SteamVR is not up). Send what is waiting, then idle quietly.
                    if (filled > 0)
                    {
                        _sink.PostBatch(batch, filled);
                        Interlocked.Increment(ref _batches);
                        batch = new LinkSample[perBatch];
                        filled = 0;
                    }
                    if (wasStreaming)
                    {
                        wasStreaming = false;
                        _streaming = false;
                        _log.Info("Stopped streaming (" + _source.Detail + "). Waiting.");
                        nextStatus = ended;
                    }
                    havePrevious = false;
                    SendStatusIfDue(ref nextStatus, statusEvery, false);
                    for (int i = 0; i < 5 && !_stop; i++) Thread.Sleep(50);
                    origin = Clock.Ticks;
                    slot = 1;
                    continue;
                }

                SendStatusIfDue(ref nextStatus, statusEvery, true);

                if (ended >= nextLog)
                {
                    nextLog = ended + Clock.ToTicks(LogEverySeconds);
                    LogWindow();
                }

                // Next slot. If this poll ran late, skip any slot that is already past or would come less
                // than a quarter of a period after it, rather than firing two polls almost on top of each other.
                slot++;
                long missed = 0;
                while (origin + (long)(slot * periodTicks) - began < (long)(periodTicks * 0.25))
                {
                    slot++;
                    missed++;
                }
                if (missed > 0)
                {
                    lock (_statsGate) _overruns += missed;
                }
            }

            if (filled > 0) _sink.PostBatch(batch, filled);
        }

        private void SendStatusIfDue(ref long nextStatus, long every, bool streaming)
        {
            long now = Clock.Ticks;
            if (now < nextStatus) return;
            nextStatus = now + every;
            LinkStatus status = new LinkStatus();
            status.Time = Clock.ToSeconds(now);
            status.State = streaming ? LinkState.Streaming : LinkState.WaitingForSource;
            lock (_statsGate)
            {
                status.SampleCount = _samples;
                status.Overruns = _overruns;
            }
            status.Detail = _source.Detail;
            _sink.PostStatus(status);
        }

        private void LogWindow()
        {
            StatsSummary interval, lateness, cost;
            long overruns;
            lock (_statsGate)
            {
                interval = _windowInterval.Summarize();
                lateness = _windowLateness.Summarize();
                cost = _windowReadCost.Summarize();
                overruns = _overruns;
                _windowInterval.Reset();
                _windowLateness.Reset();
                _windowReadCost.Reset();
            }
            _log.Info("Poll gaps: " + interval.Describe() + " | late by: " + lateness.DescribeDuration()
                + " | one read took: " + cost.DescribeDuration()
                + " | skipped slots so far: " + overruns.ToString(CultureInfo.InvariantCulture));
            string health = _source.DescribeHealth();
            if (!string.IsNullOrEmpty(health)) _log.Info(health);
        }
    }
}
