using System.Diagnostics;
using Meridian.Server.Zone;

namespace Meridian.Server.Tests;

public class TickMetricsTests
{
    [Fact]
    public void A_window_reports_percentiles_and_every_sample_then_resets()
    {
        var m = new TickMetrics(0);
        foreach (var ms in new[] { 2.0, 1.0, 9.5, 1.5 })
        {
            m.RecordTick(ms, overran: false);
        }
        var r = m.Report(Stopwatch.Frequency, players: 7);
        Assert.Equal(4, r.Ticks);
        Assert.Equal(9.5, r.WorkMaxMs);
        Assert.Equal("TICKS players=7 work_us=1000,1500,2000,9500", r.ToSamplesLine());

        m.RecordTick(0.25, overran: true);
        var next = m.Report(2 * Stopwatch.Frequency, players: 7);
        Assert.Equal([250], next.WorkMicros);
        Assert.Equal(1, next.Overruns);
    }
}
