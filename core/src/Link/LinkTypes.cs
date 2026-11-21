namespace Portaballs.Core
{
    /// <summary>
    /// The link between the helper program and the game.
    ///
    /// What it is for: s&box only tells the game where the hands are once per drawn frame, with no
    /// clock reading and no eye tracking. The helper reads all of that from SteamVR several hundred
    /// times a second and sends it to the game as text over a WebSocket on this PC. These are the
    /// things that travel over that link.
    ///
    /// Everything here is already in the core's own conventions (see Units.cs): metres, seconds,
    /// +X forward, +Y left, +Z up, relative to the middle of the play area floor. All times are
    /// seconds on the PC-wide Stopwatch clock: Stopwatch.GetTimestamp() / Stopwatch.Frequency.
    /// Every program on this PC reads the same clock, so the game can compare these times with its own.
    /// </summary>
    public static class LinkProtocol
    {
        /// <summary>Goes up whenever the text format changes in a way old readers cannot follow.</summary>
        public const int Version = 1;

        /// <summary>The port the helper listens on (127.0.0.1 only) unless told otherwise.</summary>
        public const int DefaultPort = 28461;

        /// <summary>Between two fields on a line.</summary>
        public const char FieldSeparator = ' ';

        /// <summary>At the end of every line.</summary>
        public const char LineSeparator = '\n';

        /// <summary>How many numbers follow the "S" on one sample line.</summary>
        public const int SampleFieldCount = 2 + PoseFieldCount + HandFieldCount + HandFieldCount + GazeFieldCount;

        public const int PoseFieldCount = 14;
        public const int HandFieldCount = PoseFieldCount + 4;
        public const int GazeFieldCount = 7;
    }

    /// <summary>Bits in <see cref="LinkPose.Flags"/>.</summary>
    public static class LinkPoseFlags
    {
        /// <summary>The device is switched on and known to SteamVR.</summary>
        public const int Connected = 1;

        /// <summary>The position and rotation are real tracked values right now.</summary>
        public const int PoseValid = 2;

        /// <summary>Hands only: the grip and trigger readings are live.</summary>
        public const int InputActive = 4;
    }

    /// <summary>Bits in <see cref="LinkGaze.Flags"/>.</summary>
    public static class LinkGazeFlags
    {
        /// <summary>SteamVR has an eye tracker feeding this reading.</summary>
        public const int Active = 1;

        /// <summary>The origin and direction are usable.</summary>
        public const int Valid = 2;

        /// <summary>The eyes are being tracked this instant (false during a blink).</summary>
        public const int Tracked = 4;

        /// <summary>All three: the only case in which the game should aim with the gaze.</summary>
        public const int Usable = Active | Valid | Tracked;
    }

    /// <summary>What the helper is doing, sent about once a second.</summary>
    public static class LinkState
    {
        /// <summary>SteamVR is not running (or not answering). No samples are flowing.</summary>
        public const int WaitingForSource = 0;

        /// <summary>Samples are flowing.</summary>
        public const int Streaming = 1;
    }

    /// <summary>Where one tracked thing is, which way it is turned and how it is moving.</summary>
    public struct LinkPose
    {
        /// <summary>See <see cref="LinkPoseFlags"/>.</summary>
        public int Flags;

        /// <summary>Position, metres.</summary>
        public Vec3 Position;

        /// <summary>Rotation. Its local +X is the device's forward, +Y its left, +Z its up.</summary>
        public Quat Rotation;

        /// <summary>Speed and direction of travel as SteamVR reports it, metres per second.</summary>
        public Vec3 Velocity;

        /// <summary>Spin as SteamVR reports it: the axis it turns about (in play-area axes), length in radians per second.</summary>
        public Vec3 AngularVelocity;

        public bool IsValid { get { return (Flags & LinkPoseFlags.PoseValid) != 0; } }
    }

    /// <summary>One hand controller: its pose plus how hard the grip and trigger are squeezed.</summary>
    public struct LinkHand
    {
        public LinkPose Pose;

        /// <summary>Grip squeeze, 0 (open) to 1 (full).</summary>
        public float Grip;

        /// <summary>
        /// When the grip last CHANGED to this value, as SteamVR reports it. This is usually earlier than
        /// the sample's own time and does not sit on the helper's polling grid. Feed (GripTime, Grip) to
        /// the release detector whenever GripTime moves, and the let-go moment comes out finer than the
        /// polling interval.
        /// </summary>
        public double GripTime;

        /// <summary>Trigger pull, 0 to 1.</summary>
        public float Trigger;

        /// <summary>When the trigger last changed to this value. See <see cref="GripTime"/>.</summary>
        public double TriggerTime;

        public bool InputActive { get { return (Pose.Flags & LinkPoseFlags.InputActive) != 0; } }
    }

    /// <summary>Where the eyes are looking.</summary>
    public struct LinkGaze
    {
        /// <summary>See <see cref="LinkGazeFlags"/>.</summary>
        public int Flags;

        /// <summary>Where the gaze ray starts, metres.</summary>
        public Vec3 Origin;

        /// <summary>Which way it points (length 1 when valid).</summary>
        public Vec3 Direction;

        public bool IsUsable { get { return (Flags & LinkGazeFlags.Usable) == LinkGazeFlags.Usable; } }
    }

    /// <summary>Everything the helper read at one instant.</summary>
    public struct LinkSample
    {
        /// <summary>Counts up by one per sample since the helper started. A jump means samples were lost.</summary>
        public long Index;

        /// <summary>When the poses were read, seconds on the shared clock.</summary>
        public double Time;

        public LinkPose Head;
        public LinkHand Left;
        public LinkHand Right;
        public LinkGaze Gaze;
    }

    /// <summary>The first thing the helper says to a new listener.</summary>
    public struct LinkHello
    {
        /// <summary>The helper's <see cref="LinkProtocol.Version"/>.</summary>
        public int Version;

        /// <summary>Where the samples come from: "openvr" or "fake".</summary>
        public string Source;

        /// <summary>Samples per second the helper aims to take.</summary>
        public float PollHz;

        /// <summary>Messages per second the helper normally sends (more while a grip or trigger is moving).</summary>
        public float BatchHz;

        /// <summary>The helper's clock when it said hello. Compare with your own to check the clocks agree.</summary>
        public double Time;
    }

    /// <summary>A short note on how the helper is doing.</summary>
    public struct LinkStatus
    {
        public double Time;

        /// <summary>See <see cref="LinkState"/>.</summary>
        public int State;

        /// <summary>Samples taken since the helper started.</summary>
        public long SampleCount;

        /// <summary>Polls that started late enough to skip a slot.</summary>
        public long Overruns;

        /// <summary>One word of detail, for logs. Never contains spaces.</summary>
        public string Detail;
    }
}
