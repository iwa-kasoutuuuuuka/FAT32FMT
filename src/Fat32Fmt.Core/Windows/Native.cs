using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Fat32Fmt.Core.IO;
using Microsoft.Win32.SafeHandles;

namespace Fat32Fmt.Core.Windows;

[SupportedOSPlatform("windows")]
internal static unsafe class Native
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1;
    public const uint FILE_SHARE_WRITE = 2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

    public const uint FSCTL_LOCK_VOLUME = 0x00090018;
    public const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
    public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
    public const uint FSCTL_ALLOW_EXTENDED_DASD_IO = 0x00090083;
    public const uint IOCTL_DISK_GET_PARTITION_INFO_EX = 0x00070048;
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    public const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;
    public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const uint IOCTL_DISK_IS_WRITABLE = 0x00070024;
    public const uint FSCTL_SET_SPARSE = 0x000900C4;
    public const int ERROR_WRITE_PROTECT = 19;

    public const uint DRIVE_UNKNOWN = 0, DRIVE_NO_ROOT_DIR = 1, DRIVE_REMOVABLE = 2, DRIVE_FIXED = 3,
                      DRIVE_REMOTE = 4, DRIVE_CDROM = 5, DRIVE_RAMDISK = 6;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, void* lpInBuffer,
        uint nInBufferSize, void* lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool SetFilePointerEx(SafeFileHandle hFile, long liDistanceToMove, out long lpNewFilePointer, uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool WriteFile(SafeFileHandle hFile, void* lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool ReadFile(SafeFileHandle hFile, void* lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool FlushFileBuffers(SafeFileHandle hFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint GetDriveTypeW(string lpRootPathName);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern bool GetVolumeInformationW(string lpRootPathName, StringBuilder lpVolumeNameBuffer,
        uint nVolumeNameSize, out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags, StringBuilder lpFileSystemNameBuffer, uint nFileSystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern bool GetDiskFreeSpaceExW(string lpDirectoryName, out ulong lpFreeBytesAvailable,
        out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    public static Win32Exception LastError(string what)
    {
        int err = Marshal.GetLastWin32Error();
        string msg = new Win32Exception(err).Message;
        return new Win32Exception(err, $"{what}: {msg} (Win32 エラー {err})");
    }

    public static bool Ioctl(SafeFileHandle h, uint code) =>
        DeviceIoControl(h, code, null, 0, null, 0, out _, IntPtr.Zero);

    public static bool IoctlOut(SafeFileHandle h, uint code, Span<byte> output, out uint returned)
    {
        fixed (byte* p = output)
            return DeviceIoControl(h, code, null, 0, p, (uint)output.Length, out returned, IntPtr.Zero);
    }

    public static bool IoctlInOut(SafeFileHandle h, uint code, ReadOnlySpan<byte> input, Span<byte> output, out uint returned)
    {
        fixed (byte* pi = input)
        fixed (byte* po = output)
            return DeviceIoControl(h, code, pi, (uint)input.Length, po, (uint)output.Length, out returned, IntPtr.Zero);
    }
}

/// <summary>FILE_FLAG_NO_BUFFERING で開いたハンドルに対し、整列バッファ経由でセクタ単位 I/O を行う。</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class RawHandleIo : IDisposable
{
    private readonly AlignedBuffer _buf = new(BlockDeviceExtensions.ChunkSize);

    public SafeFileHandle Handle { get; }
    public int SectorSize { get; }

    public RawHandleIo(SafeFileHandle handle, int sectorSize)
    {
        Handle = handle;
        SectorSize = sectorSize;
    }

    public static SafeFileHandle Open(string path, uint access, uint flags)
    {
        var h = Native.CreateFileW(path, access, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                                   Native.OPEN_EXISTING, flags, IntPtr.Zero);
        if (h.IsInvalid) throw Native.LastError($"{path} を開けません");
        return h;
    }

    public void Read(long offset, Span<byte> buffer)
    {
        CheckAligned(offset, buffer.Length);
        int done = 0;
        while (done < buffer.Length)
        {
            int n = Math.Min(_buf.Size, buffer.Length - done);
            Seek(offset + done);
            if (!Native.ReadFile(Handle, _buf.Pointer, (uint)n, out uint got, IntPtr.Zero))
                throw IoError("読み込みに失敗しました", offset + done, n);
            if (got != n) throw new DeviceIOException($"読み込みサイズ不一致 ({got}/{n} バイト)", offset + done, n);
            _buf.Slice(n).CopyTo(buffer.Slice(done, n));
            done += n;
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        CheckAligned(offset, data.Length);
        int done = 0;
        while (done < data.Length)
        {
            int n = Math.Min(_buf.Size, data.Length - done);
            data.Slice(done, n).CopyTo(_buf.Slice(n));
            Seek(offset + done);
            if (!Native.WriteFile(Handle, _buf.Pointer, (uint)n, out uint written, IntPtr.Zero))
                throw IoError("書き込みに失敗しました", offset + done, n);
            if (written != n) throw new DeviceIOException($"書き込みサイズ不一致 ({written}/{n} バイト)", offset + done, n);
            done += n;
        }
    }

    /// <summary>生デバイスでは失敗することがあるため戻り値は無視する。</summary>
    public void Flush() => Native.FlushFileBuffers(Handle);

    private static DeviceIOException IoError(string what, long offset, int length)
    {
        int err = Marshal.GetLastWin32Error();
        return new DeviceIOException($"{what} (offset={offset}, length={length}): {new Win32Exception(err).Message}", offset, length, err);
    }

    private void Seek(long offset)
    {
        if (!Native.SetFilePointerEx(Handle, offset, out _, 0))
            throw Native.LastError($"オフセット {offset} へのシークに失敗");
    }

    private void CheckAligned(long offset, int length)
    {
        if (offset < 0 || offset % SectorSize != 0 || length % SectorSize != 0)
            throw new ArgumentException($"セクタ境界に整列していない I/O です (offset={offset}, length={length}, sector={SectorSize})");
    }

    public void Dispose()
    {
        _buf.Dispose();
        Handle.Dispose();
    }
}
