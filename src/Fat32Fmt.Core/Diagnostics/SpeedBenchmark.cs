using System.Diagnostics;
using Fat32Fmt.Core.IO;

namespace Fat32Fmt.Core.Diagnostics;

public sealed record BenchmarkOptions
{
    /// <summary>書き込みの測定も行う（測定範囲のデータを破壊する）。</summary>
    public bool IncludeWrite { get; init; }

    /// <summary>連続読み書きで転送する最大バイト数。</summary>
    public long SequentialBytes { get; init; } = 512L << 20;

    /// <summary>連続読み書きの 1 回の転送サイズ。</summary>
    public int SequentialBlockBytes { get; init; } = 4 << 20;

    /// <summary>連続読み書きの最大時間（遅いカードで待たせすぎない）。</summary>
    public TimeSpan SequentialMaxDuration { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>ランダム 4K の測定時間。</summary>
    public TimeSpan RandomDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>ランダム 4K の対象範囲（先頭から）。SD の A1/A2 は 1 GiB 程度の範囲で測る。</summary>
    public long RandomRegionBytes { get; init; } = 1L << 30;

    public int RandomBlockBytes { get; init; } = 4096;

    public int? Seed { get; init; }
}

/// <summary>1 項目の測定結果。</summary>
public sealed record BenchmarkResult(string Name, long Bytes, int Operations, TimeSpan Elapsed)
{
    public double BytesPerSecond => Elapsed.TotalSeconds > 0 ? Bytes / Elapsed.TotalSeconds : 0;

    public double Iops => Elapsed.TotalSeconds > 0 ? Operations / Elapsed.TotalSeconds : 0;
}

/// <summary>SD のスピードクラス 1 つについての判定。Meets が null なら判定に必要な測定をしていない。</summary>
public sealed record SpeedClassVerdict(string Name, string Requirement, bool? Meets);

public sealed record BenchmarkReport(
    long DeviceBytes,
    int SectorSize,
    BenchmarkResult SequentialRead,
    BenchmarkResult RandomRead4K,
    BenchmarkResult? SequentialWrite,
    BenchmarkResult? RandomWrite4K,
    IReadOnlyList<SpeedClassVerdict> SpeedClasses,
    string Summary);

/// <summary>
/// 実測の読み書き速度テスト。連続（既定 4 MiB 単位）とランダム 4K を測る。
/// 読み取りのみなら非破壊。書き込みを含めると先頭からの測定範囲のデータを破壊する。
/// </summary>
public static class SpeedBenchmark
{
    public static BenchmarkReport Run(IBlockDevice device, BenchmarkOptions? options = null,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        var opt = options ?? new BenchmarkOptions();
        if (opt.IncludeWrite && !device.CanWrite) throw new InvalidOperationException("書き込みの測定には書き込み可能なデバイスが必要です。");
        int ss = device.SectorSize;
        int seqBlock = Math.Max(ss, opt.SequentialBlockBytes - opt.SequentialBlockBytes % ss);
        int rndBlock = Math.Max(ss, opt.RandomBlockBytes - opt.RandomBlockBytes % ss);
        long seqBytes = Math.Min(opt.SequentialBytes, device.Length);
        seqBytes -= seqBytes % ss;
        long region = Math.Min(opt.RandomRegionBytes, device.Length);
        var rng = opt.Seed is int s ? new Random(s) : new Random();
        var buf = new byte[seqBlock];

        BenchmarkResult? seqW = null, rndW = null;
        if (opt.IncludeWrite)
        {
            // 書いてから読むと、読み取りが未書き込み領域の特別扱い（0 を即返すなど）に左右されない
            rng.NextBytes(buf);
            seqW = Sequential(device, "連続書き込み", seqBytes, buf, write: true, opt.SequentialMaxDuration, progress, ct);
            rndW = Random4K(device, "ランダム 4K 書き込み", region, rndBlock, buf, write: true, rng, opt.RandomDuration, progress, ct);
        }
        var seqR = Sequential(device, "連続読み取り", seqBytes, buf, write: false, opt.SequentialMaxDuration, progress, ct);
        var rndR = Random4K(device, "ランダム 4K 読み取り", region, rndBlock, buf, write: false, rng, opt.RandomDuration, progress, ct);

        var classes = SpeedClassRating.Evaluate(seqW?.BytesPerSecond, rndR.Iops, rndW?.Iops);
        string summary = $"連続読み取り {SizeFormat.Speed(seqR.BytesPerSecond)}、ランダム 4K 読み取り {rndR.Iops:0} IOPS";
        if (seqW != null && rndW != null)
        {
            summary += $"、連続書き込み {SizeFormat.Speed(seqW.BytesPerSecond)}、ランダム 4K 書き込み {rndW.Iops:0} IOPS。";
            string? best = SpeedClassRating.Highest(classes);
            summary += best != null ? $"実測では {best} 相当の書き込み速度です。" : "実測の書き込み速度は Class 10 の基準 (10 MB/s) を下回っています。";
        }
        else
        {
            summary += "。スピードクラス（書き込みの最低速度の規格）の判定には書き込みの測定が必要です。";
        }
        return new BenchmarkReport(device.Length, ss, seqR, rndR, seqW, rndW, classes, summary);
    }

    private static BenchmarkResult Sequential(IBlockDevice device, string name, long bytes, byte[] buf, bool write,
        TimeSpan maxDuration, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var tracker = new ProgressTracker(progress, name, bytes);
        var sw = Stopwatch.StartNew();
        long done = 0;
        int ops = 0;
        while (done < bytes && sw.Elapsed < maxDuration)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buf.Length, bytes - done);
            if (write) device.Write(done, buf.AsSpan(0, n));
            else device.Read(done, buf.AsSpan(0, n));
            done += n;
            ops++;
            tracker.Report(done);
        }
        if (write) device.Flush();
        sw.Stop();
        tracker.Complete();
        return new BenchmarkResult(name, done, ops, sw.Elapsed);
    }

    private static BenchmarkResult Random4K(IBlockDevice device, string name, long region, int block, byte[] buf, bool write,
        Random rng, TimeSpan duration, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        long slots = Math.Max(1, region / block);
        long totalMs = (long)duration.TotalMilliseconds;
        var tracker = new ProgressTracker(progress, name, totalMs);
        var sw = Stopwatch.StartNew();
        int ops = 0;
        var span = buf.AsSpan(0, block);
        do
        {
            ct.ThrowIfCancellationRequested();
            long off = rng.NextInt64(slots) * block;
            if (write) device.Write(off, span);
            else device.Read(off, span);
            ops++;
            tracker.Report(Math.Min(totalMs, (long)sw.Elapsed.TotalMilliseconds));
        }
        while (sw.Elapsed < duration);
        if (write) device.Flush();
        sw.Stop();
        tracker.Complete();
        return new BenchmarkResult(name, (long)ops * block, ops, sw.Elapsed);
    }
}

/// <summary>
/// 実測値を SD のスピードクラスの基準に照らす。基準は「書き込みの最低保証速度」なので、判定は書き込みの実測で行う。
/// 規格の測定はカード内部の割り当て単位 (AU) ごとの条件で行うため、PC からの実測は目安にとどまる（カードリーダーの性能にも左右される）。
/// </summary>
public static class SpeedClassRating
{
    private const double MB = 1_000_000;

    private static readonly (string Name, double SeqWrite, double ReadIops, double WriteIops, string Requirement)[] Classes =
    {
        ("Class 10", 10 * MB, 0, 0, "連続書き込み 10 MB/s 以上"),
        ("U1", 10 * MB, 0, 0, "連続書き込み 10 MB/s 以上 (UHS)"),
        ("U3", 30 * MB, 0, 0, "連続書き込み 30 MB/s 以上 (UHS)"),
        ("V30", 30 * MB, 0, 0, "連続書き込み 30 MB/s 以上（ビデオ）"),
        ("V60", 60 * MB, 0, 0, "連続書き込み 60 MB/s 以上（ビデオ）"),
        ("V90", 90 * MB, 0, 0, "連続書き込み 90 MB/s 以上（ビデオ）"),
        ("A1", 10 * MB, 1500, 500, "ランダム読み取り 1500 IOPS・書き込み 500 IOPS・連続書き込み 10 MB/s 以上"),
        ("A2", 10 * MB, 4000, 2000, "ランダム読み取り 4000 IOPS・書き込み 2000 IOPS・連続書き込み 10 MB/s 以上"),
    };

    public static IReadOnlyList<SpeedClassVerdict> Evaluate(double? seqWriteBytesPerSecond, double? randomReadIops, double? randomWriteIops)
    {
        var list = new List<SpeedClassVerdict>();
        foreach (var c in Classes)
        {
            bool? meets;
            if (seqWriteBytesPerSecond is not double w) meets = null;
            else if (c.ReadIops > 0 && (randomReadIops is null || randomWriteIops is null)) meets = null;
            else meets = w >= c.SeqWrite && (c.ReadIops == 0 || (randomReadIops >= c.ReadIops && randomWriteIops >= c.WriteIops));
            list.Add(new SpeedClassVerdict(c.Name, c.Requirement, meets));
        }
        return list;
    }

    /// <summary>満たしている最上位のクラス（連続書き込み系と A 系を「V30 / A1」のようにまとめる）。どれも満たさなければ null。</summary>
    public static string? Highest(IReadOnlyList<SpeedClassVerdict> verdicts)
    {
        string? seq = verdicts.LastOrDefault(v => v.Meets == true && !v.Name.StartsWith('A'))?.Name;
        string? app = verdicts.LastOrDefault(v => v.Meets == true && v.Name.StartsWith('A'))?.Name;
        return seq == null ? null : app == null ? seq : $"{seq} / {app}";
    }
}
