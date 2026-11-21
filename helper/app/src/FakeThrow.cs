using System;
using Portaballs.Core;

namespace Portaballs.Helper
{
    /// <summary>
    /// A made-up player who throws underarm with the right hand every two seconds, so the whole chain
    /// can be proven with nobody wearing the headset.
    ///
    /// Everything is worked out from the timestamp alone: ask for the same time twice, in this program
    /// or in another one, and you get exactly the same numbers. That is what lets a listener check
    /// every sample it receives against what must have been sent.
    ///
    /// The motion is built in SteamVR's axes (+X right, +Y up, -Z forward) and converted the same way
    /// the real readings are, so the conversion is exercised too.
    ///
    /// One cycle (times in seconds from the start of the cycle):
    ///   0.25 - 0.30   right grip closes
    ///   0.50 - 1.00   arm swings back
    ///   0.70 - 0.75   eyes jump from the hand to a target five metres ahead, then stay on it
    ///   1.00 - 1.30   arm swings forward, peaking near 10 m/s
    ///   1.1837        fingers START to open (deliberately not on any polling tick); fully open 40 ms later
    ///   1.50 - 1.70   left trigger squeezed and let go
    ///   1.60 - 1.80   eyes drift back to the hand
    ///   1.85 - 1.95   a blink (gaze not tracked)
    ///
    /// Like a real controller, grip and trigger are only "reported" every 4 ms, off the polling grid,
    /// and each reading carries the time of the report that last changed it (GripTime / TriggerTime).
    /// </summary>
    internal static class FakeThrow
    {
        public const double CycleSeconds = 2.0;

        public const double GrabStart = 0.25;
        public const double GrabEnd = 0.30;
        public const double ReleaseStart = 1.1837;
        public const double ReleaseRampSeconds = 0.040;

        public const double WindUpStart = 0.5;
        public const double SwingStart = 1.0;
        public const double SwingEnd = 1.3;

        public const double TriggerStart = 1.50;
        public const double TriggerRamp = 0.05;
        public const double TriggerEnd = 1.70;
        public const double TriggerPeak = 0.6;

        /// <summary>The pretend controller sends a new grip / trigger reading this often.</summary>
        public const double ReportInterval = 0.004;
        public const double ReportOffset = 0.0007;

        private const double Deg = Math.PI / 180.0;
        private const double RestAngle = 10.0 * Deg;
        private const double BackAngle = -70.0 * Deg;
        private const double FrontAngle = 80.0 * Deg;
        private const double ArmLength = 0.6;

        private const int LiveHand = LinkPoseFlags.Connected | LinkPoseFlags.PoseValid | LinkPoseFlags.InputActive;
        private const int LiveHead = LinkPoseFlags.Connected | LinkPoseFlags.PoseValid;

        /// <summary>Start time of the cycle that contains this moment.</summary>
        public static double CycleStart(double time)
        {
            return Math.Floor(time / CycleSeconds) * CycleSeconds;
        }

        /// <summary>The moment in this cycle at which the right hand's fingers start to open.</summary>
        public static double ReleaseOnset(double time)
        {
            return CycleStart(time) + ReleaseStart;
        }

        /// <summary>The true squeeze of the right grip at any moment (before the 4 ms reporting).</summary>
        public static double TrueGrip(double time)
        {
            double p = time - CycleStart(time);
            return Pulse(p, GrabStart, GrabEnd - GrabStart, ReleaseStart, ReleaseRampSeconds, 1.0);
        }

        /// <summary>Everything at one moment. Index is left at 0 for the poll loop to fill in.</summary>
        public static LinkSample SampleAt(double time)
        {
            double cycle = CycleStart(time);
            double p = time - cycle;

            LinkSample s = new LinkSample();
            s.Time = time;

            // ---- Head: sways a little and turns slowly from side to side. ----
            double sway = Math.Sin(Math.PI * p);
            double swayRate = Math.PI * Math.Cos(Math.PI * p);
            double hx = 0.02 * sway, hy = 1.65 + 0.005 * Math.Sin(2.0 * Math.PI * p), hz = 0.03 * sway;
            double yaw = 10.0 * Deg * sway;
            Vec3 headPosition = V(hx, hy, hz);
            s.Head = PoseConvert.FromOpenVr(
                LiveHead,
                headPosition,
                Quat.CreateFromAxisAngle(new Vec3(0f, 1f, 0f), (float)yaw),
                V(0.02 * swayRate, 0.005 * 2.0 * Math.PI * Math.Cos(2.0 * Math.PI * p), 0.03 * swayRate),
                V(0.0, 10.0 * Deg * swayRate, 0.0));

            // ---- Right hand: the throwing arm, a stick swinging about the shoulder. ----
            double angle, angleRate;
            ArmAngle(p, out angle, out angleRate);
            // angle is measured from straight down, positive toward the front (-Z).
            double sin = Math.Sin(angle), cos = Math.Cos(angle);
            Vec3 shoulder = V(0.2, 1.45, 0.0);
            s.Right.Pose = PoseConvert.FromOpenVr(
                LiveHand,
                shoulder + V(0.0, -ArmLength * cos, -ArmLength * sin),
                Quat.CreateFromAxisAngle(new Vec3(1f, 0f, 0f), (float)angle),
                V(0.0, ArmLength * angleRate * sin, -ArmLength * angleRate * cos),
                V(angleRate, 0.0, 0.0));

            // ---- Left hand: hangs by the side and bobs. ----
            double bob = Math.Sin(2.0 * Math.PI * p);
            s.Left.Pose = PoseConvert.FromOpenVr(
                LiveHand,
                V(-0.25, 0.9 + 0.02 * bob, -0.1),
                Quat.CreateFromAxisAngle(new Vec3(0f, 0f, 1f), (float)(5.0 * Deg * bob)),
                V(0.0, 0.02 * 2.0 * Math.PI * Math.Cos(2.0 * Math.PI * p), 0.0),
                V(0.0, 0.0, 5.0 * Deg * 2.0 * Math.PI * Math.Cos(2.0 * Math.PI * p)));

            // ---- Grip and trigger, as last reported by the pretend controller. ----
            double report = LastReportAtOrBefore(time);
            double gripTime, triggerTime;
            s.Right.Grip = (float)Reported(report, GrabStart, GrabEnd - GrabStart, ReleaseStart, ReleaseRampSeconds, 1.0, out gripTime);
            s.Right.GripTime = gripTime;
            s.Right.Trigger = 0f;
            s.Right.TriggerTime = report;
            s.Left.Grip = 0f;
            s.Left.GripTime = report;
            s.Left.Trigger = (float)Reported(report, TriggerStart, TriggerRamp, TriggerEnd - TriggerRamp, TriggerRamp, TriggerPeak, out triggerTime);
            s.Left.TriggerTime = triggerTime;

            // ---- Eyes. ----
            Vec3 hand = V(0.2, 1.0, -0.4);
            Vec3 target = V(0.4, 1.3, -5.0);
            double toTarget = Smooth((p - 0.70) / 0.05) - Smooth((p - 1.60) / 0.20);
            Vec3 lookAt = hand + (target - hand) * (float)toTarget;
            bool blink = p >= 1.85 && p < 1.95;
            s.Gaze = PoseConvert.GazeFromOpenVr(true, !blink, !blink, headPosition, lookAt);

            return s;
        }

        /// <summary>Arm angle and how fast it is changing, at phase p of the cycle.</summary>
        private static void ArmAngle(double p, out double angle, out double rate)
        {
            if (p < WindUpStart)
            {
                angle = RestAngle;
                rate = 0.0;
            }
            else if (p < SwingStart)
            {
                Ease(p, WindUpStart, SwingStart, RestAngle, BackAngle, out angle, out rate);
            }
            else if (p < SwingEnd)
            {
                Ease(p, SwingStart, SwingEnd, BackAngle, FrontAngle, out angle, out rate);
            }
            else
            {
                Ease(p, SwingEnd, CycleSeconds, FrontAngle, RestAngle, out angle, out rate);
            }
        }

        /// <summary>Move smoothly from a to b between t0 and t1, starting and ending at rest.</summary>
        private static void Ease(double t, double t0, double t1, double a, double b, out double value, out double rate)
        {
            double u = (t - t0) / (t1 - t0);
            if (u < 0.0) u = 0.0;
            if (u > 1.0) u = 1.0;
            double s = u * u * u * (10.0 + u * (-15.0 + 6.0 * u));
            double ds = 30.0 * u * u * (1.0 - u) * (1.0 - u);
            value = a + (b - a) * s;
            rate = (b - a) * ds / (t1 - t0);
        }

        private static double Smooth(double u)
        {
            if (u <= 0.0) return 0.0;
            if (u >= 1.0) return 1.0;
            return u * u * u * (10.0 + u * (-15.0 + 6.0 * u));
        }

        /// <summary>0, a straight ramp up to 'peak', a hold, a straight ramp down, 0.</summary>
        private static double Pulse(double p, double riseStart, double riseSeconds, double fallStart, double fallSeconds, double peak)
        {
            if (p <= riseStart) return 0.0;
            if (p < riseStart + riseSeconds) return peak * (p - riseStart) / riseSeconds;
            if (p <= fallStart) return peak;
            if (p < fallStart + fallSeconds) return peak * (1.0 - (p - fallStart) / fallSeconds);
            return 0.0;
        }

        /// <summary>The newest moment at or before 'time' at which the pretend controller sent a reading.</summary>
        public static double LastReportAtOrBefore(double time)
        {
            return Math.Floor((time - ReportOffset) / ReportInterval) * ReportInterval + ReportOffset;
        }

        private static double FirstReportAtOrAfter(double time)
        {
            return Math.Ceiling((time - ReportOffset) / ReportInterval) * ReportInterval + ReportOffset;
        }

        /// <summary>
        /// The value in the report sent at 'report', and the time of the report that last CHANGED the value
        /// (what SteamVR's "update time" means).
        /// </summary>
        private static double Reported(double report, double riseStart, double riseSeconds, double fallStart, double fallSeconds, double peak, out double changedAt)
        {
            double cycle = CycleStart(report);
            double p = report - cycle;
            double value = Pulse(p, riseStart, riseSeconds, fallStart, fallSeconds, peak);

            double previous = report - ReportInterval;
            double previousValue = Pulse(previous - CycleStart(previous), riseStart, riseSeconds, fallStart, fallSeconds, peak);
            if (value != previousValue)
            {
                changedAt = report;   // still moving: this very report changed it
            }
            else if (value > 0.0)
            {
                changedAt = FirstReportAtOrAfter(cycle + riseStart + riseSeconds);   // holding: since the ramp up ended
            }
            else if (p >= fallStart + fallSeconds)
            {
                changedAt = FirstReportAtOrAfter(cycle + fallStart + fallSeconds);   // open: since this cycle's ramp down ended
            }
            else
            {
                changedAt = FirstReportAtOrAfter(cycle - CycleSeconds + fallStart + fallSeconds);   // open since last cycle
            }
            if (changedAt > report) changedAt = report;
            return value;
        }

        private static Vec3 V(double x, double y, double z)
        {
            return new Vec3((float)x, (float)y, (float)z);
        }
    }
}
