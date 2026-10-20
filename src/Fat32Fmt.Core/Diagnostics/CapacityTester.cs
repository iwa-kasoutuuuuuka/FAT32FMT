using System.Diagnostics;
using System.Numerics;
using Fat32Fmt.Core.IO;

namespace Fat32Fmt.Core.Diagnostics;

public enum CapacityTestMode
{
    /// <summary>抜き取り検査。2 のべき乗の間隔で並べた標本だけを書いて読み戻す（数分で終わる）。</summary>
    Quick,

    /// <summary>全域検査（H2testw と同じ）。全体を書き終えてから全体を読み戻す。</summary>
    Full,
}

public enum CapacityVerdict
{
    /// <summary>異常なし。表示どおりの容量がある。</summary>
    Genuine,

    /// <summary>容量偽装。書き込みが折り返す、または一定位置より後ろが保存されない。</summary>
    Fake,

    /// <summary>一部に不良がある（偽装ではなく劣化・故障の可能性）。</summary>
    Damaged,

    /// <summary>中断されたため判定できない。</summary>
    Incomplete,
}

public sealed record CapacityTestOptions
{
    public CapacityTestMode Mode { get; init; } = CapacityTestMode.Quick;

    /// <summary>全域検査で 1 回に読み書きするバイト数。</summary>
    public int ChunkBytes { get; init; } = 4 << 20;

    /// <summary>抜き取り検査の標本 1 個のバイト数。</summary>
    public int SampleBytes { get; init; } = 1 << 20;

    /// <summary>抜き取り検査の標本数の目安（間隔は 2 のべき乗に切り上げるので、実際はこれ以下になる）。</summary>
    public int TargetSamples { get; init; } = 512;

    /// <summary>
    /// 検査対象の先頭がディスク上のどこにあるか（パーティション開始位置）。
    /// 偽装カードの折り返しはディスク先頭からのアドレスで起きるので、標本の格子をディスク先頭基準でそろえる。
    /// </summary>
    public long OriginOffset { get; init; }

    /// <summary>パターンの種。null なら実行ごとに乱数で決める。</summary>
    public ulong? Seed { get; init; }

    /// <summary>I/O エラーがこの回数続いたら、その工程を打ち切って残りを不良として扱う。</summary>
    public int MaxConsecutiveErrors { get; init; } = 32;

    public int MaxReportedRanges { get; init; } = 100;
}

/// <summary>容量偽装チェックの結果。</summary>
public sealed record CapacityTestReport(
    CapacityTestMode Mode,
    CapacityVerdict Verdict,
    bool Completed,
    long ReportedBytes,
    long TestedBytes,
    long OkBytes,
    long BadBytes,
    long EstimatedRealBytes,
    long? FirstBadOffset,
    long? AliasDistance,
    int BadRangeCount,
    IReadOnlyList<ByteRange> BadRanges,
    int WriteErrors,
    int ReadErrors,
    double WriteBytesPerSecond,
    double ReadBytesPerSecond,
    TimeSpan Elapsed,
    string Summary);

/// <summary>
/// 容量偽装（フェイクカード）チェック。位置ごとに異なるパターンを書き、すべて書き終えてから読み戻して照合する。
/// 書き込みが折り返すカードでは後から書いたデータが先に書いたデータを上書きするので、照合で検出できる。
/// <b>対象の全データを破壊する。</b>
/// </summary>
public static class CapacityTester
{
    public static CapacityTestReport Run(IBlockDevice device, CapacityTestOptions? options = null,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        var opt = options ?? new CapacityTestOptions();
        if (!device.CanWrite) throw new InvalidOperationException("容量偽装チェックには書き込み可能なデバイスが必要です。");
        var sw = Stopwatch.StartNew();
        int ss = device.SectorSize;
        long length = device.Length;
        var pattern = opt.Seed is ulong seed ? new TestPattern(seed) : TestPattern.CreateRandom();
        var segments = opt.Mode == CapacityTestMode.Full
            ? FullSegments(length, RoundDown(Math.Max(opt.ChunkBytes, ss), ss))
            : SampleSegments(length, ss, opt);
        long total = segments.Sum(s => s.Length);
        int maxSeg = (int)segments.Max(s => s.Length);
        var buf = new byte[maxSeg];
        var expected = new byte[maxSeg];

        var bad = new RangeCollector(opt.MaxReportedRanges);
        int writeErrors = 0, readErrors = 0, consecutive = 0;
        bool cancelled = false;

        // ---- 書き込み（全体を書き終えてから読む。途中で読むと折り返しを見逃す）
        var writeTime = new Stopwatch();
        long written = 0;
        int writtenSegments = 0;
        var tracker = new ProgressTracker(progress, "検査パターンの書き込み", total);
        for (; writtenSegments < segments.Count; writtenSegments++)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            var seg = segments[writtenSegments];
            var span = buf.AsSpan(0, (int)seg.Length);
            pattern.Fill(seg.Offset, span);
            try
            {
                writeTime.Start();
                device.Write(seg.Offset, span);
                consecutive = 0;
            }
            catch (IOException)
            {
                writeErrors++;
                if (++consecutive >= opt.MaxConsecutiveErrors) { writtenSegments++; break; }
            }
            finally
            {
                writeTime.Stop();
            }
            written += seg.Length;
            tracker.Report(written);
        }
        try { device.Flush(); } catch (IOException) { }
        tracker.Complete();

        // ---- 読み戻しと照合
        var readTime = new Stopwatch();
        long okBytes = 0, read = 0, lastOkEnd = 0;
        long? aliasMin = null;
        consecutive = 0;
        if (!cancelled)
        {
            tracker = new ProgressTracker(progress, "読み戻しと照合", total);
            for (int i = 0; i < segments.Count; i++)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                var seg = segments[i];
                if (i >= writtenSegments || consecutive >= opt.MaxConsecutiveErrors)
                {
                    bad.Add(seg.Offset, seg.Length); // 書けなかった／読めなくなった残り
                    continue;
                }
                var span = buf.AsSpan(0, (int)seg.Length);
                try
                {
                    readTime.Start();
                    device.Read(seg.Offset, span);
                    consecutive = 0;
                }
                catch (IOException)
                {
                    readErrors++;
                    consecutive++;
                    bad.Add(seg.Offset, seg.Length);
                    continue;
                }
                finally
                {
                    readTime.Stop();
                    read += seg.Length;
                    tracker.Report(read);
                }

                var exp = expected.AsSpan(0, (int)seg.Length);
                pattern.Fill(seg.Offset, exp);
                bool identified = false;
                for (int b = 0; b < span.Length; b += TestPattern.BlockSize)
                {
                    long at = seg.Offset + b;
                    if (span.Slice(b, TestPattern.BlockSize).SequenceEqual(exp.Slice(b, TestPattern.BlockSize)))
                    {
                        okBytes += TestPattern.BlockSize;
                        lastOkEnd = at + TestPattern.BlockSize;
                        continue;
                    }
                    bad.Add(at, TestPattern.BlockSize);
                    // 区間ごとに最初の不良ブロックだけ、どこのデータが返ってきたかを調べる（全部調べると遅い）
                    if (!identified)
                    {
                        identified = true;
                        if (pattern.TryIdentify(span.Slice(b, TestPattern.BlockSize), out long source) && source != at && source < length)
                        {
                            long d = Math.Abs(source - at);
                            if (aliasMin is null || d < aliasMin) aliasMin = d;
                        }
                    }
                }
            }
            tracker.Complete();
        }

        return BuildReport(opt.Mode, !cancelled, length, total, okBytes, bad, lastOkEnd, aliasMin, writeErrors, readErrors,
            Rate(written, writeTime), Rate(read, readTime), sw.Elapsed);
    }

    private static CapacityTestReport BuildReport(CapacityTestMode mode, bool completed, long length, long tested, long okBytes,
        RangeCollector bad, long lastOkEnd, long? alias, int writeErrors, int readErrors, double writeBps, double readBps, TimeSpan elapsed)
    {
        CapacityVerdict verdict;
        long real;
        bool tailBad = bad.FirstOffset is long first && lastOkEnd <= first;
        if (!completed)
        {
            verdict = CapacityVerdict.Incomplete;
            real = 0;
        }
        else if (bad.TotalBytes == 0)
        {
            verdict = CapacityVerdict.Genuine;
            real = length;
        }
        else if (alias is long a)
        {
            verdict = CapacityVerdict.Fake;
            real = a;
        }
        else if (tailBad && length - bad.FirstOffset!.Value >= length / 20)
        {
            verdict = CapacityVerdict.Fake;
            real = bad.FirstOffset.Value;
        }
        else
        {
            verdict = CapacityVerdict.Damaged;
            real = length;
        }

        string what = mode == CapacityTestMode.Full ? "全域" : "抜き取り";
        string summary = verdict switch
        {
            CapacityVerdict.Genuine => $"{what}検査で異常なし。表示容量 {SizeFormat.Both(length)} を確認しました。",
            CapacityVerdict.Fake when alias != null =>
                $"容量偽装の疑いが強い: 書き込みが約 {SizeFormat.Both(alias.Value)} ごとに折り返しています。実容量は約 {SizeFormat.Both(real)} です（表示 {SizeFormat.Both(length)}）。",
            CapacityVerdict.Fake =>
                $"容量偽装の疑いが強い: 先頭から {SizeFormat.Both(real)} より後ろにデータが保存されていません（表示 {SizeFormat.Both(length)}）。",
            CapacityVerdict.Damaged =>
                $"不良あり: {bad.Count} か所・計 {SizeFormat.Binary(bad.TotalBytes)} が正しく読み戻せませんでした。偽装ではなく劣化・故障の可能性があります。",
            _ => "中断されたため判定できません。",
        };
        if (mode == CapacityTestMode.Quick && verdict == CapacityVerdict.Genuine)
            summary += "（確実に調べるには全域検査を実行してください）";

        return new CapacityTestReport(mode, verdict, completed, length, tested, okBytes, bad.TotalBytes, real, bad.FirstOffset, alias,
            bad.Count, bad.Ranges, writeErrors, readErrors, writeBps, readBps, elapsed, summary);
    }

    private static List<ByteRange> FullSegments(long length, int chunk)
    {
        var list = new List<ByteRange>();
        for (long off = 0; off < length; off += chunk) list.Add(new ByteRange(off, Math.Min(chunk, length - off)));
        return list;
    }

    /// <summary>
    /// 抜き取り検査の標本位置。ディスク先頭基準で 2 のべき乗の間隔の格子に置く。
    /// 折り返し幅（実容量）は 2 のべき乗であることが多く、その場合は上位の標本が下位の標本と同じ物理位置に重なって検出できる。
    /// 先頭と末尾には必ず標本を置く。
    /// </summary>
    public static List<ByteRange> SampleSegments(long length, int sectorSize, CapacityTestOptions opt)
    {
        long sample = RoundDown(Math.Max(opt.SampleBytes, sectorSize), sectorSize);
        if (length <= sample) return new List<ByteRange> { new(0, length) };

        long want = Math.Max(sample, (length + Math.Max(1, opt.TargetSamples) - 1) / Math.Max(1, opt.TargetSamples));
        long step = (long)BitOperations.RoundUpToPowerOf2((ulong)want);
        long origin = ((opt.OriginOffset % step) + step) % step;
        var starts = new SortedSet<long> { 0, RoundDown(length - sample, sectorSize) };
        for (long p = (step - origin) % step; p + sectorSize <= length; p += step)
            starts.Add(RoundDown(p, sectorSize));

        var list = new List<ByteRange>();
        var arr = starts.ToArray();
        for (int i = 0; i < arr.Length; i++)
        {
            long limit = i + 1 < arr.Length ? arr[i + 1] : length;
            long len = Math.Min(sample, limit - arr[i]);
            if (len >= sectorSize) list.Add(new ByteRange(arr[i], RoundDown(len, sectorSize)));
        }
        return list;
    }

    private static int RoundDown(int v, int unit) => v - v % unit;

    private static long RoundDown(long v, long unit) => v - v % unit;

    internal static double Rate(long bytes, Stopwatch sw) => sw.Elapsed.TotalSeconds > 0 ? bytes / sw.Elapsed.TotalSeconds : 0;
}
