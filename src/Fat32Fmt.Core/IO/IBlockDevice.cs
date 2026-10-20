namespace Fat32Fmt.Core.IO;

/// <summary>
/// セクタ単位でアクセスするブロックデバイス。物理ディスク・ボリューム・イメージファイルを同じ形で扱う。
/// オフセットと長さはセクタサイズの倍数でなければならない。
/// </summary>
public interface IBlockDevice : IDisposable
{
    /// <summary>デバイスの総バイト数(セクタサイズの倍数)。</summary>
    long Length { get; }

    /// <summary>論理セクタサイズ(バイト)。</summary>
    int SectorSize { get; }

    bool CanWrite { get; }

    void Read(long offset, Span<byte> buffer);

    void Write(long offset, ReadOnlySpan<byte> buffer);

    void Flush();
}

/// <summary>デバイス I/O の失敗。失敗した範囲を保持する。</summary>
public sealed class DeviceIOException : IOException
{
    public DeviceIOException(string message, long offset, int length, int win32Error = 0)
        : base(message)
    {
        Offset = offset;
        Length = length;
        Win32Error = win32Error;
    }

    public long Offset { get; }

    public int Length { get; }

    public int Win32Error { get; }
}

internal static class BlockDeviceGuard
{
    public static void CheckRange(IBlockDevice device, long offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > device.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset),
                $"範囲外のアクセスです (offset={offset}, length={length}, device={device.Length})。");
        }

        if (offset % device.SectorSize != 0 || length % device.SectorSize != 0)
        {
            throw new ArgumentException(
                $"オフセットと長さはセクタサイズ ({device.SectorSize}) の倍数である必要があります (offset={offset}, length={length})。");
        }
    }
}
