using System.Buffers.Binary;
using System.Text;
using Fat32Fmt.Core.IO;

namespace Fat32Fmt.Core.Fat32;

/// <summary>FAT32 ボリュームの読み取り(検証・情報表示・削除ファイル復旧で使う)。</summary>
public sealed class Fat32Volume
{
    private readonly IBlockDevice _device;
    private readonly Dictionary<long, byte[]> _fatCache = new();

    private Fat32Volume(IBlockDevice device, Fat32Layout layout, uint rootCluster, string label, uint volumeId)
    {
        _device = device;
        Layout = layout;
        RootCluster = rootCluster;
        BootLabel = label;
        VolumeId = volumeId;
    }

    public Fat32Layout Layout { get; }

    public uint RootCluster { get; }

    /// <summary>ブートセクタに記録されたラベル。</summary>
    public string BootLabel { get; }

    public uint VolumeId { get; }

    public IBlockDevice Device => _device;

    public static bool TryOpen(IBlockDevice device, out Fat32Volume? volume, out string? error)
    {
        try
        {
            volume = Open(device);
            error = null;
            return true;
        }
        catch (InvalidDataException ex)
        {
            volume = null;
            error = ex.Message;
            return false;
        }
    }

    public static Fat32Volume Open(IBlockDevice device)
    {
        var boot = new byte[device.SectorSize];
        device.Read(0, boot);
        ReadOnlySpan<byte> b = boot;
        if (b[510] != 0x55 || b[511] != 0xAA)
        {
            throw new InvalidDataException("ブートセクタの署名 (55 AA) がありません。");
        }

        int bps = BinaryPrimitives.ReadUInt16LittleEndian(b[11..]);
        int spc = b[13];
        int reserved = BinaryPrimitives.ReadUInt16LittleEndian(b[14..]);
        int nFats = b[16];
        ushort rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(b[17..]);
        ushort fatSz16 = BinaryPrimitives.ReadUInt16LittleEndian(b[22..]);
        uint hidden = BinaryPrimitives.ReadUInt32LittleEndian(b[28..]);
        uint total = BinaryPrimitives.ReadUInt16LittleEndian(b[19..]);
        if (total == 0)
        {
            total = BinaryPrimitives.ReadUInt32LittleEndian(b[32..]);
        }

        uint fatSz = BinaryPrimitives.ReadUInt32LittleEndian(b[36..]);
        uint rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(b[44..]);

        if (bps != device.SectorSize || spc == 0 || (spc & (spc - 1)) != 0 || reserved == 0 || nFats == 0)
        {
            throw new InvalidDataException("BPB の値が不正です。");
        }

        if (rootEntries != 0 || fatSz16 != 0 || fatSz == 0)
        {
            throw new InvalidDataException("FAT32 ではありません(FAT12/16 または不正な BPB)。");
        }

        long dataStart = reserved + (long)nFats * fatSz;
        if (dataStart >= total || (long)total * bps > device.Length)
        {
            throw new InvalidDataException("BPB のサイズ情報がデバイスと一致しません。");
        }

        uint clusters = (uint)((total - dataStart) / spc);
        var layout = new Fat32Layout
        {
            BytesPerSector = bps,
            SectorsPerCluster = spc,
            ReservedSectors = reserved,
            NumberOfFats = nFats,
            FatSizeSectors = fatSz,
            TotalSectors = total,
            ClusterCount = clusters,
            HiddenSectors = hidden,
        };
        uint volumeId = b[66] == 0x29 ? BinaryPrimitives.ReadUInt32LittleEndian(b[67..]) : 0;
        string label = b[66] == 0x29 ? VolumeLabel.Decode(b.Slice(71, 11)) : "";
        return new Fat32Volume(device, layout, rootCluster, label, volumeId);
    }

    public bool IsValidCluster(uint cluster) => cluster >= 2 && cluster < Layout.ClusterCount + 2;

    public uint ReadFatEntry(uint cluster)
    {
        int bps = Layout.BytesPerSector;
        long byteOffset = (long)cluster * 4;
        long sector = Layout.FatStartSector + byteOffset / bps;
        if (!_fatCache.TryGetValue(sector, out byte[]? data))
        {
            if (_fatCache.Count > 4096)
            {
                _fatCache.Clear();
            }

            data = new byte[bps];
            _device.Read(sector * bps, data);
            _fatCache[sector] = data;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)(byteOffset % bps))) & 0x0FFFFFFF;
    }

    /// <summary>クラスタチェーンをたどる(ループや範囲外で打ち切る)。</summary>
    public IEnumerable<uint> Chain(uint start)
    {
        uint c = start;
        var seen = new HashSet<uint>();
        while (IsValidCluster(c) && seen.Add(c))
        {
            yield return c;
            c = ReadFatEntry(c);
        }
    }

    public byte[] ReadCluster(uint cluster)
    {
        var data = new byte[Layout.BytesPerCluster];
        _device.Read(Layout.ClusterOffset(cluster), data);
        return data;
    }

    /// <summary>空きクラスタ数を数える(FAT を全走査)。</summary>
    public long CountFreeClusters(CancellationToken ct = default)
    {
        int bps = Layout.BytesPerSector;
        var buf = new byte[Math.Min((long)Layout.FatSizeSectors * bps, 1 << 20)];
        long free = 0;
        long totalEntries = Layout.ClusterCount + 2L;
        long entry = 0;
        for (long off = 0; entry < totalEntries; off += buf.Length)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buf.Length, (long)Layout.FatSizeSectors * bps - off);
            _device.Read(Layout.FatStartSector * bps + off, buf.AsSpan(0, n));
            for (int i = 0; i + 4 <= n && entry < totalEntries; i += 4, entry++)
            {
                if (entry >= 2 && (BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i)) & 0x0FFFFFFF) == 0)
                {
                    free++;
                }
            }
        }

        return free;
    }

    /// <summary>
    /// FAT の仕様上、1 つのディレクトリは 65,536 エントリ (32 バイト × 65,536 = 2 MiB) までなので、それを超えては読まない。
    /// 細工されたカードで巨大なクラスタチェーンを作られても、メモリを使い果たさないようにする。
    /// </summary>
    public const int MaxDirectoryBytes = 65536 * 32;

    /// <summary>ディレクトリのエントリを列挙する。</summary>
    /// <param name="clusters">ディレクトリを構成するクラスタ(通常は Chain の結果)。</param>
    /// <param name="includeDeleted">削除済みエントリも返すか。</param>
    public List<FatDirectoryEntry> ReadDirectory(IEnumerable<uint> clusters, bool includeDeleted)
    {
        int maxClusters = Math.Max(1, MaxDirectoryBytes / Layout.BytesPerCluster);
        var data = new List<byte>();
        foreach (uint c in clusters.Take(maxClusters))
        {
            data.AddRange(ReadCluster(c));
        }

        return FatDirectoryParser.Parse(data.ToArray(), includeDeleted);
    }

    public List<FatDirectoryEntry> ReadDirectory(uint firstCluster, bool includeDeleted = false) =>
        ReadDirectory(Chain(firstCluster), includeDeleted);
}

/// <summary>ディレクトリエントリ(LFN を結合済み)。</summary>
public sealed record FatDirectoryEntry
{
    public required string Name { get; init; }

    public required string ShortName { get; init; }

    public required byte Attributes { get; init; }

    public required uint FirstCluster { get; init; }

    public required uint Size { get; init; }

    public required bool IsDeleted { get; init; }

    public DateTime? Modified { get; init; }

    public bool IsDirectory => (Attributes & 0x10) != 0;

    public bool IsVolumeLabel => (Attributes & 0x08) != 0 && (Attributes & 0x0F) != 0x0F;

    public bool IsDotEntry => ShortName is "." or "..";
}

internal static class FatDirectoryParser
{
    private const int EntrySize = 32;

    public static List<FatDirectoryEntry> Parse(byte[] data, bool includeDeleted)
    {
        var result = new List<FatDirectoryEntry>();
        var lfn = new List<(byte Seq, byte Checksum, string Part)>();

        for (int off = 0; off + EntrySize <= data.Length; off += EntrySize)
        {
            ReadOnlySpan<byte> e = data.AsSpan(off, EntrySize);
            byte first = e[0];
            if (first == 0x00)
            {
                break; // 以降は未使用
            }

            bool deleted = first == 0xE5;
            byte attr = e[11];
            if ((attr & 0x3F) == 0x0F)
            {
                lfn.Add((first, e[13], LfnPart(e)));
                continue;
            }

            if (deleted && !includeDeleted)
            {
                lfn.Clear();
                continue;
            }

            byte[] raw = e[..11].ToArray();
            if (raw[0] == 0x05)
            {
                raw[0] = 0xE5;
            }

            string? longName = null;
            if (lfn.Count > 0)
            {
                byte checksum = lfn[^1].Checksum;
                if (deleted)
                {
                    // 削除で失われた先頭バイトを LFN のチェックサムから逆算する
                    for (int x = 0x20; x < 0x100; x++)
                    {
                        raw[0] = (byte)x;
                        if (ShortNameChecksum(raw) == checksum)
                        {
                            longName = AssembleLfn(lfn);
                            break;
                        }
                    }

                    if (longName is null)
                    {
                        raw[0] = (byte)'_';
                    }
                }
                else if (ShortNameChecksum(raw) == checksum)
                {
                    longName = AssembleLfn(lfn);
                }
            }
            else if (deleted)
            {
                raw[0] = (byte)'_';
            }

            lfn.Clear();
            // 名前はカードの作り手が自由に決められるので、制御文字・双方向制御文字をここで無害化する
            string shortName = TextSanitizer.Clean(DecodeShortName(raw, e[12]));
            longName = longName is null ? null : TextSanitizer.Clean(longName);
            uint cluster = ((uint)BinaryPrimitives.ReadUInt16LittleEndian(e[20..]) << 16)
                           | BinaryPrimitives.ReadUInt16LittleEndian(e[26..]);
            result.Add(new FatDirectoryEntry
            {
                Name = longName ?? shortName,
                ShortName = shortName,
                Attributes = attr,
                FirstCluster = cluster,
                Size = BinaryPrimitives.ReadUInt32LittleEndian(e[28..]),
                IsDeleted = deleted,
                Modified = DecodeDateTime(BinaryPrimitives.ReadUInt16LittleEndian(e[24..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(e[22..])),
            });
        }

        return result;
    }

    public static byte ShortNameChecksum(ReadOnlySpan<byte> name11)
    {
        byte sum = 0;
        for (int i = 0; i < 11; i++)
        {
            sum = (byte)(((sum & 1) << 7) + (sum >> 1) + name11[i]);
        }

        return sum;
    }

    private static string LfnPart(ReadOnlySpan<byte> e)
    {
        Span<byte> chars = stackalloc byte[26];
        e.Slice(1, 10).CopyTo(chars);
        e.Slice(14, 12).CopyTo(chars[10..]);
        e.Slice(28, 4).CopyTo(chars[22..]);
        string s = Encoding.Unicode.GetString(chars);
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s.TrimEnd('￿');
    }

    /// <summary>LFN エントリはディスク上では末尾の断片が先に並ぶので逆順に結合する。</summary>
    private static string AssembleLfn(List<(byte Seq, byte Checksum, string Part)> parts)
    {
        var sb = new StringBuilder();
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            sb.Append(parts[i].Part);
        }

        return sb.ToString();
    }

    private static string DecodeShortName(byte[] raw, byte caseFlags)
    {
        string name = VolumeLabel.OemEncoding.GetString(raw, 0, 8).TrimEnd(' ');
        string ext = VolumeLabel.OemEncoding.GetString(raw, 8, 3).TrimEnd(' ');
        if ((caseFlags & 0x08) != 0)
        {
            name = name.ToLowerInvariant();
        }

        if ((caseFlags & 0x10) != 0)
        {
            ext = ext.ToLowerInvariant();
        }

        return ext.Length > 0 ? $"{name}.{ext}" : name;
    }

    private static DateTime? DecodeDateTime(ushort date, ushort time)
    {
        int year = 1980 + (date >> 9);
        int month = (date >> 5) & 0x0F;
        int day = date & 0x1F;
        int hour = time >> 11;
        int minute = (time >> 5) & 0x3F;
        int second = (time & 0x1F) * 2;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
    }
}
