namespace Fat32Fmt.Tests;

/// <summary>
/// 書き込まれたページだけを保持するスパースなメモリストリーム。
/// 64 GB などの大きな仮想デバイスをメモリ上でフォーマットするテストに使う。未書き込み領域は 0 として読める。
/// </summary>
public sealed class SparseMemoryStream : Stream
{
    private const int PageSize = 64 * 1024;
    private readonly Dictionary<long, byte[]> _pages = new();
    private long _length;
    private long _position;

    public SparseMemoryStream(long length) => _length = length;

    public int PageCount => _pages.Count;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position { get => _position; set => _position = value; }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= _length) return 0;
        int n = (int)Math.Min(buffer.Length, _length - _position);
        var dst = buffer[..n];
        long pos = _position;
        while (!dst.IsEmpty)
        {
            long page = pos / PageSize;
            int inPage = (int)(pos % PageSize);
            int chunk = Math.Min(dst.Length, PageSize - inPage);
            if (_pages.TryGetValue(page, out var data)) data.AsSpan(inPage, chunk).CopyTo(dst);
            else dst[..chunk].Clear();
            dst = dst[chunk..];
            pos += chunk;
        }
        _position += n;
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_position + buffer.Length > _length) throw new IOException("ストリーム末尾を超えた書き込みです。");
        long pos = _position;
        while (!buffer.IsEmpty)
        {
            long page = pos / PageSize;
            int inPage = (int)(pos % PageSize);
            int chunk = Math.Min(buffer.Length, PageSize - inPage);
            var src = buffer[..chunk];
            if (!_pages.TryGetValue(page, out var data))
            {
                if (src.IndexOfAnyExcept((byte)0) < 0) { buffer = buffer[chunk..]; pos += chunk; continue; }
                data = new byte[PageSize];
                _pages[page] = data;
            }
            src.CopyTo(data.AsSpan(inPage));
            if (data.AsSpan().IndexOfAnyExcept((byte)0) < 0) _pages.Remove(page);
            buffer = buffer[chunk..];
            pos += chunk;
        }
        _position += (int)(pos - _position);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    public override void SetLength(long value) => _length = value;
}
