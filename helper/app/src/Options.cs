using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Portaballs.Core;

namespace Portaballs.Helper
{
    /// <summary>
    /// The helper's settings. Defaults first, then helper.json beside the program (if there is one),
    /// then the command line, each overriding the one before.
    ///
    /// helper.json example:  { "port": 28461, "pollHz": 500, "batchHz": 100, "appType": "overlay" }
    /// </summary>
    internal sealed class Options
    {
        /// <summary>Port on 127.0.0.1 the game connects to.</summary>
        public int Port = LinkProtocol.DefaultPort;

        /// <summary>Samples per second to take. 500 = one every 2 ms.</summary>
        public double PollHz = 500.0;

        /// <summary>
        /// Messages per second to send when nothing is happening. The game only looks once per drawn
        /// frame (72 to 144 times a second), so sending much more often than that buys nothing. 100 keeps
        /// the wait under one frame. While a grip or trigger is moving, every sample goes out at once
        /// (see <see cref="FlushOnInputChange"/>), so a release is never held back by batching.
        /// </summary>
        public double BatchHz = 100.0;

        /// <summary>Send at once whenever a grip or trigger value changed since the previous sample.</summary>
        public bool FlushOnInputChange = true;

        /// <summary>
        /// How long before each poll the timer thread stops sleeping and spins instead, in microseconds.
        /// More is steadier but costs CPU on one core (spin / poll interval of it).
        /// </summary>
        public int SpinMicros = 150;

        /// <summary>Use the made-up throw instead of SteamVR.</summary>
        public bool Fake;

        /// <summary>Show a console window and print the log there too.</summary>
        public bool ShowConsole;

        /// <summary>Stop by itself after this many seconds (0 = run until told to stop). For testing.</summary>
        public double Seconds;

        /// <summary>
        /// How the helper introduces itself to SteamVR: "overlay" or "background". See OpenVrSource for why
        /// overlay is the default.
        /// </summary>
        public string AppType = "overlay";

        /// <summary>
        /// Let web pages in a browser connect. Off by default: a page on the internet has no business
        /// reading where the player is looking. Programs (the game, test tools) are always let in.
        /// </summary>
        public bool AllowAnyOrigin;

        /// <summary>Ask a running helper to stop, then exit.</summary>
        public bool Stop;

        /// <summary>Act as a listener instead: connect to a running helper, check what arrives, print a report.</summary>
        public bool Probe;

        /// <summary>Log file. Empty = helper.log beside the program.</summary>
        public string LogPath = "";

        public bool ShowHelp;

        public const string HelpText =
            "PortaballsHelper [switches]\n" +
            "  (none)           read SteamVR and stream on ws://127.0.0.1:28461 ; no window, logs to helper.log\n" +
            "  --fake           stream a made-up throw instead of SteamVR (no headset needed)\n" +
            "  --console        show a console and print the log there as well\n" +
            "  --port N         listen on (or probe) another port\n" +
            "  --poll-hz N      samples per second (default 500)\n" +
            "  --batch-hz N     messages per second when idle (default 100)\n" +
            "  --spin-us N      microseconds of spinning before each poll (default 150)\n" +
            "  --no-flush       do not send early when a grip or trigger moves\n" +
            "  --app-type X     overlay (default) or background\n" +
            "  --allow-any-origin   let browser pages connect too\n" +
            "  --seconds N      stop after N seconds\n" +
            "  --log PATH       write the log somewhere else\n" +
            "  --stop           tell the running helper (on that port) to stop\n" +
            "  --probe          connect to a running helper, check the stream for --seconds (default 5), print a report\n";

        public static Options Load(string[] args, string baseDirectory, out string problem)
        {
            Options o = new Options();
            problem = null;
            try
            {
                string file = Path.Combine(baseDirectory, "helper.json");
                if (File.Exists(file)) o.ApplyJson(File.ReadAllText(file));
            }
            catch (Exception e)
            {
                problem = "helper.json could not be read (" + e.Message + "); using defaults.";
            }

            string argProblem = o.ApplyArgs(args ?? new string[0]);
            if (argProblem != null) problem = argProblem;
            o.Clamp();
            return o;
        }

        private void ApplyJson(string text)
        {
            JsonDocumentOptions lenient = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
            using (JsonDocument doc = JsonDocument.Parse(text, lenient))
            {
                foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                {
                    string name = p.Name.ToLowerInvariant();
                    JsonElement v = p.Value;
                    if (name == "port") Port = v.GetInt32();
                    else if (name == "pollhz") PollHz = v.GetDouble();
                    else if (name == "batchhz") BatchHz = v.GetDouble();
                    else if (name == "spinmicros") SpinMicros = v.GetInt32();
                    else if (name == "flushoninputchange") FlushOnInputChange = v.GetBoolean();
                    else if (name == "apptype") AppType = v.GetString();
                    else if (name == "allowanyorigin") AllowAnyOrigin = v.GetBoolean();
                    else if (name == "fake") Fake = v.GetBoolean();
                }
            }
        }

        private string ApplyArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                try
                {
                    if (a == "--fake") Fake = true;
                    else if (a == "--console") ShowConsole = true;
                    else if (a == "--stop") Stop = true;
                    else if (a == "--probe") Probe = true;
                    else if (a == "--no-flush") FlushOnInputChange = false;
                    else if (a == "--allow-any-origin") AllowAnyOrigin = true;
                    else if (a == "--help" || a == "-h" || a == "/?") ShowHelp = true;
                    else if (a == "--port") Port = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    else if (a == "--poll-hz") PollHz = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    else if (a == "--batch-hz") BatchHz = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    else if (a == "--spin-us") SpinMicros = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    else if (a == "--seconds") Seconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    else if (a == "--app-type") AppType = args[++i];
                    else if (a == "--log") LogPath = args[++i];
                    else return "Unknown switch " + args[i] + " (try --help).";
                }
                catch (Exception)
                {
                    return "Switch " + args[i < args.Length ? i : args.Length - 1] + " needs a value (try --help).";
                }
            }
            return null;
        }

        private void Clamp()
        {
            if (Port < 0 || Port > 65535) Port = LinkProtocol.DefaultPort;
            if (!(PollHz >= 10.0)) PollHz = 10.0;
            if (PollHz > 2000.0) PollHz = 2000.0;
            if (!(BatchHz >= 1.0)) BatchHz = 1.0;
            if (BatchHz > PollHz) BatchHz = PollHz;
            if (SpinMicros < 0) SpinMicros = 0;
            if (SpinMicros > 2000) SpinMicros = 2000;
            if (Seconds < 0.0 || double.IsNaN(Seconds)) Seconds = 0.0;
            AppType = (AppType ?? "overlay").Trim().ToLowerInvariant();
            if (AppType != "background") AppType = "overlay";
        }

        /// <summary>Samples that normally go in one message.</summary>
        public int SamplesPerBatch
        {
            get
            {
                int n = (int)Math.Round(PollHz / BatchHz);
                return n < 1 ? 1 : n;
            }
        }
    }
}
