using System.Security.Cryptography;
using Fat32Fmt.Core.Diagnostics;
using Fat32Fmt.Core.Formatting;
using Fat32Fmt.Core.Imaging;
using Fat32Fmt.Core.IO;
using Fat32Fmt.Core.Windows;
using Xunit;

namespace Fat32Fmt.Tests;

public class ImagingTests
{
    private const long MiB = 1L << 20, GiB = 1L << 30;

    /// <summary>MBR + FAT32 を作り、データ領域のあちこちに乱数を書いた「使用中のカード」を作る。</summary>
    private static StreamBlockDevice MakeCard(long size, int seed = 1)
    {
        var dev = new StreamBlockDevice(new SparseMemoryStream(size), 512);
        DiskInitializer.InitializeMbrFat32(dev, "CARD");
        var rnd = new Random(seed);
        var data = new byte[1 * MiB];
        foreach (long at in new[] { 100 * MiB, size / 2, size - 2 * MiB })
        {
            rnd.NextBytes(data);
            dev.Write(at, data);
        }
        return dev;
    }

    private static string Sha(IBlockDevice dev, long length) => DiskImager.Hash(dev, length);

    [Fact]
    public void Backup_CopiesEverything_AndSkipsZeroChunks()
    {
        using var card = MakeCard(4 * GiB);
        using var image = new StreamBlockDevice(new SparseMemoryStream(card.Length), 512);

        var r = DiskImager.Backup(card, image, imageIsZeroFilled: true);

        Assert.Equal(card.Length, r.Bytes);
        Assert.Equal(Sha(card, card.Length), r.Sha256);
        Assert.Equal(r.Sha256, Sha(image, image.Length));
        Assert.True(r.ZeroBytesSkipped > 3 * GiB, $"skipped={r.ZeroBytesSkipped}");
    }

    [Fact]
    public void Backup_ToRealFile_HashMatchesFileContents()
    {
        string dir = Directory.CreateTempSubdirectory("fat32fmt-test-").FullName;
        try
        {
            string path = Path.Combine(dir, "card.img");
            using var card = MakeCard(64 * MiB);
            ImageResult r;
            using (var image = StreamBlockDevice.CreateImage(path, card.Length))
            {
                r = DiskImager.Backup(card, image, imageIsZeroFilled: true);
            }
            ImageHashFile.Write(path, r.Sha256);

            using (var fs = File.OpenRead(path))
            {
                Assert.Equal(r.Sha256, Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant());
            }
            Assert.Equal(r.Sha256, ImageHashFile.TryRead(path));
            Assert.EndsWith(" *card.img", File.ReadAllText(ImageHashFile.PathFor(path)).TrimEnd());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Backup_RejectsSmallerImage()
    {
        using var card = new StreamBlockDevice(new SparseMemoryStream(64 * MiB), 512);
        using var image = new StreamBlockDevice(new SparseMemoryStream(32 * MiB), 512);
        Assert.Throws<ArgumentException>(() => DiskImager.Backup(card, image, true));
    }

    [Fact]
    public void BackupWipeRestore_RoundTrip_RestoresIdenticalCard()
    {
        using var card = MakeCard(2 * GiB);
        string original = Sha(card, card.Length);
        using var image = new StreamBlockDevice(new SparseMemoryStream(card.Length), 512);
        var backup = DiskImager.Backup(card, image, true);

        SecureEraser.Wipe(card, new WipeOptions { Pattern = WipePattern.Random });
        Assert.NotEqual(original, Sha(card, card.Length));

        var r = DiskImager.Restore(image, card, backup.Sha256);
        Assert.True(r.Verified);
        Assert.True(r.MatchesRecordedHash);
        Assert.Equal(original, r.ReadbackSha256);
        Assert.Equal(original, Sha(card, card.Length));

        // 書き戻したカードは FAT32 として検査に通る
        var vol = Fat32Verifier.LocateVolume(card, out long offset);
        Assert.True(offset > 0);
        var report = Fat32Verifier.Verify(vol);
        Assert.True(report.Ok, string.Join(" / ", report.Errors));
        Assert.Equal("CARD", report.Label);
    }

    [Fact]
    public void Restore_ReportsMismatchWithRecordedHash()
    {
        using var image = MakeCard(64 * MiB);
        using var disk = new StreamBlockDevice(new SparseMemoryStream(64 * MiB), 512);
        var r = DiskImager.Restore(image, disk, expectedSha256: new string('0', 64));
        Assert.True(r.Verified);
        Assert.False(r.MatchesRecordedHash);
    }

    [Fact]
    public void Restore_SmallerImage_LeavesTailUntouched()
    {
        using var image = MakeCard(64 * MiB);
        using var disk = new StreamBlockDevice(new SparseMemoryStream(80 * MiB), 512);
        var marker = new byte[512];
        marker.AsSpan().Fill(0x5A);
        disk.Write(79 * MiB, marker);

        var r = DiskImager.Restore(image, disk);

        Assert.Equal(64 * MiB, r.Bytes);
        var back = new byte[512];
        disk.Read(79 * MiB, back);
        Assert.Equal(marker, back);
    }

    [Fact]
    public void Restore_LargerImageWithZeroTail_IsTruncated()
    {
        using var image = new StreamBlockDevice(new SparseMemoryStream(72 * MiB), 512);
        using (var card = MakeCard(64 * MiB))
        {
            DiskImager.Backup(card, image, true); // 末尾 8 MiB は 0 のまま
        }
        using var disk = new StreamBlockDevice(new SparseMemoryStream(64 * MiB), 512);
        var logs = new List<string>();

        var r = DiskImager.Restore(image, disk, log: logs.Add);

        Assert.True(r.Verified);
        Assert.Equal(8 * MiB, r.TruncatedZeroBytes);
        Assert.Equal(Sha(image, image.Length), r.ImageSha256);
        Assert.NotEqual(r.ImageSha256, r.WrittenSha256);
        Assert.Contains(logs, l => l.Contains("すべて 0"));
    }

    [Fact]
    public void Restore_LargerImageWithDataInTail_IsRejected()
    {
        using var image = new StreamBlockDevice(new SparseMemoryStream(72 * MiB), 512);
        var junk = new byte[512];
        junk[7] = 1;
        image.Write(70 * MiB, junk);
        using var disk = new StreamBlockDevice(new SparseMemoryStream(64 * MiB), 512);

        var ex = Assert.Throws<ArgumentException>(() => DiskImager.Restore(image, disk));
        Assert.Contains("はみ出す", ex.Message);
    }

    [Fact]
    public void Restore_OnFakeCapacityCard_FailsVerification()
    {
        using var image = MakeCard(64 * MiB);
        // 32 MiB より先への書き込みが先頭に折り返す「容量偽装カード」
        using var fake = new WrappingDevice(new StreamBlockDevice(new SparseMemoryStream(64 * MiB), 512), realBytes: 32 * MiB);

        var ex = Assert.Throws<DataVerificationException>(() => DiskImager.Restore(image, fake));
        Assert.Contains("照合に失敗", ex.Message);
    }

    [Fact]
    public void ImageHashFile_MissingOrMalformed_ReturnsNull()
    {
        string dir = Directory.CreateTempSubdirectory("fat32fmt-test-").FullName;
        try
        {
            string img = Path.Combine(dir, "a.img");
            Assert.Null(ImageHashFile.TryRead(img));
            File.WriteAllText(ImageHashFile.PathFor(img), "not-a-hash *a.img\n");
            Assert.Null(ImageHashFile.TryRead(img));
            File.WriteAllText(ImageHashFile.PathFor(img), new string('A', 64) + "  a.img\r\n");
            Assert.Equal(new string('a', 64), ImageHashFile.TryRead(img));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("disk2", 2)]
    [InlineData("Disk:3", 3)]
    [InlineData("PhysicalDrive10", 10)]
    [InlineData(@"\\.\PhysicalDrive1", 1)]
    [InlineData("E:", null)]
    [InlineData("diskX", null)]
    public void DiskTargets_ParseDiskNumber(string spec, int? expected) =>
        Assert.Equal(expected, DiskTargets.ParseDiskNumber(spec));

    [Theory]
    [InlineData("e", 'E')]
    [InlineData("E:", 'E')]
    [InlineData(@"f:\", 'F')]
    [InlineData("disk2", null)]
    [InlineData("12", null)]
    public void DiskTargets_ParseDriveLetter(string spec, char? expected) =>
        Assert.Equal(expected, DiskTargets.ParseDriveLetter(spec));
}

/// <summary>実容量 realBytes を超える位置への読み書きが先頭に折り返す、容量偽装カードの模型。</summary>
internal sealed class WrappingDevice(IBlockDevice inner, long realBytes) : IBlockDevice
{
    public long Length => inner.Length;
    public int SectorSize => inner.SectorSize;
    public bool CanWrite => true;

    public void Read(long offset, Span<byte> buffer)
    {
        for (int done = 0; done < buffer.Length; done += SectorSize)
            inner.Read((offset + done) % realBytes, buffer.Slice(done, SectorSize));
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        for (int done = 0; done < buffer.Length; done += SectorSize)
            inner.Write((offset + done) % realBytes, buffer.Slice(done, SectorSize));
    }

    public void Flush() => inner.Flush();

    public void Dispose() => inner.Dispose();
}
