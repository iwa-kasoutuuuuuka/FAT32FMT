using System.Buffers.Binary;
using System.Text;
using Fat32Fmt.Core.Fat32;
using Fat32Fmt.Core.IO;
using Xunit;

namespace Fat32Fmt.Tests;

public class FormatterTests
{
    private const long MiB = 1L << 20, GiB = 1L << 30;

    private static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    private static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);

    private static byte[] ReadSector(IBlockDevice dev, long sector)
    {
        var b = new byte[dev.SectorSize];
        dev.Read(sector * dev.SectorSize, b);
        return b;
    }

    [Fact]
    public void Format64GiB_WritesConsistentOnDiskStructures()
    {
        const long size = 64 * GiB;
        using var dev = new StreamBlockDevice(new SparseMemoryStream(size), 512);
        var layout = Fat32LayoutCalculator.Calculate(size, 512, 32768, 1 * MiB, 16 * MiB);
        var when = new DateTime(2026, 10, 20, 15, 30, 44);
        var opt = new Fat32FormatOptions { Label = "testcard", VolumeId = 0x12345678, Timestamp = when };

        var result = Fat32Formatter.Format(dev, layout, opt);
        Assert.Empty(result.BadClusters);
        Assert.Equal(0x12345678u, result.VolumeId);

        // ---- ブートセクタ ----
        var boot = ReadSector(dev, 0);
        Assert.Equal(new byte[] { 0xEB, 0x58, 0x90 }, boot[..3]);
        Assert.Equal("MSWIN4.1", Encoding.ASCII.GetString(boot, 3, 8));
        Assert.Equal(512, U16(boot, 11));
        Assert.Equal(layout.SectorsPerCluster, boot[13]);
        Assert.Equal(layout.ReservedSectors, U16(boot, 14));
        Assert.Equal(2, boot[16]);
        Assert.Equal(0, U16(boot, 17));                 // RootEntCnt
        Assert.Equal(0, U16(boot, 19));                 // TotSec16
        Assert.Equal(0xF8, boot[21]);
        Assert.Equal(0, U16(boot, 22));                 // FATSz16
        Assert.Equal(2048u, U32(boot, 28));             // HiddSec = 1MiB / 512
        Assert.Equal(layout.TotalSectors, U32(boot, 32));
        Assert.Equal(layout.FatSizeSectors, U32(boot, 36));
        Assert.Equal(0, U16(boot, 40));                 // ExtFlags
        Assert.Equal(0, U16(boot, 42));                 // FSVer
        Assert.Equal(2u, U32(boot, 44));                // RootClus
        Assert.Equal(1, U16(boot, 48));                 // FSInfo
        Assert.Equal(6, U16(boot, 50));                 // BkBootSec
        Assert.Equal(0x80, boot[64]);
        Assert.Equal(0x29, boot[66]);
        Assert.Equal(0x12345678u, U32(boot, 67));
        Assert.Equal("TESTCARD   ", Encoding.ASCII.GetString(boot, 71, 11));
        Assert.Equal("FAT32   ", Encoding.ASCII.GetString(boot, 82, 8));
        Assert.Equal(0xAA55, U16(boot, 510));

        // バックアップブートセクタ (6) は同一
        Assert.Equal(boot, ReadSector(dev, 6));

        // ---- FSInfo (1, 7) ----
        var fsinfo = ReadSector(dev, 1);
        Assert.Equal(0x41615252u, U32(fsinfo, 0));
        Assert.Equal(0x61417272u, U32(fsinfo, 484));
        Assert.Equal(layout.ClusterCount - 1, U32(fsinfo, 488));
        Assert.Equal(3u, U32(fsinfo, 492));
        Assert.Equal(0xAA55, U16(fsinfo, 510));
        Assert.Equal(fsinfo, ReadSector(dev, 7));

        // ---- FAT ×2 ----
        for (int f = 0; f < 2; f++)
        {
            var fat = ReadSector(dev, layout.FatStartSector + (long)f * layout.FatSizeSectors);
            Assert.Equal(0x0FFFFFF8u, U32(fat, 0));
            Assert.Equal(0x0FFFFFFFu, U32(fat, 4));
            Assert.Equal(0x0FFFFFFFu, U32(fat, 8));
            Assert.True(fat.AsSpan(12).IndexOfAnyExcept((byte)0) < 0);
        }

        // ---- ルートディレクトリ: ボリュームラベルエントリ ----
        var root = new byte[layout.BytesPerCluster];
        dev.Read(layout.ClusterOffset(2), root);
        Assert.Equal("TESTCARD   ", Encoding.ASCII.GetString(root, 0, 11));
        Assert.Equal(0x08, root[11]);
        Assert.Equal(Fat32BootSector.DosTime(when), U16(root, 22));
        Assert.Equal(Fat32BootSector.DosDate(when), U16(root, 24));
        Assert.True(root.AsSpan(32).IndexOfAnyExcept((byte)0) < 0);

        // 読み戻して Fat32Volume として認識できる
        var vol = Fat32Volume.Open(dev);
        Assert.Equal(layout.ClusterCount, vol.Layout.ClusterCount);
        Assert.Equal("TESTCARD", vol.BootLabel);
        Assert.Equal(layout.ClusterCount - 1, (uint)vol.CountFreeClusters());
        var entries = vol.ReadDirectory(2);
        Assert.Single(entries);
        Assert.True(entries[0].IsVolumeLabel);
    }

    [Fact]
    public void Format_WithoutLabel_WritesNoName()
    {
        const long size = 40 * GiB;
        using var dev = new StreamBlockDevice(new SparseMemoryStream(size), 512);
        var layout = Fat32LayoutCalculator.Calculate(size, 512, ClusterSizePolicy.Default(size), 4 * MiB, 16 * MiB);
        Fat32Formatter.Format(dev, layout, new Fat32FormatOptions());
        var boot = ReadSector(dev, 0);
        Assert.Equal("NO NAME    ", Encoding.ASCII.GetString(boot, 71, 11));
        var root = new byte[layout.BytesPerCluster];
        dev.Read(layout.ClusterOffset(2), root);
        Assert.True(root.AsSpan().IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void Format_4KSectorDevice()
    {
        const long size = 64 * GiB;
        using var dev = new StreamBlockDevice(new SparseMemoryStream(size), 4096);
        var layout = Fat32LayoutCalculator.Calculate(size, 4096, 32768, 1 * MiB, 16 * MiB);
        Fat32Formatter.Format(dev, layout, new Fat32FormatOptions { Label = "BIG" });
        var boot = ReadSector(dev, 0);
        Assert.Equal(4096, U16(boot, 11));
        Assert.Equal(0xAA55, U16(boot, 510)); // 署名は常に 510/511
        Assert.Equal("BIG        ", Encoding.ASCII.GetString(boot, 71, 11));
        Assert.True(Fat32Volume.TryOpen(dev, out _, out _));
    }

    [Fact]
    public void ImageFile_RoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), $"fat32fmt-test-{Guid.NewGuid():N}.img");
        try
        {
            const long size = 64 * MiB;
            using (var dev = StreamBlockDevice.CreateImage(path, size))
            {
                var layout = Fat32LayoutCalculator.Calculate(size, 512, ClusterSizePolicy.Default(size), 0, 0);
                Assert.Equal(512, layout.BytesPerCluster); // 64MiB → 既定 512B クラスタ
                Fat32Formatter.Format(dev, layout, new Fat32FormatOptions { Label = "IMG" });
            }
            using (var dev = StreamBlockDevice.OpenImage(path, writable: false))
            {
                var vol = Fat32Volume.Open(dev);
                Assert.Equal("IMG", vol.BootLabel);
                Assert.Equal(0u, vol.Layout.HiddenSectors);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("sdcard", true)]
    [InlineData("My Card", true)]
    [InlineData("ABCDEFGHIJK", true)]
    [InlineData("ABCDEFGHIJKL", false)]  // 12 バイト
    [InlineData("A.B", false)]
    [InlineData("A/B", false)]
    [InlineData("A*B", false)]
    public void VolumeLabel_Validation(string label, bool valid)
    {
        Assert.Equal(valid, VolumeLabel.TryValidate(label, out _));
    }

    [Fact]
    public void VolumeLabel_UppercasesAsciiAndPads()
    {
        var enc = VolumeLabel.Encode("sdcard");
        Assert.NotNull(enc);
        Assert.Equal("SDCARD     ", Encoding.ASCII.GetString(enc!));
    }

    [Fact]
    public void VolumeLabel_Japanese_WhenOemIsCp932()
    {
        if (VolumeLabel.OemEncoding.CodePage != 932) return; // 日本語環境でのみ検証
        Assert.True(VolumeLabel.TryValidate("カード", out _));           // 6 バイト
        Assert.False(VolumeLabel.TryValidate("カードカードカ", out _));   // 12 バイト
        var enc = VolumeLabel.Encode("カード")!;
        Assert.Equal(11, enc.Length);
        Assert.Equal("カード", VolumeLabel.Decode(enc));
    }
}
