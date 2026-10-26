using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Fat32Fmt.Core.IO;
using Microsoft.Win32.SafeHandles;

namespace Fat32Fmt.Core.Windows;

/// <summary>物理ディスク（カード全体）の情報。</summary>
public sealed record DiskInfo
{
    public required int DiskNumber { get; init; }
    public required long Length { get; init; }
    public int SectorSize { get; init; } = 512;
    public string DeviceName { get; init; } = "";
    public StorageBusType BusType { get; init; }
    /// <summary>リムーバブルメディア（SD カード、USB メモリなど）と判断できる。</summary>
    public bool IsRemovable { get; init; }
    public bool IsSystemDisk { get; init; }
    /// <summary>このディスク上のボリュームに割り当てられたドライブ文字。</summary>
    public IReadOnlyList<char> Letters { get; init; } = Array.Empty<char>();

    public string DevicePath => PhysicalDisk.DevicePath(DiskNumber);

    public string Display =>
        $"Disk {DiskNumber}  {SizeFormat.Both(Length)}" +
        (DeviceName.Length > 0 ? $"  [{DeviceName}]" : "") +
        (Letters.Count > 0 ? $"  ドライブ {string.Join(", ", Letters.Select(l => l + ":"))}" : "  （ドライブ文字なし）") +
        (IsRemovable ? "" : "  ※固定ディスク");
}

/// <summary>コマンドの対象指定（"E:" や "disk2"）を物理ディスクに解決し、安全確認を行う。</summary>
[SupportedOSPlatform("windows")]
public static class DiskTargets
{
    /// <summary>"disk2" / "PhysicalDrive2" / "\\.\PhysicalDrive2" ならディスク番号、それ以外は null。</summary>
    public static int? ParseDiskNumber(string spec)
    {
        string s = spec.Trim();
        foreach (var prefix in new[] { @"\\.\PhysicalDrive", "PhysicalDrive", "disk:", "disk" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(s[prefix.Length..], out int n) && n >= 0)
                return n;
        }
        return null;
    }

    /// <summary>"E" / "E:" / "E:\" ならドライブ文字、それ以外は null。</summary>
    public static char? ParseDriveLetter(string spec)
    {
        string t = spec.Trim().TrimEnd('\\', '/').TrimEnd(':');
        return t.Length == 1 && char.IsAsciiLetter(t[0]) ? char.ToUpperInvariant(t[0]) : null;
    }

    /// <summary>ドライブ文字またはディスク指定から、そのドライブがある物理ディスクを求める。</summary>
    public static DiskInfo Resolve(string spec)
    {
        if (ParseDiskNumber(spec) is int n) return Describe(n);
        if (ParseDriveLetter(spec) is not char letter)
            throw new VolumeFormatException($"対象の指定が不正です: \"{spec}\"（例: E:、disk2）");
        if (DriveEnumerator.KindOf(letter) == DriveKind.NoRootDir)
            throw new VolumeFormatException($"ドライブ {letter}: は存在しません。");
        var geo = VolumeQuery.TryQueryGeometry(letter);
        if (geo is not { DiskNumber: >= 0 })
            throw new VolumeFormatException($"ドライブ {letter}: の物理ディスクを特定できません。メディアが挿入されているか確認してください。");
        return Describe(geo.DiskNumber);
    }

    /// <summary>ディスクの情報を集める（管理者権限不要）。</summary>
    public static DiskInfo Describe(int diskNumber)
    {
        long length;
        int sectorSize = 512;
        try
        {
            using var h = RawHandleIo.Open(PhysicalDisk.DevicePath(diskNumber), 0, 0);
            (length, sectorSize) = PhysicalDisk.QuerySize(h);
        }
        catch (Exception ex) when (ex is not VolumeFormatException)
        {
            throw new VolumeFormatException($"Disk {diskNumber} を開けません: {ex.Message}");
        }

        var letters = VolumesOnDisk(diskNumber).SelectMany(v => v.Letters).Distinct().OrderBy(c => c).ToList();
        var dev = VolumeQuery.TryQueryDevice(diskNumber);
        bool removable = dev?.RemovableMedia == true
                         || dev?.BusType is StorageBusType.Sd or StorageBusType.Mmc
                         || letters.Any(l => DriveEnumerator.KindOf(l) == DriveKind.Removable);
        return new DiskInfo
        {
            DiskNumber = diskNumber,
            Length = length,
            SectorSize = sectorSize,
            DeviceName = dev?.DisplayName ?? "",
            BusType = dev?.BusType ?? StorageBusType.Unknown,
            IsRemovable = removable,
            IsSystemDisk = ProtectedDisks.IsProtected(diskNumber, ProtectedDisks.Query()),
            Letters = letters,
        };
    }

    /// <summary>書き込み・読み出しの対象にしてよいか確認する。問題があれば例外。</summary>
    public static void Validate(DiskInfo disk, bool allowFixed)
    {
        ProtectedDisks.EnsureNotProtected(disk.DiskNumber); // Windows・起動用パーティションのディスク。判定できなければ中止
        if (!disk.IsRemovable && !allowFixed)
            throw new VolumeFormatException($"Disk {disk.DiskNumber} はリムーバブルではなく固定ディスクとして認識されています。本当に対象にする場合は --allow-fixed を指定してください。");
        if (disk.Length <= 0)
            throw new VolumeFormatException($"Disk {disk.DiskNumber} のサイズを取得できません。メディアが挿入されているか確認してください。");
        string cwd = Environment.CurrentDirectory;
        if (DiskNumberOfPath(cwd) == disk.DiskNumber)
            throw new VolumeFormatException($"カレントディレクトリが Disk {disk.DiskNumber} 上にあります。別のドライブから実行してください。");
    }

    /// <summary>パスがある物理ディスクの番号（ネットワークドライブなどで分からなければ null）。</summary>
    public static int? DiskNumberOfPath(string path)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        if (root is not { Length: >= 2 } || root[1] != ':') return null;
        var geo = VolumeQuery.TryQueryGeometry(root[0]);
        return geo is { DiskNumber: >= 0 } ? geo.DiskNumber : null;
    }

    /// <summary>ディスク上のボリューム（GUID パスとドライブ文字）。</summary>
    public static List<(string VolumePath, List<char> Letters)> VolumesOnDisk(int diskNumber) =>
        DiskNative.EnumerateVolumeGuidPaths()
            .Where(v => DiskNative.DisksOfVolume(v).Contains(diskNumber))
            .Select(v => (v, DiskNative.DriveLettersOf(v)))
            .ToList();

    /// <summary>パーティションの無いカードに Windows がドライブ文字を付けたときの説明（消去後の正常な状態）。</summary>
    public static string SuperfloppyNote(IEnumerable<char> letters) =>
        $"パーティションの無いカードは、Windows がディスク全体を未フォーマットのボリューム {string.Join(", ", letters.Select(l => l + ":"))} として表示します（正常）。" +
        "使うには wipe に --format を付けて MBR + FAT32 で初期化するか、イメージを書き戻してください。";

    /// <summary>書き込み後、Windows がボリュームを認識してドライブ文字を割り当てるのを待つ。</summary>
    public static IReadOnlyList<char> WaitForLetters(int diskNumber, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var letters = VolumesOnDisk(diskNumber).SelectMany(v => v.Letters).Distinct().OrderBy(c => c).ToList();
            if (letters.Count > 0 || DateTime.UtcNow >= deadline) return letters;
            Thread.Sleep(500);
        }
    }
}

/// <summary>マウント中のボリューム (\\.\X:) を読み取り専用で開く（削除ファイル復旧用。ロックしないので使用中でも開ける）。管理者権限が必要。</summary>
[SupportedOSPlatform("windows")]
public sealed class ReadOnlyVolume : IBlockDevice
{
    private readonly RawHandleIo _io;

    private ReadOnlyVolume(RawHandleIo io, VolumeGeometry geo)
    {
        _io = io; Geometry = geo;
    }

    public VolumeGeometry Geometry { get; }
    public long Length => Geometry.Length;
    public int SectorSize => Geometry.SectorSize;
    public bool CanWrite => false;

    public static ReadOnlyVolume Open(char letter)
    {
        var h = RawHandleIo.Open(WindowsVolume.DevicePath(letter), Native.GENERIC_READ, Native.FILE_FLAG_NO_BUFFERING);
        try
        {
            Native.Ioctl(h, Native.FSCTL_ALLOW_EXTENDED_DASD_IO); // 失敗しても続行
            var geo = VolumeQuery.QueryGeometry(h);
            return new ReadOnlyVolume(new RawHandleIo(h, geo.SectorSize), geo);
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    public void Read(long offset, Span<byte> buffer)
    {
        BlockDeviceGuard.CheckRange(this, offset, buffer.Length);
        _io.Read(offset, buffer);
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer) => throw new InvalidOperationException("読み取り専用のボリュームです。");

    public void Flush() { }

    public void Dispose() => _io.Dispose();
}

/// <summary>
/// 物理ディスク (\\.\PhysicalDriveN) 全体を <see cref="IBlockDevice"/> として開く。
/// 書き込み用に開くと、ディスク上の全ボリュームをロックしてマウント解除する（Windows はマウント中のボリューム範囲への生書き込みを拒否するため）。
/// Dispose でパーティション情報を再読込させ、ロックを解除して Windows に再マウントさせる。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PhysicalDisk : IBlockDevice
{
    private readonly RawHandleIo _io;
    private readonly List<SafeFileHandle> _volumes;
    private bool _disposed;

    private PhysicalDisk(int diskNumber, RawHandleIo io, long length, bool writable, List<SafeFileHandle> volumes)
    {
        DiskNumber = diskNumber; _io = io; Length = length; CanWrite = writable; _volumes = volumes;
    }

    public int DiskNumber { get; }
    public long Length { get; }
    public int SectorSize => _io.SectorSize;
    public bool CanWrite { get; }

    public static string DevicePath(int diskNumber) => $@"\\.\PhysicalDrive{diskNumber}";

    /// <summary>
    /// ディスクを開く。管理者権限が必要。
    /// writable なら全ボリュームのロックとマウント解除が必須（できなければ例外）。
    /// 読み取り専用ならロックだけ試み（バックアップ中にファイルが書き換わらないように）、失敗しても警告を出して続ける。
    /// </summary>
    public static PhysicalDisk Open(int diskNumber, bool writable, Action<string>? log = null, int lockRetries = 10)
    {
        var volumes = new List<SafeFileHandle>();
        try
        {
            foreach (var (path, letters) in DiskTargets.VolumesOnDisk(diskNumber))
            {
                string name = letters.Count > 0 ? string.Join(", ", letters.Select(l => l + ":")) : path;
                var h = LockVolume(path, name, writable ? lockRetries : 2, log);
                if (h == null)
                {
                    if (writable)
                        throw new VolumeFormatException(
                            $"ボリューム {name} をロックできません。エクスプローラーなどでこのドライブを開いているウィンドウやアプリを閉じてから再試行してください。");
                    log?.Invoke($"警告: ボリューム {name} をロックできないため、使用中のまま読み取ります（読み取り中に書き込まれた部分は不整合になる可能性があります）。");
                    continue;
                }
                volumes.Add(h);
                if (writable)
                {
                    if (!Native.Ioctl(h, Native.FSCTL_DISMOUNT_VOLUME))
                        throw Native.LastError($"ボリューム {name} のマウント解除に失敗");
                    log?.Invoke($"ボリューム {name} をロックしてマウント解除しました。");
                }
                else log?.Invoke($"ボリューム {name} をロックしました（読み取り中の書き込みを防止）。");
            }

            uint access = Native.GENERIC_READ | (writable ? Native.GENERIC_WRITE : 0);
            var dh = RawHandleIo.Open(DevicePath(diskNumber), access, Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_WRITE_THROUGH);
            try
            {
                if (writable && VolumeQuery.IsWriteProtected(dh) == true)
                    throw new VolumeFormatException($"Disk {diskNumber} は書き込み禁止です。SD カード側面のロックスイッチを解除してから挿し直してください。");
                var (length, sectorSize) = QuerySize(dh);
                return new PhysicalDisk(diskNumber, new RawHandleIo(dh, sectorSize), length, writable, volumes);
            }
            catch
            {
                dh.Dispose();
                throw;
            }
        }
        catch
        {
            ReleaseVolumes(volumes, dismount: false);
            throw;
        }
    }

    internal static (long Length, int SectorSize) QuerySize(SafeFileHandle h)
    {
        Span<byte> geo = stackalloc byte[256];
        if (!Native.IoctlOut(h, Native.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, geo, out _))
            throw Native.LastError("ディスクのジオメトリを取得できません");
        // DISK_GEOMETRY_EX: DISK_GEOMETRY(24: Cylinders(8) MediaType(4) Tracks(4) SectorsPerTrack(4) BytesPerSector(4)) DiskSize(8)
        uint bps = BinaryPrimitives.ReadUInt32LittleEndian(geo[20..]);
        int sectorSize = bps is 512 or 1024 or 2048 or 4096 ? (int)bps : 512;
        long length = BinaryPrimitives.ReadInt64LittleEndian(geo[24..]);
        if (length <= 0) throw new VolumeFormatException("ディスクサイズが 0 です。メディアが挿入されているか確認してください。");
        return (length - length % sectorSize, sectorSize);
    }

    private static SafeFileHandle? LockVolume(string volumeGuidPath, string name, int retries, Action<string>? log)
    {
        SafeFileHandle h;
        try
        {
            h = RawHandleIo.Open(volumeGuidPath.TrimEnd('\\'), Native.GENERIC_READ | Native.GENERIC_WRITE, 0);
        }
        catch (Exception ex)
        {
            log?.Invoke($"ボリューム {name} を開けません: {ex.Message}");
            return null;
        }

        for (int i = 0; ; i++)
        {
            if (Native.Ioctl(h, Native.FSCTL_LOCK_VOLUME)) return h;
            int err = Marshal.GetLastWin32Error();
            if (i + 1 >= retries)
            {
                log?.Invoke($"ボリューム {name} のロックに失敗しました (Win32 エラー {err})。");
                h.Dispose();
                return null;
            }
            log?.Invoke($"ボリューム {name} のロック待機中... ({i + 1}/{retries})");
            Thread.Sleep(500);
        }
    }

    private static void ReleaseVolumes(List<SafeFileHandle> volumes, bool dismount)
    {
        foreach (var h in volumes)
        {
            if (dismount) Native.Ioctl(h, Native.FSCTL_DISMOUNT_VOLUME); // 書き込み前の古いファイルシステム情報を捨てさせる
            Native.Ioctl(h, Native.FSCTL_UNLOCK_VOLUME);
            h.Dispose();
        }
        volumes.Clear();
    }

    public void Read(long offset, Span<byte> buffer)
    {
        BlockDeviceGuard.CheckRange(this, offset, buffer.Length);
        _io.Read(offset, buffer);
    }

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (!CanWrite) throw new InvalidOperationException("読み取り専用で開いたディスクです。");
        BlockDeviceGuard.CheckRange(this, offset, data.Length);
        _io.Write(offset, data);
    }

    public void Flush() => _io.Flush();

    /// <summary>
    /// パーティションテーブルを削除したことを Windows に伝える（IOCTL_DISK_DELETE_DRIVE_LAYOUT。MBR の署名 55 AA も消える）。
    /// パーティションを作らない消去の最後に呼び、パーティションマネージャが古いレイアウトを持ち越さないようにする。
    /// なお、パーティションの無いリムーバブルディスクは Windows がディスク全体を 1 つのボリューム（スーパーフロッピー、
    /// オフセット 0）として見せるので、ドライブ文字は消えない（正常な動作）。
    /// </summary>
    public void DeletePartitionTable(Action<string>? log = null)
    {
        if (!CanWrite) throw new InvalidOperationException("読み取り専用で開いたディスクです。");
        _io.Flush();
        if (Native.Ioctl(_io.Handle, DiskNative.IOCTL_DISK_DELETE_DRIVE_LAYOUT))
            log?.Invoke("パーティションテーブルを削除しました（Windows に「パーティションなし」と通知）。");
        else
            log?.Invoke($"警告: パーティションテーブル削除の通知に失敗しました (Win32 エラー {Marshal.GetLastWin32Error()})。");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (CanWrite)
        {
            _io.Flush();
            // 古いボリュームのロックを先に外してから再読込させる（ロック中だと古いパーティションが残ることがある）
            ReleaseVolumes(_volumes, dismount: true);
            Native.Ioctl(_io.Handle, Native.IOCTL_DISK_UPDATE_PROPERTIES); // 新しいパーティションテーブルを読み直させる
        }
        _io.Dispose();
        ReleaseVolumes(_volumes, dismount: false);
    }
}
