using Fat32Fmt.Core.Fat32;
using Xunit;

namespace Fat32Fmt.Tests;

public class LayoutTests
{
    private const long MiB = 1L << 20, GiB = 1L << 30, TiB = 1L << 40;

    // 実機 (128GB SDXC): パーティション 1MiB 開始、長さ 127,860,211,712 B、ディスク 127,865,454,592 B
    private const long Sdxc128Volume = 127_860_211_712;
    private const long Sdxc128Disk = 127_865_454_592;

    private static void AssertValid(Fat32Layout l)
    {
        Assert.InRange(l.ClusterCount, Fat32LayoutCalculator.MinClusters, Fat32LayoutCalculator.MaxClusters);
        Assert.InRange(l.ReservedSectors, Fat32LayoutCalculator.MinReservedSectors, ushort.MaxValue);
        Assert.True((l.ClusterCount + 2L) * 4 <= (long)l.FatSizeSectors * l.BytesPerSector, "FAT が全クラスタ分のエントリを収容できていない");
        Assert.True(l.DataStartSector < l.TotalSectors);
        Assert.True(l.DataStartSector + (long)l.ClusterCount * l.SectorsPerCluster <= l.TotalSectors, "データ領域がボリュームを超えている");
        Assert.True(l.SectorsPerCluster is >= 1 and <= 128);
    }

    [Fact]
    public void Sdxc128GB_DefaultIs32K_AlignedToSdBoundaryUnit()
    {
        long bu = SdCardGeometry.BoundaryUnitBytes(Sdxc128Disk);
        Assert.Equal(16 * MiB, bu);

        int cluster = ClusterSizePolicy.Default(Sdxc128Volume);
        Assert.Equal(32768, cluster);

        var l = Fat32LayoutCalculator.Calculate(Sdxc128Volume, 512, cluster, 1 * MiB, bu);
        AssertValid(l);
        Assert.Equal(64, l.SectorsPerCluster);
        Assert.Equal(2048u, l.HiddenSectors);
        Assert.Equal(0, l.AbsoluteDataStartOffset % bu);
        Assert.Equal((uint)(Sdxc128Volume / 512), l.TotalSectors);
        Assert.InRange(l.FatSizeSectors, 30_000u, 31_000u); // 約 3.9M クラスタ × 4 B ≒ 15.6 MB
    }

    [Theory]
    [InlineData(40 * MiB)]
    [InlineData(100 * MiB)]
    [InlineData(1 * GiB)]
    [InlineData(8 * GiB)]
    [InlineData(32 * GiB)]
    [InlineData(64 * GiB)]
    [InlineData(256 * GiB)]
    [InlineData(512 * GiB)]
    [InlineData(1 * TiB)]
    [InlineData(2 * TiB - 1 * MiB)]
    public void DefaultClusterAndSdAlignment_ProduceValidLayout(long size)
    {
        long bu = SdCardGeometry.BoundaryUnitBytes(size + MiB);
        var l = Fat32LayoutCalculator.Calculate(size, 512, ClusterSizePolicy.Default(size), 1 * MiB, bu);
        AssertValid(l);
        Assert.Equal(0, l.AbsoluteDataStartOffset % bu);
    }

    [Theory]
    [InlineData(64 * GiB, 4096)]
    [InlineData(64 * GiB, 65536)]
    [InlineData(128 * GiB, 16384)]
    [InlineData(2 * TiB - 1 * MiB, 32768)]
    public void ExplicitClusterSize_IsHonoured(long size, int cluster)
    {
        var l = Fat32LayoutCalculator.Calculate(size, 512, cluster, 1 * MiB, 4 * MiB);
        AssertValid(l);
        Assert.Equal(cluster, l.BytesPerCluster);
    }

    [Fact]
    public void VolumeLargerThan2TiB_Throws()
    {
        Assert.Throws<Fat32LayoutException>(() => Fat32LayoutCalculator.Calculate(3 * TiB, 512, 32768));
    }

    [Fact]
    public void ClusterTooLargeForSmallVolume_Throws()
    {
        // 100 MiB / 64 KiB = 1600 クラスタ → 下限 65525 未満
        Assert.Throws<Fat32LayoutException>(() => Fat32LayoutCalculator.Calculate(100 * MiB, 512, 65536));
    }

    [Fact]
    public void ClusterTooSmallForHugeVolume_Throws()
    {
        Assert.Throws<Fat32LayoutException>(() => Fat32LayoutCalculator.Calculate(2 * TiB - MiB, 512, 4096));
    }

    [Fact]
    public void ValidSizes_ExcludesClustersOutsideFat32Limits()
    {
        // 128 GB は 512 B クラスタでも約 2.5 億 < 上限 268,435,445 なので全サイズが有効
        Assert.Equal(ClusterSizePolicy.Candidates, ClusterSizePolicy.ValidSizes(Sdxc128Volume, 512));

        // 300 GiB では 512 B / 1 KiB だとクラスタ数が上限 (約 2.68 億) を超える
        var sizes = ClusterSizePolicy.ValidSizes(300 * GiB, 512);
        Assert.DoesNotContain(512, sizes);
        Assert.DoesNotContain(1024, sizes);
        Assert.Contains(2048, sizes);
        Assert.Contains(65536, sizes);

        // 100 MiB では 32 KiB 以上だとクラスタ数が下限に届かない
        var small = ClusterSizePolicy.ValidSizes(100 * MiB, 512);
        Assert.Contains(512, small);
        Assert.DoesNotContain(32768, small);
        Assert.DoesNotContain(65536, small);
    }

    [Fact]
    public void FourKSectors_Work()
    {
        var l = Fat32LayoutCalculator.Calculate(64 * GiB, 4096, 32768, 1 * MiB, 16 * MiB);
        AssertValid(l);
        Assert.Equal(8, l.SectorsPerCluster);
    }

    [Theory]
    [InlineData(32_000_000_000L, 4 * MiB)]
    [InlineData(64_000_000_000L, 16 * MiB)]
    [InlineData(128_000_000_000L, 16 * MiB)]
    [InlineData(256_000_000_000L, 32 * MiB)]
    public void BoundaryUnit_FollowsSdSpec(long capacity, long expected)
    {
        Assert.Equal(expected, SdCardGeometry.BoundaryUnitBytes(capacity));
    }
}
