using System.Text;
using System.Text.Json;
using Fat32Fmt.Core;
using Fat32Fmt.Core.Diagnostics;
using Fat32Fmt.Core.Fat32;
using Fat32Fmt.Core.IO;
using Fat32Fmt.Core.Windows;

namespace Fat32Fmt.Cli;

internal static class Program
{
    private const string Version = "1.0.1";

    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = Encoding.UTF8;
        }
        catch { /* 標準出力がリダイレクトされていない場合など */ }

        if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
        {
            PrintUsage();
            return ElevatedFormat.ExitUsage;
        }

        try
        {
            // backup / restore / wipe / recover は独自のオプションを持つので DataCommands で解析する
            if (DataCommands.Commands.Contains(args[0].ToLowerInvariant())) return DataCommands.Run(args);
            // test / bench（検査）も独自のオプションを持つので DiagnosticCommands で解析する
            if (DiagnosticCommands.Commands.Contains(args[0].ToLowerInvariant())) return DiagnosticCommands.Run(args);

            var opts = Options.Parse(args);
            return opts.Command switch
            {
                "list" => CmdList(opts),
                "plan" => CmdPlan(opts),
                "format" => CmdFormat(opts),
                "image" => CmdImage(opts),
                "verify" => CmdVerify(opts),
                "mcp" => McpServer.Run(opts.AllowFixed),
                "version" => Print($"fat32fmt {Version}"),
                _ => Usage($"不明なコマンドです: {opts.Command}"),
            };
        }
        catch (UsageException ex)
        {
            return Usage(ex.Message);
        }
        catch (Exception ex) when (ex is VolumeFormatException or Fat32LayoutException or ArgumentException or IOException)
        {
            Console.Error.WriteLine($"エラー: {ex.Message}");
            return ElevatedFormat.ExitError;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"エラー: {ex.GetType().Name}: {ex.Message}");
            return ElevatedFormat.ExitError;
        }
    }

    // ---- list ---------------------------------------------------------------

    private static int CmdList(Options o)
    {
        var drives = DriveEnumerator.Enumerate(includeFixed: o.AllowFixed);
        if (o.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { elevated = VolumeFormatService.IsElevated(), drives }, JsonDefaults.Options));
            return ElevatedFormat.ExitOk;
        }
        if (drives.Count == 0)
        {
            Console.WriteLine(o.AllowFixed ? "フォーマット可能なドライブがありません。" : "リムーバブルドライブがありません。（固定ディスクも含めるには --allow-fixed）");
            return ElevatedFormat.ExitOk;
        }
        foreach (var d in drives) Console.WriteLine(d.Display);
        return ElevatedFormat.ExitOk;
    }

    // ---- plan ---------------------------------------------------------------

    private static int CmdPlan(Options o)
    {
        var plan = VolumeFormatService.Plan(o.ToRequest());
        if (o.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { plan, summary = plan.Describe() }, JsonDefaults.Options));
        }
        else
        {
            Console.WriteLine(plan.Drive.DescribeDetails());
            Console.WriteLine();
            Console.WriteLine("--- フォーマット後のレイアウト（予定） ---");
            Console.WriteLine(plan.Describe());
            Console.WriteLine($"選択可能なクラスタサイズ: {string.Join(", ", plan.ValidClusterSizes.Select(SizeFormat.Cluster))}");
        }
        return ElevatedFormat.ExitOk;
    }

    // ---- format -------------------------------------------------------------

    private static int CmdFormat(Options o)
    {
        var req = o.ToRequest();

        // 事前検証とレイアウト表示（管理者権限不要）
        var plan = VolumeFormatService.Plan(req);

        if (!o.Yes)
        {
            if (o.Json) throw new UsageException("--json を使う場合は --yes も指定してください（対話確認ができません）。");
            Console.WriteLine(plan.Drive.DescribeDetails());
            Console.WriteLine();
            Console.WriteLine("--- フォーマット後のレイアウト ---");
            Console.WriteLine(plan.Describe());
            Console.WriteLine();
            Console.WriteLine($"警告: ドライブ {plan.Drive.Letter}: 上のデータはすべて消去されます。");
            Console.Write($"続行するにはドライブ文字 '{plan.Drive.Letter}' を入力してください: ");
            var input = Console.ReadLine()?.Trim().TrimEnd(':');
            if (!string.Equals(input, plan.Drive.Letter.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("中止しました。");
                return ElevatedFormat.ExitCancelled;
            }
        }

        FormatOutcome outcome;
        if (!VolumeFormatService.IsElevated())
        {
            string exe = Environment.ProcessPath ?? throw new VolumeFormatException("実行ファイルのパスを取得できません。");
            if (!o.Json) Console.Error.WriteLine("管理者権限が必要です。UAC の確認ダイアログで「はい」を選択してください...");
            outcome = ElevatedFormat.RunViaElevatedCli(exe, req);
        }
        else
        {
            outcome = RunFormatHere(req, quiet: o.Json);
            if (o.ResultFile != null)
            {
                try { File.WriteAllText(o.ResultFile, JsonSerializer.Serialize(outcome, JsonDefaults.Options), new UTF8Encoding(false)); }
                catch (Exception ex) { Console.Error.WriteLine($"結果ファイルの書き込みに失敗: {ex.Message}"); }
            }
        }

        if (o.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(outcome, JsonDefaults.Options));
        }
        else if (outcome.Success && outcome.Result is { } r)
        {
            Console.WriteLine();
            Console.WriteLine($"完了: {r.DriveLetter}: を FAT32 でフォーマットしました（{r.Elapsed.TotalSeconds:0.0} 秒）。");
            Console.WriteLine(LayoutDescriber.Describe(r.Layout, plan.AlignmentBytes));
            Console.WriteLine($"ボリュームシリアル: {r.VolumeId >> 16:X4}-{r.VolumeId & 0xFFFF:X4}");
            if (r.BadClusters.Count > 0) Console.WriteLine($"不良クラスタ: {r.BadClusters.Count} 個を FAT に登録しました。");
            if (r.PartitionTypeChanged) Console.WriteLine("MBR パーティション種別を 0x0C (FAT32 LBA) に変更しました。");
            Console.WriteLine(r.MountedFileSystem != null
                ? $"再マウント確認: {r.MountedFileSystem}  ラベル \"{r.MountedLabel}\""
                : "再マウントの確認はできませんでした。");
        }
        else
        {
            foreach (var l in outcome.Log) Console.Error.WriteLine(l);
            Console.Error.WriteLine($"エラー: {outcome.Error}");
        }
        return outcome.Success ? ElevatedFormat.ExitOk
             : outcome.Error?.Contains("キャンセル") == true ? ElevatedFormat.ExitCancelled
             : ElevatedFormat.ExitError;
    }

    /// <summary>このプロセス（管理者権限あり）でフォーマットを実行する。</summary>
    private static FormatOutcome RunFormatHere(FormatRequest req, bool quiet)
    {
        var log = new List<string>();
        void Log(string s)
        {
            log.Add(s);
            if (!quiet) Console.Error.WriteLine(s);
        }

        try
        {
            var result = VolumeFormatService.Format(req, quiet ? null : ConsoleProgress(), Log);
            return new FormatOutcome { Success = true, Result = result, Log = log };
        }
        catch (Exception ex)
        {
            if (!quiet) Console.Error.WriteLine();
            bool friendly = ex is VolumeFormatException or Fat32LayoutException or ArgumentException or IOException or OperationCanceledException;
            return FormatOutcome.Fail(friendly ? ex.Message : $"{ex.GetType().Name}: {ex.Message}", log);
        }
    }

    internal static IProgress<OperationProgress> ConsoleProgress()
    {
        string last = "";
        string? finishedPhase = null;
        return new SyncProgress(p =>
        {
            if (p.Done == 0) finishedPhase = null; // 新しいフェーズの開始
            if (p.Phase == finishedPhase) return;  // 完了通知が 2 回来ても 1 行にする
            string speed = p.BytesPerSecond > 0 ? $"  {SizeFormat.Speed(p.BytesPerSecond)}" : "";
            string eta = p.Remaining is { } r ? $"  残り {(int)r.TotalHours}:{r:mm\\:ss}" : "";
            string line = $"\r{p.Phase} {p.Fraction * 100:0.0}%{speed}{eta}      ";
            if (line != last) { Console.Error.Write(line); last = line; }
            if (p.Done >= p.Total) { Console.Error.WriteLine(); finishedPhase = p.Phase; }
        });
    }

    // ---- image --------------------------------------------------------------

    private static int CmdImage(Options o)
    {
        if (o.Target == null) throw new UsageException("image コマンドにはイメージファイルのパスが必要です。");
        if (o.Mbr && o.ImageOffset != null) throw new UsageException("--mbr と --offset は同時に指定できません（--mbr ではパーティション位置を自動で決めます）。");
        bool exists = File.Exists(o.Target);
        if (!exists && o.ImageSize == null) throw new UsageException("新規イメージを作る場合は --size <バイト数> を指定してください。");
        if (exists && o.ImageSize != null) throw new UsageException($"{o.Target} は既に存在します。既存イメージを再フォーマットする場合は --size を外してください。");

        try
        {
            using var disk = exists ? StreamBlockDevice.OpenImage(o.Target, writable: true) : SparseImage.Create(o.Target, o.ImageSize!.Value);
            IBlockDevice dev = disk;
            long offset = o.ImageOffset ?? 0;
            long align = o.Align ?? SdCardGeometry.BoundaryUnitBytes(disk.Length);
            PartitionPlan? part = null;
            if (o.Mbr)
            {
                // SD カードと同じく、パーティション開始を境界単位にそろえた MBR を作る
                part = PartitionPlanner.PlanSinglePartition(disk.Length, disk.SectorSize, align > 0 ? align : PartitionPlanner.DefaultAlignmentBytes);
                offset = part.StartBytes;
                dev = new SubBlockDevice(disk, part.StartBytes, part.LengthBytes);
            }

            int cluster = o.ClusterSize == 0 ? ClusterSizePolicy.Default(dev.Length, dev.SectorSize) : o.ClusterSize;
            var layout = Fat32LayoutCalculator.Calculate(dev.Length, dev.SectorSize, cluster, offset, align);
            var result = Fat32Formatter.Format(dev, layout, new Fat32FormatOptions { Label = o.Label, FullFormat = o.Full }, o.Json ? null : ConsoleProgress());
            if (part != null)
            {
                disk.Write(0, MbrBuilder.Build(disk.SectorSize, part.StartBytes, part.LengthBytes, PartitionPlanner.Fat32LbaType, result.VolumeId));
                disk.Flush();
            }

            if (o.Json) Console.WriteLine(JsonSerializer.Serialize(new { path = o.Target, partitionOffset = part?.StartBytes, layout, volumeId = result.VolumeId, badClusters = result.BadClusters.Count }, JsonDefaults.Options));
            else
            {
                Console.WriteLine($"イメージ {o.Target} を FAT32 でフォーマットしました。");
                if (part != null) Console.WriteLine($"MBR パーティション: 開始 {SizeFormat.Binary(part.StartBytes)}、種別 0x0C{(part.Truncated ? "（2 TiB を超える部分は未使用）" : "")}");
                Console.WriteLine(LayoutDescriber.Describe(layout, align));
            }
            return ElevatedFormat.ExitOk;
        }
        catch when (!exists)
        {
            try { File.Delete(o.Target); } catch { } // 作りかけのイメージを残さない
            throw;
        }
    }

    // ---- verify -------------------------------------------------------------

    private static int CmdVerify(Options o)
    {
        if (o.Target == null) throw new UsageException("verify コマンドにはイメージファイルのパスが必要です。");
        if (!File.Exists(o.Target)) throw new UsageException($"ファイルが見つかりません: {o.Target}");

        using var disk = StreamBlockDevice.OpenImage(o.Target, writable: false);
        var vol = Fat32Verifier.LocateVolume(disk, out long partitionOffset);
        var report = Fat32Verifier.Verify(vol);
        if (o.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { path = o.Target, partitionOffset, report }, JsonDefaults.Options));
        }
        else
        {
            Console.WriteLine($"{o.Target}: {(report.Ok ? "OK" : "異常あり")}");
            if (partitionOffset > 0) Console.WriteLine($"MBR パーティション 1: 開始 {SizeFormat.Binary(partitionOffset)}");
            if (report.Layout is { } l)
            {
                Console.WriteLine(LayoutDescriber.Describe(l, SdCardGeometry.BoundaryUnitBytes(disk.Length)));
                Console.WriteLine($"ラベル: \"{report.Label}\"  シリアル: {report.VolumeId >> 16:X4}-{report.VolumeId & 0xFFFF:X4}  空き: {SizeFormat.Both(report.FreeClusters * l.BytesPerCluster)}");
            }
            foreach (var e in report.Errors) Console.WriteLine($"エラー: {e}");
            foreach (var w in report.Warnings) Console.WriteLine($"警告: {w}");
        }
        return report.Ok ? ElevatedFormat.ExitOk : ElevatedFormat.ExitError;
    }

    // ---- usage --------------------------------------------------------------

    private static int Print(string s) { Console.WriteLine(s); return ElevatedFormat.ExitOk; }

    private static int Usage(string error)
    {
        Console.Error.WriteLine($"エラー: {error}");
        Console.Error.WriteLine();
        PrintUsage();
        return ElevatedFormat.ExitUsage;
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            fat32fmt {Version} - 32GB を超える SDXC カード等を FAT32 でフォーマットするツール

            使い方:
              fat32fmt list   [--allow-fixed] [--json]
              fat32fmt plan   <X:> [オプション] [--json]
              fat32fmt format <X:> [オプション] [--yes] [--json]
              fat32fmt image  <ファイル> [--size <サイズ>] [--mbr | --offset <バイト数>] [オプション]
              fat32fmt verify <ファイル> [--json]
              fat32fmt backup  <X: | diskN> <出力.img> [--overwrite] [--json]
              fat32fmt restore <入力.img> <X: | diskN> [--yes] [--json]
              fat32fmt wipe    <X: | diskN> [--passes N] [--random] [--verify] [--format [--label 名前]] [--yes] [--json]
              fat32fmt recover <X: | イメージ.img> (--to <フォルダ> | --list) [--all] [--match *.jpg] [--json]
              fat32fmt test    <X:> [--quick | --full | --scan [--write]] [--yes] [--json]
              fat32fmt bench   <X:> [--write] [--yes] [--json]
              fat32fmt mcp [--allow-fixed]
              fat32fmt version

            コマンド:
              list     フォーマット可能なドライブを一覧表示（既定はリムーバブルのみ）
              plan     書き込みは行わず、対象の情報とフォーマット後のレイアウトを表示
              format   FAT32 でフォーマット（管理者権限が無ければ UAC で昇格した子プロセスを起動）
              image    ディスクイメージファイルを FAT32 でフォーマット（テスト用。新規作成時は --size 必須、スパースファイルで作成）
                       --mbr で SD カードと同じ MBR + パーティション 1 個の形にする
              verify   イメージファイルの FAT32 構造を読み戻して検査（MBR 付きならパーティション 1 を検査）
              backup   カード全体（物理ディスク）を .img に保存（スパースファイル）。SHA-256 を <img>.sha256 に記録
              restore  .img をカード全体に書き戻し、読み戻した内容を SHA-256 で照合（確認入力あり）
              wipe     カード全体を 0（既定）または乱数 (--random) で --passes 回上書き（確認入力あり）
                       パーティションも消えるので、通常は --format を付けて MBR + FAT32 で作り直す（推奨）
              recover  削除ファイルを探して --to のフォルダ（復旧元とは別のドライブ）へ取り出す
                       連続クラスタを仮定。既定では上書き済みのものは除外（--all で含める）
              ※ backup / restore / wipe とドライブからの recover は管理者権限が必要（無ければ UAC で昇格）
                 対象は E: のようなドライブ文字か、ドライブ文字の無いカードなら disk2 のようにディスク番号で指定
              test     カードの検査（--quick / --full / --write は全データ消去。確認入力あり）
                         --quick  容量偽装チェック（抜き取り。既定）
                         --full   容量偽装チェック（全域。H2testw と同じく全体を書いてから読み戻す）
                         --scan   不良セクタの読み取りスキャン（非破壊。カード全体を読む）
                         --scan --write  不良セクタの書き込みスキャン（破壊）
              bench    実測の読み書き速度（連続・ランダム 4K）とスピードクラスの目安。既定は読み取りのみ（非破壊）
                         --write  書き込みも測る（破壊）。スピードクラスの判定には書き込みの測定が必要
              ※ test / bench は管理者権限が必要（無ければ UAC で昇格）。検査で問題が見つかると終了コード 4
              mcp      MCP サーバー (stdio) として起動。Claude Code / Antigravity 等から操作する用
                         --allow-fixed  固定ディスクを対象にすることを許可（既定では AI から指定されても拒否）

            オプション:
              --cluster <N>      クラスタサイズ。4096 / 32K / 64K など（既定: 自動。32GB 超は 32K）
              --label <名前>     ボリュームラベル（11 バイト以内。日本語は 1 文字 2 バイト）
              --full             全データ領域を書き込み検査する通常フォーマット（既定はクイック）
              --no-ptype         MBR パーティション種別の 0x0C への修正を行わない
              --allow-fixed      固定ディスクとして認識されるドライブも対象にする（危険）
              --align <N>        データ領域の整列境界（既定: SD 規格の境界単位を容量から自動選択）。0 で無効
              --yes              確認プロンプトを省略
              --json             機械可読な JSON で出力（format では --yes が必須）
              --result-file <p>  （内部用）結果 JSON の出力先

            終了コード: 0=成功 1=エラー 2=使い方の誤り 3=キャンセル 4=検査で問題を検出（test / bench）
            """);
    }
}

internal sealed class UsageException : Exception
{
    public UsageException(string message) : base(message) { }
}

/// <summary>Progress&lt;T&gt; は SynchronizationContext 経由で非同期に通知するため、CLI では同期版を使う。</summary>
internal sealed class SyncProgress : IProgress<OperationProgress>
{
    private readonly Action<OperationProgress> _cb;
    public SyncProgress(Action<OperationProgress> cb) => _cb = cb;
    public void Report(OperationProgress value) => _cb(value);
}

internal sealed class Options
{
    public string Command = "";
    public string? Target;
    public int ClusterSize;
    public string? Label;
    public bool Full;
    public bool NoPtype;
    public bool AllowFixed;
    public long? Align;
    public bool Yes;
    public bool Json;
    public string? ResultFile;
    public long? ImageSize;
    public long? ImageOffset;
    public bool Mbr;

    public static Options Parse(string[] args)
    {
        var o = new Options { Command = args[0].ToLowerInvariant() };
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            string Next(string name) => i + 1 < args.Length ? args[++i] : throw new UsageException($"{name} には値が必要です。");
            switch (a.ToLowerInvariant())
            {
                case "--cluster": o.ClusterSize = (int)Math.Min(int.MaxValue, Size(Next(a), a)); break;
                case "--label": o.Label = Next(a); break;
                case "--full": o.Full = true; break;
                case "--no-ptype": o.NoPtype = true; break;
                case "--allow-fixed": case "--force": o.AllowFixed = true; break;
                case "--align": o.Align = Size(Next(a), a); break;
                case "--yes": case "-y": o.Yes = true; break;
                case "--json": o.Json = true; break;
                case "--result-file": o.ResultFile = Next(a); break;
                case "--size": o.ImageSize = Size(Next(a), a); break;
                case "--offset": o.ImageOffset = Size(Next(a), a); break;
                case "--mbr": o.Mbr = true; break;
                default:
                    if (a.StartsWith('-')) throw new UsageException($"不明なオプションです: {a}");
                    if (o.Target != null) throw new UsageException($"引数が多すぎます: {a}");
                    o.Target = a;
                    break;
            }
        }
        return o;
    }

    private static long Size(string value, string option) =>
        SizeFormat.TryParse(value, out long v) ? v : throw new UsageException($"{option} の値が不正です: {value}（例: 32K, 4096, 64M）");

    public char DriveLetter()
    {
        if (string.IsNullOrEmpty(Target)) throw new UsageException("ドライブ文字を指定してください（例: E:）。");
        string t = Target.TrimEnd('\\', '/').TrimEnd(':');
        if (t.Length != 1 || !char.IsAsciiLetter(t[0])) throw new UsageException($"ドライブ文字が不正です: {Target}");
        return char.ToUpperInvariant(t[0]);
    }

    public FormatRequest ToRequest() => new()
    {
        DriveLetter = DriveLetter(),
        ClusterBytes = ClusterSize,
        Label = Label,
        FullFormat = Full,
        FixPartitionType = !NoPtype,
        AllowFixedDisk = AllowFixed,
        AlignmentBytes = Align,
    };
}
