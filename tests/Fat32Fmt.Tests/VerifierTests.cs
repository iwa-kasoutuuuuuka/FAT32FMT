using Fat32Fmt.Core.Diagnostics;
using Fat32Fmt.Core.Fat32;
using Fat32Fmt.Core.IO;
using Fat32Fmt.Core.Windows;
using Xunit;

namespace Fat32Fmt.Tests;

public class VerifierTests
{
    private const long MiB = 1L << 20, GiB = 1L << 30;

    private static Fat32FormatResult FormatVolume(IBlockDevice dev, long partitionOffset, string? label = "VERIFY")
    {
        var layout = Fat32LayoutCalculator.Calculate(dev.Length, dev.SectorSize, ClusterSizePolicy.Default(dev.Length, dev.SectorSize),
            partitionOffset, SdCardGeometry.BoundaryUnitBytes(partitionOffset + dev.Length));
        return Fat32Formatter.Format(dev, layout, new Fat32FormatOptions { Label = label });
    }

    [Fact]
    public void FreshFormat_PassesWithoutWarnings()
    {
        using var dev = new StreamBlockDevice(new SparseMemoryStream(64 * GiB), 512);
        var result = FormatVolume(dev, 0);

        var report = Fat32Verifier.Verify(dev);
        Assert.True(report.Ok, string.Join(" / ", report.Errors));
        Assert.Empty(report.Warnings);
        Assert.Equal("VERIFY", report.Label);
        Assert.Equal(result.VolumeId, report.VolumeId);
        Assert.Equal(result.Layout.ClusterCount - 1L, report.FreeClusters);
        Assert.Equal((uint)report.FreeClusters, report.FsInfoFreeClusters);
    }

    [Fact]
    public void NoLabel_ReportsEmptyLabel()
    {
        using var dev = new StreamBlockDevice(new SparseMemoryStream(1 * GiB), 512);
        FormatVolume(dev, 0, label: null);
        var report = Fat32Verifier.Verify(dev);
        Assert.True(report.Ok);
        Assert.Equal("", report.Label);
    }

    [Fact]
    public void CorruptedBackupBootSector_IsAnError()
    {
        using var dev = new StreamBlockDevice(new SparseMemoryStream(1 * GiB), 512);
        FormatVolume(dev, 0);
        var junk = new byte[512];
        junk[0] = 0xAB;
        dev.Write(Fat32BootSector.BackupBootSector * 512L, junk);

        var report = Fat32Verifier.Verify(dev);
        Assert.False(report.Ok);
        Assert.Contains(report.Errors, e => e.Contains("バックアップブートセクタ"));
    }

    [Fact]
    public void MismatchedFatCopies_IsAnError()
    {
        using var dev = new StreamBlockDevice(new SparseMemoryStream(1 * GiB), 512);
        var result = FormatVolume(dev, 0);
        var l = result.Layout;
        var sector = new byte[512];
        sector[100] = 0x01; // 2 番目の FAT だけ書き換える
        dev.Write((l.FatStartSector + l.FatSizeSectors) * 512, sector);

        var report = Fat32Verifier.Verify(dev);
        Assert.False(report.Ok);
        Assert.Contains(report.Errors, e => e.Contains("FAT のコピー"));
    }

    [Fact]
    public void NotFat32_IsAnError()
    {
        using var dev = new StreamBlockDevice(new SparseMemoryStream(64 * MiB), 512);
        var report = Fat32Verifier.Verify(dev);
        Assert.False(report.Ok);
        Assert.Null(report.Layout);
    }

    [Fact]
    public void MbrImage_PartitionIsLocatedAndAligned()
    {
        const long size = 128_000_000_000; // 表記 128 GB のカード (BU 16 MiB)
        using var disk = new StreamBlockDevice(new SparseMemoryStream(size), 512);
        long bu = SdCardGeometry.BoundaryUnitBytes(size);
        var part = PartitionPlanner.PlanSinglePartition(size, 512, bu);
        var result = FormatVolume(new SubBlockDevice(disk, part.StartBytes, part.LengthBytes), part.StartBytes);
        disk.Write(0, MbrBuilder.Build(512, part.StartBytes, part.LengthBytes, PartitionPlanner.Fat32LbaType, result.VolumeId));

        var vol = Fat32Verifier.LocateVolume(disk, out long offset);
        Assert.Equal(16 * MiB, offset);
        var report = Fat32Verifier.Verify(vol);
        Assert.True(report.Ok, string.Join(" / ", report.Errors));
        Assert.Equal((uint)(offset / 512), report.Layout!.HiddenSectors);
        Assert.Equal(0, report.Layout.AbsoluteDataStartOffset % bu);
    }

    [Fact]
    public void SuperFloppyImage_IsUsedAsIs()
    {
        using var disk = new StreamBlockDevice(new SparseMemoryStream(1 * GiB), 512);
        FormatVolume(disk, 0);
        Assert.Same(disk, Fat32Verifier.LocateVolume(disk, out long offset));
        Assert.Equal(0, offset);
    }

    [Fact]
    public void SparseImage_CanBeLargerThanFreeSpace_AndVerifies()
    {
        string path = Path.Combine(Path.GetTempPath(), $"fat32fmt-sparse-{Guid.NewGuid():N}.img");
        try
        {
            using (var dev = SparseImage.Create(path, 4L << 40)) // 4 TiB: 空き容量より確実に大きい
            {
                Assert.Equal(4L << 40, dev.Length);
                var sub = new SubBlockDevice(dev, 0, 64 * GiB);
                FormatVolume(sub, 0);
            }

            Assert.True(new FileInfo(path).Attributes.HasFlag(FileAttributes.SparseFile));
            using var img = StreamBlockDevice.OpenImage(path, writable: false);
            Assert.True(Fat32Verifier.Verify(new SubBlockDevice(img, 0, 64 * GiB)).Ok);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SystemDiskNumber_IsKnown_AndSystemDriveIsOnIt()
    {
        Assert.True(DriveEnumerator.SystemDiskNumber >= 0);
        Assert.True(DriveEnumerator.Describe(DriveEnumerator.SystemDriveLetter).IsOnSystemDisk);
    }
}
