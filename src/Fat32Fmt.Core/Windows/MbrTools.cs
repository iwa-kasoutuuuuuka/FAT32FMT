using System.Buffers.Binary;
using System.Runtime.Versioning;
using Fat32Fmt.Core.Fat32;

namespace Fat32Fmt.Core.Windows;

/// <summary>実ディスクの MBR パーティションテーブルの操作。</summary>
[SupportedOSPlatform("windows")]
public static class MbrTools
{
    public const byte TypeFat32Chs = 0x0B;
    public const byte TypeFat32Lba = PartitionPlanner.Fat32LbaType;
    public const byte TypeGptProtective = 0xEE;

    /// <summary>
    /// 指定ディスクの MBR で、指定オフセットから始まるパーティションの種別を FAT32 LBA (0x0C) にする。
    /// SD Association のフォーマッタが作る exFAT パーティション (0x07) のままだと、
    /// パーティション種別を見て判断する機器（カメラ・カーナビ等）が FAT32 を認識しないことがあるため。
    /// </summary>
    /// <returns>変更した場合 true。</returns>
    public static bool EnsureFat32PartitionType(int diskNumber, long partitionOffsetBytes, int sectorSize, Action<string>? log = null)
    {
        if (diskNumber < 0) throw new ArgumentOutOfRangeException(nameof(diskNumber));
        if (partitionOffsetBytes <= 0)
        {
            log?.Invoke("パーティションテーブルの無いディスク（スーパーフロッピー）のため MBR 修正はスキップします。");
            return false;
        }

        using var io = new RawHandleIo(
            RawHandleIo.Open($@"\\.\PhysicalDrive{diskNumber}", Native.GENERIC_READ | Native.GENERIC_WRITE,
                             Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_WRITE_THROUGH),
            sectorSize);

        var mbr = new byte[sectorSize];
        io.Read(0, mbr);
        if (mbr[510] != 0x55 || mbr[511] != 0xAA)
        {
            log?.Invoke("MBR 署名が見つからないため、パーティション種別の修正はスキップします。");
            return false;
        }

        long startLba = partitionOffsetBytes / sectorSize;
        int found = -1;
        for (int i = 0; i < 4; i++)
        {
            int e = 446 + 16 * i;
            byte type = mbr[e + 4];
            if (type == TypeGptProtective)
            {
                log?.Invoke("GPT ディスクのため、MBR パーティション種別の修正はスキップします。");
                return false;
            }
            uint lba = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(e + 8));
            if (type != 0 && lba == startLba) found = e;
        }
        if (found < 0)
        {
            log?.Invoke($"MBR 内に LBA {startLba} から始まるパーティションが見つからないため、種別の修正はスキップします。");
            return false;
        }

        byte current = mbr[found + 4];
        if (current is TypeFat32Chs or TypeFat32Lba)
        {
            log?.Invoke($"パーティション種別はすでに FAT32 (0x{current:X2}) です。");
            return false;
        }

        mbr[found + 4] = TypeFat32Lba;
        io.Write(0, mbr);
        io.Flush();
        Native.Ioctl(io.Handle, Native.IOCTL_DISK_UPDATE_PROPERTIES);
        log?.Invoke($"MBR のパーティション種別を 0x{current:X2} → 0x0C (FAT32 LBA) に変更しました。");
        return true;
    }
}
