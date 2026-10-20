using Fat32Fmt.Core;
using Fat32Fmt.Core.Diagnostics;
using Fat32Fmt.Core.Formatting;
using Fat32Fmt.Core.Imaging;
using Fat32Fmt.Core.IO;
using Xunit;

namespace Fat32Fmt.Tests;

public class EraserTests
{
    private const long MiB = 1L << 20, GiB = 1L << 30;

    private static StreamBlockDevice FilledDevice(long size)
    {
        var dev = new StreamBlockDevice(new SparseMemoryStream(size), 512);
        var junk = new byte[1 * MiB];
        new Random(7).NextBytes(junk);
        for (long off = 0; off < size; off += 8 * MiB) dev.Write(off, junk);
        return dev;
    }

    private static bool AllZero(IBlockDevice dev)
    {
        var buf = new byte[4 * MiB];
        for (long off = 0; off < dev.Length; off += buf.Length)
        {
            int n = (int)Math.Min(buf.Length, dev.Length - off);
            dev.Read(off, buf.AsSpan(0, n));
            if (buf.AsSpan(0, n).IndexOfAnyExcept((byte)0) >= 0) return false;
        }
        return true;
    }

    [Fact]
    public void ZeroWipe_ClearsEverything_AndVerifies()
    {
        using var dev = FilledDevice(64 * MiB);
        var phases = new List<string>();
        var r = SecureEraser.Wipe(dev, new WipeOptions { Verify = true }, new SyncProgress(p => phases.Add(p.Phase)));

        Assert.True(AllZero(dev));
        Assert.True(r.Verified);
        Assert.Equal(64 * MiB, r.Bytes);
        Assert.Contains("上書き (0)", phases);
        Assert.Contains("読み戻し確認", phases);
    }

    [Fact]
    public void RandomWipe_MultiplePasses_OverwritesWithNonZero_AndVerifies()
    {
        using var dev = FilledDevice(32 * MiB);
        string before = DiskImager.Hash(dev, dev.Length);
        var phases = new HashSet<string>();
        var r = SecureEraser.Wipe(dev, new WipeOptions { Passes = 3, Pattern = WipePattern.Random, Verify = true },
            new SyncProgress(p => phases.Add(p.Phase)));

        Assert.Equal(3, r.Passes);
        Assert.Contains("上書き 1/3 (乱数)", phases);
        Assert.Contains("上書き 3/3 (乱数)", phases);
        Assert.NotEqual(before, DiskImager.Hash(dev, dev.Length));
        var sector = new byte[512];
        dev.Read(dev.Length - 512, sector);
        Assert.True(sector.Count(b => b == 0) < 32, "乱数で埋まっていない");
    }

    [Theory]
    [InlineData(WipePattern.Zero)]
    [InlineData(WipePattern.Random)]
    public void Verify_DetectsFakeCapacity(WipePattern pattern)
    {
        // 書いたつもりの後半が前半に折り返すので、乱数なら前半が後半の内容で上書きされて照合が合わない。
        // 0 埋めは折り返しても 0 なので、乱数で埋めたあと 0 埋めしても検出できないことを確認する(既知の限界)
        using var fake = new WrappingDevice(new StreamBlockDevice(new SparseMemoryStream(64 * MiB), 512), realBytes: 16 * MiB);
        var opt = new WipeOptions { Pattern = pattern, Verify = true };
        if (pattern == WipePattern.Random)
        {
            var ex = Assert.Throws<DataVerificationException>(() => SecureEraser.Wipe(fake, opt));
            Assert.True(ex.Offset >= 0);
        }
        else
        {
            Assert.True(SecureEraser.Wipe(fake, opt).Verified);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(SecureEraser.MaxPasses + 1)]
    public void InvalidPassCount_Throws(int passes)
    {
        using var dev = new StreamBlockDevice(new SparseMemoryStream(1 * MiB), 512);
        Assert.Throws<ArgumentOutOfRangeException>(() => SecureEraser.Wipe(dev, new WipeOptions { Passes = passes }));
    }

    [Fact]
    public void Wipe_IsCancellable()
    {
        using var dev = FilledDevice(64 * MiB);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => SecureEraser.Wipe(dev, new WipeOptions(), ct: cts.Token));
    }

    [Fact]
    public void WipeThenInitialize_ProducesVerifiableMbrFat32Card()
    {
        using var dev = FilledDevice(1 * GiB);
        SecureEraser.Wipe(dev, new WipeOptions());
        var init = DiskInitializer.InitializeMbrFat32(dev, "WIPED");

        var vol = Fat32Verifier.LocateVolume(dev, out long offset);
        Assert.Equal(init.Partition.StartBytes, offset);
        var report = Fat32Verifier.Verify(vol);
        Assert.True(report.Ok, string.Join(" / ", report.Errors));
        Assert.Equal("WIPED", report.Label);
    }

    [Fact]
    public void Initialize_64GiB_AlignsPartitionToBoundaryUnit()
    {
        using var dev = new StreamBlockDevice(new SparseMemoryStream(64 * GiB), 512);
        var init = DiskInitializer.InitializeMbrFat32(dev);
        Assert.Equal(0, init.Partition.StartBytes % (16 * MiB));
        Assert.Equal(32768, init.Format.Layout.BytesPerCluster);
        Assert.True(Fat32Verifier.Verify(Fat32Verifier.LocateVolume(dev, out _)).Ok);
    }
}

internal sealed class SyncProgress(Action<OperationProgress> cb) : IProgress<OperationProgress>
{
    public void Report(OperationProgress value) => cb(value);
}
