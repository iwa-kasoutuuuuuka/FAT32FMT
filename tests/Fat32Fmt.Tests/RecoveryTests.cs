using System.Buffers.Binary;
using System.Text;
using Fat32Fmt.Core.Fat32;
using Fat32Fmt.Core.IO;
using Fat32Fmt.Core.Recovery;
using Xunit;

namespace Fat32Fmt.Tests;

public class RecoveryTests : IDisposable
{
    private const long MiB = 1L << 20;
    private static readonly DateTime When = new(2026, 10, 20, 12, 34, 56);

    private readonly StreamBlockDevice _dev;
    private readonly FatImageWriter _w;
    private readonly string _out = Directory.CreateTempSubdirectory("fat32fmt-recover-").FullName;

    public RecoveryTests()
    {
        _dev = new StreamBlockDevice(new SparseMemoryStream(512 * MiB), 512);
        var layout = Fat32LayoutCalculator.Calculate(_dev.Length, 512, 4096, 0, 0);
        Fat32Formatter.Format(_dev, layout, new Fat32FormatOptions());
        _w = new FatImageWriter(_dev);
    }

    public void Dispose()
    {
        _dev.Dispose();
        Directory.Delete(_out, recursive: true);
    }

    private static byte[] Data(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    private List<DeletedFile> Scan() => DeletedFileRecovery.Scan(Fat32Volume.Open(_dev));

    [Fact]
    public void DeletedFileWithLfn_NameRestoredFromChecksum_AndContentRecovered()
    {
        var data = Data(10_000, 1);
        var f = _w.AddFile(_w.Root, "写真 2026-10-20.jpg", "PHOTO~1 JPG", data);
        _w.AddFile(_w.Root, "残すファイル.txt", "KEEP~1  TXT", Data(100, 2)); // 削除していないものは出てこない
        _w.Delete(f);

        var found = Assert.Single(Scan());
        Assert.Equal("写真 2026-10-20.jpg", found.Path);
        Assert.Equal("PHOTO~1.JPG", found.Entry.ShortName); // 先頭の 'P' をチェックサムから推定できている
        Assert.True(found.NameRecovered);
        Assert.Equal(RecoveryChance.Good, found.Chance);
        Assert.Equal(When, found.Entry.Modified);

        var r = Assert.Single(DeletedFileRecovery.Recover(Fat32Volume.Open(_dev), [found], _out));
        Assert.Equal(Path.Combine(_out, "写真 2026-10-20.jpg"), r.OutputPath);
        Assert.Equal(data, File.ReadAllBytes(r.OutputPath));
        Assert.Equal(When, File.GetLastWriteTime(r.OutputPath));
    }

    [Fact]
    public void DeletedShortNameOnly_FirstCharacterUnknown()
    {
        var f = _w.AddFile(_w.Root, null, "README  TXT", Encoding.ASCII.GetBytes("hello"));
        _w.Delete(f);

        var found = Assert.Single(Scan());
        Assert.Equal("_EADME.TXT", found.Path);
        Assert.False(found.NameRecovered);
        Assert.Equal(RecoveryChance.Good, found.Chance);
    }

    [Fact]
    public void ReusedClusters_AreReportedAsOverwrittenOrPartial()
    {
        var a = _w.AddFile(_w.Root, "overwritten.bin", "OVERWR~1BIN", Data(3 * 4096, 3));
        var b = _w.AddFile(_w.Root, "partial.bin", "PARTIA~1BIN", Data(4 * 4096, 4));
        _w.Delete(a);
        _w.Delete(b);
        _w.AddFile(_w.Root, "new1.bin", "NEW1    BIN", Data(4096, 5), firstCluster: a.FirstCluster);
        _w.AddFile(_w.Root, "new2.bin", "NEW2    BIN", Data(4096, 6), firstCluster: b.FirstCluster + 2);

        var found = Scan().ToDictionary(x => x.Path);
        Assert.Equal(RecoveryChance.Overwritten, found["overwritten.bin"].Chance);
        Assert.Equal(RecoveryChance.Partial, found["partial.bin"].Chance);
    }

    [Fact]
    public void EmptyAndOutOfRangeFiles()
    {
        var empty = _w.AddFile(_w.Root, "empty.txt", "EMPTY   TXT", []);
        var bogus = _w.AddFile(_w.Root, "bogus.bin", "BOGUS   BIN", Data(100, 7));
        _w.Delete(empty);
        _w.Delete(bogus);
        _w.PatchEntry(bogus, firstCluster: 0x0FFFFFF0); // 範囲外のクラスタを指す壊れたエントリ

        var found = Scan().ToDictionary(x => x.Path);
        Assert.Equal(RecoveryChance.Empty, found["empty.txt"].Chance);
        Assert.Equal(RecoveryChance.Invalid, found["bogus.bin"].Chance);

        var r = Assert.Single(DeletedFileRecovery.Recover(Fat32Volume.Open(_dev), found.Values, _out));
        Assert.Equal(0, new FileInfo(r.OutputPath).Length);
    }

    [Fact]
    public void FilesInsideDeletedDirectory_AreFound()
    {
        uint dir = _w.AddDirectory(_w.Root, "Camera Roll", "CAMERA~1   ", out var dirEntry);
        var data = Data(70_000, 8);
        var f = _w.AddFile(dir, "IMG_0001.JPG", "IMG_0001JPG", data);
        _w.Delete(f);
        _w.Delete(dirEntry);

        var found = Assert.Single(Scan());
        Assert.Equal("Camera Roll/IMG_0001.JPG", found.Path);
        Assert.Equal(RecoveryChance.Good, found.Chance);

        var r = Assert.Single(DeletedFileRecovery.Recover(Fat32Volume.Open(_dev), [found], _out));
        Assert.Equal(Path.Combine(_out, "Camera Roll", "IMG_0001.JPG"), r.OutputPath);
        Assert.Equal(data, File.ReadAllBytes(r.OutputPath));
    }

    [Fact]
    public void DeletedFileInLiveSubdirectory_HasFullPath()
    {
        uint dir = _w.AddDirectory(_w.Root, "DCIM", "DCIM       ", out _);
        uint sub = _w.AddDirectory(dir, "100MEDIA", "100MEDIA   ", out _);
        var f = _w.AddFile(sub, "clip.mp4", "CLIP    MP4", Data(5000, 9));
        _w.Delete(f);

        Assert.Equal("DCIM/100MEDIA/clip.mp4", Assert.Single(Scan()).Path);
    }

    [Fact]
    public void Recover_SameNameTwice_GetsUniqueOutputNames()
    {
        var a = _w.AddFile(_w.Root, "same.txt", "SAME    TXT", Encoding.ASCII.GetBytes("first"));
        _w.Delete(a);
        var b = _w.AddFile(_w.Root, "same.txt", "SAME    TXT", Encoding.ASCII.GetBytes("second"));
        _w.Delete(b);

        var found = Scan();
        Assert.Equal(2, found.Count);
        var r = DeletedFileRecovery.Recover(Fat32Volume.Open(_dev), found, _out);
        Assert.Equal(new[] { "same.txt", "same (2).txt" }, r.Select(x => Path.GetFileName(x.OutputPath)));
        Assert.Equal("first", File.ReadAllText(r[0].OutputPath));
        Assert.Equal("second", File.ReadAllText(r[1].OutputPath));
    }

    [Fact]
    public void Recover_FromImageFileWithMbr()
    {
        // 実際の使い方: カードを backup した .img から取り出す
        string img = Path.Combine(_out, "card.img");
        using (var disk = StreamBlockDevice.CreateImage(img, 600 * MiB))
        {
            var part = new SubBlockDevice(disk, 4 * MiB, 512 * MiB);
            Fat32Formatter.Format(part, Fat32LayoutCalculator.Calculate(part.Length, 512, 4096, 4 * MiB, 0), new Fat32FormatOptions());
            disk.Write(0, MbrBuilder.Build(512, 4 * MiB, 512 * MiB, PartitionPlanner.Fat32LbaType, 1));
            var w = new FatImageWriter(part);
            w.Delete(w.AddFile(w.Root, "memo.txt", "MEMO    TXT", Encoding.UTF8.GetBytes("メモ")));
        }

        using var image = StreamBlockDevice.OpenImage(img, writable: false);
        var vol = Fat32Volume.Open(Fat32Fmt.Core.Diagnostics.Fat32Verifier.LocateVolume(image, out _));
        var found = Assert.Single(DeletedFileRecovery.Scan(vol));
        var r = Assert.Single(DeletedFileRecovery.Recover(vol, [found], Path.Combine(_out, "out")));
        Assert.Equal("メモ", File.ReadAllText(r.OutputPath));
    }
}

/// <summary>テスト用: FAT32 ボリュームに Windows と同じ形でファイルを書き、削除する(連続クラスタに配置)。</summary>
internal sealed class FatImageWriter
{
    public sealed record Entry(uint DirCluster, int FirstSlot, int SlotCount, uint FirstCluster, int Clusters);

    private readonly IBlockDevice _dev;
    private readonly Fat32Layout _l;
    private uint _nextFree;

    public FatImageWriter(IBlockDevice dev)
    {
        _dev = dev;
        var vol = Fat32Volume.Open(dev);
        _l = vol.Layout;
        Root = vol.RootCluster;
        _nextFree = Root + 1;
    }

    public uint Root { get; }

    public Entry AddFile(uint dir, string? longName, string shortName11, byte[] data, uint? firstCluster = null)
    {
        int clusters = (data.Length + _l.BytesPerCluster - 1) / _l.BytesPerCluster;
        uint first = clusters == 0 ? 0 : firstCluster ?? Alloc(clusters);
        for (int i = 0; i < clusters; i++)
        {
            SetFat((uint)(first + i), i == clusters - 1 ? Fat32Formatter.EndOfChain : (uint)(first + i + 1));
        }

        if (data.Length > 0)
        {
            var padded = new byte[clusters * _l.BytesPerCluster];
            data.CopyTo(padded, 0);
            _dev.Write(_l.ClusterOffset(first), padded);
        }

        return AddEntry(dir, longName, shortName11, 0x20, first, (uint)data.Length, clusters);
    }

    public uint AddDirectory(uint parent, string? longName, string shortName11, out Entry entry)
    {
        uint c = Alloc(1);
        SetFat(c, Fat32Formatter.EndOfChain);
        var data = new byte[_l.BytesPerCluster];
        ShortEntry(".          ", 0x10, c, 0).CopyTo(data, 0);
        ShortEntry("..         ", 0x10, parent == Root ? 0 : parent, 0).CopyTo(data, 32);
        _dev.Write(_l.ClusterOffset(c), data);
        entry = AddEntry(parent, longName, shortName11, 0x10, c, 0, 1);
        return c;
    }

    /// <summary>Windows と同じ削除: エントリ先頭を 0xE5 にし、クラスタチェーンを解放する(データは残る)。</summary>
    public void Delete(Entry e)
    {
        var dir = ReadCluster(e.DirCluster);
        for (int i = 0; i < e.SlotCount; i++) dir[(e.FirstSlot + i) * 32] = 0xE5;
        _dev.Write(_l.ClusterOffset(e.DirCluster), dir);
        for (int i = 0; i < e.Clusters; i++) SetFat((uint)(e.FirstCluster + i), 0);
    }

    public void PatchEntry(Entry e, uint firstCluster)
    {
        var dir = ReadCluster(e.DirCluster);
        int off = (e.FirstSlot + e.SlotCount - 1) * 32;
        BinaryPrimitives.WriteUInt16LittleEndian(dir.AsSpan(off + 20), (ushort)(firstCluster >> 16));
        BinaryPrimitives.WriteUInt16LittleEndian(dir.AsSpan(off + 26), (ushort)firstCluster);
        _dev.Write(_l.ClusterOffset(e.DirCluster), dir);
    }

    private Entry AddEntry(uint dirCluster, string? longName, string shortName11, byte attr, uint first, uint size, int clusters)
    {
        var name = Encoding.ASCII.GetBytes(shortName11);
        Assert.Equal(11, name.Length);
        var slots = new List<byte[]>();
        if (longName != null)
        {
            byte sum = 0;
            foreach (byte b in name) sum = (byte)(((sum & 1) << 7) + (sum >> 1) + b);
            var chars = new List<char>(longName);
            if (chars.Count % 13 != 0) chars.Add('\0');
            while (chars.Count % 13 != 0) chars.Add('￿');
            int n = chars.Count / 13;
            for (int seq = n; seq >= 1; seq--)
            {
                var e = new byte[32];
                e[0] = (byte)(seq | (seq == n ? 0x40 : 0));
                e[11] = 0x0F;
                e[13] = sum;
                var part = chars.GetRange((seq - 1) * 13, 13);
                int[] at = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];
                for (int i = 0; i < 13; i++) BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(at[i]), part[i]);
                slots.Add(e);
            }
        }

        slots.Add(ShortEntry(shortName11, attr, first, size));
        var dir = ReadCluster(dirCluster);
        int slot = 0;
        while (dir[slot * 32] != 0) slot++;
        for (int i = 0; i < slots.Count; i++) slots[i].CopyTo(dir, (slot + i) * 32);
        _dev.Write(_l.ClusterOffset(dirCluster), dir);
        return new Entry(dirCluster, slot, slots.Count, first, clusters);
    }

    private static byte[] ShortEntry(string name11, byte attr, uint first, uint size)
    {
        var e = new byte[32];
        Encoding.ASCII.GetBytes(name11).CopyTo(e, 0);
        e[11] = attr;
        var t = new DateTime(2026, 10, 20, 12, 34, 56);
        ushort time = (ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));
        ushort date = (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(20), (ushort)(first >> 16));
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(22), time);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(24), date);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(26), (ushort)first);
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(28), size);
        return e;
    }

    private uint Alloc(int clusters)
    {
        uint first = _nextFree;
        _nextFree += (uint)clusters;
        return first;
    }

    private byte[] ReadCluster(uint c)
    {
        var b = new byte[_l.BytesPerCluster];
        _dev.Read(_l.ClusterOffset(c), b);
        return b;
    }

    private void SetFat(uint cluster, uint value)
    {
        int bps = _l.BytesPerSector;
        long byteOffset = cluster * 4L;
        for (int fat = 0; fat < _l.NumberOfFats; fat++)
        {
            long sector = _l.FatStartSector + (long)fat * _l.FatSizeSectors + byteOffset / bps;
            var s = new byte[bps];
            _dev.Read(sector * bps, s);
            BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan((int)(byteOffset % bps)), value);
            _dev.Write(sector * bps, s);
        }
    }
}
