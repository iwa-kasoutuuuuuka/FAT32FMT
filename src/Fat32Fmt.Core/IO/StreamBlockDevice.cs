namespace Fat32Fmt.Core.IO;

/// <summary>Stream(イメージファイルやメモリ)を背後に持つブロックデバイス。</summary>
public sealed class StreamBlockDevice : IBlockDevice
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;

    public StreamBlockDevice(Stream stream, int sectorSize = 512, long? length = null, bool ownsStream = true)
    {
        if (sectorSize < 512 || (sectorSize & (sectorSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorSize));
        }

        _stream = stream;
        _ownsStream = ownsStream;
        SectorSize = sectorSize;
        long len = length ?? stream.Length;
        Length = len - len % sectorSize;
    }

    public long Length { get; }

    public int SectorSize { get; }

    public bool CanWrite => _stream.CanWrite;

    /// <summary>既存のイメージファイルを開く。</summary>
    public static StreamBlockDevice OpenImage(string path, bool writable, int sectorSize = 512)
    {
        var fs = new FileStream(path, FileMode.Open, writable ? FileAccess.ReadWrite : FileAccess.Read,
            FileShare.Read, 1 << 20, FileOptions.RandomAccess);
        return new StreamBlockDevice(fs, sectorSize);
    }

    /// <summary>指定サイズのイメージファイルを作成する。</summary>
    public static StreamBlockDevice CreateImage(string path, long size, int sectorSize = 512)
    {
        size -= size % sectorSize;
        var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20,
            FileOptions.RandomAccess);
        fs.SetLength(size);
        return new StreamBlockDevice(fs, sectorSize);
    }

    public void Read(long offset, Span<byte> buffer)
    {
        BlockDeviceGuard.CheckRange(this, offset, buffer.Length);
        _stream.Position = offset;
        _stream.ReadExactly(buffer);
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        BlockDeviceGuard.CheckRange(this, offset, buffer.Length);
        _stream.Position = offset;
        _stream.Write(buffer);
    }

    public void Flush() => _stream.Flush();

    public void Dispose()
    {
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }
}
