using System.Buffers.Binary;
using Fat32Fmt.Core.Fat32;
using Fat32Fmt.Core.IO;

namespace Fat32Fmt.Core.Diagnostics;

/// <summary>FAT32 ボリュームの整合性チェック結果。</summary>
public sealed record Fat32VerifyReport(
    bool Ok, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings,
    Fat32Layout? Layout, string Label, uint VolumeId, long FreeClusters, uint? FsInfoFreeClusters);

/// <summary>
/// フォーマット結果を読み戻して検証する（ブートセクタとバックアップ、FSInfo、FAT の先頭エントリと全 FAT の一致、
/// ルートディレクトリ、空きクラスタ数）。書き込みは一切行わない。
/// </summary>
public static class Fat32Verifier
{
    public static Fat32VerifyReport Verify(IBlockDevice device, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!Fat32Volume.TryOpen(device, out var vol, out string? openError))
        {
            errors.Add(openError ?? "FAT32 ボリュームとして開けません。");
            return new Fat32VerifyReport(false, errors, warnings, null, "", 0, 0, null);
        }

        var l = vol!.Layout;
        int bps = l.BytesPerSector;
        byte[] boot = ReadSector(device, 0);

        if (l.ClusterCount < Fat32LayoutCalculator.MinClusters)
            errors.Add($"クラスタ数 {l.ClusterCount} が FAT32 の下限 {Fat32LayoutCalculator.MinClusters} 未満です（FAT16 と誤認されます）。");
        if ((long)l.FatSizeSectors * bps / 4 < l.ClusterCount + 2L)
            errors.Add("FAT のサイズがクラスタ数に対して不足しています。");

        // バックアップブートセクタ
        int backupSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(50));
        if (backupSector is 0 or 0xFFFF)
            warnings.Add("バックアップブートセクタが設定されていません。");
        else if (backupSector >= l.ReservedSectors)
            errors.Add($"バックアップブートセクタ ({backupSector}) が予約領域の外を指しています。");
        else if (!ReadSector(device, backupSector).AsSpan().SequenceEqual(boot))
            errors.Add($"バックアップブートセクタ (セクタ {backupSector}) が本体と一致しません。");

        // FSInfo
        uint? fsInfoFree = null;
        int fsInfoSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(48));
        if (fsInfoSector is 0 or 0xFFFF || fsInfoSector >= l.ReservedSectors)
        {
            warnings.Add("FSInfo セクタが設定されていません。");
        }
        else
        {
            byte[] fsi = ReadSector(device, fsInfoSector);
            if (U32(fsi, 0) != 0x41615252 || U32(fsi, 484) != 0x61417272 || U32(fsi, 508) != 0xAA550000)
            {
                errors.Add($"FSInfo (セクタ {fsInfoSector}) の署名が不正です。");
            }
            else
            {
                uint free = U32(fsi, 488);
                if (free != 0xFFFFFFFF) fsInfoFree = free;
            }

            if (backupSector is not (0 or 0xFFFF) && backupSector + fsInfoSector < l.ReservedSectors
                && !ReadSector(device, backupSector + fsInfoSector).AsSpan().SequenceEqual(fsi))
                warnings.Add("バックアップ FSInfo が本体と一致しません。");
        }

        // FAT の先頭エントリ
        uint fat0 = vol.ReadFatEntry(0);
        uint fat1 = vol.ReadFatEntry(1);
        if ((fat0 & 0xFF) != boot[21] || (fat0 | 0xFF) != 0x0FFFFFFF)
            errors.Add($"FAT[0] (0x{fat0:X8}) がメディアディスクリプタ 0x{boot[21]:X2} と一致しません。");
        if ((fat1 & 0x03FFFFFF) != 0x03FFFFFF)
            errors.Add($"FAT[1] (0x{fat1:X8}) が不正です。");
        else if ((fat1 & 0x08000000) == 0)
            warnings.Add("FAT[1] のクリーンシャットダウンビットが立っていません（前回正しく取り外されていない可能性）。");

        // FAT のミラー一致
        if (l.NumberOfFats > 1 && !FatsMatch(device, l, ct))
            errors.Add("FAT のコピー同士が一致しません。");

        // ルートディレクトリ
        string label = vol.BootLabel;
        if (!vol.IsValidCluster(vol.RootCluster))
        {
            errors.Add($"ルートディレクトリのクラスタ番号 {vol.RootCluster} が範囲外です。");
        }
        else
        {
            uint next = vol.ReadFatEntry(vol.RootCluster);
            if (next < 0x0FFFFFF8 && !vol.IsValidCluster(next))
                errors.Add($"ルートディレクトリのクラスタチェーンが不正です (FAT[{vol.RootCluster}] = 0x{next:X8})。");
            else
            {
                string? rootLabel = FindRootLabel(vol.ReadCluster(vol.RootCluster));
                if (rootLabel != null)
                {
                    if (rootLabel != label && label != "NO NAME")
                        warnings.Add($"ブートセクタのラベル \"{label}\" とルートディレクトリのラベル \"{rootLabel}\" が異なります。");
                    label = rootLabel;
                }
                else if (label == "NO NAME")
                {
                    label = "";
                }
            }
        }

        long freeClusters = vol.CountFreeClusters(ct);
        if (fsInfoFree is uint f && f != freeClusters)
            warnings.Add($"FSInfo の空きクラスタ数 ({f:N0}) と FAT の実際の空き ({freeClusters:N0}) が異なります。");

        return new Fat32VerifyReport(errors.Count == 0, errors, warnings, l, label, vol.VolumeId, freeClusters, fsInfoFree);
    }

    /// <summary>MBR があればパーティション 1 を、無ければデバイス全体をボリュームとして返す（イメージファイル用）。</summary>
    public static IBlockDevice LocateVolume(IBlockDevice disk, out long partitionOffset)
    {
        partitionOffset = 0;
        byte[] s0 = ReadSector(disk, 0);
        bool signature = s0[510] == 0x55 && s0[511] == 0xAA;
        bool looksLikeBoot = (s0[0] == 0xEB && s0[2] == 0x90) || s0[0] == 0xE9;
        if (!signature || looksLikeBoot) return disk;

        for (int i = 0; i < 4; i++)
        {
            int e = 446 + 16 * i;
            if (s0[e + 4] == 0) continue;
            long start = (long)U32(s0, e + 8) * disk.SectorSize;
            long length = (long)U32(s0, e + 12) * disk.SectorSize;
            if (length <= 0 || start <= 0 || start + length > disk.Length) continue;
            partitionOffset = start;
            return new SubBlockDevice(disk, start, length);
        }

        return disk;
    }

    private static bool FatsMatch(IBlockDevice device, Fat32Layout l, CancellationToken ct)
    {
        long fatBytes = (long)l.FatSizeSectors * l.BytesPerSector;
        int chunk = (int)Math.Min(fatBytes, BlockDeviceExtensions.ChunkSize);
        var a = new byte[chunk];
        var b = new byte[chunk];
        for (long off = 0; off < fatBytes; off += chunk)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(chunk, fatBytes - off);
            device.Read(l.FatStartSector * l.BytesPerSector + off, a.AsSpan(0, n));
            for (int fat = 1; fat < l.NumberOfFats; fat++)
            {
                device.Read((l.FatStartSector + (long)fat * l.FatSizeSectors) * l.BytesPerSector + off, b.AsSpan(0, n));
                if (!a.AsSpan(0, n).SequenceEqual(b.AsSpan(0, n))) return false;
            }
        }

        return true;
    }

    /// <summary>ルートディレクトリ先頭クラスタからボリュームラベルのエントリを探す。</summary>
    private static string? FindRootLabel(byte[] cluster)
    {
        for (int off = 0; off + 32 <= cluster.Length; off += 32)
        {
            byte first = cluster[off];
            if (first == 0x00) break;
            byte attr = cluster[off + 11];
            if (first != 0xE5 && (attr & 0x3F) != 0x0F && (attr & 0x18) == 0x08)
                return VolumeLabel.Decode(cluster.AsSpan(off, VolumeLabel.MaxBytes));
        }

        return null;
    }

    private static byte[] ReadSector(IBlockDevice device, long sector)
    {
        var b = new byte[device.SectorSize];
        device.Read(sector * device.SectorSize, b);
        return b;
    }

    private static uint U32(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset));
}
