using System.Diagnostics;
using Fat32Fmt.Core.IO;

namespace Fat32Fmt.Core.Diagnostics;

public enum SurfaceScanMode
{
    /// <summary>読み取りのみ（非破壊）。</summary>
    ReadOnly,

    /// <summary>区間ごとにパターンを書いてすぐ読み戻す（破壊）。</summary>
    WriteVerify,
}

public sealed record SurfaceScanOptions
{
    public SurfaceScanMode Mode { get; init; } = SurfaceScanMode.ReadOnly;

    /// <summary>1 回に読み書きするバイト数。エラーが出た区間は二分して読み直し、不良セクタを特定する。</summary>
    public int ChunkBytes { get; init; } = 1 << 20;

    /// <summary>1 区間の読み取りにこれ以上かかったら「遅い区間」として数える（劣化の兆候）。</summary>
    public TimeSpan SlowThreshold { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 失敗した読み書きがこれ以上かかったら、その範囲は分割して調べ直さず、まとめて不良とする。
    /// 劣化したカードではドライバが 1 回のエラーを返すまでに 1 分以上かかることがあり、セクタ単位で読み直すと終わらないため。
    /// </summary>
    public TimeSpan SlowErrorThreshold { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>エラー処理（失敗した I/O と、不良箇所を絞り込む読み直し）に使う時間の合計の上限。超えたら打ち切る。</summary>
    public TimeSpan MaxErrorHandlingTime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>区間全体が不良のまま、これだけ続いたら打ち切る（カードが抜けた、応答しないなど）。</summary>
    public int MaxConsecutiveBadChunks { get; init; } = 16;

    public ulong? Seed { get; init; }

    public int MaxReportedRanges { get; init; } = 100;
}

/// <summary>不良セクタスキャンの結果。不良の位置は検査対象の先頭からのバイト位置。</summary>
public sealed record SurfaceScanReport(
    SurfaceScanMode Mode,
    bool Completed,
    long DeviceBytes,
    long ScannedBytes,
    int SectorSize,
    long BadSectors,
    int BadRangeCount,
    IReadOnlyList<ByteRange> BadRanges,
    int ReadErrors,
    int WriteErrors,
    long MismatchSectors,
    int SlowChunks,
    int SlowErrors,
    TimeSpan ErrorHandlingTime,
    double BytesPerSecond,
    TimeSpan Elapsed,
    string Summary)
{
    public bool Ok => Completed && BadSectors == 0;
}

/// <summary>
/// 不良セクタの読み取りスキャン（非破壊）と、書き込み＋読み戻しスキャン（破壊）。
/// 不良区間で時間が際限なく延びないよう、失敗した範囲は二分探索で絞り込み、応答の遅いエラーは分割せずにまとめて不良とし、
/// エラー処理に使う時間の合計にも上限を設ける。
/// </summary>
public static class SurfaceScanner
{
    public static SurfaceScanReport Run(IBlockDevice device, SurfaceScanOptions? options = null,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        var opt = options ?? new SurfaceScanOptions();
        bool write = opt.Mode == SurfaceScanMode.WriteVerify;
        if (write && !device.CanWrite) throw new InvalidOperationException("書き込みスキャンには書き込み可能なデバイスが必要です。");

        var sw = Stopwatch.StartNew();
        int ss = device.SectorSize;
        long length = device.Length;
        int chunk = Math.Max(ss, opt.ChunkBytes - opt.ChunkBytes % ss);
        var buf = new byte[chunk];
        var expected = write ? new byte[chunk] : Array.Empty<byte>();
        var pattern = write ? (opt.Seed is ulong seed ? new TestPattern(seed) : TestPattern.CreateRandom()) : null;

        var bad = new RangeCollector(opt.MaxReportedRanges);
        var errors = new ErrorBudget(opt.SlowErrorThreshold, opt.MaxErrorHandlingTime);
        int readErrors = 0, writeErrors = 0, slow = 0, consecutiveBad = 0;
        long mismatch = 0, scanned = 0;
        bool completed = true;
        var tracker = new ProgressTracker(progress, write ? "書き込みスキャン" : "読み取りスキャン", length);

        for (long off = 0; off < length; off += chunk)
        {
            if (ct.IsCancellationRequested) { completed = false; break; }
            long at = off;
            int n = (int)Math.Min(chunk, length - off);
            int sectors = n / ss;
            var badSector = new bool[sectors];

            if (write)
            {
                pattern!.Fill(off, expected.AsSpan(0, n));
                var wt = Stopwatch.StartNew();
                if (!TryWrite(device, off, expected, 0, n))
                {
                    writeErrors++;
                    errors.Failed(wt.Elapsed, 0, n, ss, badSector, (s, l) => TryWrite(device, at + s, expected, s, l));
                }
            }

            var rt = Stopwatch.StartNew();
            bool readOk = TryRead(device, off, buf, 0, n);
            if (rt.Elapsed > opt.SlowThreshold) slow++;
            if (!readOk)
            {
                readErrors++;
                errors.Failed(rt.Elapsed, 0, n, ss, badSector, (s, l) => TryRead(device, at + s, buf, s, l));
                for (int s = 0; s < sectors; s++)
                    if (badSector[s]) buf.AsSpan(s * ss, ss).Clear();
            }

            if (write)
            {
                for (int s = 0; s < sectors; s++)
                {
                    if (badSector[s] || buf.AsSpan(s * ss, ss).SequenceEqual(expected.AsSpan(s * ss, ss))) continue;
                    mismatch++;
                    badSector[s] = true;
                }
            }

            int badInChunk = 0;
            for (int s = 0; s < sectors; s++)
            {
                if (!badSector[s]) continue;
                badInChunk++;
                bad.Add(off + (long)s * ss, ss);
            }

            scanned += n;
            tracker.Report(scanned);
            consecutiveBad = badInChunk == sectors ? consecutiveBad + 1 : 0;
            if (consecutiveBad >= opt.MaxConsecutiveBadChunks || errors.Exhausted) { completed = false; break; }
        }
        tracker.Complete();

        long badSectors = bad.TotalBytes / ss;
        string summary;
        if (!completed && errors.Exhausted)
            summary = $"エラー処理に {errors.Spent.TotalMinutes:0.0} 分かかったため、{SizeFormat.Binary(scanned)} の位置で打ち切りました" +
                      $"（応答に時間のかかる不良区間があります。遅いエラー {errors.SlowErrors} 回）。不良セクタ {badSectors:N0} 個以上。重要なデータには使わないでください。";
        else if (!completed && consecutiveBad >= opt.MaxConsecutiveBadChunks)
            summary = $"{SizeFormat.Binary(scanned)} の位置で読み書きできない区間が続いたため打ち切りました（カードが抜けたか、応答しません）。不良セクタ {badSectors:N0} 個。";
        else if (!completed)
            summary = $"中断しました（{SizeFormat.Binary(scanned)} / {SizeFormat.Binary(length)} を検査）。ここまでの不良セクタ {badSectors:N0} 個。";
        else if (badSectors == 0)
            summary = $"不良セクタはありません（{SizeFormat.Both(length)} を{(write ? "書き込み・読み戻し" : "読み取り")}検査）。" +
                      (slow > 0 ? $" ただし読み取りが遅い区間が {slow} か所あります。" : "");
        else
            summary = $"不良セクタ {badSectors:N0} 個（{bad.Count} か所、計 {SizeFormat.Binary(bad.TotalBytes)}）が見つかりました。重要なデータには使わないでください。" +
                      (errors.SlowErrors > 0 ? $"（応答の遅いエラー {errors.SlowErrors} 回の範囲は、絞り込まずにまとめて不良としています）" : "");

        return new SurfaceScanReport(opt.Mode, completed, length, scanned, ss, badSectors, bad.Count, bad.Ranges,
            readErrors, writeErrors, mismatch, slow, errors.SlowErrors, errors.Spent,
            sw.Elapsed.TotalSeconds > 0 ? scanned / sw.Elapsed.TotalSeconds : 0, sw.Elapsed, summary);
    }

    /// <summary>エラー処理の時間を数え、失敗した範囲の不良セクタを二分探索で特定する。</summary>
    private sealed class ErrorBudget
    {
        private readonly TimeSpan _slowError;
        private readonly TimeSpan _max;

        public ErrorBudget(TimeSpan slowError, TimeSpan max)
        {
            _slowError = slowError;
            _max = max;
        }

        public TimeSpan Spent { get; private set; }

        public int SlowErrors { get; private set; }

        public bool Exhausted => Spent >= _max;

        /// <summary>
        /// 範囲 [start, start+length) の I/O が elapsed かけて失敗した。応答が遅ければ範囲ごと不良とし、
        /// 速ければ半分ずつ試して不良セクタを絞り込む（不良 1 セクタなら約 2×log2(セクタ数) 回で済む）。
        /// 時間の上限を超えたら、調べていない残りは不良とする。
        /// </summary>
        public void Failed(TimeSpan elapsed, int start, int length, int sectorSize, bool[] badSector, Func<int, int, bool> io)
        {
            Spent += elapsed;
            bool slowError = elapsed >= _slowError;
            if (slowError) SlowErrors++;
            if (slowError || length <= sectorSize || Exhausted)
            {
                Mark(start, length, sectorSize, badSector);
                return;
            }

            int half = length / sectorSize / 2 * sectorSize;
            foreach (var (s, l) in new[] { (start, half), (start + half, length - half) })
            {
                if (Exhausted)
                {
                    Mark(s, l, sectorSize, badSector);
                    continue;
                }
                var t = Stopwatch.StartNew();
                if (io(s, l))
                {
                    Spent += t.Elapsed;
                    continue;
                }
                Failed(t.Elapsed, s, l, sectorSize, badSector, io);
            }
        }

        private static void Mark(int start, int length, int sectorSize, bool[] badSector)
        {
            for (int i = start / sectorSize; i < (start + length) / sectorSize; i++) badSector[i] = true;
        }
    }

    private static bool TryRead(IBlockDevice device, long offset, byte[] buffer, int start, int length)
    {
        try
        {
            device.Read(offset, buffer.AsSpan(start, length));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool TryWrite(IBlockDevice device, long offset, byte[] buffer, int start, int length)
    {
        try
        {
            device.Write(offset, buffer.AsSpan(start, length));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
