namespace Fat32Fmt.Core.Fat32;

/// <summary>クラスタサイズの選択肢と既定値。</summary>
public static class ClusterSizePolicy
{
    public static readonly IReadOnlyList<int> Candidates =
        [512, 1024, 2048, 4096, 8192, 16384, 32768, 65536];

    /// <summary>
    /// 容量に応じた既定のクラスタサイズ(Windows の FAT32 既定値に準拠し、32 GB 超は 32 KB)。
    /// FAT32 として成立しない場合は成立するサイズに調整する。
    /// </summary>
    public static int Default(long volumeBytes, int bytesPerSector = 512)
    {
        const long MiB = 1L << 20;
        const long GiB = 1L << 30;
        int preferred = volumeBytes switch
        {
            <= 64 * MiB => 512,
            <= 128 * MiB => 1024,
            <= 256 * MiB => 2048,
            <= 8 * GiB => 4096,
            <= 16 * GiB => 8192,
            <= 32 * GiB => 16384,
            _ => 32768,
        };
        return Fit(volumeBytes, bytesPerSector, preferred);
    }

    /// <summary>
    /// 希望サイズで FAT32 が成立しなければ、成立する最も近いサイズに調整する。どのサイズでも成立しない場合は例外。
    /// </summary>
    public static int Fit(long volumeBytes, int bytesPerSector, int preferred)
    {
        int size = Math.Max(preferred, bytesPerSector);
        foreach (int candidate in OrderByDistance(size, bytesPerSector))
        {
            if (IsValid(volumeBytes, bytesPerSector, candidate))
            {
                return candidate;
            }
        }

        throw new Fat32LayoutException(
            $"{SizeFormat.Binary(volumeBytes)} のボリュームは FAT32 でフォーマットできません(小さすぎるか大きすぎます)。");
    }

    public static bool IsValid(long volumeBytes, int bytesPerSector, int clusterBytes)
    {
        try
        {
            Fat32LayoutCalculator.Calculate(volumeBytes, bytesPerSector, clusterBytes);
            return true;
        }
        catch (Fat32LayoutException)
        {
            return false;
        }
    }

    /// <summary>指定サイズで成立するクラスタサイズの一覧。</summary>
    public static IReadOnlyList<int> ValidSizes(long volumeBytes, int bytesPerSector) =>
        Candidates.Where(c => c >= bytesPerSector && IsValid(volumeBytes, bytesPerSector, c)).ToList();

    private static IEnumerable<int> OrderByDistance(int preferred, int bytesPerSector) =>
        Candidates.Where(c => c >= bytesPerSector)
            .OrderBy(c => Math.Abs(Math.Log2(c) - Math.Log2(preferred)))
            .ThenByDescending(c => c);
}
