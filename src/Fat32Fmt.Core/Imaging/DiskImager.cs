using System.Security.Cryptography;
using Fat32Fmt.Core.IO;

namespace Fat32Fmt.Core.Imaging;

/// <summary>バックアップの結果。</summary>
/// <param name="Bytes">コピーしたバイト数。</param>
/// <param name="Sha256">コピーした内容の SHA-256(小文字 16 進)。</param>
/// <param name="ZeroBytesSkipped">0 だけのため書き込みを省いたバイト数(スパースイメージの穴になる)。</param>
public sealed record ImageResult(long Bytes, string Sha256, long ZeroBytesSkipped, TimeSpan Elapsed);

/// <summary>書き戻しの結果。</summary>
/// <param name="Bytes">書き込んだバイト数。</param>
/// <param name="ImageSha256">イメージ全体の SHA-256。</param>
/// <param name="WrittenSha256">書き込んだ範囲のイメージの SHA-256(イメージがディスクより大きく末尾を切った場合のみ ImageSha256 と異なる)。</param>
/// <param name="ReadbackSha256">書き込み後にディスクから読み戻した範囲の SHA-256。</param>
/// <param name="ExpectedSha256">バックアップ時に記録されたハッシュ(.sha256 ファイル)。無ければ null。</param>
public sealed record RestoreResult(long Bytes, string ImageSha256, string WrittenSha256, string ReadbackSha256,
    string? ExpectedSha256, long TruncatedZeroBytes, TimeSpan Elapsed)
{
    /// <summary>読み戻した内容がイメージと一致した。</summary>
    public bool Verified => string.Equals(WrittenSha256, ReadbackSha256, StringComparison.Ordinal);

    /// <summary>イメージがバックアップ時から変わっていない(記録が無い場合は true)。</summary>
    public bool MatchesRecordedHash =>
        ExpectedSha256 is null || string.Equals(ExpectedSha256, ImageSha256, StringComparison.OrdinalIgnoreCase);
}

/// <summary>書き込み後の照合で不一致が見つかった。</summary>
public sealed class DataVerificationException : IOException
{
    public DataVerificationException(string message, long offset = -1)
        : base(message)
    {
        Offset = offset;
    }

    /// <summary>不一致が見つかった位置(分からなければ -1)。</summary>
    public long Offset { get; }
}

/// <summary>ディスク全体とイメージファイルの間のコピー(バックアップ / 書き戻し)。デバイスの種類には依存しない。</summary>
public static class DiskImager
{
    /// <summary>
    /// ディスクの内容をイメージへコピーする。
    /// </summary>
    /// <param name="imageIsZeroFilled">イメージが作りたてで全体が 0 と読める(スパースファイルなど)なら true。0 だけのチャンクの書き込みを省く。</param>
    public static ImageResult Backup(IBlockDevice disk, IBlockDevice image, bool imageIsZeroFilled,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        if (image.Length < disk.Length)
        {
            throw new ArgumentException($"イメージ ({image.Length:N0} B) がディスク ({disk.Length:N0} B) より小さいです。");
        }

        if (image.SectorSize != disk.SectorSize)
        {
            throw new ArgumentException($"セクタサイズが異なります (ディスク {disk.SectorSize} B / イメージ {image.SectorSize} B)。");
        }

        var started = DateTime.UtcNow;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long skipped = 0;
        var tracker = new ProgressTracker(progress, "バックアップ", disk.Length);
        var buf = new byte[BlockDeviceExtensions.ChunkSize];
        for (long off = 0; off < disk.Length;)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buf.Length, disk.Length - off);
            var chunk = buf.AsSpan(0, n);
            disk.Read(off, chunk);
            sha.AppendData(chunk);
            if (imageIsZeroFilled && chunk.IndexOfAnyExcept((byte)0) < 0)
            {
                skipped += n;
            }
            else
            {
                image.Write(off, chunk);
            }

            off += n;
            tracker.Report(off);
        }

        image.Flush();
        tracker.Complete();
        return new ImageResult(disk.Length, Hex(sha), skipped, DateTime.UtcNow - started);
    }

    /// <summary>
    /// イメージをディスクへ書き戻し、読み戻してハッシュで照合する。不一致なら <see cref="DataVerificationException"/>。
    /// イメージがディスクより大きい場合、はみ出す部分がすべて 0 なら切り捨てて書く(同じ公称容量でも実容量は個体差があるため)。
    /// </summary>
    /// <param name="expectedSha256">バックアップ時に記録したハッシュ。結果に含めるだけで、不一致でも例外にはしない。</param>
    public static RestoreResult Restore(IBlockDevice image, IBlockDevice disk, string? expectedSha256 = null,
        IProgress<OperationProgress>? progress = null, Action<string>? log = null, CancellationToken ct = default)
    {
        if (image.SectorSize != disk.SectorSize)
        {
            throw new ArgumentException($"セクタサイズが異なります (イメージ {image.SectorSize} B / ディスク {disk.SectorSize} B)。");
        }

        var started = DateTime.UtcNow;
        long writeLength = Math.Min(image.Length, disk.Length);
        long tail = image.Length - writeLength;
        if (tail > 0 && !IsZero(image, writeLength, tail, ct))
        {
            throw new ArgumentException(
                $"イメージ ({SizeFormat.Both(image.Length)}) がディスク ({SizeFormat.Both(disk.Length)}) より大きく、はみ出す部分にデータがあります。");
        }

        if (tail > 0)
        {
            log?.Invoke($"イメージ末尾の {SizeFormat.Binary(tail)} はすべて 0 なので、ディスクに収まる範囲だけを書き込みます。");
        }
        else if (writeLength < disk.Length)
        {
            log?.Invoke($"イメージはディスクより {SizeFormat.Binary(disk.Length - writeLength)} 小さいため、ディスク末尾は変更しません。");
        }

        using var written = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var full = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[BlockDeviceExtensions.ChunkSize];
        var tracker = new ProgressTracker(progress, "書き戻し", writeLength);
        for (long off = 0; off < writeLength;)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buf.Length, writeLength - off);
            var chunk = buf.AsSpan(0, n);
            image.Read(off, chunk);
            written.AppendData(chunk);
            full.AppendData(chunk);
            disk.Write(off, chunk);
            off += n;
            tracker.Report(off);
        }

        disk.Flush();
        tracker.Complete();
        if (tail > 0)
        {
            Array.Clear(buf);
            for (long left = tail; left > 0; left -= Math.Min(left, buf.Length))
            {
                full.AppendData(buf, 0, (int)Math.Min(left, buf.Length));
            }
        }

        string writtenHash = Hex(written);
        string imageHash = Hex(full);
        string readback = Hash(disk, writeLength, progress, "照合(読み戻し)", ct);
        var result = new RestoreResult(writeLength, imageHash, writtenHash, readback, expectedSha256?.ToLowerInvariant(), tail,
            DateTime.UtcNow - started);
        if (!result.Verified)
        {
            throw new DataVerificationException(
                $"書き戻し後の照合に失敗しました。イメージ SHA-256 {writtenHash} に対し、ディスクから読み戻した内容は {readback} でした。カードの不良や容量偽装の可能性があります。");
        }

        return result;
    }

    /// <summary>デバイス先頭から length バイトの SHA-256 を計算する。</summary>
    public static string Hash(IBlockDevice device, long length, IProgress<OperationProgress>? progress = null,
        string phase = "ハッシュ計算", CancellationToken ct = default)
    {
        if (length < 0 || length > device.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[BlockDeviceExtensions.ChunkSize];
        var tracker = new ProgressTracker(progress, phase, length);
        for (long off = 0; off < length;)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buf.Length, length - off);
            device.Read(off, buf.AsSpan(0, n));
            sha.AppendData(buf, 0, n);
            off += n;
            tracker.Report(off);
        }

        tracker.Complete();
        return Hex(sha);
    }

    private static bool IsZero(IBlockDevice device, long offset, long length, CancellationToken ct)
    {
        var buf = new byte[(int)Math.Min(BlockDeviceExtensions.ChunkSize, length)];
        for (long done = 0; done < length;)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buf.Length, length - done);
            device.Read(offset + done, buf.AsSpan(0, n));
            if (buf.AsSpan(0, n).IndexOfAnyExcept((byte)0) >= 0)
            {
                return false;
            }

            done += n;
        }

        return true;
    }

    private static string Hex(IncrementalHash h) => Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
}
