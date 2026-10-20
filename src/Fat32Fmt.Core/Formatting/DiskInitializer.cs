using Fat32Fmt.Core.Fat32;
using Fat32Fmt.Core.IO;

namespace Fat32Fmt.Core.Formatting;

public sealed record DiskInitResult(PartitionPlan Partition, Fat32FormatResult Format);

/// <summary>
/// 空のディスク全体に、SD カードと同じ「MBR + FAT32 パーティション 1 個」を作る。
/// 消去後のカードはパーティションもドライブ文字も無いので、そのままでは通常のフォーマットができないため。
/// </summary>
public static class DiskInitializer
{
    public static DiskInitResult InitializeMbrFat32(IBlockDevice disk, string? label = null,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        long align = SdCardGeometry.BoundaryUnitBytes(disk.Length);
        var part = PartitionPlanner.PlanSinglePartition(disk.Length, disk.SectorSize,
            align > 0 ? align : PartitionPlanner.DefaultAlignmentBytes);
        var volume = new SubBlockDevice(disk, part.StartBytes, part.LengthBytes);
        int cluster = ClusterSizePolicy.Default(volume.Length, volume.SectorSize);
        var layout = Fat32LayoutCalculator.Calculate(volume.Length, volume.SectorSize, cluster, part.StartBytes, align);
        var result = Fat32Formatter.Format(volume, layout, new Fat32FormatOptions { Label = label }, progress, ct);

        // MBR は最後に書く(途中で失敗したとき、中途半端なパーティションを Windows に見せない)
        disk.Write(0, MbrBuilder.Build(disk.SectorSize, part.StartBytes, part.LengthBytes, PartitionPlanner.Fat32LbaType,
            result.VolumeId));
        disk.Flush();
        return new DiskInitResult(part, result);
    }
}
