namespace Fat32Fmt.Core.Fat32;

/// <summary>MBR に作成する単一パーティションの配置。</summary>
public sealed record PartitionPlan(long StartBytes, long LengthBytes, bool Truncated);

public static class PartitionPlanner
{
    /// <summary>FAT32 (LBA) のパーティションタイプ。</summary>
    public const byte Fat32LbaType = 0x0C;

    public const long DefaultAlignmentBytes = 1L << 20;

    /// <summary>
    /// ディスク全体を使う 1 パーティションを計画する。開始位置は整列単位(SD なら BU、それ以外は 1 MiB)。
    /// MBR / FAT32 の 32 ビットセクタ数の上限を超える部分は使わない。
    /// </summary>
    public static PartitionPlan PlanSinglePartition(long diskBytes, int bytesPerSector, long alignmentBytes)
    {
        long align = Math.Max(alignmentBytes, bytesPerSector);
        align -= align % bytesPerSector;
        long start = align;
        long length = diskBytes - start;
        length -= length % bytesPerSector;
        if (length <= 0)
        {
            throw new Fat32LayoutException("ディスクが小さすぎてパーティションを作成できません。");
        }

        long maxLength = (long)uint.MaxValue * bytesPerSector;
        bool truncated = false;
        if (start / bytesPerSector + length / bytesPerSector > uint.MaxValue || length > maxLength)
        {
            length = Math.Min(maxLength, ((long)uint.MaxValue - start / bytesPerSector) * bytesPerSector);
            truncated = true;
        }

        return new PartitionPlan(start, length, truncated);
    }
}

/// <summary>MBR セクタを組み立てる(イメージファイル向け。実ディスクは IOCTL で作成する)。</summary>
public static class MbrBuilder
{
    public static byte[] Build(int bytesPerSector, long partitionStartBytes, long partitionLengthBytes, byte partitionType, uint diskSignature)
    {
        var mbr = new byte[bytesPerSector];
        uint lbaStart = checked((uint)(partitionStartBytes / bytesPerSector));
        uint lbaCount = checked((uint)(partitionLengthBytes / bytesPerSector));

        BitConverter.TryWriteBytes(mbr.AsSpan(440), diskSignature);
        Span<byte> entry = mbr.AsSpan(446, 16);
        entry[0] = 0x00; // 非アクティブ
        WriteChs(entry[1..4], lbaStart);
        entry[4] = partitionType;
        WriteChs(entry[5..8], lbaStart + lbaCount - 1);
        BitConverter.TryWriteBytes(entry[8..], lbaStart);
        BitConverter.TryWriteBytes(entry[12..], lbaCount);
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        return mbr;
    }

    private static void WriteChs(Span<byte> dst, uint lba)
    {
        const int Heads = 255;
        const int Sectors = 63;
        uint cylinder = lba / (Heads * Sectors);
        if (cylinder > 1023)
        {
            dst[0] = 0xFE;
            dst[1] = 0xFF;
            dst[2] = 0xFF;
            return;
        }

        uint head = lba / Sectors % Heads;
        uint sector = lba % Sectors + 1;
        dst[0] = (byte)head;
        dst[1] = (byte)(sector | ((cylinder >> 2) & 0xC0));
        dst[2] = (byte)cylinder;
    }
}
