using System;

namespace Portaballs.Core
{
    /// <summary>
    /// The maths for lining up two tracking spaces that describe the same room.
    ///
    /// What it is for: the helper program reports the head and hands in SteamVR's own space; the game
    /// engine reports them in its play-area space. Both are in metres with +Z up, but they can be
    /// turned and shifted against each other. Seeing the SAME device in both at the same moment tells
    /// us the turn-and-shift that carries one onto the other.
    ///
    /// "Source" is the space being converted from (the helper's); "target" is the space being
    /// converted to (the engine's play area).
    /// </summary>
    public static class AlignmentMath
    {
        /// <summary>
        /// The transform T for which T * (source pose) = (target pose): one device seen in both spaces.
        /// </summary>
        public static RigidTransform FromPosePair(Vec3 sourcePosition, Quat sourceRotation, Vec3 targetPosition, Quat targetRotation)
        {
            Quat r = Quat.Normalize(targetRotation * Quat.Conjugate(sourceRotation));
            Vec3 t = targetPosition - Vec3.Transform(sourcePosition, r);
            return new RigidTransform(r, t);
        }

        /// <summary>
        /// Split a rotation into its turn about the up axis (yaw, radians, anticlockwise seen from above)
        /// and how far it tips the up axis over (tilt, radians, never negative).
        /// </summary>
        public static void SplitYawTilt(Quat rotation, out float yaw, out float tilt)
        {
            Vec3 up = Units.Up;
            tilt = MathUtil.AngleBetween(Vec3.Transform(up, rotation), up);

            // The part of the rotation that is a pure turn about Z keeps only the Z and W numbers.
            float z = rotation.Z, w = rotation.W;
            if (z * z + w * w < 1e-12f)
            {
                yaw = 0f;   // tipped a full half turn: yaw has no meaning
                return;
            }
            yaw = WrapAngle(2f * MathF.Atan2(z, w));
        }

        /// <summary>A turn about the up axis.</summary>
        public static Quat YawRotation(float yaw)
        {
            return Quat.CreateFromAxisAngle(Units.Up, yaw);
        }

        /// <summary>The same angle brought into the range -pi..pi.</summary>
        public static float WrapAngle(float angle)
        {
            const float TwoPi = 2f * MathF.PI;
            if (float.IsNaN(angle) || float.IsInfinity(angle)) return 0f;
            angle %= TwoPi;
            if (angle > MathF.PI) angle -= TwoPi;
            if (angle < -MathF.PI) angle += TwoPi;
            return angle;
        }

        /// <summary>The angle between two rotations, radians (0 when they are the same).</summary>
        public static float AngleBetween(Quat a, Quat b)
        {
            float dot = MathF.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);
            if (float.IsNaN(dot)) return 0f;
            if (dot > 1f) dot = 1f;
            return 2f * MathF.Acos(dot);
        }

        /// <summary>
        /// A level version of a transform: only the turn about the up axis is kept, and the shift is
        /// chosen so that the given source point still lands exactly where the full transform put it.
        /// </summary>
        public static RigidTransform YawOnlyAbout(in RigidTransform full, Vec3 pivotSource)
        {
            float yaw, tilt;
            SplitYawTilt(full.Rotation, out yaw, out tilt);
            Quat r = YawRotation(yaw);
            Vec3 landed = full.TransformPoint(pivotSource);
            return new RigidTransform(r, landed - Vec3.Transform(pivotSource, r));
        }

        /// <summary>
        /// Best level turn-and-shift carrying a set of source points onto their target points, using
        /// positions only. It does not care how either space labels a device's own axes, so it is the
        /// cross-check on <see cref="FromPosePair"/>. Needs the points spread out across the floor.
        /// </summary>
        /// <param name="rmsResidual">Typical distance left between a carried source point and its target, metres.</param>
        /// <param name="horizontalSpread">How spread out the source points are across the floor, metres. Below a few
        /// centimetres the turn is not really known.</param>
        /// <returns>False when there are fewer than two points or they all sit on one spot.</returns>
        public static bool SolveYawFromPoints(Vec3[] source, Vec3[] target, int count,
            out RigidTransform transform, out float rmsResidual, out float horizontalSpread)
        {
            transform = RigidTransform.Identity;
            rmsResidual = 0f;
            horizontalSpread = 0f;
            if (source == null || target == null) return false;
            if (count > source.Length) count = source.Length;
            if (count > target.Length) count = target.Length;
            if (count < 2) return false;

            double sx = 0, sy = 0, sz = 0, tx = 0, ty = 0, tz = 0;
            for (int i = 0; i < count; i++)
            {
                sx += source[i].X; sy += source[i].Y; sz += source[i].Z;
                tx += target[i].X; ty += target[i].Y; tz += target[i].Z;
            }
            double inv = 1.0 / count;
            sx *= inv; sy *= inv; sz *= inv; tx *= inv; ty *= inv; tz *= inv;

            double dot = 0, cross = 0, spreadSq = 0;
            for (int i = 0; i < count; i++)
            {
                double ax = source[i].X - sx, ay = source[i].Y - sy;
                double bx = target[i].X - tx, by = target[i].Y - ty;
                dot += ax * bx + ay * by;
                cross += ax * by - ay * bx;
                spreadSq += ax * ax + ay * ay;
            }
            horizontalSpread = (float)Math.Sqrt(spreadSq * inv);
            if (double.IsNaN(dot) || double.IsNaN(cross) || horizontalSpread < 1e-4f) return false;

            float yaw = (float)Math.Atan2(cross, dot);
            Quat r = YawRotation(yaw);
            Vec3 centreSource = new Vec3((float)sx, (float)sy, (float)sz);
            Vec3 centreTarget = new Vec3((float)tx, (float)ty, (float)tz);
            transform = new RigidTransform(r, centreTarget - Vec3.Transform(centreSource, r));

            double sumSq = 0;
            for (int i = 0; i < count; i++)
            {
                sumSq += (transform.TransformPoint(source[i]) - target[i]).LengthSquared();
            }
            rmsResidual = (float)Math.Sqrt(sumSq * inv);
            return true;
        }

        /// <summary>
        /// The fixed offset G between two descriptions of one device, in the device's own axes:
        ///     (target pose) = alignment * (raw pose) * G.
        /// Used for a hand controller: SteamVR's raw pose and the engine's "grip" pose are two points
        /// on the same lump of plastic, a fixed distance and turn apart.
        /// </summary>
        public static RigidTransform LocalOffset(in RigidTransform alignment, Vec3 rawPosition, Quat rawRotation,
            Vec3 targetPosition, Quat targetRotation)
        {
            Vec3 p = alignment.TransformPoint(rawPosition);
            Quat q = alignment.TransformRotation(rawRotation);
            Quat inverse = Quat.Conjugate(q);
            return new RigidTransform(inverse * targetRotation, Vec3.Transform(targetPosition - p, inverse));
        }
    }

    /// <summary>
    /// Says when something has been holding still for long enough to be measured.
    ///
    /// What it is for: the engine's poses are guessed slightly ahead in time (to when the picture will
    /// be shown) and the helper's are "right now". The two only agree when the device is barely
    /// moving, because then a guess ahead changes nothing. So calibration only looks while this says "still".
    /// </summary>
    public struct StillnessGate
    {
        private bool _wasStill;
        private double _stillSince;

        /// <summary>How long it has been still, seconds (0 when moving).</summary>
        public double StillFor { get; private set; }

        /// <summary>True while under both limits, however briefly.</summary>
        public bool IsStill { get { return _wasStill; } }

        /// <param name="time">Now, seconds (any steady clock).</param>
        /// <param name="linearSpeed">How fast it is moving, m/s.</param>
        /// <param name="angularSpeed">How fast it is turning, rad/s.</param>
        /// <returns>True once it has stayed under both limits for at least stillSeconds.</returns>
        public bool Update(double time, float linearSpeed, float angularSpeed, float maxLinearSpeed, float maxAngularSpeed, double stillSeconds)
        {
            bool still = linearSpeed <= maxLinearSpeed && angularSpeed <= maxAngularSpeed;   // false for NaN
            if (!still || double.IsNaN(time))
            {
                _wasStill = false;
                StillFor = 0.0;
                return false;
            }
            if (!_wasStill || time < _stillSince)
            {
                _wasStill = true;
                _stillSince = time;
            }
            StillFor = time - _stillSince;
            return StillFor >= stillSeconds;
        }

        public void Reset()
        {
            _wasStill = false;
            StillFor = 0.0;
        }
    }

    /// <summary>Tunable numbers for <see cref="SpaceAligner"/>.</summary>
    public sealed class SpaceAlignerSettings
    {
        /// <summary>The device counts as still below this speed, m/s. With a 40 ms guess-ahead this allows 2 mm of disagreement.</summary>
        public float MaxLinearSpeed = 0.05f;

        /// <summary>...and below this rate of turning, rad/s (about 11 degrees a second; 0.5 degrees over 40 ms).</summary>
        public float MaxAngularSpeed = 0.20f;

        /// <summary>It must have been still this long before a reading is taken, seconds.</summary>
        public double StillSeconds = 0.15;

        /// <summary>At most one reading is taken per this long, seconds.</summary>
        public double MinIntervalSeconds = 0.05;

        /// <summary>
        /// How much each new reading moves the answer once it has settled (0.05 = one twentieth of the
        /// way). The first readings count for more: reading number n moves it by 1/n until that falls below this.
        /// </summary>
        public float Blend = 0.05f;

        /// <summary>A reading that puts the device further than this from where the current answer puts it is an outlier, metres.</summary>
        public float OutlierDistance = 0.05f;

        /// <summary>...or whose turn differs from the current answer by more than this, radians.</summary>
        public float OutlierAngle = 3f * (MathF.PI / 180f);

        /// <summary>
        /// This many outliers in a row means the spaces really did move against each other (SteamVR was
        /// re-centred, say), and the answer starts again from the newest reading.
        /// </summary>
        public int OutliersToReseed = 12;

        /// <summary>
        /// Both spaces have "up" pointing up, so the true answer is a level turn plus a shift. With this
        /// on, only that is solved for, which stops a small error in the head's tilt from swinging the
        /// far end of the room about.
        /// </summary>
        public bool YawOnly = true;

        /// <summary>
        /// With YawOnly: a reading whose full solution tips "up" over by more than this is refused,
        /// radians. A steady large tilt means the two programs disagree about the device's own axes.
        /// </summary>
        public float MaxTilt = 5f * (MathF.PI / 180f);
    }

    /// <summary>What <see cref="SpaceAligner.Add"/> did with one reading.</summary>
    public enum AlignerOutcome
    {
        /// <summary>Used: the answer was updated.</summary>
        Accepted = 0,

        /// <summary>The device was moving too fast for the two spaces' readings to be compared.</summary>
        Moving = 1,

        /// <summary>Still, but not for long enough yet, or too soon after the last reading.</summary>
        Waiting = 2,

        /// <summary>The reading tipped "up" over too far to be believed.</summary>
        TooTilted = 3,

        /// <summary>Disagreed with the current answer; ignored for now.</summary>
        Outlier = 4,

        /// <summary>Enough outliers in a row: the answer was restarted from this reading.</summary>
        Reseeded = 5,

        /// <summary>A number in the reading was not a real number.</summary>
        BadInput = 6,
    }

    /// <summary>
    /// Works out, and keeps refining, the fixed turn-and-shift between two tracking spaces from one
    /// device (the head) seen in both.
    ///
    /// Feed it every frame with the device's pose in each space and how fast it is moving. It only
    /// uses moments when the device is nearly still, averages them, shrugs off the odd bad reading,
    /// and starts again by itself if the spaces genuinely move against each other.
    /// It never allocates after construction.
    /// </summary>
    public sealed class SpaceAligner
    {
        public SpaceAlignerSettings Settings;

        private StillnessGate _gate;
        private double _lastReadingTime = double.NegativeInfinity;
        private int _weight;
        private int _outlierRun;

        public SpaceAligner(SpaceAlignerSettings settings = null)
        {
            Settings = settings ?? new SpaceAlignerSettings();
            Reset();
        }

        /// <summary>True once at least one reading has been accepted.</summary>
        public bool HasEstimate { get; private set; }

        /// <summary>The answer: carries a source-space point or pose into target space.</summary>
        public RigidTransform Current { get; private set; }

        /// <summary>Readings used so far.</summary>
        public int Accepted { get; private set; }

        /// <summary>Readings ignored for disagreeing with the answer.</summary>
        public int Outliers { get; private set; }

        /// <summary>Readings refused for tipping "up" over.</summary>
        public int RejectedTilt { get; private set; }

        /// <summary>Times the answer was started again.</summary>
        public int Reseeds { get; private set; }

        /// <summary>Tilt of the newest reading's full solution, radians. Near zero when all is well.</summary>
        public float LastTilt { get; private set; }

        /// <summary>Typical distance between where the answer puts the device and where the target space says it is, metres.</summary>
        public float ResidualDistance { get; private set; }

        /// <summary>Typical difference in turn between a reading and the answer, radians.</summary>
        public float ResidualAngle { get; private set; }

        /// <summary>How long the device has currently been still, seconds.</summary>
        public double StillFor { get { return _gate.StillFor; } }

        public void Reset()
        {
            HasEstimate = false;
            Current = RigidTransform.Identity;
            Accepted = 0;
            Outliers = 0;
            RejectedTilt = 0;
            Reseeds = 0;
            LastTilt = 0f;
            ResidualDistance = 0f;
            ResidualAngle = 0f;
            _gate.Reset();
            _lastReadingTime = double.NegativeInfinity;
            _weight = 0;
            _outlierRun = 0;
        }

        /// <param name="time">Now, seconds on any steady clock.</param>
        /// <param name="linearSpeed">The device's speed, m/s: pass the larger of what the two spaces report.</param>
        /// <param name="angularSpeed">Its rate of turning, rad/s: likewise the larger.</param>
        public AlignerOutcome Add(double time, Vec3 sourcePosition, Quat sourceRotation, Vec3 targetPosition, Quat targetRotation,
            float linearSpeed, float angularSpeed)
        {
            if (double.IsNaN(time) || !ThrowMath.IsFinite(sourcePosition) || !ThrowMath.IsFinite(targetPosition)
                || !IsUsable(sourceRotation) || !IsUsable(targetRotation))
            {
                return AlignerOutcome.BadInput;
            }

            SpaceAlignerSettings s = Settings;
            if (!_gate.Update(time, linearSpeed, angularSpeed, s.MaxLinearSpeed, s.MaxAngularSpeed, s.StillSeconds))
            {
                return _gate.IsStill ? AlignerOutcome.Waiting : AlignerOutcome.Moving;
            }
            if (time - _lastReadingTime < s.MinIntervalSeconds && time >= _lastReadingTime) return AlignerOutcome.Waiting;
            _lastReadingTime = time;

            sourceRotation = Quat.Normalize(sourceRotation);
            targetRotation = Quat.Normalize(targetRotation);
            RigidTransform reading = AlignmentMath.FromPosePair(sourcePosition, sourceRotation, targetPosition, targetRotation);
            float yaw, tilt;
            AlignmentMath.SplitYawTilt(reading.Rotation, out yaw, out tilt);
            LastTilt = tilt;
            if (s.YawOnly)
            {
                if (tilt > s.MaxTilt)
                {
                    RejectedTilt++;
                    return AlignerOutcome.TooTilted;
                }
                Quat level = AlignmentMath.YawRotation(yaw);
                reading = new RigidTransform(level, targetPosition - Vec3.Transform(sourcePosition, level));
            }

            if (!HasEstimate)
            {
                Current = reading;
                HasEstimate = true;
                Accepted = 1;
                _weight = 1;
                _outlierRun = 0;
                return AlignerOutcome.Accepted;
            }

            // How far the reading is from the current answer, measured where it matters: at the device.
            Vec3 landed = Current.TransformPoint(sourcePosition);
            float distance = (landed - targetPosition).Length();
            float angle = AlignmentMath.AngleBetween(Current.Rotation, reading.Rotation);
            if (distance > s.OutlierDistance || angle > s.OutlierAngle)
            {
                Outliers++;
                _outlierRun++;
                if (_outlierRun >= Math.Max(1, s.OutliersToReseed))
                {
                    Current = reading;
                    Reseeds++;
                    Accepted++;
                    _weight = 1;
                    _outlierRun = 0;
                    ResidualDistance = 0f;
                    ResidualAngle = 0f;
                    return AlignerOutcome.Reseeded;
                }
                return AlignerOutcome.Outlier;
            }
            _outlierRun = 0;

            // Move the answer part of the way toward the reading. The turn is blended directly; the shift
            // is then chosen so the device lands part of the way toward where the reading puts it.
            _weight++;
            float blend = MathF.Max(1f / _weight, MathUtil.Saturate(s.Blend));
            Quat r = Quat.Normalize(Quat.Slerp(Current.Rotation, reading.Rotation, blend));
            Vec3 aim = landed + (targetPosition - landed) * blend;
            Current = new RigidTransform(r, aim - Vec3.Transform(sourcePosition, r));
            Accepted++;

            ResidualDistance += (distance - ResidualDistance) * 0.1f;
            ResidualAngle += (angle - ResidualAngle) * 0.1f;
            return AlignerOutcome.Accepted;
        }

        private static bool IsUsable(Quat q)
        {
            float lenSq = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
            return !(float.IsNaN(lenSq) || float.IsInfinity(lenSq) || lenSq < 1e-12f);
        }
    }

    /// <summary>
    /// A running average of a small fixed offset (see <see cref="AlignmentMath.LocalOffset"/>), fed
    /// only with readings taken while the device is still. Like <see cref="SpaceAligner"/> it ignores
    /// the odd reading that disagrees and starts again if they keep disagreeing.
    /// </summary>
    public sealed class RigidOffsetAverager
    {
        /// <summary>How much each new reading moves the answer once settled.</summary>
        public float Blend = 0.05f;

        /// <summary>
        /// Offsets longer than this are refused outright, metres. Two points on one controller are never
        /// this far apart, so a longer one means the two readings are not of the same device (left and
        /// right swapped, for instance).
        /// </summary>
        public float MaxDistance = 0.25f;

        /// <summary>A reading this far from the current answer is an outlier, metres / radians.</summary>
        public float OutlierDistance = 0.03f;
        public float OutlierAngle = 8f * (MathF.PI / 180f);

        /// <summary>This many outliers in a row restart the answer.</summary>
        public int OutliersToReseed = 12;

        private int _weight;
        private int _outlierRun;

        public bool HasEstimate { get; private set; }

        /// <summary>The answer. Translation is the offset's position, Rotation its turn, both in the device's own axes.</summary>
        public RigidTransform Current { get; private set; } = RigidTransform.Identity;

        public int Accepted { get; private set; }

        /// <summary>Readings refused for being impossibly long.</summary>
        public int RejectedTooFar { get; private set; }

        public int Outliers { get; private set; }

        public int Reseeds { get; private set; }

        public void Reset()
        {
            HasEstimate = false;
            Current = RigidTransform.Identity;
            Accepted = 0;
            RejectedTooFar = 0;
            Outliers = 0;
            Reseeds = 0;
            _weight = 0;
            _outlierRun = 0;
        }

        /// <summary>Take one reading. Returns true when it was used.</summary>
        public bool Add(in RigidTransform reading)
        {
            if (!ThrowMath.IsFinite(reading.Translation)) return false;
            if (reading.Translation.Length() > MaxDistance)
            {
                RejectedTooFar++;
                return false;
            }

            if (!HasEstimate)
            {
                Current = reading;
                HasEstimate = true;
                Accepted = 1;
                _weight = 1;
                _outlierRun = 0;
                return true;
            }

            float distance = (reading.Translation - Current.Translation).Length();
            float angle = AlignmentMath.AngleBetween(reading.Rotation, Current.Rotation);
            if (distance > OutlierDistance || angle > OutlierAngle)
            {
                Outliers++;
                _outlierRun++;
                if (_outlierRun >= Math.Max(1, OutliersToReseed))
                {
                    Current = reading;
                    Reseeds++;
                    Accepted++;
                    _weight = 1;
                    _outlierRun = 0;
                    return true;
                }
                return false;
            }
            _outlierRun = 0;

            _weight++;
            float blend = MathF.Max(1f / _weight, MathUtil.Saturate(Blend));
            Quat r = Quat.Normalize(Quat.Slerp(Current.Rotation, reading.Rotation, blend));
            Vec3 t = Current.Translation + (reading.Translation - Current.Translation) * blend;
            Current = new RigidTransform(r, t);
            Accepted++;
            return true;
        }
    }
}
