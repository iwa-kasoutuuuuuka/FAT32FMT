using System.Globalization;

namespace Fat32Fmt.Core.Diagnostics;

/// <summary>検査結果の人間向け説明（CLI / GUI 共用）。</summary>
public static class DiagnosticReportText
{
    private const int MaxListedRanges = 10;

    public static string Describe(CapacityTestReport r)
    {
        var ci = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            $"判定               : {VerdictName(r.Verdict)}",
            $"表示容量           : {SizeFormat.Both(r.ReportedBytes)}",
            $"検査方式           : {(r.Mode == CapacityTestMode.Full ? "全域（全体を書いてから読み戻し）" : "抜き取り")}  検査量 {SizeFormat.Binary(r.TestedBytes)}",
            $"正常に読み戻せた量 : {SizeFormat.Binary(r.OkBytes)}",
            $"異常               : {SizeFormat.Binary(r.BadBytes)}（{r.BadRangeCount.ToString(ci)} か所）",
        };
        if (r.Verdict == CapacityVerdict.Fake) lines.Add($"推定実容量         : {SizeFormat.Both(r.EstimatedRealBytes)}");
        if (r.AliasDistance is long a) lines.Add($"折り返し幅         : {SizeFormat.Binary(a)}");
        if (r.WriteErrors + r.ReadErrors > 0) lines.Add($"I/O エラー         : 書き込み {r.WriteErrors} 回、読み取り {r.ReadErrors} 回");
        lines.Add($"実測速度           : 書き込み {SizeFormat.Speed(r.WriteBytesPerSecond)}、読み取り {SizeFormat.Speed(r.ReadBytesPerSecond)}");
        lines.Add($"所要時間           : {Duration(r.Elapsed)}");
        AddRanges(lines, r.BadRanges, r.BadRangeCount);
        lines.Add("");
        lines.Add(r.Summary);
        return string.Join(Environment.NewLine, lines);
    }

    public static string Describe(SurfaceScanReport r)
    {
        var lines = new List<string>
        {
            $"方式               : {(r.Mode == SurfaceScanMode.ReadOnly ? "読み取りのみ（非破壊）" : "書き込み＋読み戻し（破壊）")}",
            $"検査範囲           : {SizeFormat.Binary(r.ScannedBytes)} / {SizeFormat.Both(r.DeviceBytes)}（セクタ {r.SectorSize} B）",
            $"不良セクタ         : {r.BadSectors:N0} 個（{r.BadRangeCount} か所）",
        };
        if (r.ReadErrors + r.WriteErrors > 0) lines.Add($"I/O エラー         : 読み取り {r.ReadErrors} 区間、書き込み {r.WriteErrors} 区間");
        if (r.MismatchSectors > 0) lines.Add($"内容不一致         : {r.MismatchSectors:N0} セクタ");
        if (r.SlowChunks > 0) lines.Add($"遅い区間           : {r.SlowChunks} か所");
        if (r.SlowErrors > 0) lines.Add($"応答の遅いエラー   : {r.SlowErrors} 回（エラー処理に計 {r.ErrorHandlingTime.TotalSeconds:0} 秒）");
        lines.Add($"平均速度           : {SizeFormat.Speed(r.BytesPerSecond)}");
        lines.Add($"所要時間           : {Duration(r.Elapsed)}");
        AddRanges(lines, r.BadRanges, r.BadRangeCount);
        lines.Add("");
        lines.Add(r.Summary);
        return string.Join(Environment.NewLine, lines);
    }

    public static string Describe(BenchmarkReport r)
    {
        var lines = new List<string>
        {
            $"対象               : {SizeFormat.Both(r.DeviceBytes)}（セクタ {r.SectorSize} B）",
            Line(r.SequentialRead, iops: false),
            Line(r.RandomRead4K, iops: true),
        };
        if (r.SequentialWrite != null) lines.Add(Line(r.SequentialWrite, iops: false));
        if (r.RandomWrite4K != null) lines.Add(Line(r.RandomWrite4K, iops: true));
        lines.Add("");
        lines.Add("スピードクラスの目安（書き込みの最低保証速度との比較）:");
        foreach (var c in r.SpeedClasses)
            lines.Add($"  {c.Name,-9} {(c.Meets switch { true => "○ 満たす", false => "× 満たさない", null => "－ 未測定" }),-8}  {c.Requirement}");
        lines.Add("※ 規格値はカード内部の条件で測る最低保証値です。PC での実測はカードリーダーの性能にも左右されるため目安です。");
        lines.Add("");
        lines.Add(r.Summary);
        return string.Join(Environment.NewLine, lines);
    }

    public static string VerdictName(CapacityVerdict v) => v switch
    {
        CapacityVerdict.Genuine => "正常（容量偽装なし）",
        CapacityVerdict.Fake => "容量偽装の疑い",
        CapacityVerdict.Damaged => "不良あり",
        _ => "判定不能（中断）",
    };

    private static string Line(BenchmarkResult b, bool iops) =>
        $"{b.Name}: {SizeFormat.Speed(b.BytesPerSecond)}" + (iops ? $"  {b.Iops:0} IOPS" : "") +
        $"  ({SizeFormat.Binary(b.Bytes)} / {b.Elapsed.TotalSeconds:0.0} 秒)";

    private static void AddRanges(List<string> lines, IReadOnlyList<ByteRange> ranges, int total)
    {
        if (ranges.Count == 0) return;
        lines.Add("異常のあった範囲（先頭からの位置）:");
        foreach (var r in ranges.Take(MaxListedRanges))
            lines.Add($"  {SizeFormat.Binary(r.Offset)} 〜 {SizeFormat.Binary(r.End)}  ({SizeFormat.Binary(r.Length)}, オフセット {r.Offset:N0})");
        if (total > MaxListedRanges) lines.Add($"  …ほか {total - MaxListedRanges} か所");
    }

    private static string Duration(TimeSpan t) => $"{(int)t.TotalHours}:{t:mm\\:ss}";
}
