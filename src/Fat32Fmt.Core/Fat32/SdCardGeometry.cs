namespace Fat32Fmt.Core.Fat32;

/// <summary>
/// SD 規格(SD Physical Layer / File System Specification)に沿った配置パラメータ。
/// SD カードのコントローラは「境界単位 (Boundary Unit, BU)」ごとに消去・書き込みを管理するため、
/// パーティション開始とデータ領域開始を BU 境界にそろえると書き込み性能と寿命が改善する。
/// </summary>
public static class SdCardGeometry
{
    private const long MiB = 1L << 20;
    private const long GB = 1_000_000_000L;

    /// <summary>
    /// 容量に応じた境界単位。SDHC(32 GB 以下)は 4 MB、SDXC は 128 GB 以下で 16 MB、それ以上で 32 MB。
    /// </summary>
    public static long BoundaryUnitBytes(long capacityBytes) => capacityBytes switch
    {
        <= 32 * GB + 2 * GB => 4 * MiB, // 表記 32 GB のカードは実容量が少し上下するので余裕を持たせる
        <= 128 * GB + 8 * GB => 16 * MiB,
        _ => 32 * MiB,
    };

    /// <summary>
    /// SD 規格の推奨クラスタサイズ(SDHC/SDXC とも FAT32 では 32 KB)。FAT32 が成立しない場合は調整する。
    /// </summary>
    public static int RecommendedClusterBytes(long volumeBytes, int bytesPerSector = 512) =>
        ClusterSizePolicy.Fit(volumeBytes, bytesPerSector, 32 * 1024);

    /// <summary>容量からカード規格名を推定する(表示用)。</summary>
    public static string CapacityClass(long capacityBytes) => capacityBytes switch
    {
        <= 2 * GB + GB / 10 => "SDSC 相当 (〜2 GB)",
        <= 32 * GB + 2 * GB => "SDHC 相当 (〜32 GB)",
        <= 2000 * GB + 100 * GB => "SDXC 相当 (〜2 TB)",
        _ => "SDUC 相当 (2 TB 超)",
    };
}
