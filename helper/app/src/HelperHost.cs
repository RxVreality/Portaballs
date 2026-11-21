using System;
using System.Globalization;
using Portaballs.Core;

namespace Portaballs.Helper
{
    /// <summary>
    /// The helper with its parts plugged together: a source (SteamVR or the fake), the poll loop, and
    /// the WebSocket server. Program.cs runs one of these; the tests run one too, in the test process.
    /// </summary>
    internal sealed class HelperHost : IDisposable
    {
        private readonly Options _options;
        private readonly FileLog _log;
        private bool _started;

        public HelperHost(Options options, FileLog log, string baseDirectory, ISampleSource source = null)
        {
            _options = options;
            _log = log;
            Source = source ?? (options.Fake ? (ISampleSource)new FakeSource() : new OpenVrSource(log, options.AppType, baseDirectory));
            Server = new LinkServer(options.Port, options.AllowAnyOrigin, MakeHello, log);
            Pump = new SamplePump(Source, Server, options, log);
        }

        public ISampleSource Source { get; private set; }
        public LinkServer Server { get; private set; }
        public SamplePump Pump { get; private set; }

        /// <summary>The port in use (known after <see cref="Start"/>).</summary>
        public int Port { get { return Server.Port; } }

        /// <summary>Open the port and start polling. Throws if the port is taken.</summary>
        public void Start()
        {
            Server.Start();
            Pump.Start();
            _started = true;
            _log.Info(string.Format(CultureInfo.InvariantCulture,
                "Listening on ws://127.0.0.1:{0}/ . Source: {1}. Polling {2:0.#} times a second, sending {3:0.#} messages a second ({4} samples each){5}. Protocol {6}.",
                Server.Port, Source.Name, _options.PollHz, _options.PollHz / _options.SamplesPerBatch, _options.SamplesPerBatch,
                _options.FlushOnInputChange ? ", at once while a grip or trigger moves" : "", LinkProtocol.Version));
        }

        private LinkHello MakeHello()
        {
            LinkHello hello = new LinkHello();
            hello.Version = LinkProtocol.Version;
            hello.Source = Source.Name;
            hello.PollHz = (float)_options.PollHz;
            hello.BatchHz = (float)(_options.PollHz / _options.SamplesPerBatch);
            hello.Time = Clock.Now;
            return hello;
        }

        public void Dispose()
        {
            if (_started)
            {
                Pump.Stop();
                PumpTotals totals = Pump.GetTotals();
                _log.Info("Stopping. Whole run: " + totals.Samples.ToString(CultureInfo.InvariantCulture) + " samples, "
                    + totals.Batches.ToString(CultureInfo.InvariantCulture) + " batches, "
                    + totals.Overruns.ToString(CultureInfo.InvariantCulture) + " skipped slots, "
                    + Server.MessagesSent.ToString(CultureInfo.InvariantCulture) + " messages sent, "
                    + Server.BatchesDropped.ToString(CultureInfo.InvariantCulture) + " dropped for slow listeners. Memory clean-ups (small/medium/full): "
                    + GC.CollectionCount(0).ToString(CultureInfo.InvariantCulture) + "/" + GC.CollectionCount(1).ToString(CultureInfo.InvariantCulture)
                    + "/" + GC.CollectionCount(2).ToString(CultureInfo.InvariantCulture) + ".");
                if (totals.Interval.Count > 0)
                {
                    _log.Info("Whole run poll gaps: " + totals.Interval.Describe());
                    _log.Info("Whole run late by: " + totals.Lateness.DescribeDuration() + " | one read took: " + totals.ReadCost.DescribeDuration());
                }
                _started = false;
            }
            Server.Dispose();
            Source.Dispose();
        }
    }
}
