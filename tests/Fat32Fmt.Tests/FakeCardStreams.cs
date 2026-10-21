namespace Fat32Fmt.Tests;

/// <summary>
/// 容量偽装カードを模擬するストリーム。表示上は <c>reportedLength</c> あるが、実際の記憶領域は <c>realLength</c> しかない。
/// Wrap: アドレスが実容量で折り返す（上位アドレスへの書き込みが下位を上書きする。よくある偽装の手口）。
/// Discard: 実容量より後ろへの書き込みは捨てられ、読むと 0 が返る。
/// </summary>
public sealed class FakeCardStream : Stream
{
    public enum Behavior { Wrap, Discard }

    private readonly byte[] _storage;
    private readonly long _reported;
    private readonly Behavior _behavior;
    private long _position;

    public FakeCardStream(long reportedLength, int realLength, Behavior behavior)
    {
        _storage = new byte[realLength];
        _reported = reportedLength;
        _behavior = behavior;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _reported;
    public override long Position { get => _position; set => _position = value; }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = (int)Math.Max(0, Math.Min(buffer.Length, _reported - _position));
        for (int i = 0; i < n; i++)
        {
            long p = _position + i;
            buffer[i] = _behavior == Behavior.Wrap ? _storage[p % _storage.Length]
                      : p < _storage.Length ? _storage[p] : (byte)0;
        }
        _position += n;
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_position + buffer.Length > _reported) throw new IOException("ストリーム末尾を超えた書き込みです。");
        for (int i = 0; i < buffer.Length; i++)
        {
            long p = _position + i;
            if (_behavior == Behavior.Wrap) _storage[p % _storage.Length] = buffer[i];
            else if (p < _storage.Length) _storage[p] = buffer[i];
        }
        _position += buffer.Length;
    }

    public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => _reported + offset,
    };

    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>指定範囲にかかる読み取り（と、指定すれば書き込み）で IOException を投げるストリーム（不良セクタの模擬）。</summary>
public sealed class FaultyStream : Stream
{
    private readonly Stream _inner;
    private readonly List<(long Offset, long Length)> _bad;
    private readonly bool _failWrites;

    public FaultyStream(Stream inner, IEnumerable<(long Offset, long Length)> bad, bool failWrites = false)
    {
        _inner = inner;
        _bad = bad.ToList();
        _failWrites = failWrites;
    }

    public int WriteCount { get; private set; }

    public int ReadCount { get; private set; }

    /// <summary>失敗する読み取りが例外を投げるまでの遅延（応答の遅い不良区間の模擬）。</summary>
    public TimeSpan FailDelay { get; init; }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ReadCount++;
        if (Hits(Position, buffer.Length))
        {
            if (FailDelay > TimeSpan.Zero) Thread.Sleep(FailDelay);
            throw new IOException($"模擬の読み取りエラー (offset={Position})");
        }
        return _inner.Read(buffer);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        WriteCount++;
        if (_failWrites && Hits(Position, buffer.Length)) throw new IOException($"模擬の書き込みエラー (offset={Position})");
        _inner.Write(buffer);
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => _inner.SetLength(value);

    private bool Hits(long offset, int length) => _bad.Any(b => offset < b.Offset + b.Length && b.Offset < offset + length);
}
