using System.Text.Json;
using Fat32Fmt.Cli;
using Fat32Fmt.Core.Diagnostics;
using Fat32Fmt.Core.IO;
using Fat32Fmt.Core.Windows;
using Xunit;

namespace Fat32Fmt.Tests;

public class DiagnosticsTests
{
    private const int KiB = 1024, MiB = 1 << 20;
    private const long GiB = 1L << 30;

    private static StreamBlockDevice Fake(long reported, int real, FakeCardStream.Behavior b) =>
        new(new FakeCardStream(reported, real, b), 512);

    // ---- パターン ---------------------------------------------------------------

    [Fact]
    public void Pattern_IsDeterministicAndPositionDependent()
    {
        var p = new TestPattern(42);
        var a = new byte[1024];
        var b = new byte[1024];
        p.Fill(4096, a);
        p.Fill(4096, b);
        Assert.Equal(a, b);
        Assert.NotEqual(a.AsSpan(0, 512).ToArray(), a.AsSpan(512, 512).ToArray());

        p.Fill(8192, b);
        Assert.NotEqual(a, b);
        new TestPattern(43).Fill(4096, b);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Pattern_IdentifiesSourceOffset()
    {
        var p = new TestPattern(7);
        var block = new byte[512];
        p.Fill(123 * 512L, block);
        Assert.True(p.TryIdentify(block, out long src));
        Assert.Equal(123 * 512L, src);

        Assert.False(new TestPattern(8).TryIdentify(block, out _)); // 別の実行（種）のデータは認めない
        block[100] ^= 1;
        Assert.False(p.TryIdentify(block, out _));
        Assert.False(p.TryIdentify(new byte[512], out _));
    }

    [Fact]
    public void RangeCollector_MergesAdjacentAndCapsList()
    {
        var c = new RangeCollector(maxRanges: 2);
        c.Add(0, 512);
        c.Add(512, 512);
        c.Add(4096, 512);
        c.Add(8192, 512);
        Assert.Equal(3, c.Count);
        Assert.Equal(2048, c.TotalBytes);
        Assert.Equal(new[] { new ByteRange(0, 1024), new ByteRange(4096, 512) }, c.Ranges);
        Assert.Equal(0L, c.FirstOffset);
    }

    // ---- 容量偽装チェック ---------------------------------------------------------

    [Fact]
    public void FullTest_GenuineDevice_IsGenuine()
    {
        using var dev = new StreamBlockDevice(new MemoryStream(new byte[32 * MiB]), 512);
        var r = CapacityTester.Run(dev, new CapacityTestOptions { Mode = CapacityTestMode.Full, Seed = 1 });
        Assert.Equal(CapacityVerdict.Genuine, r.Verdict);
        Assert.True(r.Completed);
        Assert.Equal(32L * MiB, r.OkBytes);
        Assert.Equal(0, r.BadBytes);
        Assert.Equal(32L * MiB, r.EstimatedRealBytes);
        Assert.Empty(r.BadRanges);
    }

    [Fact]
    public void FullTest_WrappingFake_ReportsRealCapacity()
    {
        // 256 MiB と称して実際は 64 MiB。上位への書き込みが折り返して下位を上書きする
        using var dev = Fake(256L * MiB, 64 * MiB, FakeCardStream.Behavior.Wrap);
        var r = CapacityTester.Run(dev, new CapacityTestOptions { Mode = CapacityTestMode.Full, Seed = 2 });
        Assert.Equal(CapacityVerdict.Fake, r.Verdict);
        Assert.Equal(64L * MiB, r.AliasDistance);
        Assert.Equal(64L * MiB, r.EstimatedRealBytes);
        Assert.Equal(64L * MiB, r.OkBytes);            // 最後に書いた 64 MiB 分だけが残る
        Assert.Equal(192L * MiB, r.BadBytes);
        Assert.Equal(0L, r.FirstBadOffset);
        Assert.Contains("折り返し", r.Summary);
    }

    [Fact]
    public void FullTest_DiscardingFake_ReportsWhereDataStopsBeingStored()
    {
        using var dev = Fake(128L * MiB, 32 * MiB, FakeCardStream.Behavior.Discard);
        var r = CapacityTester.Run(dev, new CapacityTestOptions { Mode = CapacityTestMode.Full, Seed = 3 });
        Assert.Equal(CapacityVerdict.Fake, r.Verdict);
        Assert.Null(r.AliasDistance);
        Assert.Equal(32L * MiB, r.FirstBadOffset);
        Assert.Equal(32L * MiB, r.EstimatedRealBytes);
        Assert.Equal(new[] { new ByteRange(32L * MiB, 96L * MiB) }, r.BadRanges);
    }

    [Fact]
    public void QuickTest_WrappingFake_IsDetectedBySampleGrid()
    {
        using var dev = Fake(1 * GiB, 128 * MiB, FakeCardStream.Behavior.Wrap);
        var r = CapacityTester.Run(dev, new CapacityTestOptions
        {
            Mode = CapacityTestMode.Quick, SampleBytes = 64 * KiB, TargetSamples = 64, Seed = 4,
        });
        Assert.Equal(CapacityVerdict.Fake, r.Verdict);
        Assert.Equal(128L * MiB, r.EstimatedRealBytes);
        Assert.True(r.TestedBytes < 8L * MiB);
    }

    [Fact]
    public void QuickTest_DiscardingFake_IsDetected()
    {
        using var dev = Fake(1 * GiB, 200 * MiB, FakeCardStream.Behavior.Discard);
        var r = CapacityTester.Run(dev, new CapacityTestOptions
        {
            Mode = CapacityTestMode.Quick, SampleBytes = 64 * KiB, TargetSamples = 64, Seed = 5,
        });
        Assert.Equal(CapacityVerdict.Fake, r.Verdict);
        // 実容量は直前の正常な標本と最初の不良標本の間にある
        Assert.InRange(r.EstimatedRealBytes, 200L * MiB, 200L * MiB + 16 * MiB);
    }

    [Fact]
    public void QuickTest_GenuineLargeDevice_IsGenuine()
    {
        using var dev = new StreamBlockDevice(new SparseMemoryStream(64 * GiB), 512);
        var r = CapacityTester.Run(dev, new CapacityTestOptions { Mode = CapacityTestMode.Quick, SampleBytes = 64 * KiB, Seed = 6 });
        Assert.Equal(CapacityVerdict.Genuine, r.Verdict);
        Assert.Equal(r.TestedBytes, r.OkBytes);
        Assert.Contains("全域検査", r.Summary);
    }

    [Fact]
    public void SampleSegments_AreOnAbsolutePowerOfTwoGridAndCoverBothEnds()
    {
        long len = 10 * GiB;
        long origin = 4 * MiB; // パーティション開始位置
        var segs = CapacityTester.SampleSegments(len, 512, new CapacityTestOptions { SampleBytes = MiB, TargetSamples = 100, OriginOffset = origin });
        long step = 128L * MiB; // 10 GiB / 100 を 2 のべき乗に切り上げ
        Assert.Equal(0, segs[0].Offset);
        Assert.Equal(len, segs[^1].End);
        Assert.All(segs.Skip(1).SkipLast(1), s => Assert.Equal(0, (origin + s.Offset) % step));
        for (int i = 1; i < segs.Count; i++) Assert.True(segs[i - 1].End <= segs[i].Offset);
        Assert.InRange(segs.Count, 80, 102);
    }

    [Fact]
    public void CapacityTest_Cancelled_IsIncomplete()
    {
        using var dev = new StreamBlockDevice(new MemoryStream(new byte[8 * MiB]), 512);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = CapacityTester.Run(dev, new CapacityTestOptions { Mode = CapacityTestMode.Full }, ct: cts.Token);
        Assert.Equal(CapacityVerdict.Incomplete, r.Verdict);
        Assert.False(r.Completed);
    }

    [Fact]
    public void CapacityTest_RequiresWritableDevice()
    {
        using var dev = new StreamBlockDevice(new MemoryStream(new byte[MiB], writable: false), 512);
        Assert.Throws<InvalidOperationException>(() => CapacityTester.Run(dev));
    }

    // ---- 不良セクタスキャン -------------------------------------------------------

    [Fact]
    public void ReadOnlyScan_FindsExactBadSectorsWithoutWriting()
    {
        var data = new byte[16 * MiB];
        var faulty = new FaultyStream(new MemoryStream(data, writable: false), new[] { (5L * MiB + 1024, 1536L), (9L * MiB, 512L) });
        using var dev = new StreamBlockDevice(faulty, 512);
        var r = SurfaceScanner.Run(dev, new SurfaceScanOptions { Mode = SurfaceScanMode.ReadOnly });
        Assert.True(r.Completed);
        Assert.Equal(4, r.BadSectors);
        Assert.Equal(new[] { new ByteRange(5L * MiB + 1024, 1536), new ByteRange(9L * MiB, 512) }, r.BadRanges);
        Assert.Equal(2, r.ReadErrors);
        Assert.Equal(0, faulty.WriteCount);
        Assert.False(r.Ok);
    }

    [Fact]
    public void ReadOnlyScan_HealthyDevice_IsOk()
    {
        using var dev = new StreamBlockDevice(new MemoryStream(new byte[4 * MiB], writable: false), 512);
        var r = SurfaceScanner.Run(dev);
        Assert.True(r.Ok);
        Assert.Equal(4L * MiB, r.ScannedBytes);
    }

    [Fact]
    public void WriteScan_DetectsSectorsThatDoNotHoldData()
    {
        using var dev = Fake(16L * MiB, 10 * MiB, FakeCardStream.Behavior.Discard);
        var r = SurfaceScanner.Run(dev, new SurfaceScanOptions { Mode = SurfaceScanMode.WriteVerify, MaxConsecutiveBadChunks = 100 });
        Assert.True(r.Completed);
        Assert.Equal(6L * MiB / 512, r.BadSectors);
        Assert.Equal(r.BadSectors, r.MismatchSectors);
        Assert.Equal(new[] { new ByteRange(10L * MiB, 6L * MiB) }, r.BadRanges);
    }

    [Fact]
    public void WriteScan_WriteErrorsAreLocalisedToSectors()
    {
        var faulty = new FaultyStream(new MemoryStream(new byte[4 * MiB]), new[] { (2L * MiB, 512L) }, failWrites: true);
        using var dev = new StreamBlockDevice(faulty, 512);
        var r = SurfaceScanner.Run(dev, new SurfaceScanOptions { Mode = SurfaceScanMode.WriteVerify });
        Assert.Equal(1, r.BadSectors);
        Assert.Equal(1, r.WriteErrors);
        Assert.Equal(new[] { new ByteRange(2L * MiB, 512) }, r.BadRanges);
    }

    [Fact]
    public void Scan_StopsWhenDeviceStopsResponding()
    {
        var faulty = new FaultyStream(new MemoryStream(new byte[64 * MiB], writable: false), new[] { (8L * MiB, 56L * MiB) });
        using var dev = new StreamBlockDevice(faulty, 512);
        var r = SurfaceScanner.Run(dev, new SurfaceScanOptions { ChunkBytes = 256 * KiB, MaxConsecutiveBadChunks = 4 });
        Assert.False(r.Completed);
        Assert.Equal(9L * MiB, r.ScannedBytes);
        Assert.Contains("打ち切り", r.Summary);
    }

    // ---- 速度測定 -----------------------------------------------------------------

    private static BenchmarkOptions FastBench(bool write) => new()
    {
        IncludeWrite = write,
        SequentialBytes = 8 * MiB,
        SequentialBlockBytes = MiB,
        RandomDuration = TimeSpan.FromMilliseconds(50),
        RandomRegionBytes = 4 * MiB,
        Seed = 1,
    };

    [Fact]
    public void Benchmark_ReadOnly_DoesNotWriteAndLeavesClassesUnmeasured()
    {
        var faulty = new FaultyStream(new MemoryStream(new byte[16 * MiB], writable: false), Array.Empty<(long, long)>());
        using var dev = new StreamBlockDevice(faulty, 512);
        var r = SpeedBenchmark.Run(dev, FastBench(write: false));
        Assert.Equal(8L * MiB, r.SequentialRead.Bytes);
        Assert.True(r.SequentialRead.BytesPerSecond > 0);
        Assert.True(r.RandomRead4K.Operations > 0);
        Assert.Equal(4096L * r.RandomRead4K.Operations, r.RandomRead4K.Bytes);
        Assert.Null(r.SequentialWrite);
        Assert.Null(r.RandomWrite4K);
        Assert.All(r.SpeedClasses, c => Assert.Null(c.Meets));
        Assert.Equal(0, faulty.WriteCount);
    }

    [Fact]
    public void Benchmark_WithWrite_MeasuresAllFour()
    {
        using var dev = new StreamBlockDevice(new MemoryStream(new byte[16 * MiB]), 512);
        var r = SpeedBenchmark.Run(dev, FastBench(write: true));
        Assert.NotNull(r.SequentialWrite);
        Assert.NotNull(r.RandomWrite4K);
        Assert.True(r.SequentialWrite!.BytesPerSecond > 0);
        Assert.All(r.SpeedClasses, c => Assert.NotNull(c.Meets)); // メモリなら全クラスを満たすはず
        Assert.Contains("相当", r.Summary);
    }

    [Fact]
    public void SpeedClass_EvaluatesAgainstSdMinimums()
    {
        var v = SpeedClassRating.Evaluate(35e6, 2000, 600);
        bool? Meets(string n) => v.Single(c => c.Name == n).Meets;
        Assert.True(Meets("Class 10"));
        Assert.True(Meets("U1"));
        Assert.True(Meets("U3"));
        Assert.True(Meets("V30"));
        Assert.False(Meets("V60"));
        Assert.True(Meets("A1"));
        Assert.False(Meets("A2"));
        Assert.Equal("V30 / A1", SpeedClassRating.Highest(v));

        var slow = SpeedClassRating.Evaluate(8e6, 3000, 900);
        Assert.False(slow.Single(c => c.Name == "Class 10").Meets);
        Assert.Null(SpeedClassRating.Highest(slow));

        Assert.All(SpeedClassRating.Evaluate(null, 3000, null), c => Assert.Null(c.Meets));
    }

    // ---- CLI / 受け渡し -----------------------------------------------------------

    [Theory]
    [InlineData(DiagnosticKind.CapacityQuick)]
    [InlineData(DiagnosticKind.CapacityFull)]
    [InlineData(DiagnosticKind.ScanRead)]
    [InlineData(DiagnosticKind.ScanWrite)]
    [InlineData(DiagnosticKind.Bench)]
    [InlineData(DiagnosticKind.BenchWrite)]
    public void BuildCliArgs_RoundTripsThroughParser(DiagnosticKind kind)
    {
        var req = new DiagnosticRequest { DriveLetter = 'e', Kind = kind, AllowFixedDisk = true };
        var args = DiagnosticsService.BuildCliArgs(req);
        Assert.Equal("E:", args[1]);
        Assert.Contains("--yes", args);
        var o = DiagnosticOptions.Parse(args.ToArray());
        var parsed = o.ToRequest();
        Assert.Equal(kind, parsed.Kind);
        Assert.Equal('E', parsed.DriveLetter);
        Assert.True(parsed.AllowFixedDisk);
        Assert.True(o.Yes);
    }

    [Fact]
    public void Options_DefaultTestIsQuickAndConflictsAreRejected()
    {
        Assert.Equal(DiagnosticKind.CapacityQuick, DiagnosticOptions.Parse(["test", "E:"]).Kind());
        Assert.Equal(DiagnosticKind.Bench, DiagnosticOptions.Parse(["bench", "E:"]).Kind());
        Assert.Throws<UsageException>(() => DiagnosticOptions.Parse(["test", "E:", "--quick", "--full"]).Kind());
        Assert.Throws<UsageException>(() => DiagnosticOptions.Parse(["test", "E:", "--write"]).Kind());
        Assert.Throws<UsageException>(() => DiagnosticOptions.Parse(["bench", "E:", "--scan"]).Kind());
        Assert.Throws<UsageException>(() => DiagnosticOptions.Parse(["test", "E:", "--bogus"]));
    }

    [Fact]
    public void DestructiveKinds_RequireEraseConfirmation()
    {
        Assert.False(new DiagnosticRequest { DriveLetter = 'E', Kind = DiagnosticKind.ScanRead }.IsDestructive);
        Assert.False(new DiagnosticRequest { DriveLetter = 'E', Kind = DiagnosticKind.Bench }.IsDestructive);
        var req = new DiagnosticRequest { DriveLetter = 'e', Kind = DiagnosticKind.CapacityFull };
        Assert.True(req.IsDestructive);
        Assert.Equal("ERASE E", req.ConfirmPhrase);
    }

    [Fact]
    public void Outcome_SurvivesJsonRoundTrip()
    {
        using var dev = Fake(64L * MiB, 16 * MiB, FakeCardStream.Behavior.Wrap);
        var outcome = new DiagnosticOutcome
        {
            Success = true,
            Kind = DiagnosticKind.CapacityFull,
            Target = "テスト",
            Capacity = CapacityTester.Run(dev, new CapacityTestOptions { Mode = CapacityTestMode.Full, Seed = 9 }),
        };
        string json = JsonSerializer.Serialize(outcome, JsonDefaults.Options);
        var back = JsonSerializer.Deserialize<DiagnosticOutcome>(json, JsonDefaults.Options)!;
        Assert.True(back.ProblemFound);
        Assert.Equal(CapacityVerdict.Fake, back.Capacity!.Verdict);
        Assert.Equal(outcome.Capacity.EstimatedRealBytes, back.Capacity.EstimatedRealBytes);
        Assert.Equal(outcome.Capacity.BadRanges, back.Capacity.BadRanges);
        Assert.Contains("容量偽装", back.Describe());
    }
}
