using System.Diagnostics;

namespace Portaballs;

/// <summary>
/// The clock the helper program stamps its samples with, read from inside the game.
///
/// It is Windows' PC-wide performance counter turned into seconds. Every program on this PC that
/// asks gets the same number, so a time read here can be compared directly with a time the helper sent.
/// </summary>
public static class HelperClock
{
	/// <summary>Seconds on the shared clock, right now.</summary>
	public static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
}
