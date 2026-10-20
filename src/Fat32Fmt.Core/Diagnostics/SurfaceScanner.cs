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

    /// <summary>1 回に読み書きするバイト数。エラーが出た区間はセクタ単位で読み直して不良セクタを特定する。</summary>
    public int ChunkBytes { get; init; } = 1 << 20;

    /// <summary>1 区間の読み取りにこれ以上かかったら「遅い区間」として数える（劣化の兆候）。</summary>
    public TimeSpan SlowThreshold { get; init; } = TimeSpan.FromMilliseconds(500);

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
    double BytesPerSecond,
    TimeSpan Elapsed,
    string Summary)
{
    public bool Ok => Completed && BadSectors == 0;
}

/// <summary>不良セクタの読み取りスキャン（非破壊）と、書き込み＋読み戻しスキャン（破壊）。</summary>
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
        int readErrors = 0, writeErrors = 0, slow = 0, consecutiveBad = 0;
        long mismatch = 0, scanned = 0;
        bool completed = true;
        var tracker = new ProgressTracker(progress, write ? "書き込みスキャン" : "読み取りスキャン", length);

        for (long off = 0; off < length; off += chunk)
        {
            if (ct.IsCancellationRequested) { completed = false; break; }
            int n = (int)Math.Min(chunk, length - off);
            int sectors = n / ss;
            var badSector = new bool[sectors];

            if (write)
            {
                pattern!.Fill(off, expected.AsSpan(0, n));
                if (!TryWrite(device, off, expected, 0, n))
                {
                    writeErrors++;
                    for (int s = 0; s < sectors; s++)
                        if (!TryWrite(device, off + (long)s * ss, expected, s * ss, ss)) badSector[s] = true;
                }
            }

            var t = Stopwatch.StartNew();
            bool readOk = TryRead(device, off, buf, 0, n);
            if (t.Elapsed > opt.SlowThreshold) slow++;
            if (!readOk)
            {
                // 区間のどこが悪いのか、セクタ単位で読み直して特定する
                readErrors++;
                for (int s = 0; s < sectors; s++)
                {
                    if (TryRead(device, off + (long)s * ss, buf, s * ss, ss)) continue;
                    badSector[s] = true;
                    buf.AsSpan(s * ss, ss).Clear();
                }
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
            if (consecutiveBad >= opt.MaxConsecutiveBadChunks) { completed = false; break; }
        }
        tracker.Complete();

        long badSectors = bad.TotalBytes / ss;
        string summary;
        if (!completed && consecutiveBad >= opt.MaxConsecutiveBadChunks)
            summary = $"{SizeFormat.Binary(scanned)} の位置で読み書きできない区間が続いたため打ち切りました（カードが抜けたか、応答しません）。不良セクタ {badSectors:N0} 個。";
        else if (!completed)
            summary = $"中断しました（{SizeFormat.Binary(scanned)} / {SizeFormat.Binary(length)} を検査）。ここまでの不良セクタ {badSectors:N0} 個。";
        else if (badSectors == 0)
            summary = $"不良セクタはありません（{SizeFormat.Both(length)} を{(write ? "書き込み・読み戻し" : "読み取り")}検査）。" +
                      (slow > 0 ? $" ただし読み取りが遅い区間が {slow} か所あります。" : "");
        else
            summary = $"不良セクタ {badSectors:N0} 個（{bad.Count} か所、計 {SizeFormat.Binary(bad.TotalBytes)}）が見つかりました。重要なデータには使わないでください。";

        return new SurfaceScanReport(opt.Mode, completed, length, scanned, ss, badSectors, bad.Count, bad.Ranges,
            readErrors, writeErrors, mismatch, slow, sw.Elapsed.TotalSeconds > 0 ? scanned / sw.Elapsed.TotalSeconds : 0, sw.Elapsed, summary);
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
