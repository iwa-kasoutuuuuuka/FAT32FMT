using System.Runtime.Versioning;
using System.Text.Json;
using Fat32Fmt.Core.Diagnostics;
using Fat32Fmt.Core.IO;

namespace Fat32Fmt.Core.Windows;

public enum DiagnosticKind
{
    /// <summary>容量偽装チェック（抜き取り）。破壊。</summary>
    CapacityQuick,

    /// <summary>容量偽装チェック（全域）。破壊。</summary>
    CapacityFull,

    /// <summary>不良セクタの読み取りスキャン。非破壊。</summary>
    ScanRead,

    /// <summary>不良セクタの書き込みスキャン。破壊。</summary>
    ScanWrite,

    /// <summary>速度測定（読み取りのみ）。非破壊。</summary>
    Bench,

    /// <summary>速度測定（書き込みも）。破壊。</summary>
    BenchWrite,
}

public sealed class DiagnosticRequest
{
    public required char DriveLetter { get; init; }
    public required DiagnosticKind Kind { get; init; }
    /// <summary>固定ディスク扱いのドライブも許可する（危険）。</summary>
    public bool AllowFixedDisk { get; init; }

    /// <summary>ドライブのデータを破壊する検査か。</summary>
    public bool IsDestructive => IsDestructiveKind(Kind);

    /// <summary>MCP などで破壊的な検査を実行するときに要求する確認文字列。</summary>
    public string ConfirmPhrase => $"ERASE {char.ToUpperInvariant(DriveLetter)}";

    public static bool IsDestructiveKind(DiagnosticKind k) => k is DiagnosticKind.CapacityQuick or DiagnosticKind.CapacityFull
                                                                   or DiagnosticKind.ScanWrite or DiagnosticKind.BenchWrite;

    public static string KindName(DiagnosticKind k) => k switch
    {
        DiagnosticKind.CapacityQuick => "容量偽装チェック（抜き取り）",
        DiagnosticKind.CapacityFull => "容量偽装チェック（全域）",
        DiagnosticKind.ScanRead => "不良セクタスキャン（読み取りのみ）",
        DiagnosticKind.ScanWrite => "不良セクタスキャン（書き込み）",
        DiagnosticKind.Bench => "速度測定（読み取り）",
        DiagnosticKind.BenchWrite => "速度測定（読み書き）",
        _ => k.ToString(),
    };
}

/// <summary>検査の結果（JSON で CLI / MCP / 昇格プロセス間を受け渡す）。</summary>
public sealed class DiagnosticOutcome
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public DiagnosticKind Kind { get; set; }
    /// <summary>実際に検査したデバイス（"Disk 2 全体 (\\.\PhysicalDrive2)" など）。</summary>
    public string? Target { get; set; }
    public CapacityTestReport? Capacity { get; set; }
    public SurfaceScanReport? Scan { get; set; }
    public BenchmarkReport? Benchmark { get; set; }
    public List<string> Log { get; set; } = new();

    /// <summary>検査は完了し、カードに問題が見つかった（偽装・不良セクタ）。</summary>
    public bool ProblemFound =>
        Success && (Capacity is { Verdict: CapacityVerdict.Fake or CapacityVerdict.Damaged } || Scan is { BadSectors: > 0 });

    public static DiagnosticOutcome Fail(DiagnosticKind kind, string error, IEnumerable<string>? log = null) =>
        new() { Success = false, Kind = kind, Error = error, Log = log?.ToList() ?? new() };

    /// <summary>人間向けの結果説明。</summary>
    public string Describe()
    {
        if (!Success) return $"エラー: {Error}";
        string head = $"{DiagnosticRequest.KindName(Kind)}  対象: {Target}";
        string body = Capacity != null ? DiagnosticReportText.Describe(Capacity)
                    : Scan != null ? DiagnosticReportText.Describe(Scan)
                    : Benchmark != null ? DiagnosticReportText.Describe(Benchmark)
                    : "";
        return head + Environment.NewLine + body;
    }
}

/// <summary>Windows 上でドライブ文字を指定して検査を実行するサービス層。GUI / CLI / MCP が共用する。</summary>
[SupportedOSPlatform("windows")]
public static class DiagnosticsService
{
    public const string DestroyedNotice = "検査パターンで上書きしたため、ファイルシステムは失われています。続けてフォーマットしてください。";

    /// <summary>対象ドライブを検証し、その情報を返す（管理者権限不要）。非破壊の検査では書き込み禁止のカードも許可する。</summary>
    public static DriveEntry ValidateTarget(DiagnosticRequest req) =>
        VolumeFormatService.ValidateTarget(new FormatRequest { DriveLetter = req.DriveLetter, AllowFixedDisk = req.AllowFixedDisk },
                                           allowWriteProtected: !req.IsDestructive);

    /// <summary>
    /// 検査を実行する。管理者権限が必要。
    /// 非破壊の検査はカード全体（物理ディスク）をロックせずに読む。破壊的な検査はボリューム（パーティション）をロックして書く
    /// （ドライブ文字が残るので、そのままフォーマットできる）。
    /// </summary>
    public static DiagnosticOutcome Run(DiagnosticRequest req, IProgress<OperationProgress>? progress = null,
                                        Action<string>? log = null, CancellationToken ct = default)
    {
        if (!VolumeFormatService.IsElevated()) throw new VolumeFormatException("検査には管理者権限が必要です。管理者として実行してください。");
        var entry = ValidateTarget(req);
        var outcome = new DiagnosticOutcome { Kind = req.Kind };
        void Log(string s)
        {
            outcome.Log.Add(s);
            log?.Invoke(s);
        }

        Log($"対象: {entry.Display}");
        IBlockDevice dev;
        long origin = 0;
        if (req.IsDestructive)
        {
            Log($"ボリューム {entry.Letter}: をロックしてマウント解除します...");
            var vol = WindowsVolume.OpenForFormat(entry.Letter, Log);
            dev = vol;
            origin = vol.PartitionOffset;
            outcome.Target = $"{entry.Letter}: のパーティション（ディスク先頭から {SizeFormat.Binary(origin)}、{SizeFormat.Both(vol.Length)}）";
        }
        else if (entry.DiskNumber >= 0)
        {
            var disk = RawDiskReader.Open(entry.DiskNumber);
            dev = disk;
            outcome.Target = $"Disk {entry.DiskNumber} 全体（{disk.DevicePath}、{SizeFormat.Both(disk.Length)}）";
        }
        else
        {
            var vol = ReadOnlyVolume.Open(entry.Letter);
            dev = vol;
            outcome.Target = $"{entry.Letter}: のパーティション（{SizeFormat.Both(vol.Length)}）";
        }
        Log($"検査対象: {outcome.Target}");

        try
        {
            switch (req.Kind)
            {
                case DiagnosticKind.CapacityQuick:
                case DiagnosticKind.CapacityFull:
                    outcome.Capacity = CapacityTester.Run(dev, new CapacityTestOptions
                    {
                        Mode = req.Kind == DiagnosticKind.CapacityFull ? CapacityTestMode.Full : CapacityTestMode.Quick,
                        OriginOffset = origin,
                    }, progress, ct);
                    break;
                case DiagnosticKind.ScanRead:
                case DiagnosticKind.ScanWrite:
                    outcome.Scan = SurfaceScanner.Run(dev, new SurfaceScanOptions
                    {
                        Mode = req.Kind == DiagnosticKind.ScanWrite ? SurfaceScanMode.WriteVerify : SurfaceScanMode.ReadOnly,
                    }, progress, ct);
                    break;
                default:
                    outcome.Benchmark = SpeedBenchmark.Run(dev, new BenchmarkOptions { IncludeWrite = req.Kind == DiagnosticKind.BenchWrite }, progress, ct);
                    break;
            }
        }
        finally
        {
            dev.Dispose();
        }

        if (req.IsDestructive) Log(DestroyedNotice);
        outcome.Success = true;
        return outcome;
    }

    /// <summary>昇格した CLI に渡す引数（--result-file などは <see cref="ElevatedCli"/> が付ける）。</summary>
    public static List<string> BuildCliArgs(DiagnosticRequest req)
    {
        string drive = $"{char.ToUpperInvariant(req.DriveLetter)}:";
        var a = req.Kind switch
        {
            DiagnosticKind.CapacityQuick => new List<string> { "test", drive, "--quick" },
            DiagnosticKind.CapacityFull => new List<string> { "test", drive, "--full" },
            DiagnosticKind.ScanRead => new List<string> { "test", drive, "--scan" },
            DiagnosticKind.ScanWrite => new List<string> { "test", drive, "--scan", "--write" },
            DiagnosticKind.Bench => new List<string> { "bench", drive },
            _ => new List<string> { "bench", drive, "--write" },
        };
        a.Add("--yes");
        if (req.AllowFixedDisk) a.Add("--allow-fixed");
        return a;
    }

    /// <summary>CLI を UAC 昇格して検査を実行し、結果を受け取る。進捗とログは子プロセスからファイル経由で届く。</summary>
    public static DiagnosticOutcome RunViaElevatedCli(string cliExePath, DiagnosticRequest req, Action<OperationProgress>? onProgress = null,
                                                      Action<string>? onLog = null, CancellationToken ct = default)
    {
        var run = ElevatedCli.Run(cliExePath, BuildCliArgs(req), onProgress, onLog, ct);
        if (run.Json == null) return DiagnosticOutcome.Fail(req.Kind, run.Error ?? "不明なエラー");
        try
        {
            return JsonSerializer.Deserialize<DiagnosticOutcome>(run.Json, JsonDefaults.Options)
                   ?? DiagnosticOutcome.Fail(req.Kind, "結果ファイルの解析に失敗しました。");
        }
        catch (JsonException ex)
        {
            return DiagnosticOutcome.Fail(req.Kind, $"結果ファイルの解析に失敗しました: {ex.Message}");
        }
    }
}
