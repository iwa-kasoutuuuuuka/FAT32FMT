using Fat32Fmt.Core;
using Fat32Fmt.Core.Fat32;
using Fat32Fmt.Core.IO;
using Xunit;

namespace Fat32Fmt.Tests;

public class ProgressTrackerTests
{
    private sealed class Collector : IProgress<OperationProgress>
    {
        public readonly List<OperationProgress> Items = new();
        public void Report(OperationProgress value) => Items.Add(value);
    }

    [Fact]
    public void FirstReport_DoesNotOverflow_AndIsDelivered()
    {
        // 回帰: _lastReport が TimeSpan.MinValue だと "Elapsed - _lastReport" が OverflowException になり、
        // 進捗コールバック付きのフォーマットが最初の 4 MB 書き込み直後に失敗していた。
        var c = new Collector();
        var t = new ProgressTracker(c, "test", 100);
        t.Report(10);
        t.Complete();
        Assert.True(c.Items.Count >= 2);
        Assert.Equal(0, c.Items[0].Done);
        Assert.Equal(100, c.Items[^1].Done);
        Assert.Equal(1.0, c.Items[^1].Fraction);
    }

    [Fact]
    public void Format_WithProgressCallback_Succeeds()
    {
        const long size = 40L << 30;
        using var dev = new StreamBlockDevice(new SparseMemoryStream(size), 512);
        var layout = Fat32LayoutCalculator.Calculate(size, 512, 32768, 1 << 20, 16 << 20);
        var c = new Collector();
        Fat32Formatter.Format(dev, layout, new Fat32FormatOptions(), c);
        Assert.NotEmpty(c.Items);
        Assert.Contains(c.Items, p => p.Fraction >= 1.0);
    }
}
