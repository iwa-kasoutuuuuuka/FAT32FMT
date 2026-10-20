namespace Fat32Fmt.Core.Diagnostics;

/// <summary>デバイス上のバイト範囲。</summary>
public sealed record ByteRange(long Offset, long Length)
{
    public long End => Offset + Length;
}

/// <summary>
/// 異常のあった範囲を、隣接するものをつなげながら集める。報告する件数には上限を設け、総数と総バイト数は別に数える。
/// 範囲は昇順に追加されることを前提にする（昇順でなければ別の範囲として記録する）。
/// </summary>
public sealed class RangeCollector
{
    private readonly List<ByteRange> _ranges = new();
    private readonly int _maxRanges;
    private long _lastOffset = -1, _lastEnd = -1;

    public RangeCollector(int maxRanges = 100) => _maxRanges = Math.Max(1, maxRanges);

    /// <summary>報告用の範囲（先頭から最大件数まで）。</summary>
    public IReadOnlyList<ByteRange> Ranges => Snapshot();

    /// <summary>つなげた後の範囲の総数（上限を超えた分も含む）。</summary>
    public int Count { get; private set; }

    public long TotalBytes { get; private set; }

    public long? FirstOffset { get; private set; }

    public long? LastEnd => _lastEnd < 0 ? null : _lastEnd;

    public void Add(long offset, long length)
    {
        if (length <= 0) return;
        FirstOffset ??= offset;
        TotalBytes += length;
        if (offset == _lastEnd)
        {
            _lastEnd += length;
            return;
        }
        Commit();
        _lastOffset = offset;
        _lastEnd = offset + length;
        Count++;
    }

    private void Commit()
    {
        if (_lastEnd < 0) return;
        if (_ranges.Count < _maxRanges) _ranges.Add(new ByteRange(_lastOffset, _lastEnd - _lastOffset));
    }

    private List<ByteRange> Snapshot()
    {
        var list = new List<ByteRange>(_ranges);
        if (_lastEnd >= 0 && list.Count < _maxRanges) // つなげている途中の最後の範囲はまだ _ranges に入っていない
            list.Add(new ByteRange(_lastOffset, _lastEnd - _lastOffset));
        return list;
    }
}
