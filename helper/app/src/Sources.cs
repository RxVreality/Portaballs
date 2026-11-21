using System;
using Portaballs.Core;

namespace Portaballs.Helper
{
    /// <summary>Something the poll loop can ask "where is everything right now?".</summary>
    internal interface ISampleSource : IDisposable
    {
        /// <summary>Goes in the hello message: "openvr" or "fake".</summary>
        string Name { get; }

        /// <summary>One word on what the source is doing, for the status message and the log.</summary>
        string Detail { get; }

        /// <summary>
        /// Read everything once and stamp it. Returns false when there is nothing to read yet (SteamVR
        /// not running); the loop then waits a moment and asks again. Called from the poll thread only.
        /// </summary>
        bool TryRead(ref LinkSample sample);

        /// <summary>Lines worth adding to the log's ten-second summary (may be null).</summary>
        string DescribeHealth();
    }

    /// <summary>
    /// Builds link poses out of readings that are still in SteamVR's axes. Both the real source and the
    /// fake one go through here, so the fake exercises the same conversion the headset will.
    /// </summary>
    internal static class PoseConvert
    {
        public static LinkPose FromOpenVr(int flags, Vec3 position, Quat rotation, Vec3 velocity, Vec3 angularVelocity)
        {
            LinkPose pose = new LinkPose();
            pose.Flags = flags;
            pose.Position = OpenVrSpace.ToCore(position);
            pose.Rotation = OpenVrSpace.ToCore(rotation);
            pose.Velocity = OpenVrSpace.ToCore(velocity);
            pose.AngularVelocity = OpenVrSpace.ToCore(angularVelocity);
            return pose;
        }

        /// <summary>Gaze from SteamVR's "where the ray starts" and "the point being looked at".</summary>
        public static LinkGaze GazeFromOpenVr(bool active, bool valid, bool tracked, Vec3 origin, Vec3 target)
        {
            LinkGaze gaze = new LinkGaze();
            gaze.Origin = OpenVrSpace.ToCore(origin);
            Vec3 direction;
            bool hasDirection = OpenVrSpace.TryDirection(gaze.Origin, OpenVrSpace.ToCore(target), out direction);
            gaze.Direction = direction;
            int flags = 0;
            if (active) flags |= LinkGazeFlags.Active;
            if (valid && hasDirection) flags |= LinkGazeFlags.Valid;
            if (tracked) flags |= LinkGazeFlags.Tracked;
            gaze.Flags = flags;
            return gaze;
        }
    }

    /// <summary>The made-up throw, read against the real clock. Needs no headset.</summary>
    internal sealed class FakeSource : ISampleSource
    {
        public string Name { get { return "fake"; } }

        public string Detail { get { return "fake_throw_every_" + FakeThrow.CycleSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "s"; } }

        public bool TryRead(ref LinkSample sample)
        {
            sample = FakeThrow.SampleAt(Clock.Now);
            return true;
        }

        public string DescribeHealth()
        {
            return null;
        }

        public void Dispose()
        {
        }
    }
}
