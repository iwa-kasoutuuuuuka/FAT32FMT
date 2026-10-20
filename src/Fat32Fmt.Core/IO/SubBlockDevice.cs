namespace Fat32Fmt.Core.IO;

/// <summary>親デバイスの一部(パーティションなど)を独立したデバイスとして見せるビュー。</summary>
public sealed class SubBlockDevice : IBlockDevice
{
    private readonly IBlockDevice _parent;

    public SubBlockDevice(IBlockDevice parent, long offset, long length)
    {
        if (offset < 0 || length <= 0 || offset + length > parent.Length
            || offset % parent.SectorSize != 0 || length % parent.SectorSize != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "サブデバイスの範囲が不正です。");
        }

        _parent = parent;
        Offset = offset;
        Length = length;
    }

    public long Offset { get; }

    public long Length { get; }

    public int SectorSize => _parent.SectorSize;

    public bool CanWrite => _parent.CanWrite;

    public void Read(long offset, Span<byte> buffer)
    {
        BlockDeviceGuard.CheckRange(this, offset, buffer.Length);
        _parent.Read(Offset + offset, buffer);
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        BlockDeviceGuard.CheckRange(this, offset, buffer.Length);
        _parent.Write(Offset + offset, buffer);
    }

    public void Flush() => _parent.Flush();

    /// <summary>親デバイスの所有権は持たないので何もしない。</summary>
    public void Dispose()
    {
    }
}
