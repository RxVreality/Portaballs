using System.Globalization;
using System.Text;

namespace Portaballs.Core
{
    /// <summary>
    /// Writes link messages as text. The helper uses this; the game only needs <see cref="LinkDecoder"/>.
    ///
    /// The format, so it can be read by eye in a log:
    ///   A message is one or more lines ending in '\n'. Fields on a line are separated by one space.
    ///   The first field says what the line is. Numbers are written so they read back to exactly the
    ///   same value, always with '.' as the decimal point whatever the PC's language settings.
    ///
    ///   H version source pollHz batchHz time                       hello, sent once to each new listener
    ///   I time state sampleCount overruns detail                   status, about once a second
    ///   B sequence sendTime count                                  a batch: the next 'count' lines are samples
    ///   S index time HEAD LEFT RIGHT GAZE                          one sample (60 fields in all)
    ///
    ///   HEAD        = flags px py pz qx qy qz qw vx vy vz wx wy wz          (14 numbers)
    ///   LEFT, RIGHT = the same 14, then grip gripTime trigger triggerTime   (18 numbers)
    ///   GAZE        = flags ox oy oz dx dy dz                               (7 numbers)
    ///
    /// Lines that start with any other letter are for later versions; readers skip them.
    /// </summary>
    public static class LinkEncoder
    {
        public static void AppendHello(StringBuilder sb, LinkHello hello)
        {
            sb.Append('H');
            Field(sb, hello.Version);
            Word(sb, hello.Source);
            Field(sb, hello.PollHz);
            Field(sb, hello.BatchHz);
            Field(sb, hello.Time);
            sb.Append(LinkProtocol.LineSeparator);
        }

        public static void AppendStatus(StringBuilder sb, LinkStatus status)
        {
            sb.Append('I');
            Field(sb, status.Time);
            Field(sb, status.State);
            Field(sb, status.SampleCount);
            Field(sb, status.Overruns);
            Word(sb, status.Detail);
            sb.Append(LinkProtocol.LineSeparator);
        }

        /// <summary>Starts a batch. Follow it with exactly <paramref name="count"/> calls to <see cref="AppendSample"/>.</summary>
        public static void AppendBatchHeader(StringBuilder sb, long sequence, double sendTime, int count)
        {
            sb.Append('B');
            Field(sb, sequence);
            Field(sb, sendTime);
            Field(sb, count);
            sb.Append(LinkProtocol.LineSeparator);
        }

        public static void AppendSample(StringBuilder sb, LinkSample sample)
        {
            sb.Append('S');
            Field(sb, sample.Index);
            Field(sb, sample.Time);
            Pose(sb, sample.Head);
            Hand(sb, sample.Left);
            Hand(sb, sample.Right);
            Field(sb, sample.Gaze.Flags);
            Vector(sb, sample.Gaze.Origin);
            Vector(sb, sample.Gaze.Direction);
            sb.Append(LinkProtocol.LineSeparator);
        }

        private static void Hand(StringBuilder sb, LinkHand hand)
        {
            Pose(sb, hand.Pose);
            Field(sb, hand.Grip);
            Field(sb, hand.GripTime);
            Field(sb, hand.Trigger);
            Field(sb, hand.TriggerTime);
        }

        private static void Pose(StringBuilder sb, LinkPose pose)
        {
            Field(sb, pose.Flags);
            Vector(sb, pose.Position);
            Field(sb, pose.Rotation.X);
            Field(sb, pose.Rotation.Y);
            Field(sb, pose.Rotation.Z);
            Field(sb, pose.Rotation.W);
            Vector(sb, pose.Velocity);
            Vector(sb, pose.AngularVelocity);
        }

        private static void Vector(StringBuilder sb, Vec3 v)
        {
            Field(sb, v.X);
            Field(sb, v.Y);
            Field(sb, v.Z);
        }

        private static void Field(StringBuilder sb, float value)
        {
            sb.Append(LinkProtocol.FieldSeparator);
            sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void Field(StringBuilder sb, double value)
        {
            sb.Append(LinkProtocol.FieldSeparator);
            sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void Field(StringBuilder sb, int value)
        {
            sb.Append(LinkProtocol.FieldSeparator);
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void Field(StringBuilder sb, long value)
        {
            sb.Append(LinkProtocol.FieldSeparator);
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>A short piece of text as one field: spaces and line ends become '_', empty becomes '-'.</summary>
        private static void Word(StringBuilder sb, string text)
        {
            sb.Append(LinkProtocol.FieldSeparator);
            if (string.IsNullOrEmpty(text))
            {
                sb.Append('-');
                return;
            }
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                sb.Append(c <= ' ' ? '_' : c);
            }
        }
    }
}
