using Fat32Fmt.Core.Fat32;
using Fat32Fmt.Core.IO;
using Xunit;

namespace Fat32Fmt.Tests;

/// <summary>フルフォーマットの不良ブロック処理（実機で 1 回の失敗に 83 秒かかるカードで固まった件の回帰テスト）。</summary>
public class FullFormatBadBlockTests
{
    private const long GiB = 1L << 30;

    /// <summary>指定範囲への I/O を失敗させるデバイス。失敗までの遅延も指定できる。</summary>
    private sealed class FaultyDevice(IBlockDevice inner, long badStart, long badLength, TimeSpan failDelay) : IBlockDevice
    {
        public int Calls;
        public long Length => inner.Length;
        public int SectorSize => inner.SectorSize;
        public bool CanWrite => true;

        private void Check(long offset, int length)
        {
            Calls++;
            if (offset < badStart + badLength && badStart < offset + length)
            {
                if (failDelay > TimeSpan.Zero) Thread.Sleep(failDelay);
                throw new DeviceIOException("bad block", offset, length);
            }
        }

        public void Read(long offset, Span<byte> buffer) { Check(offset, buffer.Length); inner.Read(offset, buffer); }
        public void Write(long offset, ReadOnlySpan<byte> buffer) { Check(offset, buffer.Length); inner.Write(offset, buffer); }
        public void Flush() => inner.Flush();
        public void Dispose() => inner.Dispose();
    }

    private static Fat32Layout Layout(long size) => Fat32LayoutCalculator.Calculate(size, 512, 4096);

    [Fact]
    public void FastFailingSector_IsIsolatedToOneCluster_WithFewRetries()
    {
        var layout = Layout(1 * GiB);
        long badOffset = layout.DataStartOffset + 100L * 1024 * 1024 + 4096 * 7 + 512; // 1 セクタだけ不良
        using var dev = new FaultyDevice(new StreamBlockDevice(new SparseMemoryStream(1 * GiB), 512), badOffset, 512, TimeSpan.Zero);

        var result = Fat32Formatter.Format(dev, layout, new Fat32FormatOptions { FullFormat = true });

        uint expected = (uint)(2 + (badOffset - layout.DataStartOffset) / 4096);
        Assert.Equal(new[] { expected }, result.BadClusters);
        // 二分探索なので、4 MiB 区間（1024 クラスタ）の切り分けは数十回で済む（以前は 1024 回）
        int chunks = (int)((layout.DataBytes + (4 << 20) - 1) / (4 << 20));
        Assert.True(dev.Calls < chunks * 2 + 200, $"calls={dev.Calls}");
    }

    [Fact]
    public void SlowFailingChunk_IsMarkedWhole_WithoutPerClusterRetries()
    {
        var layout = Layout(1 * GiB);
        long badOffset = layout.DataStartOffset + 64L * 1024 * 1024 + 8192;
        using var dev = new FaultyDevice(new StreamBlockDevice(new SparseMemoryStream(1 * GiB), 512), badOffset, 512, TimeSpan.FromMilliseconds(60));
        var opt = new Fat32FormatOptions { FullFormat = true, SlowFailureThreshold = TimeSpan.FromMilliseconds(30) };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = Fat32Formatter.Format(dev, layout, opt);

        Assert.Equal(1024, result.BadClusters.Count); // 4 MiB 区間をまるごと不良に
        uint first = (uint)(2 + 64L * 1024 * 1024 / 4096);
        Assert.Equal(first, result.BadClusters[0]);
        Assert.Equal(first + 1023, result.BadClusters[^1]);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"elapsed={sw.Elapsed}");

        // FAT にも不良として登録され、読み戻しで整合している
        Assert.True(Fat32Fmt.Core.Diagnostics.Fat32Verifier.Verify(dev).Ok);
    }

    [Fact]
    public void ManySlowFailingChunks_AbortWithClearError()
    {
        var layout = Layout(1 * GiB);
        long badStart = layout.DataStartOffset + 32L * 1024 * 1024;
        using var dev = new FaultyDevice(new StreamBlockDevice(new SparseMemoryStream(1 * GiB), 512), badStart, 1 * GiB, TimeSpan.FromMilliseconds(40));
        var opt = new Fat32FormatOptions { FullFormat = true, SlowFailureThreshold = TimeSpan.FromMilliseconds(20), MaxConsecutiveSlowFailures = 4 };

        var ex = Assert.Throws<IOException>(() => Fat32Formatter.Format(dev, layout, opt));
        Assert.Contains("応答しません", ex.Message);
    }
}
