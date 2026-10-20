using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Fat32Fmt.Core.IO;
using Microsoft.Win32.SafeHandles;

namespace Fat32Fmt.Core.Windows;

public enum PartitionStyle { Mbr = 0, Gpt = 1, Raw = 2 }

public enum StorageBusType
{
    Unknown = 0, Scsi = 1, Atapi = 2, Ata = 3, Ieee1394 = 4, Ssa = 5, Fibre = 6, Usb = 7, Raid = 8, iScsi = 9,
    Sas = 10, Sata = 11, Sd = 12, Mmc = 13, Virtual = 14, FileBackedVirtual = 15, Spaces = 16, Nvme = 17, Scm = 18, Ufs = 19,
}

public sealed record StorageDeviceInfo(string Vendor, string Product, StorageBusType BusType, bool RemovableMedia)
{
    public string DisplayName => string.Join(' ', new[] { Vendor, Product }.Where(s => s.Length > 0));
}

/// <summary>ボリュームの位置情報。</summary>
public sealed record VolumeGeometry(long Length, int SectorSize, int DiskNumber, int PartitionNumber,
                                    long PartitionOffset, PartitionStyle Style, byte MbrPartitionType);

/// <summary>
/// Windows のボリューム (\\.\X:) をロック・マウント解除し、<see cref="IBlockDevice"/> として開く。
/// Dispose でマウント解除→ロック解除し、Windows に再マウントさせる。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVolume : IBlockDevice
{
    private readonly RawHandleIo _io;
    private readonly bool _locked;
    private bool _disposed;

    public char Letter { get; }
    public VolumeGeometry Geometry { get; }
    public long Length => Geometry.Length;
    public int SectorSize => Geometry.SectorSize;
    public bool CanWrite => true;
    public long PartitionOffset => Geometry.PartitionOffset;

    private WindowsVolume(char letter, RawHandleIo io, VolumeGeometry geo, bool locked)
    {
        Letter = letter; _io = io; Geometry = geo; _locked = locked;
    }

    public static string DevicePath(char letter) => $@"\\.\{char.ToUpperInvariant(letter)}:";

    public static string WriteProtectedMessage(char letter) =>
        $"ドライブ {char.ToUpperInvariant(letter)}: は書き込み禁止です。SD カード側面のロックスイッチを解除してから挿し直してください。";

    /// <summary>フォーマット用に開く。ロック→マウント解除まで行う。失敗時は例外。</summary>
    public static WindowsVolume OpenForFormat(char letter, Action<string>? log = null, int lockRetries = 10)
    {
        var h = RawHandleIo.Open(DevicePath(letter), Native.GENERIC_READ | Native.GENERIC_WRITE,
                                 Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_WRITE_THROUGH);
        bool locked = false;
        try
        {
            Native.Ioctl(h, Native.FSCTL_ALLOW_EXTENDED_DASD_IO); // 失敗しても続行

            for (int i = 0; ; i++)
            {
                if (Native.Ioctl(h, Native.FSCTL_LOCK_VOLUME)) { locked = true; break; }
                int err = Marshal.GetLastWin32Error();
                if (i >= lockRetries)
                    throw new VolumeFormatException(
                        $"ボリューム {char.ToUpperInvariant(letter)}: をロックできません (Win32 エラー {err})。" +
                        "エクスプローラーなどでこのドライブを開いているウィンドウやアプリを閉じてから再試行してください。");
                log?.Invoke($"ボリュームのロック待機中... ({i + 1}/{lockRetries})");
                Thread.Sleep(500);
            }

            if (VolumeQuery.IsWriteProtected(h) == true)
                throw new VolumeFormatException(WriteProtectedMessage(letter));

            if (!Native.Ioctl(h, Native.FSCTL_DISMOUNT_VOLUME))
                throw Native.LastError("ボリュームのマウント解除に失敗");

            var geo = VolumeQuery.QueryGeometry(h);
            return new WindowsVolume(char.ToUpperInvariant(letter), new RawHandleIo(h, geo.SectorSize), geo, locked);
        }
        catch
        {
            if (locked) Native.Ioctl(h, Native.FSCTL_UNLOCK_VOLUME);
            h.Dispose();
            throw;
        }
    }

    public void Read(long offset, Span<byte> buffer)
    {
        BlockDeviceGuard.CheckRange(this, offset, buffer.Length);
        _io.Read(offset, buffer);
    }

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        BlockDeviceGuard.CheckRange(this, offset, data.Length);
        _io.Write(offset, data);
    }

    public void Flush() => _io.Flush();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _io.Flush();
        Native.Ioctl(_io.Handle, Native.FSCTL_DISMOUNT_VOLUME); // 古いファイルシステム情報を確実に破棄
        if (_locked) Native.Ioctl(_io.Handle, Native.FSCTL_UNLOCK_VOLUME);
        _io.Dispose();
    }
}

/// <summary>ボリューム／ディスクの情報を問い合わせる（管理者権限なしでも概ね動作する）。</summary>
[SupportedOSPlatform("windows")]
public static class VolumeQuery
{
    public static VolumeGeometry QueryGeometry(SafeFileHandle h)
    {
        int sectorSize = 512;
        Span<byte> geo = stackalloc byte[256];
        if (Native.IoctlOut(h, Native.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, geo, out _))
        {
            // DISK_GEOMETRY: Cylinders(8) MediaType(4) TracksPerCylinder(4) SectorsPerTrack(4) BytesPerSector(4)
            uint bps = BinaryPrimitives.ReadUInt32LittleEndian(geo[20..]);
            if (bps is 512 or 1024 or 2048 or 4096) sectorSize = (int)bps;
        }

        long length = 0;
        long offset = 0;
        var style = PartitionStyle.Raw;
        byte mbrType = 0;
        int partNo = 0;
        Span<byte> part = stackalloc byte[256];
        if (Native.IoctlOut(h, Native.IOCTL_DISK_GET_PARTITION_INFO_EX, part, out _))
        {
            // PARTITION_INFORMATION_EX: PartitionStyle(4) pad(4) StartingOffset(8) PartitionLength(8) PartitionNumber(4)
            //                            RewritePartition(1) IsServicePartition(1) pad(2) union@32 { MBR: PartitionType(1) ... }
            style = (PartitionStyle)BinaryPrimitives.ReadUInt32LittleEndian(part);
            offset = BinaryPrimitives.ReadInt64LittleEndian(part[8..]);
            length = BinaryPrimitives.ReadInt64LittleEndian(part[16..]);
            partNo = (int)BinaryPrimitives.ReadUInt32LittleEndian(part[24..]);
            if (style == PartitionStyle.Mbr) mbrType = part[32];
        }

        Span<byte> len = stackalloc byte[8];
        if (Native.IoctlOut(h, Native.IOCTL_DISK_GET_LENGTH_INFO, len, out _))
            length = BinaryPrimitives.ReadInt64LittleEndian(len);

        int disk = -1;
        Span<byte> dn = stackalloc byte[12];
        if (Native.IoctlOut(h, Native.IOCTL_STORAGE_GET_DEVICE_NUMBER, dn, out _))
        {
            disk = (int)BinaryPrimitives.ReadUInt32LittleEndian(dn[4..]);
            if (partNo == 0) partNo = (int)BinaryPrimitives.ReadUInt32LittleEndian(dn[8..]);
        }

        if (length <= 0) throw new VolumeFormatException("ボリュームサイズを取得できません。");
        length -= length % sectorSize;
        return new VolumeGeometry(length, sectorSize, disk, partNo, offset, style, mbrType);
    }

    /// <summary>アクセス権 0 で開いて問い合わせる（管理者権限不要）。失敗したら null。</summary>
    public static VolumeGeometry? TryQueryGeometry(char letter)
    {
        try
        {
            using var h = RawHandleIo.Open(WindowsVolume.DevicePath(letter), 0, 0);
            return QueryGeometry(h);
        }
        catch { return null; }
    }

    /// <summary>
    /// メディアが書き込み禁止（SD カードのロックスイッチなど）か調べる（管理者権限不要）。
    /// true = 書き込み禁止、false = 書き込み可、null = 判定できない。
    /// </summary>
    public static bool? TryQueryWriteProtected(char letter)
    {
        try
        {
            using var h = RawHandleIo.Open(WindowsVolume.DevicePath(letter), 0, 0);
            return IsWriteProtected(h);
        }
        catch { return null; }
    }

    public static bool? IsWriteProtected(SafeFileHandle h)
    {
        if (Native.Ioctl(h, Native.IOCTL_DISK_IS_WRITABLE)) return false;
        return Marshal.GetLastWin32Error() == Native.ERROR_WRITE_PROTECT ? true : null;
    }

    public static StorageDeviceInfo? TryQueryDevice(int diskNumber)
    {
        if (diskNumber < 0) return null;
        try
        {
            using var h = RawHandleIo.Open($@"\\.\PhysicalDrive{diskNumber}", 0, 0);
            var query = new byte[12]; // STORAGE_PROPERTY_QUERY { StorageDeviceProperty=0, PropertyStandardQuery=0 }
            var buf = new byte[1024];
            if (!Native.IoctlInOut(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, query, buf, out uint ret) || ret < 36) return null;
            // STORAGE_DEVICE_DESCRIPTOR: Version(4) Size(4) DeviceType(1) Modifier(1) RemovableMedia(1) CommandQueueing(1)
            //   VendorIdOffset(4)@12 ProductIdOffset(4)@16 ProductRevisionOffset(4)@20 SerialNumberOffset(4)@24 BusType(4)@28
            bool removable = buf[10] != 0;
            string vendor = CString(buf, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(12)), ret);
            string product = CString(buf, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(16)), ret);
            var bus = (StorageBusType)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(28));
            return new StorageDeviceInfo(vendor, product, bus, removable);
        }
        catch { return null; }
    }

    private static string CString(byte[] buf, uint offset, uint limit)
    {
        if (offset == 0 || offset >= limit) return "";
        int end = (int)offset;
        while (end < limit && buf[end] != 0) end++;
        return Encoding.ASCII.GetString(buf, (int)offset, end - (int)offset).Trim();
    }
}
