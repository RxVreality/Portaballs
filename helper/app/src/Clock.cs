using System.Diagnostics;

namespace Portaballs.Helper
{
    /// <summary>
    /// The one clock everything is stamped with: Stopwatch.GetTimestamp() turned into seconds.
    /// On Windows this is the PC-wide performance counter, so the game's own Stopwatch reads the same
    /// numbers and can compare its times with the helper's directly.
    /// </summary>
    internal static class Clock
    {
        public static readonly long TicksPerSecond = Stopwatch.Frequency;

        public static long Ticks { get { return Stopwatch.GetTimestamp(); } }

        public static double Now { get { return ToSeconds(Stopwatch.GetTimestamp()); } }

        public static double ToSeconds(long ticks)
        {
            return (double)ticks / TicksPerSecond;
        }

        public static long ToTicks(double seconds)
        {
            return (long)(seconds * TicksPerSecond);
        }
    }
}
