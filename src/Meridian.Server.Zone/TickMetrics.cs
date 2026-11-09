using System.Globalization;

namespace Meridian.Server.Zone;

/// <summary>One reporting window of tick timings.</summary>
public sealed record TickReport(
    int Ticks,
    double SecondsElapsed,
    double AchievedHz,
    double WorkP50Ms,
    double WorkP99Ms,
    double WorkMaxMs,
    int Overruns,
    int Players,
    long BytesOut)
{
    public string ToLogLine() => string.Create(
        CultureInfo.InvariantCulture,
        $"METRICS players={Players} hz={AchievedHz:F2} work_p50_ms={WorkP50Ms:F3} work_p99_ms={WorkP99Ms:F3} work_max_ms={WorkMaxMs:F3} overruns={Overruns} out_kbps={BytesOut / SecondsElapsed / 1024:F1}");
}

/// <summary>Collects per-tick work time and produces percentile reports.</summary>
public sealed class TickMetrics
{
    private readonly List<double> _work = [];
    private int _overruns;
    private long _bytesOut;
    private long _windowStart;

    public TickMetrics(long nowTimestamp) => _windowStart = nowTimestamp;

    public void RecordTick(double workMs, bool overran)
    {
        _work.Add(workMs);
        if (overran)
        {
            _overruns++;
        }
    }

    public void RecordBytesOut(int bytes) => _bytesOut += bytes;

    public TickReport Report(long nowTimestamp, int players)
    {
        var seconds = (nowTimestamp - _windowStart) / (double)System.Diagnostics.Stopwatch.Frequency;
        _work.Sort();
        var report = new TickReport(
            _work.Count,
            seconds,
            seconds > 0 ? _work.Count / seconds : 0,
            Percentile(0.50),
            Percentile(0.99),
            _work.Count > 0 ? _work[^1] : 0,
            _overruns,
            players,
            _bytesOut);
        _work.Clear();
        _overruns = 0;
        _bytesOut = 0;
        _windowStart = nowTimestamp;
        return report;
    }

    private double Percentile(double p)
    {
        if (_work.Count == 0)
        {
            return 0;
        }
        var index = (int)Math.Ceiling(p * _work.Count) - 1;
        return _work[Math.Clamp(index, 0, _work.Count - 1)];
    }
}
