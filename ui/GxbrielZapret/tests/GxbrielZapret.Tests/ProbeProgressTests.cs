using System.Collections.Concurrent;
using GxbrielZapret.Services.Strategies;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class ProbeProgressTests
{
    [Fact]
    public async Task WaitingProducesHeartbeatsAndDisposalStopsThem()
    {
        var reports = new ConcurrentQueue<string>();
        var progress = new ProbeProgress(reports.Enqueue);
        progress.Stage("Видеоданные YouTube · замер 2/3", 25);
        progress.Update("Ожидание первых данных");
        await Task.Delay(1200);
        Assert.Contains(reports, text => text.Contains("1 с") && text.Contains("24 с") && text.Contains("Ожидание первых данных"));
        progress.Dispose();
        int count = reports.Count;
        await Task.Delay(1100);
        Assert.Equal(count, reports.Count);
    }
}
