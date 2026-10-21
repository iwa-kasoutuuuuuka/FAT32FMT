using System.Runtime.Versioning;
using System.Text;
using Fat32Fmt.Core.Fat32;

namespace Fat32Fmt.Core.Windows;

public enum DriveKind { Unknown, NoRootDir, Removable, Fixed, Remote, Cdrom, RamDisk }

public sealed record DriveEntry
{
    public required char Letter { get; init; }
    public required DriveKind Kind { get; init; }
    public string Label { get; init; } = "";
    public string FileSystem { get; init; } = "";
    public long SizeBytes { get; init; }
    public int DiskNumber { get; init; } = -1;
    public int PartitionNumber { get; init; }
    public long PartitionOffset { get; init; }
    public PartitionStyle PartitionStyle { get; init; } = PartitionStyle.Raw;
    public byte MbrPartitionType { get; init; }
    public int SectorSize { get; init; } = 512;
    public string DeviceName { get; init; } = "";
    public StorageBusType BusType { get; init; }
    public bool IsSystemDrive { get; init; }
    /// <summary>システムドライブと同じ物理ディスク上にある（内蔵ディスクの別パーティションなど）。</summary>
    public bool IsOnSystemDisk { get; init; }
    /// <summary>書き込み禁止（SD カードのロックスイッチなど）。判定できない場合は false。</summary>
    public bool IsWriteProtected { get; init; }

    public bool IsRemovable => Kind == DriveKind.Removable;

    /// <summary>
    /// パーティション表が無く、Windows がディスク全体を 1 つのボリュームとして見せている（スーパーフロッピー形式）。
    /// 消去後のカードなど。Windows はこの場合も開始位置 0 の疑似パーティションとドライブ文字を出す。
    /// </summary>
    public bool IsSuperFloppy => DiskNumber >= 0 && PartitionOffset == 0 && SizeBytes > 0;

    /// <summary>カード規格の目安（ディスク容量 ≒ パーティション開始 + サイズ）。</summary>
    public string CapacityClass => SdCardGeometry.CapacityClass(PartitionOffset + SizeBytes);

    /// <summary>GUI のドロップダウン等に出す 1 行表記。</summary>
    public string Display
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(Letter).Append(":  ");
            sb.Append(Label.Length > 0 ? Label : "(ラベルなし)");
            sb.Append("  ").Append(SizeFormat.Both(SizeBytes));
            if (FileSystem.Length > 0) sb.Append("  ").Append(FileSystem);
            if (DeviceName.Length > 0) sb.Append("  [").Append(DeviceName).Append(']');
            if (Kind == DriveKind.Fixed) sb.Append("  ※固定ディスク");
            if (IsWriteProtected) sb.Append("  ※書き込み禁止");
            return sb.ToString();
        }
    }

    public string DescribeDetails()
    {
        var lines = new List<string>
        {
            $"ドライブ          : {Letter}:  ({KindName(Kind)}{(IsSystemDrive ? "、システムドライブ" : IsOnSystemDisk ? "、システムと同じ物理ディスク" : "")}{(IsWriteProtected ? "、書き込み禁止" : "")})",
            $"現在のラベル      : {(Label.Length > 0 ? Label : "(なし)")}",
            $"現在の FS         : {(FileSystem.Length > 0 ? FileSystem : "(不明/未フォーマット)")}",
            $"サイズ            : {SizeFormat.Both(SizeBytes)}  {CapacityClass}",
            $"デバイス          : {(DeviceName.Length > 0 ? DeviceName : "(不明)")}  バス: {BusType}",
        };
        if (DiskNumber >= 0)
            lines.Add($"物理ディスク      : Disk {DiskNumber}, パーティション {PartitionNumber}, オフセット {SizeFormat.Binary(PartitionOffset)}");
        lines.Add(IsSuperFloppy
            ? "パーティション    : なし（ディスク全体を 1 ボリュームとして使う形式）" + Environment.NewLine +
              $"                    ※ SD 規格どおりの MBR 形式にするには `fat32fmt wipe disk{DiskNumber} --format` を使ってください（機器によってはこの形式を認識しません）"
            : PartitionStyle switch
        {
            PartitionStyle.Mbr => $"パーティション    : MBR  種別 0x{MbrPartitionType:X2}{MbrTypeName(MbrPartitionType)}",
            PartitionStyle.Gpt => "パーティション    : GPT",
            _ => "パーティション    : なし / 不明",
        });
        return string.Join(Environment.NewLine, lines);
    }

    public static string KindName(DriveKind k) => k switch
    {
        DriveKind.Removable => "リムーバブル",
        DriveKind.Fixed => "固定ディスク",
        DriveKind.Remote => "ネットワーク",
        DriveKind.Cdrom => "CD/DVD",
        DriveKind.RamDisk => "RAM ディスク",
        _ => "不明",
    };

    public static string MbrTypeName(byte t) => t switch
    {
        0x07 => " (NTFS/exFAT)",
        0x0B => " (FAT32 CHS)",
        0x0C => " (FAT32 LBA)",
        0x0E => " (FAT16 LBA)",
        0x06 => " (FAT16)",
        0xEE => " (GPT 保護)",
        _ => "",
    };
}

[SupportedOSPlatform("windows")]
public static class DriveEnumerator
{
    public static char SystemDriveLetter => char.ToUpperInvariant(Environment.SystemDirectory[0]);

    public static DriveKind KindOf(char letter) => Native.GetDriveTypeW($"{char.ToUpperInvariant(letter)}:\\") switch
    {
        Native.DRIVE_NO_ROOT_DIR => DriveKind.NoRootDir,
        Native.DRIVE_REMOVABLE => DriveKind.Removable,
        Native.DRIVE_FIXED => DriveKind.Fixed,
        Native.DRIVE_REMOTE => DriveKind.Remote,
        Native.DRIVE_CDROM => DriveKind.Cdrom,
        Native.DRIVE_RAMDISK => DriveKind.RamDisk,
        _ => DriveKind.Unknown,
    };

    /// <summary>
    /// フォーマット対象になり得るドライブを列挙する。
    /// 既定ではリムーバブルのみ。includeFixed で固定ディスク（システムドライブと同じ物理ディスク上のもの以外）も含める。
    /// </summary>
    public static List<DriveEntry> Enumerate(bool includeFixed = false)
    {
        var result = new List<DriveEntry>();
        uint mask = Native.GetLogicalDrives();
        for (int i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) == 0) continue;
            char letter = (char)('A' + i);
            var kind = KindOf(letter);
            if (kind == DriveKind.Removable || (includeFixed && kind == DriveKind.Fixed))
            {
                var e = Describe(letter);
                if (e.IsSystemDrive || e.IsOnSystemDisk) continue;
                result.Add(e);
            }
        }
        return result;
    }

    public static DriveEntry Describe(char letter)
    {
        letter = char.ToUpperInvariant(letter);
        string root = $"{letter}:\\";
        var kind = KindOf(letter);

        string label = "", fsName = "";
        var lbl = new StringBuilder(261);
        var fs = new StringBuilder(261);
        if (Native.GetVolumeInformationW(root, lbl, 261, out _, out _, out _, fs, 261))
        {
            label = lbl.ToString();
            fsName = fs.ToString();
        }

        long size = 0;
        if (Native.GetDiskFreeSpaceExW(root, out _, out ulong total, out _)) size = (long)total;

        var geo = VolumeQuery.TryQueryGeometry(letter);
        if (geo is { Length: > 0 }) size = geo.Length;
        var dev = geo is { DiskNumber: >= 0 } ? VolumeQuery.TryQueryDevice(geo.DiskNumber) : null;

        return new DriveEntry
        {
            Letter = letter,
            Kind = kind,
            Label = label,
            FileSystem = fsName,
            SizeBytes = size,
            DiskNumber = geo?.DiskNumber ?? -1,
            PartitionNumber = geo?.PartitionNumber ?? 0,
            PartitionOffset = geo?.PartitionOffset ?? 0,
            PartitionStyle = geo?.Style ?? PartitionStyle.Raw,
            MbrPartitionType = geo?.MbrPartitionType ?? 0,
            SectorSize = geo?.SectorSize ?? 512,
            DeviceName = dev?.DisplayName ?? "",
            BusType = dev?.BusType ?? StorageBusType.Unknown,
            IsSystemDrive = letter == SystemDriveLetter,
            IsOnSystemDisk = geo is { DiskNumber: >= 0 } && geo.DiskNumber == SystemDiskNumber,
            IsWriteProtected = kind is DriveKind.Removable or DriveKind.Fixed && VolumeQuery.TryQueryWriteProtected(letter) == true,
        };
    }

    /// <summary>システムドライブがある物理ディスクの番号（取得できなければ -1）。</summary>
    public static int SystemDiskNumber => VolumeQuery.TryQueryGeometry(SystemDriveLetter)?.DiskNumber ?? -1;
}
