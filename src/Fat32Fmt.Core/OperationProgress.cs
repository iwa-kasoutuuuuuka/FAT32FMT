using System.Diagnostics;

namespace Fat32Fmt.Core;

/// <summary>長時間処理の進捗通知。</summary>
public sealed record OperationProgress(string Phase, long Done, long Total, double BytesPerSecond, TimeSpan? Remaining)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);
}

/// <summary>進捗と転送速度を計算して一定間隔で通知するヘルパー。</summary>
public sealed class ProgressTracker
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    private readonly IProgress<OperationProgress>? _progress;
    private readonly string _phase;
    private readonly long _total;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private TimeSpan _lastReport = -Interval; // NOTE: TimeSpan.MinValue would overflow in "_sw.Elapsed - _lastReport"; -Interval makes the first Report() send immediately
    private long _done;

    public ProgressTracker(IProgress<OperationProgress>? progress, string phase, long total)
    {
        _progress = progress;
        _phase = phase;
        _total = total;
        _progress?.Report(new OperationProgress(phase, 0, total, 0, null));
    }

    public TimeSpan Elapsed => _sw.Elapsed;

    public double BytesPerSecond => _sw.Elapsed.TotalSeconds > 0 ? _done / _sw.Elapsed.TotalSeconds : 0;

    public void Report(long done)
    {
        _done = done;
        if (_progress is null || _sw.Elapsed - _lastReport < Interval)
        {
            return;
        }

        _lastReport = _sw.Elapsed;
        Send();
    }

    public void Complete()
    {
        _done = Math.Max(_done, _total);
        Send();
    }

    private void Send()
    {
        double bps = BytesPerSecond;
        TimeSpan? remaining = bps > 0 && _total > _done ? TimeSpan.FromSeconds((_total - _done) / bps) : null;
        _progress?.Report(new OperationProgress(_phase, _done, _total, bps, remaining));
    }
}
