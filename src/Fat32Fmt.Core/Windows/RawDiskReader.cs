using System.Runtime.Versioning;
using Fat32Fmt.Core.IO;

namespace Fat32Fmt.Core.Windows;

/// <summary>
/// 物理ディスク (\\.\PhysicalDriveN) 全体を読み取り専用で開く（検査・速度測定用）。
/// <see cref="PhysicalDisk"/> と違ってボリュームをロックしないので、カードを使用中のままでも読める。管理者権限が必要。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RawDiskReader : IBlockDevice
{
    private readonly RawHandleIo _io;

    private RawDiskReader(RawHandleIo io, long length, string path)
    {
        _io = io; Length = length; DevicePath = path;
    }

    public string DevicePath { get; }
    public long Length { get; }
    public int SectorSize => _io.SectorSize;
    public bool CanWrite => false;

    public static RawDiskReader Open(int diskNumber)
    {
        string path = PhysicalDisk.DevicePath(diskNumber);
        var h = RawHandleIo.Open(path, Native.GENERIC_READ, Native.FILE_FLAG_NO_BUFFERING);
        try
        {
            var (length, sectorSize) = PhysicalDisk.QuerySize(h);
            return new RawDiskReader(new RawHandleIo(h, sectorSize), length, path);
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

    public void Write(long offset, ReadOnlySpan<byte> buffer) => throw new InvalidOperationException("読み取り専用で開いたディスクです。");

    public void Flush() { }

    public void Dispose() => _io.Dispose();
}
