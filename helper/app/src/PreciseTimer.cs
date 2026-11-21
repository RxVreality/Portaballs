using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Portaballs.Helper
{
    /// <summary>
    /// Waits until an exact moment on the Stopwatch clock.
    ///
    /// Thread.Sleep on Windows is only good to a millisecond or so (15 ms on a quiet PC), which is
    /// hopeless for a 2 ms beat. This sleeps on a Windows "high resolution" waitable timer until just
    /// before the moment, then spins for the last stretch. It changes no system-wide setting.
    /// </summary>
    internal sealed class PreciseTimer : IDisposable
    {
        private const uint CreateWaitableTimerHighResolution = 0x00000002;
        private const uint TimerAllAccess = 0x001F0003;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string name, uint flags, uint access);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr argument, bool resume);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("avrt.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

        [DllImport("avrt.dll", SetLastError = true)]
        private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

        /// <summary>
        /// Put the calling thread in Windows' "Pro Audio" scheduling class (the multimedia class
        /// scheduler, the same thing audio programs use to avoid drop-outs). Returns a handle for
        /// <see cref="EndBoost"/>, or zero when Windows declines.
        /// </summary>
        public static IntPtr BoostThisThread()
        {
            try
            {
                uint index = 0;
                return AvSetMmThreadCharacteristicsW("Pro Audio", ref index);
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        public static void EndBoost(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            try { AvRevertMmThreadCharacteristics(handle); } catch (Exception) { }
        }

        private IntPtr _timer;

        public PreciseTimer()
        {
            try
            {
                _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            }
            catch (Exception)
            {
                _timer = IntPtr.Zero;
            }
        }

        /// <summary>False on a Windows too old to have the timer; waits then fall back to Sleep(1) plus spinning.</summary>
        public bool HighResolution { get { return _timer != IntPtr.Zero; } }

        /// <summary>
        /// Return as close as possible to (and not before) <paramref name="targetTicks"/> on the Stopwatch clock.
        /// The last <paramref name="spinTicks"/> are spent spinning rather than asleep.
        /// </summary>
        public void WaitUntil(long targetTicks, long spinTicks)
        {
            long now = Stopwatch.GetTimestamp();
            long sleepTicks = targetTicks - spinTicks - now;
            if (sleepTicks > 0)
            {
                if (_timer != IntPtr.Zero)
                {
                    // Relative due time, in 100 ns units, negative.
                    long due = -(sleepTicks * 10000000L / Stopwatch.Frequency);
                    if (due < 0 && SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                    {
                        WaitForSingleObject(_timer, 1000);
                    }
                }
                else
                {
                    long oneMs = Stopwatch.Frequency / 1000;
                    while (targetTicks - spinTicks - Stopwatch.GetTimestamp() > oneMs * 2) Thread.Sleep(1);
                }
            }
            while (Stopwatch.GetTimestamp() < targetTicks) Thread.SpinWait(4);
        }

        public void Dispose()
        {
            if (_timer != IntPtr.Zero)
            {
                CloseHandle(_timer);
                _timer = IntPtr.Zero;
            }
        }
    }
}
