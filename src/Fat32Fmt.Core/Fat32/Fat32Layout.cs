namespace Fat32Fmt.Core.Fat32;

/// <summary>FAT32 ボリュームの配置(予約領域・FAT・データ領域)。</summary>
public sealed record Fat32Layout
{
    public required int BytesPerSector { get; init; }

    public required int SectorsPerCluster { get; init; }

    public required int ReservedSectors { get; init; }

    public required int NumberOfFats { get; init; }

    public required uint FatSizeSectors { get; init; }

    public required uint TotalSectors { get; init; }

    public required uint ClusterCount { get; init; }

    /// <summary>ディスク先頭からパーティション開始までのセクタ数(BPB の HiddSec)。</summary>
    public required uint HiddenSectors { get; init; }

    public int BytesPerCluster => BytesPerSector * SectorsPerCluster;

    public long FatStartSector => ReservedSectors;

    public long DataStartSector => ReservedSectors + (long)NumberOfFats * FatSizeSectors;

    public long DataStartOffset => DataStartSector * BytesPerSector;

    /// <summary>データ領域の先頭がディスク先頭から何バイト目にあるか。</summary>
    public long AbsoluteDataStartOffset => (HiddenSectors + DataStartSector) * BytesPerSector;

    public long VolumeBytes => (long)TotalSectors * BytesPerSector;

    public long DataBytes => (long)ClusterCount * BytesPerCluster;

    /// <summary>クラスタ番号(2 以上)のボリューム先頭からのバイトオフセット。</summary>
    public long ClusterOffset(uint cluster) => DataStartOffset + (long)(cluster - 2) * BytesPerCluster;
}

public sealed class Fat32LayoutException(string message) : Exception(message);

public static class Fat32LayoutCalculator
{
    /// <summary>FAT32 として認識されるクラスタ数の下限(これ未満は FAT16 とみなされる)。</summary>
    public const uint MinClusters = 65525;

    /// <summary>FAT32 のクラスタ数の上限。</summary>
    public const uint MaxClusters = 0x0FFFFFF5;

    public const int MinReservedSectors = 32;

    public const int MaxClusterBytes = 64 * 1024;

    /// <summary>
    /// FAT32 のレイアウトを計算する。
    /// </summary>
    /// <param name="volumeBytes">ボリューム(パーティション)のサイズ。</param>
    /// <param name="bytesPerSector">論理セクタサイズ。</param>
    /// <param name="clusterBytes">クラスタサイズ。</param>
    /// <param name="partitionStartBytes">ディスク先頭からパーティション開始までのバイト数。</param>
    /// <param name="alignmentBytes">
    /// データ領域の開始をディスク先頭からこのバイト数の倍数にそろえる(0 なら整列しない)。
    /// SD カードでは境界単位 (BU) を指定する。
    /// </param>
    public static Fat32Layout Calculate(long volumeBytes, int bytesPerSector, int clusterBytes,
        long partitionStartBytes = 0, long alignmentBytes = 0, int numberOfFats = 2)
    {
        if (bytesPerSector is not (512 or 1024 or 2048 or 4096))
        {
            throw new Fat32LayoutException($"未対応のセクタサイズです: {bytesPerSector}");
        }

        if (clusterBytes < bytesPerSector || clusterBytes > MaxClusterBytes || (clusterBytes & (clusterBytes - 1)) != 0)
        {
            throw new Fat32LayoutException(
                $"クラスタサイズ {SizeFormat.Cluster(clusterBytes)} は指定できません。" +
                $"{SizeFormat.Cluster(bytesPerSector)}〜64 KB の 2 のべき乗を指定してください。");
        }

        if (partitionStartBytes % bytesPerSector != 0 || alignmentBytes % bytesPerSector != 0)
        {
            throw new Fat32LayoutException("パーティション開始位置と整列単位はセクタサイズの倍数である必要があります。");
        }

        long totalSectorsLong = volumeBytes / bytesPerSector;
        if (totalSectorsLong > uint.MaxValue)
        {
            throw new Fat32LayoutException(
                $"ボリュームが大きすぎます。FAT32 は最大 {SizeFormat.Binary((long)uint.MaxValue * bytesPerSector)} までです。");
        }

        uint totalSectors = (uint)totalSectorsLong;
        int spc = clusterBytes / bytesPerSector;
        long entriesPerSector = bytesPerSector / 4;
        long partStart = partitionStartBytes / bytesPerSector;
        long align = Math.Max(1, alignmentBytes / bytesPerSector);
        if (partStart > uint.MaxValue)
        {
            throw new Fat32LayoutException("パーティション開始位置が FAT32 の表現範囲を超えています。");
        }

        long reserved = MinReservedSectors;
        if (totalSectors <= reserved + numberOfFats)
        {
            throw TooSmall(volumeBytes, clusterBytes);
        }

        // FAT サイズ: fat * (entriesPerSector * spc + nFats) >= total - reserved + 2 * spc を満たす最小値
        long fat = CeilDiv(totalSectors - reserved + 2L * spc, entriesPerSector * spc + numberOfFats);

        // データ領域開始を整列させる。予約領域で吸収しきれない分は FAT を伸ばして吸収する。
        long dataStart = reserved + numberOfFats * fat;
        long pad = (align - (partStart + dataStart) % align) % align;
        if (reserved + pad <= ushort.MaxValue)
        {
            reserved += pad;
        }
        else
        {
            fat += pad / numberOfFats;
            reserved += pad % numberOfFats;
        }

        dataStart = reserved + numberOfFats * fat;
        if (dataStart >= totalSectors)
        {
            throw TooSmall(volumeBytes, clusterBytes);
        }

        long clusters = (totalSectors - dataStart) / spc;
        clusters = Math.Min(clusters, fat * entriesPerSector - 2);

        if (clusters < MinClusters)
        {
            throw TooSmall(volumeBytes, clusterBytes);
        }

        if (clusters > MaxClusters)
        {
            throw new Fat32LayoutException(
                $"クラスタサイズ {SizeFormat.Cluster(clusterBytes)} ではクラスタ数が FAT32 の上限を超えます。" +
                "より大きいクラスタサイズを指定してください。");
        }

        return new Fat32Layout
        {
            BytesPerSector = bytesPerSector,
            SectorsPerCluster = spc,
            ReservedSectors = (int)reserved,
            NumberOfFats = numberOfFats,
            FatSizeSectors = (uint)fat,
            TotalSectors = totalSectors,
            ClusterCount = (uint)clusters,
            HiddenSectors = (uint)partStart,
        };
    }

    private static Fat32LayoutException TooSmall(long volumeBytes, int clusterBytes) =>
        new($"{SizeFormat.Binary(volumeBytes)} のボリュームはクラスタサイズ {SizeFormat.Cluster(clusterBytes)} では" +
            $"クラスタ数が FAT32 の下限 ({MinClusters}) に届きません。より小さいクラスタサイズを指定してください。");

    private static long CeilDiv(long a, long b) => (a + b - 1) / b;
}
