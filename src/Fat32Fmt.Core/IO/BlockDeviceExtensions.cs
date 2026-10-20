namespace Fat32Fmt.Core.IO;

public static class BlockDeviceExtensions
{
    /// <summary>I/O の既定チャンクサイズ。</summary>
    public const int ChunkSize = 4 << 20;

    /// <summary>範囲を 0 で埋める。</summary>
    public static void Zero(this IBlockDevice device, long offset, long length,
        IProgress<OperationProgress>? progress = null, string phase = "ゼロ埋め", CancellationToken ct = default)
    {
        if (length <= 0)
        {
            return;
        }

        var zeros = new byte[(int)Math.Min(ChunkSize, length)];
        var tracker = new ProgressTracker(progress, phase, length);
        long done = 0;
        while (done < length)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(zeros.Length, length - done);
            device.Write(offset + done, zeros.AsSpan(0, n));
            done += n;
            tracker.Report(done);
        }

        tracker.Complete();
    }

    /// <summary>セクタ境界にそろっていない範囲を読む(内部で前後のセクタを読み足す)。デバイス末尾以降は 0 になる。</summary>
    public static void ReadUnaligned(this IBlockDevice device, long offset, Span<byte> buffer)
    {
        buffer.Clear();
        if (offset >= device.Length || buffer.IsEmpty)
        {
            return;
        }

        int ss = device.SectorSize;
        long start = offset - offset % ss;
        long end = offset + buffer.Length;
        end = (end + ss - 1) / ss * ss;
        end = Math.Min(end, device.Length);
        var tmp = new byte[end - start];
        device.Read(start, tmp);
        int available = (int)Math.Min(buffer.Length, end - offset);
        tmp.AsSpan((int)(offset - start), available).CopyTo(buffer);
    }
}
