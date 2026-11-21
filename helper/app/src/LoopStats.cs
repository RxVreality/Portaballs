using System;
using System.Globalization;

namespace Portaballs.Helper
{
    /// <summary>A summary of a run of time gaps (or any durations), all in seconds.</summary>
    internal struct StatsSummary
    {
        public long Count;
        public double Mean;
        public double StdDev;
        public double Min;
        public double P50;
        public double P99;
        public double P999;
        public double Max;

        /// <summary>How many per second, judged by the mean gap.</summary>
        public double Rate { get { return Mean > 0.0 ? 1.0 / Mean : 0.0; } }

        /// <summary>"n=4999 rate=500.0/s mean=2000.0us sd=3.1us p50=2000.0 p99=2008.1 p99.9=2040.3 max=2101.0us"</summary>
        public string Describe()
        {
            if (Count == 0) return "n=0";
            return string.Format(CultureInfo.InvariantCulture,
                "n={0} rate={1:0.0}/s mean={2:0.0}us sd={3:0.0}us min={4:0.0} p50={5:0.0} p99={6:0.0} p99.9={7:0.0} max={8:0.0}us",
                Count, Rate, Mean * 1e6, StdDev * 1e6, Min * 1e6, P50 * 1e6, P99 * 1e6, P999 * 1e6, Max * 1e6);
        }

        /// <summary>The same for a duration rather than a gap (no rate).</summary>
        public string DescribeDuration()
        {
            if (Count == 0) return "n=0";
            return string.Format(CultureInfo.InvariantCulture,
                "mean={0:0.0}us p50={1:0.0} p99={2:0.0} p99.9={3:0.0} max={4:0.0}us",
                Mean * 1e6, P50 * 1e6, P99 * 1e6, P999 * 1e6, Max * 1e6);
        }
    }

    /// <summary>
    /// Collects durations without allocating: a histogram of one-microsecond bins up to 50 ms, plus
    /// exact count, sum, smallest and largest. Not thread safe: one writer, read it from the same thread
    /// (or after the writer has stopped).
    /// </summary>
    internal sealed class LoopStats
    {
        private const int Bins = 50000;
        private const double BinSeconds = 1e-6;

        private readonly int[] _bins = new int[Bins + 1];
        private long _count;
        private double _sum;
        private double _sumSquares;
        private double _min = double.MaxValue;
        private double _max;

        public long Count { get { return _count; } }

        public void Add(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0.0) return;
            int bin = (int)(seconds / BinSeconds);
            if (bin > Bins) bin = Bins;
            _bins[bin]++;
            _count++;
            _sum += seconds;
            _sumSquares += seconds * seconds;
            if (seconds < _min) _min = seconds;
            if (seconds > _max) _max = seconds;
        }

        public void Reset()
        {
            Array.Clear(_bins, 0, _bins.Length);
            _count = 0;
            _sum = 0.0;
            _sumSquares = 0.0;
            _min = double.MaxValue;
            _max = 0.0;
        }

        public StatsSummary Summarize()
        {
            StatsSummary s = new StatsSummary();
            s.Count = _count;
            if (_count == 0) return s;
            s.Mean = _sum / _count;
            double variance = _sumSquares / _count - s.Mean * s.Mean;
            s.StdDev = variance > 0.0 ? Math.Sqrt(variance) : 0.0;
            s.Min = _min;
            s.Max = _max;
            s.P50 = Percentile(0.50);
            s.P99 = Percentile(0.99);
            s.P999 = Percentile(0.999);
            return s;
        }

        private double Percentile(double fraction)
        {
            long wanted = (long)Math.Ceiling(fraction * _count);
            if (wanted < 1) wanted = 1;
            long seen = 0;
            for (int i = 0; i <= Bins; i++)
            {
                seen += _bins[i];
                if (seen >= wanted)
                {
                    double value = (i + 0.5) * BinSeconds;
                    if (value > _max) value = _max;
                    if (value < _min) value = _min;
                    return value;
                }
            }
            return _max;
        }
    }
}
