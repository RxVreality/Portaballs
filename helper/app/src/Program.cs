using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace Portaballs.Helper
{
    /// <summary>
    /// Portaballs helper. Runs with no window; everything it has to say goes to helper.log beside it.
    /// See Options.HelpText for the switches.
    ///
    /// Exit codes: 0 fine, 1 a probe found a problem or the helper hit an error, 2 the port is taken or
    /// a helper is already running on it, 3 bad switches, 4 nothing was running to stop.
    /// </summary>
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int which);

        private static int Main(string[] args)
        {
            string baseDirectory = AppContext.BaseDirectory;
            string problem;
            Options options = Options.Load(args, baseDirectory, out problem);

            bool wantsConsole = options.ShowConsole || options.Probe || options.ShowHelp || options.Stop;
            if (wantsConsole) OpenConsole();

            if (options.ShowHelp)
            {
                Console.WriteLine(Options.HelpText);
                return 0;
            }

            string names = "PortaballsHelper." + options.Port.ToString(CultureInfo.InvariantCulture);
            if (options.Stop) return StopRunning(names);
            if (options.Probe) return RunProbe(options);

            // Only one helper per port. Checked before the log is opened, so a second copy started by
            // mistake leaves quietly without scribbling over the first one's log.
            bool first;
            using (Mutex single = new Mutex(true, "Local\\" + names + ".Running", out first))
            {
                if (!first)
                {
                    if (options.ShowConsole) Console.WriteLine("A helper is already running on port " + options.Port.ToString(CultureInfo.InvariantCulture) + ".");
                    return 2;
                }
                try
                {
                    return Run(options, baseDirectory, names, problem);
                }
                finally
                {
                    try { single.ReleaseMutex(); } catch (Exception) { }
                }
            }
        }

        private static int Run(Options options, string baseDirectory, string names, string problem)
        {
            string logPath = string.IsNullOrEmpty(options.LogPath) ? Path.Combine(baseDirectory, "helper.log") : options.LogPath;
            using (FileLog log = new FileLog(logPath, options.ShowConsole))
            using (EventWaitHandle stop = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\" + names + ".Stop"))
            {
                log.Info("---- Portaballs helper " + typeof(Program).Assembly.GetName().Version + " starting (" + (options.Fake ? "fake source" : "SteamVR source") + ") ----");
                if (problem != null)
                {
                    log.Warn(problem);
                    if (problem.StartsWith("Unknown switch", StringComparison.Ordinal) || problem.StartsWith("Switch ", StringComparison.Ordinal)) return 3;
                }
                stop.Reset();

                try
                {
                    using (HelperHost host = new HelperHost(options, log, baseDirectory))
                    {
                        try
                        {
                            host.Start();
                        }
                        catch (SocketException e)
                        {
                            log.Error("Could not listen on port " + options.Port.ToString(CultureInfo.InvariantCulture) + ": " + e.Message
                                + " Another program is using it; pick another with --port or helper.json.");
                            return 2;
                        }

                        Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
                        {
                            e.Cancel = true;
                            stop.Set();
                        };

                        if (options.Seconds > 0.0) stop.WaitOne(TimeSpan.FromSeconds(options.Seconds));
                        else stop.WaitOne();
                    }
                }
                catch (Exception e)
                {
                    log.Error("The helper stopped because of an error: " + e);
                    return 1;
                }
                log.Info("---- stopped ----");
            }
            return 0;
        }

        private static int StopRunning(string names)
        {
            EventWaitHandle stop;
            if (!EventWaitHandle.TryOpenExisting("Local\\" + names + ".Stop", out stop))
            {
                Console.WriteLine("No helper is running on that port.");
                return 4;
            }
            using (stop)
            {
                stop.Set();
            }
            Console.WriteLine("Asked the helper to stop.");
            return 0;
        }

        private static int RunProbe(Options options)
        {
            double seconds = options.Seconds > 0.0 ? options.Seconds : 5.0;
            Console.WriteLine("Listening to ws://127.0.0.1:" + options.Port.ToString(CultureInfo.InvariantCulture) + "/ for " + seconds.ToString("0.#", CultureInfo.InvariantCulture) + " s ...");
            ProbeResult result = Probe.RunAsync(options.Port, seconds, false, CancellationToken.None).GetAwaiter().GetResult();
            Console.Write(result.Describe());

            bool ok = result.Connected && result.HasHello && !result.VersionMismatch && result.BadLines == 0
                && result.MissedSamples == 0 && result.OutOfOrder == 0 && result.FakeMismatches == 0;
            if (ok && result.HasStatus && result.Status.State == Portaballs.Core.LinkState.Streaming)
            {
                double wanted = result.Hello.PollHz;
                ok = result.Samples > 0 && Math.Abs(result.SampleRate - wanted) <= wanted * 0.02;
            }
            Console.WriteLine(ok ? "RESULT: OK" : "RESULT: PROBLEM");
            return ok ? 0 : 1;
        }

        /// <summary>
        /// This is a windowless program, so it has no console of its own. Use the one it was started
        /// from if there is one (or the pipe its output was redirected to); otherwise open a new one.
        /// </summary>
        private static void OpenConsole()
        {
            try
            {
                IntPtr existing = GetStdHandle(-11);   // standard output
                if (existing != IntPtr.Zero && existing != new IntPtr(-1)) return;   // already redirected somewhere
                if (!AttachConsole(-1)) AllocConsole();
            }
            catch (Exception)
            {
            }
        }
    }
}
