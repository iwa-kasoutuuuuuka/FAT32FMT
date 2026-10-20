using System.Text;
using System.Text.Json;
using Fat32Fmt.Core;
using Fat32Fmt.Core.Windows;

namespace Fat32Fmt.Cli;

/// <summary>
/// 検査系コマンド: test（容量偽装チェック・不良セクタスキャン）/ bench（速度測定）。
/// 生 I/O には管理者権限が必要なので、権限が無ければ UAC 昇格した自分自身に引数を渡して実行させる。
/// </summary>
internal static class DiagnosticCommands
{
    /// <summary>検査を完了し、カードに問題（容量偽装・不良セクタ）が見つかった場合の終了コード。</summary>
    public const int ExitProblemFound = 4;

    public static readonly string[] Commands = ["test", "bench"];

    public static int Run(string[] args)
    {
        var o = DiagnosticOptions.Parse(args);
        var req = o.ToRequest();
        var entry = DiagnosticsService.ValidateTarget(req); // 事前検証（管理者権限不要）

        if (req.IsDestructive && !o.Yes)
        {
            if (o.Json) throw new UsageException("--json を使う場合は --yes も指定してください（対話確認ができません）。");
            Console.WriteLine(entry.DescribeDetails());
            Console.WriteLine();
            Console.WriteLine($"{DiagnosticRequest.KindName(req.Kind)} はドライブ全体に検査パターンを書き込みます。");
            Console.WriteLine($"警告: ドライブ {entry.Letter}: 上のデータはすべて消去されます（検査後はフォーマットが必要です）。");
            Console.Write($"続行するにはドライブ文字 '{entry.Letter}' を入力してください: ");
            var input = Console.ReadLine()?.Trim().TrimEnd(':');
            if (!string.Equals(input, entry.Letter.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("中止しました。");
                return ElevatedFormat.ExitCancelled;
            }
        }

        DiagnosticOutcome outcome;
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            // 強制終了せず、ここまでの結果を出して終わる
            e.Cancel = true;
            cts.Cancel();
            Console.Error.WriteLine();
            Console.Error.WriteLine("中止しています...");
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            if (!VolumeFormatService.IsElevated())
            {
                string exe = Environment.ProcessPath ?? throw new VolumeFormatException("実行ファイルのパスを取得できません。");
                if (!o.Json) Console.Error.WriteLine("管理者権限が必要です。UAC の確認ダイアログで「はい」を選択してください...");
                var progress = o.Json ? null : Program.ConsoleProgress();
                outcome = DiagnosticsService.RunViaElevatedCli(exe, req, progress == null ? null : progress.Report,
                    o.Json ? null : Console.Error.WriteLine, cts.Token);
            }
            else
            {
                using var cancelFile = ElevatedCli.WatchCancelFile(o.CancelFile);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancelFile.Token);
                var writer = ElevatedCli.CreateProgressWriter(o.ProgressFile);
                IProgress<OperationProgress>? progress = writer ?? (o.Json ? null : Program.ConsoleProgress());
                Action<string> log = writer != null ? writer.Log : o.Json ? _ => { } : Console.Error.WriteLine;
                outcome = Execute(req, progress, log, linked.Token);
                if (o.ResultFile != null)
                {
                    try { File.WriteAllText(o.ResultFile, JsonSerializer.Serialize(outcome, JsonDefaults.Options), new UTF8Encoding(false)); }
                    catch (Exception ex) { Console.Error.WriteLine($"結果ファイルの書き込みに失敗: {ex.Message}"); }
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        if (o.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(outcome, JsonDefaults.Options));
        }
        else if (outcome.Success)
        {
            Console.WriteLine();
            Console.WriteLine(outcome.Describe());
            if (req.IsDestructive) Console.WriteLine(DiagnosticsService.DestroyedNotice + $"（例: fat32fmt format {entry.Letter}:）");
        }
        else
        {
            foreach (var l in outcome.Log) Console.Error.WriteLine(l);
            Console.Error.WriteLine($"エラー: {outcome.Error}");
        }

        bool incomplete = outcome.Capacity is { Completed: false } || outcome.Scan is { Completed: false };
        return outcome.Success ? (outcome.ProblemFound ? ExitProblemFound : incomplete ? ElevatedFormat.ExitCancelled : ElevatedFormat.ExitOk)
             : outcome.Error?.Contains("キャンセル") == true || outcome.Error?.Contains("中止") == true ? ElevatedFormat.ExitCancelled
             : ElevatedFormat.ExitError;
    }

    /// <summary>このプロセス（管理者権限あり）で検査を実行する。</summary>
    public static DiagnosticOutcome Execute(DiagnosticRequest req, IProgress<OperationProgress>? progress, Action<string> log, CancellationToken ct)
    {
        var lines = new List<string>();
        try
        {
            return DiagnosticsService.Run(req, progress, s => { lines.Add(s); log(s); }, ct);
        }
        catch (OperationCanceledException)
        {
            return DiagnosticOutcome.Fail(req.Kind, "中止されました。", lines);
        }
        catch (Exception ex)
        {
            bool friendly = ex is VolumeFormatException or ArgumentException or IOException or InvalidOperationException;
            return DiagnosticOutcome.Fail(req.Kind, friendly ? ex.Message : $"{ex.GetType().Name}: {ex.Message}", lines);
        }
    }
}

internal sealed class DiagnosticOptions
{
    public string Command = "";
    public string? Target;
    public bool Quick, Full, Scan, Write, Yes, Json, AllowFixed;
    public string? ResultFile, ProgressFile, CancelFile;

    public static DiagnosticOptions Parse(string[] args)
    {
        var o = new DiagnosticOptions { Command = args[0].ToLowerInvariant() };
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new UsageException($"{a} には値が必要です。");
            switch (a.ToLowerInvariant())
            {
                case "--quick": o.Quick = true; break;
                case "--full": o.Full = true; break;
                case "--scan": o.Scan = true; break;
                case "--write": o.Write = true; break;
                case "--yes": case "-y": o.Yes = true; break;
                case "--json": o.Json = true; break;
                case "--allow-fixed": case "--force": o.AllowFixed = true; break;
                case ElevatedCli.ResultFileOption: o.ResultFile = Next(); break;
                case ElevatedCli.ProgressFileOption: o.ProgressFile = Next(); break;
                case ElevatedCli.CancelFileOption: o.CancelFile = Next(); break;
                default:
                    if (a.StartsWith('-')) throw new UsageException($"不明なオプションです: {a}");
                    if (o.Target != null) throw new UsageException($"引数が多すぎます: {a}");
                    o.Target = a;
                    break;
            }
        }
        return o;
    }

    public DiagnosticKind Kind()
    {
        if (Command == "bench")
        {
            if (Quick || Full || Scan) throw new UsageException("bench では --quick / --full / --scan は使えません（書き込みも測る場合は --write）。");
            return Write ? DiagnosticKind.BenchWrite : DiagnosticKind.Bench;
        }
        if (Quick && Full) throw new UsageException("--quick と --full は同時に指定できません。");
        if (Scan)
        {
            if (Quick || Full) throw new UsageException("--scan と --quick / --full は同時に指定できません。");
            return Write ? DiagnosticKind.ScanWrite : DiagnosticKind.ScanRead;
        }
        if (Write) throw new UsageException("--write は --scan（書き込みスキャン）または bench と組み合わせて使います。");
        return Full ? DiagnosticKind.CapacityFull : DiagnosticKind.CapacityQuick;
    }

    public DiagnosticRequest ToRequest()
    {
        if (string.IsNullOrEmpty(Target)) throw new UsageException("ドライブ文字を指定してください（例: E:）。");
        if (DiskTargets.ParseDriveLetter(Target) is not char letter) throw new UsageException($"ドライブ文字が不正です: {Target}");
        return new DiagnosticRequest { DriveLetter = letter, Kind = Kind(), AllowFixedDisk = AllowFixed };
    }
}
