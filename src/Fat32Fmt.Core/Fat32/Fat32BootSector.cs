using System.Buffers.Binary;

namespace Fat32Fmt.Core.Fat32;

/// <summary>FAT32 のブートセクタと FSInfo セクタを組み立てる。</summary>
public static class Fat32BootSector
{
    public const int BackupBootSector = 6;
    public const int FsInfoSector = 1;
    public const uint RootCluster = 2;

    private const uint FsInfoLeadSig = 0x41615252;
    private const uint FsInfoStructSig = 0x61417272;
    private const uint FsInfoTrailSig = 0xAA550000;

    // 起動されたときにメッセージを表示してキー入力後に再起動する最小のブートコード(オフセット 0x5A に配置)
    private static readonly byte[] BootCode =
    [
        0x0E,             // push cs
        0x1F,             // pop ds
        0xBE, 0x77, 0x7C, // mov si, 0x7C77 (メッセージ)
        0xAC,             // lodsb
        0x22, 0xC0,       // and al, al
        0x74, 0x0B,       // jz +11
        0x56,             // push si
        0xB4, 0x0E,       // mov ah, 0x0E
        0xBB, 0x07, 0x00, // mov bx, 7
        0xCD, 0x10,       // int 0x10
        0x5E,             // pop si
        0xEB, 0xF0,       // jmp lodsb
        0x32, 0xE4,       // xor ah, ah
        0xCD, 0x16,       // int 0x16
        0xCD, 0x19,       // int 0x19
        0xEB, 0xFE,       // jmp $
    ];

    private static readonly byte[] BootMessage =
        "This is not a bootable disk. Please remove it and press any key to reboot.\r\n\0"u8.ToArray();

    public static byte[] Build(Fat32Layout layout, ReadOnlySpan<byte> label11, uint volumeId,
        ushort sectorsPerTrack = 63, ushort heads = 255, byte mediaDescriptor = 0xF8)
    {
        var s = new byte[layout.BytesPerSector];
        Span<byte> b = s;

        b[0] = 0xEB;
        b[1] = 0x58;
        b[2] = 0x90;
        "MSWIN4.1"u8.CopyTo(b[3..]);
        BinaryPrimitives.WriteUInt16LittleEndian(b[11..], (ushort)layout.BytesPerSector);
        b[13] = (byte)layout.SectorsPerCluster;
        BinaryPrimitives.WriteUInt16LittleEndian(b[14..], (ushort)layout.ReservedSectors);
        b[16] = (byte)layout.NumberOfFats;
        // 17: RootEntCnt = 0, 19: TotSec16 = 0
        b[21] = mediaDescriptor;
        // 22: FATSz16 = 0
        BinaryPrimitives.WriteUInt16LittleEndian(b[24..], sectorsPerTrack);
        BinaryPrimitives.WriteUInt16LittleEndian(b[26..], heads);
        BinaryPrimitives.WriteUInt32LittleEndian(b[28..], layout.HiddenSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(b[32..], layout.TotalSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(b[36..], layout.FatSizeSectors);
        // 40: ExtFlags = 0(全 FAT をミラー), 42: FSVer = 0
        BinaryPrimitives.WriteUInt32LittleEndian(b[44..], RootCluster);
        BinaryPrimitives.WriteUInt16LittleEndian(b[48..], FsInfoSector);
        BinaryPrimitives.WriteUInt16LittleEndian(b[50..], BackupBootSector);
        b[64] = 0x80; // ドライブ番号
        b[66] = 0x29; // 拡張ブートシグネチャ
        BinaryPrimitives.WriteUInt32LittleEndian(b[67..], volumeId);
        if (label11.Length == VolumeLabel.MaxBytes)
        {
            label11.CopyTo(b[71..]);
        }
        else
        {
            VolumeLabel.NoName.CopyTo(b[71..]);
        }

        "FAT32   "u8.CopyTo(b[82..]);
        BootCode.CopyTo(b[90..]);
        BootMessage.CopyTo(b[(90 + BootCode.Length)..]);
        b[510] = 0x55;
        b[511] = 0xAA;
        return s;
    }

    public static byte[] BuildFsInfo(int bytesPerSector, uint freeClusters, uint nextFreeCluster)
    {
        var s = new byte[bytesPerSector];
        Span<byte> b = s;
        BinaryPrimitives.WriteUInt32LittleEndian(b, FsInfoLeadSig);
        BinaryPrimitives.WriteUInt32LittleEndian(b[484..], FsInfoStructSig);
        BinaryPrimitives.WriteUInt32LittleEndian(b[488..], freeClusters);
        BinaryPrimitives.WriteUInt32LittleEndian(b[492..], nextFreeCluster);
        BinaryPrimitives.WriteUInt32LittleEndian(b[508..], FsInfoTrailSig);
        return s;
    }

    /// <summary>ボリューム ID(シリアル番号)を日時から生成する(DOS/Windows と同様の方式)。</summary>
    public static uint MakeVolumeId(DateTime t)
    {
        ushort lo = (ushort)(((t.Month << 8) | t.Day) + ((t.Second << 8) | (t.Millisecond / 10)));
        ushort hi = (ushort)(((t.Hour << 8) | t.Minute) + t.Year);
        return ((uint)hi << 16) | lo;
    }

    public static ushort DosTime(DateTime t) => (ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));

    public static ushort DosDate(DateTime t) => (ushort)(((Math.Max(t.Year, 1980) - 1980) << 9) | (t.Month << 5) | t.Day);
}
