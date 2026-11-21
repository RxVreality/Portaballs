using System;

namespace Portaballs.Core
{
    /// <summary>How the hand's velocity is measured from the stored poses.</summary>
    public enum VelocityFit
    {
        /// <summary>
        /// A straight line through the window. Very steady, but because the hand speeds up and turns
        /// through a throw, the answer is really the velocity from the MIDDLE of the window (half a
        /// window before the release time). Forgiving when the release timing is not well known.
        /// </summary>
        Line = 1,

        /// <summary>
        /// A line that is allowed to bend. Gives the velocity AT the release time itself, at the price
        /// of a little more noise. The right choice once the release timing is trusted (after calibration).
        /// </summary>
        Curve = 2,
    }

    /// <summary>Which recipe produced the launch velocity. Shown on the tuning overlay.</summary>
    public enum ThrowMethod
    {
        /// <summary>There was no pose history at all; nothing could be worked out.</summary>
        NoData = 0,

        /// <summary>Only a position was known (one usable reading); the ball is simply let go with no speed.</summary>
        PositionOnly = 1,

        /// <summary>Direction and speed both come from the fit over the window.</summary>
        FitOnly = 2,

        /// <summary>Direction from the fit, speed from the fastest moment in the window.</summary>
        FitDirectionPeakSpeed = 3,
    }

    /// <summary>
    /// Every tunable number for turning a hand's motion into a ball launch.
    /// The defaults are starting guesses; the comments give a sensible range for each.
    /// </summary>
    public sealed class ThrowSettings
    {
        /// <summary>
        /// How long before the release was noticed the player really meant to let go, in seconds.
        /// Starts at Valve's own figure from their SteamVR Throwable (11 ms). Sensible range -0.03 to +0.06;
        /// values outside -0.05..+0.12 are clamped. The delay calibration can replace this with a measured value.
        /// </summary>
        public double ReleaseTimeOffset = 0.011;

        /// <summary>
        /// Length of the stretch of hand motion used to measure velocity, seconds. It ends at the release
        /// time (or a little after: see FitWindowAfterSeconds). Sensible range 0.04 to 0.10.
        /// </summary>
        public double FitWindowSeconds = 0.060;

        /// <summary>
        /// How much of that stretch may lie AFTER the release time, when such readings exist, seconds.
        /// 0 means none. The release is always noticed late, so there usually are some, and letting the
        /// Curve fit see both sides of the release makes it much more accurate. Sensible range 0 to 0.03.
        /// </summary>
        public double FitWindowAfterSeconds = 0.0;

        /// <summary>See <see cref="VelocityFit"/>.</summary>
        public VelocityFit Fit = VelocityFit.Line;

        /// <summary>
        /// If the window holds too few readings (tracking dropped out), readings up to this far back may be
        /// used instead, seconds. Sensible range 0.10 to 0.25.
        /// </summary>
        public double MaxFitWindowSeconds = 0.15;

        /// <summary>
        /// Where the ball sits relative to the controller's origin, in the controller's own axes, metres.
        /// The game knows this (it is where it draws the held ball). It matters for wrist flicks: a ball
        /// 8 cm out from the wrist picks up real speed from the hand's rotation alone.
        /// </summary>
        public Vec3 BallLocalOffset = default(Vec3);

        /// <summary>Use the fastest moment in the window for the speed (when its direction agrees with the fit).</summary>
        public bool UsePeakSpeed = true;

        /// <summary>How far back from the release time to look for the fastest moment, seconds. Sensible range 0.05 to 0.15.</summary>
        public double PeakSearchSeconds = 0.10;

        /// <summary>Readings within this long either side are averaged when measuring speed at one moment, seconds. Sensible range 0.008 to 0.025.</summary>
        public double PeakSmoothingSeconds = 0.015;

        /// <summary>The peak speed is only used when its direction is within this angle of the fitted direction, radians. Sensible range 15 to 45 degrees.</summary>
        public float PeakAgreeAngle = 30f * (MathF.PI / 180f);

        /// <summary>The peak may raise the fitted speed by at most this factor. Sensible range 1.2 to 2.</summary>
        public float MaxPeakBoost = 1.6f;

        /// <summary>At or below this hand speed (m/s) a release is a plain let-go: the ball keeps exactly the hand's motion. Sensible range 0.3 to 1.</summary>
        public float DropSpeed = 0.6f;

        /// <summary>At or above this hand speed (m/s) a release is a full throw. In between, throw help fades in smoothly. Sensible range 1.5 to 3.</summary>
        public float FullThrowSpeed = 2.0f;

        /// <summary>
        /// Overall strength multiplier for real throws (not drops). 1.0 is the hand's own speed; limited to 0.5 to 2.
        /// The game sets it (ThrowHand.SpeedScale, 1.7 since 2026-11-19): a controller is swung slower than a real ball leaves a hand.
        /// </summary>
        public float SpeedScale = 1.0f;

        /// <summary>No throw leaves faster than this, m/s. Sensible range 15 to 40.</summary>
        public float MaxSpeed = 30f;

        /// <summary>The ball is never moved further along its path than this to make up for a late release, seconds. Sensible range 0.05 to 0.2.</summary>
        public double MaxRollbackSeconds = 0.15;

        /// <summary>Gravity used for the path, m/s^2.</summary>
        public Vec3 Gravity = new Vec3(0f, 0f, -9.81f);

        /// <summary>The hard limits applied to <see cref="ReleaseTimeOffset"/>.</summary>
        public const double MinReleaseTimeOffset = -0.05;
        public const double MaxReleaseTimeOffset = 0.12;

        /// <summary>
        /// Switch to a delay that has actually been measured on this headset (by the delay calibration,
        /// or by hand). Sets the release offset to it, and because the timing is now known, also switches
        /// to the Curve fit with 20 ms of the window allowed after the release, which reads the velocity
        /// at the release moment itself instead of half a window earlier.
        /// </summary>
        public void UseMeasuredDelay(double delaySeconds)
        {
            if (double.IsNaN(delaySeconds)) return;
            ReleaseTimeOffset = delaySeconds;
            Fit = VelocityFit.Curve;
            FitWindowAfterSeconds = 0.020;
        }
    }

    /// <summary>Everything a tuning overlay would want to show about one throw.</summary>
    public struct ThrowDiagnostics
    {
        public ThrowMethod Method;

        /// <summary>The time the release was noticed, as given by the caller.</summary>
        public double EventTime;

        /// <summary>The time the ball is treated as having left the hand (event time minus the offset, kept inside the stored poses).</summary>
        public double IntendedReleaseTime;

        /// <summary>True when the wanted release time lay outside the stored poses and had to be moved.</summary>
        public bool ReleaseTimeClamped;

        /// <summary>Time between the two stored poses either side of the release time. Large means tracking was missing right then.</summary>
        public float PoseGapAtRelease;

        /// <summary>How many readings the velocity fit used.</summary>
        public int SamplesUsed;

        /// <summary>Time from the first to the last reading used, seconds. Much shorter than the asked-for window means missing data.</summary>
        public float WindowCovered;

        /// <summary>True when the window had too few readings and older ones were pulled in.</summary>
        public bool WindowWidened;

        /// <summary>The fit actually used (it falls back from Curve to Line when there are too few readings).</summary>
        public VelocityFit FitUsed;

        /// <summary>Typical distance between the hand positions and the fitted path, metres. Around the tracking noise is good; much bigger means a jerky or badly tracked motion.</summary>
        public float FitResidual;

        /// <summary>Velocity of the controller's origin from the fit, m/s.</summary>
        public Vec3 HandVelocity;

        /// <summary>The ball's extra velocity from the hand's turning (it sits out from the wrist, so turning swings it), m/s.</summary>
        public Vec3 LeverVelocity;

        /// <summary>The ball's velocity from the fit (hand + lever), before any speed adjustment.</summary>
        public Vec3 FitVelocity;
        public float FitSpeed;

        /// <summary>True when a fastest moment was found in the search window.</summary>
        public bool PeakFound;

        /// <summary>When the ball was moving fastest.</summary>
        public double PeakTime;

        /// <summary>The ball's velocity at that fastest moment.</summary>
        public Vec3 PeakVelocity;
        public float PeakSpeed;

        /// <summary>Angle between the fitted direction and the peak direction, radians.</summary>
        public float AngleBetweenMethods;

        /// <summary>0 for a plain let-go, 1 for a full throw, in between for a tiny toss.</summary>
        public float Throwness;

        /// <summary>True when the speed limit cut the throw down.</summary>
        public bool SpeedClamped;

        /// <summary>How far along its path the ball was moved to catch up with "now", seconds.</summary>
        public float RollbackSeconds;

        /// <summary>True when the catch-up was cut short by the rollback limit.</summary>
        public bool RollbackClamped;
    }

    /// <summary>The ball's launch, worked out from the hand's recent motion.</summary>
    public struct ThrowResult
    {
        /// <summary>False only when there was no pose history at all.</summary>
        public bool Valid;

        /// <summary>True when this was a let-go rather than a throw. Aim assist must leave it alone.</summary>
        public bool IsDrop;

        /// <summary>When the ball left the hand.</summary>
        public double ReleaseTime;

        /// <summary>Where the ball was at that moment.</summary>
        public Vec3 ReleasePosition;

        /// <summary>The ball's velocity at that moment, m/s.</summary>
        public Vec3 ReleaseVelocity;

        /// <summary>The hand's rate of turning at that moment (axis in world space, length in radians per second). Use it for the ball's spin.</summary>
        public Vec3 AngularVelocity;

        /// <summary>Where the ball should be spawned right now: the release position moved along its flight path for the time that has passed since.</summary>
        public Vec3 NowPosition;

        /// <summary>The velocity to give it right now (the release velocity after that much gravity).</summary>
        public Vec3 NowVelocity;

        public ThrowDiagnostics Diagnostics;
    }

    /// <summary>
    /// Turns "the grip was released at this time" plus the hand's pose history into the ball's launch.
    ///
    /// The steps: step back from the noticed release to when the player meant to let go; measure the
    /// hand's velocity and rate of turning from all the readings in a short window (not from two
    /// frames); add the swing the ball gets from sitting out from the wrist; sanity-check the speed
    /// against the fastest moment of the swing; go easy on drops; then move the ball forward along its
    /// flight path to where it would be by now.
    /// </summary>
    public static class ThrowEstimator
    {
        /// <param name="history">The throwing hand's recent poses.</param>
        /// <param name="eventTime">When the release happened according to the grip detector (may differ from the newest pose time).</param>
        /// <param name="nowTime">The current time, used to catch the ball up along its path.</param>
        public static ThrowResult Estimate(PoseHistory history, double eventTime, double nowTime, ThrowSettings settings)
        {
            ThrowResult result = default(ThrowResult);
            ThrowDiagnostics diag = default(ThrowDiagnostics);
            diag.Method = ThrowMethod.NoData;
            diag.EventTime = eventTime;
            diag.FitUsed = VelocityFit.Line;
            if (settings == null) settings = new ThrowSettings();

            if (history == null || history.Count == 0 || double.IsNaN(eventTime) || double.IsInfinity(eventTime))
            {
                result.IsDrop = true;
                result.Diagnostics = diag;
                return result;
            }

            // 1. When did the player mean to let go?
            double offset = settings.ReleaseTimeOffset;
            if (double.IsNaN(offset)) offset = 0.0;
            offset = ThrowMath.Clamp(offset, ThrowSettings.MinReleaseTimeOffset, ThrowSettings.MaxReleaseTimeOffset);
            double wanted = eventTime - offset;
            double tRel = ThrowMath.Clamp(wanted, history.OldestTime, history.NewestTime);
            diag.ReleaseTimeClamped = tRel != wanted;
            diag.IntendedReleaseTime = tRel;

            PoseSample pose;
            double gap;
            bool clamped;
            history.TrySample(tRel, out pose, out gap, out clamped);
            diag.PoseGapAtRelease = (float)gap;

            Vec3 lever = Vec3.Transform(settings.BallLocalOffset, pose.Rotation);   // ball offset in world space
            if (!ThrowMath.IsFinite(lever)) lever = default(Vec3);
            Vec3 releasePosition = pose.Position + lever;

            result.Valid = true;
            result.ReleaseTime = tRel;
            result.ReleasePosition = releasePosition;

            // 2. Which readings to fit: a window of fixed length that ends at the release time, or a little
            //    after it when that is allowed and the readings exist.
            double window = ThrowMath.Clamp(settings.FitWindowSeconds, 0.01, 0.25);
            double after = ThrowMath.Clamp(settings.FitWindowAfterSeconds, 0.0, Math.Min(0.10, window));
            double maxWindow = Math.Max(window, ThrowMath.Clamp(settings.MaxFitWindowSeconds, 0.01, 0.5));
            double windowEnd = Math.Min(tRel + after, history.NewestTime);
            double windowStart = windowEnd - window;
            int first, count;
            history.FindRange(windowStart, windowEnd, out first, out count);
            if (count < 3)
            {
                // Too few readings in the window (tracking gap). Pull in slightly older ones, within a limit.
                int end = history.FirstIndexAtOrAfter(windowEnd);
                if (end < history.Count && history[end].Time <= windowEnd) end++;
                int widenedFirst = count > 0 ? first : end;
                int widenedEnd = count > 0 ? first + count : end;
                while (widenedEnd - widenedFirst < 3 && widenedFirst > 0
                       && history[widenedFirst - 1].Time >= tRel - maxWindow)
                {
                    widenedFirst--;
                }
                if (widenedEnd - widenedFirst > count)
                {
                    diag.WindowWidened = true;
                    first = widenedFirst;
                    count = widenedEnd - widenedFirst;
                }
            }

            // 3. Fit the hand's position (for its velocity) and its rotation (for its rate of turning)
            //    over those readings.
            int wantedOrder = settings.Fit == VelocityFit.Curve ? 2 : 1;
            ThrowFit3 handFit = default(ThrowFit3);
            ThrowFit3 rotFit = default(ThrowFit3);
            handFit.Begin(window);
            rotFit.Begin(window);
            for (int i = first; i < first + count; i++)
            {
                PoseSample s = history[i];
                double t = s.Time - tRel;
                Vec3 dp = s.Position - pose.Position;
                handFit.Add(t, dp.X, dp.Y, dp.Z);
                Vec3 turn = ThrowMath.RotationVector(pose.Rotation, s.Rotation);
                rotFit.Add(t, turn.X, turn.Y, turn.Z);
            }

            ThrowVec3D value, slope, accel;
            double rms;
            int order = handFit.Solve(wantedOrder, out value, out slope, out accel, out rms);
            diag.SamplesUsed = count;
            diag.WindowCovered = (float)handFit.Span;
            diag.FitResidual = (float)rms;
            diag.FitUsed = order >= 2 ? VelocityFit.Curve : VelocityFit.Line;

            if (order < 1)
            {
                // One reading, or several at the same instant: a position but no speed. Let the ball go.
                diag.Method = ThrowMethod.PositionOnly;
                result.IsDrop = true;
                FinishRollback(ref result, ref diag, settings, nowTime);
                result.Diagnostics = diag;
                return result;
            }

            Vec3 handVelocity = ThrowMath.FiniteOrZero(slope.ToVec3());

            ThrowVec3D rValue, rSlope, rAccel;
            double rRms;
            int rOrder = rotFit.Solve(order, out rValue, out rSlope, out rAccel, out rRms);
            Vec3 angularVelocity = rOrder >= 1 ? ThrowMath.FiniteOrZero(rSlope.ToVec3()) : default(Vec3);

            // 4. The ball sits out from the wrist, so turning the hand swings it:
            //        v_ball = v_hand + turning x offset
            //    with the offset turned into world space at the release time.
            Vec3 leverVelocity = Vec3.Cross(angularVelocity, lever);
            Vec3 fitVelocity = handVelocity + leverVelocity;
            float fitSpeed = fitVelocity.Length();
            diag.HandVelocity = handVelocity;
            diag.LeverVelocity = leverVelocity;
            diag.FitVelocity = fitVelocity;
            diag.FitSpeed = fitSpeed;
            result.AngularVelocity = angularVelocity;

            // 5. Cross-check against the fastest moment of the swing.
            float throwSpeed = fitSpeed;
            diag.Method = ThrowMethod.FitOnly;
            if (settings.UsePeakSpeed)
            {
                double search = ThrowMath.Clamp(settings.PeakSearchSeconds, 0.0, 0.3);
                double smooth = ThrowMath.Clamp(settings.PeakSmoothingSeconds, 0.002, 0.05);
                SpeedPeak peak = ThrowMath.FindSpeedPeak(history, tRel - search, tRel, settings.BallLocalOffset, smooth);
                if (peak.Found)
                {
                    diag.PeakFound = true;
                    diag.PeakTime = peak.Time;
                    diag.PeakVelocity = peak.Velocity;
                    diag.PeakSpeed = peak.Speed;
                    diag.AngleBetweenMethods = MathUtil.AngleBetween(fitVelocity, peak.Velocity);
                    bool agree = fitSpeed > 1e-4f && peak.Speed > 1e-4f
                                 && diag.AngleBetweenMethods <= settings.PeakAgreeAngle;
                    if (agree)
                    {
                        float boostLimit = fitSpeed * MathF.Max(1f, settings.MaxPeakBoost);
                        throwSpeed = MathF.Min(peak.Speed, boostLimit);
                        diag.Method = ThrowMethod.FitDirectionPeakSpeed;
                    }
                }
            }

            // 6. Drops and tiny tosses: fade the throw help in with hand speed, so a let-go stays a let-go.
            float dropSpeed = MathF.Max(0f, settings.DropSpeed);
            float fullSpeed = MathF.Max(dropSpeed + 1e-3f, settings.FullThrowSpeed);
            float throwness = MathUtil.SmoothStep(dropSpeed, fullSpeed, fitSpeed);
            float scale = ThrowMath.Clamp(settings.SpeedScale, 0.5f, 2f);
            float speed = MathUtil.Lerp(fitSpeed, throwSpeed * scale, throwness);
            float maxSpeed = MathF.Max(0f, settings.MaxSpeed);
            if (speed > maxSpeed)
            {
                speed = maxSpeed;
                diag.SpeedClamped = true;
            }
            diag.Throwness = throwness;
            result.IsDrop = fitSpeed < dropSpeed || fitSpeed < 1e-4f;

            Vec3 direction = MathUtil.SafeNormalize(fitVelocity, default(Vec3));
            result.ReleaseVelocity = ThrowMath.FiniteOrZero(direction * speed);

            // 7. Catch up: where would the ball be by now, had it left the hand on time?
            FinishRollback(ref result, ref diag, settings, nowTime);
            result.Diagnostics = diag;
            return result;
        }

        private static void FinishRollback(ref ThrowResult result, ref ThrowDiagnostics diag, ThrowSettings settings, double nowTime)
        {
            double flown = nowTime - result.ReleaseTime;
            if (double.IsNaN(flown) || flown < 0.0) flown = 0.0;
            double limit = ThrowMath.Clamp(settings.MaxRollbackSeconds, 0.0, 1.0);
            if (flown > limit)
            {
                flown = limit;
                diag.RollbackClamped = true;
            }
            float t = (float)flown;
            Vec3 g = ThrowMath.FiniteOrZero(settings.Gravity);
            diag.RollbackSeconds = t;
            result.NowPosition = Ballistics.PositionAt(result.ReleasePosition, result.ReleaseVelocity, g, t);
            result.NowVelocity = Ballistics.VelocityAt(result.ReleaseVelocity, g, t);
        }
    }
}
