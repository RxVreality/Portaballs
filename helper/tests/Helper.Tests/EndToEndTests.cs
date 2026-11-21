using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Portaballs.Core;
using Xunit;
using Xunit.Abstractions;

// These tests measure timing, so they must not fight each other for the processor.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Portaballs.Helper.Tests
{
    /// <summary>A helper running inside the test process, on a spare port, with the fake throw as its source.</summary>
    internal sealed class TestHelper : IDisposable
    {
        public readonly HelperHost Host;
        public readonly FileLog Log = new FileLog(null, false);

        /// <summary>Every sample the poll loop took, exactly as it was before any text was made of it.</summary>
        public readonly Dictionary<long, LinkSample> Sent = new Dictionary<long, LinkSample>();

        public TestHelper(ISampleSource source = null, double pollHz = 500.0, double batchHz = 100.0, bool allowAnyOrigin = false)
        {
            Options options = new Options();
            options.Port = 0;   // let Windows pick a free one
            options.Fake = true;
            options.PollHz = pollHz;
            options.BatchHz = batchHz;
            options.AllowAnyOrigin = allowAnyOrigin;
            Host = new HelperHost(options, Log, AppContext.BaseDirectory, source);
            Host.Pump.Tap = delegate (LinkSample s) { lock (Sent) Sent[s.Index] = s; };
            Host.Start();
        }

        public int Port { get { return Host.Port; } }

        public void Dispose()
        {
            Host.Dispose();
            Log.Dispose();
        }
    }

    /// <summary>A source that can be switched on and off, standing in for SteamVR starting and quitting.</summary>
    internal sealed class SwitchableSource : ISampleSource
    {
        public volatile bool On;
        public string Name { get { return "openvr"; } }
        public string Detail { get { return On ? "steamvr_streaming" : "waiting_for_steamvr"; } }

        public bool TryRead(ref LinkSample sample)
        {
            if (!On) return false;
            sample = FakeThrow.SampleAt(Clock.Now);
            return true;
        }

        public string DescribeHealth() { return null; }
        public void Dispose() { }
    }

    public class EndToEndTests
    {
        private readonly ITestOutputHelper _out;

        public EndToEndTests(ITestOutputHelper output)
        {
            _out = output;
        }

        [Fact]
        public async Task Fake_throw_goes_through_server_and_client_and_comes_out_exactly_as_sent_at_the_expected_rate()
        {
            using (TestHelper helper = new TestHelper())
            {
                await Task.Delay(300);
                ProbeResult r = await Probe.RunAsync(helper.Port, 4.0, true, CancellationToken.None);
                _out.WriteLine(r.Describe());
                PumpTotals totals = helper.Host.Pump.GetTotals();
                _out.WriteLine("Helper's own view of its poll gaps: " + totals.Interval.Describe());
                _out.WriteLine("Late by: " + totals.Lateness.DescribeDuration());

                Assert.True(r.Connected, r.ConnectError);
                Assert.True(r.HasHello);
                Assert.False(r.VersionMismatch);
                Assert.Equal(LinkProtocol.Version, r.Hello.Version);
                Assert.Equal("fake", r.Hello.Source);
                Assert.Equal(500f, r.Hello.PollHz);
                Assert.Equal(100f, r.Hello.BatchHz);
                Assert.True(r.HasStatus);
                Assert.Equal(LinkState.Streaming, r.Status.State);

                // Nothing lost, nothing damaged, nothing out of order.
                Assert.Equal(0, r.BadLines);
                Assert.Equal(0, r.MissedBatches);
                Assert.Equal(0, r.MissedSamples);
                Assert.Equal(0, r.OutOfOrder);
                Assert.True(r.Samples > 1500, "only " + r.Samples + " samples in 4 s");

                // What came out is what went in: every decoded sample against the copy taken before encoding...
                int compared = 0;
                foreach (LinkSample got in r.Kept)
                {
                    LinkSample sent;
                    lock (helper.Sent) Assert.True(helper.Sent.TryGetValue(got.Index, out sent), "sample " + got.Index + " was never sent");
                    Assert.Null(Probe.FirstDifference(sent, got));
                    compared++;
                }
                Assert.Equal(r.Samples, compared);
                // ...and against the made-up throw worked out afresh from each timestamp.
                Assert.Equal(r.Samples, r.FakeChecked);
                Assert.Equal(0, r.FakeMismatches);
                for (int i = 1; i < r.Kept.Count; i++) Assert.Equal(r.Kept[i - 1].Index + 1, r.Kept[i].Index);

                // The expected rate: 500 a second by the samples' own clocks, and arriving at that pace too.
                Assert.InRange(r.SampleRate, 490.0, 510.0);
                Assert.InRange(r.ArrivalRate, 480.0, 520.0);
                Assert.InRange(r.Interval.P50, 0.0015, 0.0025);
                // 100 messages a second when idle, more while the grip or trigger moves, never more than one per sample.
                Assert.InRange(r.BatchRate, 95.0, 500.0);

                // The throw's release is placed far more finely than the 2 ms polling interval.
                Assert.True(r.ReleasesSeen >= 1, "no release seen");
                Assert.True(r.WorstReleaseErrorFromGripTime < 20e-6, "release error " + r.WorstReleaseErrorFromGripTime);
            }
        }

        [Fact]
        public async Task Several_listeners_at_once_all_get_the_same_stream()
        {
            using (TestHelper helper = new TestHelper())
            {
                await Task.Delay(200);
                Task<ProbeResult>[] probes =
                {
                    Probe.RunAsync(helper.Port, 2.0, true, CancellationToken.None),
                    Probe.RunAsync(helper.Port, 2.0, true, CancellationToken.None),
                    Probe.RunAsync(helper.Port, 2.0, true, CancellationToken.None),
                };
                await Task.Delay(400);
                Assert.Equal(3, helper.Host.Server.ClientCount);
                ProbeResult[] results = await Task.WhenAll(probes);

                foreach (ProbeResult r in results)
                {
                    Assert.True(r.Connected, r.ConnectError);
                    Assert.True(r.HasHello);
                    Assert.Equal(0, r.MissedSamples);
                    Assert.Equal(0, r.BadLines);
                    Assert.Equal(0, r.FakeMismatches);
                    Assert.True(r.Samples > 700, "only " + r.Samples + " samples");
                    foreach (LinkSample got in r.Kept)
                    {
                        LinkSample sent;
                        lock (helper.Sent) Assert.True(helper.Sent.TryGetValue(got.Index, out sent));
                        Assert.Null(Probe.FirstDifference(sent, got));
                    }
                }
                await Task.Delay(300);
                Assert.Equal(0, helper.Host.Server.ClientCount);
                Assert.Equal(0, helper.Host.Server.BatchesDropped);
            }
        }

        [Fact]
        public async Task A_listener_that_joins_late_is_not_told_it_missed_anything()
        {
            using (TestHelper helper = new TestHelper())
            {
                await Task.Delay(1000);
                ProbeResult r = await Probe.RunAsync(helper.Port, 1.0, true, CancellationToken.None);
                Assert.True(r.Connected, r.ConnectError);
                Assert.True(r.Kept[0].Index > 300, "first index " + r.Kept[0].Index);
                Assert.Equal(0, r.MissedBatches);
                Assert.Equal(0, r.MissedSamples);
            }
        }

        [Fact]
        public async Task While_the_source_is_not_ready_listeners_get_hello_and_waiting_and_then_samples_when_it_starts()
        {
            SwitchableSource source = new SwitchableSource();
            using (TestHelper helper = new TestHelper(source))
            {
                ProbeResult waiting = await Probe.RunAsync(helper.Port, 1.6, false, CancellationToken.None);
                Assert.True(waiting.Connected, waiting.ConnectError);
                Assert.True(waiting.HasHello);
                Assert.Equal("openvr", waiting.Hello.Source);
                Assert.True(waiting.HasStatus);
                Assert.Equal(LinkState.WaitingForSource, waiting.Status.State);
                Assert.Equal("waiting_for_steamvr", waiting.Status.Detail);
                Assert.Equal(0, waiting.Samples);
                Assert.False(helper.Host.Pump.IsStreaming);

                source.On = true;   // "SteamVR started"
                await Task.Delay(600);
                ProbeResult streaming = await Probe.RunAsync(helper.Port, 1.5, false, CancellationToken.None);
                Assert.Equal(LinkState.Streaming, streaming.Status.State);
                Assert.True(streaming.Samples > 500, "only " + streaming.Samples);
                Assert.Equal(0, streaming.MissedSamples);
                Assert.True(helper.Host.Pump.IsStreaming);

                source.On = false;  // "SteamVR quit"
                await Task.Delay(600);
                ProbeResult after = await Probe.RunAsync(helper.Port, 1.4, false, CancellationToken.None);
                Assert.Equal(LinkState.WaitingForSource, after.Status.State);
                Assert.Equal(0, after.Samples);
            }
        }

        [Fact]
        public async Task The_server_listens_on_this_PC_only()
        {
            using (TestHelper helper = new TestHelper())
            {
                Assert.Equal(IPAddress.Loopback, helper.Host.Server.BoundAddress);

                // From any of this PC's network addresses the port must look closed.
                List<IPAddress> others = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    .ToList();
                _out.WriteLine("Other addresses tried: " + string.Join(", ", others));
                foreach (IPAddress address in others)
                {
                    using (TcpClient client = new TcpClient(AddressFamily.InterNetwork))
                    {
                        bool connected;
                        try
                        {
                            Task attempt = client.ConnectAsync(address, helper.Port);
                            connected = await Task.WhenAny(attempt, Task.Delay(1500)) == attempt && client.Connected;
                        }
                        catch (SocketException)
                        {
                            connected = false;
                        }
                        Assert.False(connected, "the helper answered on " + address);
                    }
                }

                // And on the loopback address it does answer.
                using (TcpClient local = new TcpClient(AddressFamily.InterNetwork))
                {
                    await local.ConnectAsync(IPAddress.Loopback, helper.Port);
                    Assert.True(local.Connected);
                }
            }
        }

        [Fact]
        public async Task Web_pages_from_elsewhere_are_turned_away_but_programs_and_local_pages_are_let_in()
        {
            using (TestHelper helper = new TestHelper())
            {
                ProbeResult program = await Probe.RunAsync(helper.Port, 0.3, false, CancellationToken.None);
                Assert.True(program.Connected, program.ConnectError);

                ProbeResult page = await Probe.RunAsync(helper.Port, 0.3, false, CancellationToken.None, "https://example.com");
                Assert.False(page.Connected);

                ProbeResult localPage = await Probe.RunAsync(helper.Port, 0.3, false, CancellationToken.None, "http://localhost:3000");
                Assert.True(localPage.Connected, localPage.ConnectError);
            }

            using (TestHelper open = new TestHelper(null, 500.0, 100.0, true))
            {
                ProbeResult page = await Probe.RunAsync(open.Port, 0.3, false, CancellationToken.None, "https://example.com");
                Assert.True(page.Connected, page.ConnectError);
            }
        }

        [Fact]
        public void Origin_rule()
        {
            Assert.True(LinkServer.OriginIsLocal(null));
            Assert.True(LinkServer.OriginIsLocal(""));
            Assert.True(LinkServer.OriginIsLocal("http://127.0.0.1:8080"));
            Assert.True(LinkServer.OriginIsLocal("http://localhost"));
            Assert.True(LinkServer.OriginIsLocal("https://app.localhost:5173"));
            Assert.False(LinkServer.OriginIsLocal("https://example.com"));
            Assert.False(LinkServer.OriginIsLocal("http://127.0.0.1.example.com"));
            Assert.False(LinkServer.OriginIsLocal("http://localhost.example.com"));
            Assert.False(LinkServer.OriginIsLocal("null"));
            Assert.False(LinkServer.OriginIsLocal("file://"));
        }

        [Fact]
        public void Handshake_answer_matches_the_WebSocket_standard()
        {
            // The worked example in RFC 6455, section 1.3.
            Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", LinkServer.AcceptKey("dGhlIHNhbXBsZSBub25jZQ=="));
        }

        [Fact]
        public async Task A_plain_web_request_is_told_this_is_WebSocket_only()
        {
            using (TestHelper helper = new TestHelper())
            using (HttpClient http = new HttpClient())
            {
                HttpResponseMessage response = await http.GetAsync("http://127.0.0.1:" + helper.Port + "/");
                Assert.Equal((HttpStatusCode)426, response.StatusCode);
                Assert.Contains("WebSocket only", await response.Content.ReadAsStringAsync());
            }
        }

        [Fact]
        public async Task A_listener_that_stops_reading_does_not_hold_up_the_others()
        {
            using (TestHelper helper = new TestHelper())
            using (TcpClient stuck = new TcpClient(AddressFamily.InterNetwork))
            {
                // Shake hands by hand, then never read another byte.
                stuck.ReceiveBufferSize = 4096;
                await stuck.ConnectAsync(IPAddress.Loopback, helper.Port);
                byte[] hello = Encoding.ASCII.GetBytes(
                    "GET / HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                    "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n");
                await stuck.GetStream().WriteAsync(hello, 0, hello.Length);
                await Task.Delay(300);
                Assert.Equal(1, helper.Host.Server.ClientCount);

                ProbeResult good = await Probe.RunAsync(helper.Port, 3.0, false, CancellationToken.None);
                _out.WriteLine(good.Describe());
                Assert.True(good.Connected, good.ConnectError);
                Assert.Equal(0, good.MissedSamples);
                Assert.Equal(0, good.FakeMismatches);
                Assert.InRange(good.SampleRate, 490.0, 510.0);
                Assert.True(good.Samples > 1300, "only " + good.Samples);

                // The poll loop never noticed either.
                PumpTotals totals = helper.Host.Pump.GetTotals();
                Assert.InRange(totals.Interval.Rate, 490.0, 510.0);
            }
        }

        [Fact]
        public async Task A_second_helper_cannot_take_the_same_port()
        {
            using (TestHelper first = new TestHelper())
            {
                Options options = new Options();
                options.Port = first.Port;
                options.Fake = true;
                using (FileLog log = new FileLog(null, false))
                using (HelperHost second = new HelperHost(options, log, AppContext.BaseDirectory))
                {
                    Assert.Throws<SocketException>(() => second.Start());
                }
                // The first is untouched.
                ProbeResult r = await Probe.RunAsync(first.Port, 0.5, false, CancellationToken.None);
                Assert.True(r.Connected, r.ConnectError);
                Assert.True(r.Samples > 100);
            }
        }
    }
}
