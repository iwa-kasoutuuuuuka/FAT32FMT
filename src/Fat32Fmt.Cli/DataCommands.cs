using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fat32Fmt.Core;
using Fat32Fmt.Core.Diagnostics;
using Fat32Fmt.Core.Fat32;
using Fat32Fmt.Core.Formatting;
using Fat32Fmt.Core.Imaging;
using Fat32Fmt.Core.IO;
using Fat32Fmt.Core.Recovery;
using Fat32Fmt.Core.Windows;

namespace Fat32Fmt.Cli;

public sealed record BackupSummary(int Disk, string Image, long Bytes, string Sha256, string HashFile, long ZeroBytesSkipped, double Seconds);

public sealed record RestoreSummary(int Disk, string Image, long Bytes, string ImageSha256, string ReadbackSha256, bool Verified,
                                    string? RecordedSha256, bool MatchesRecordedHash, long TruncatedZeroBytes, double Seconds,
                                    IReadOnlyList<char> Letters);

public sealed record WipeSummary(int Disk, long Bytes, int Passes, WipePattern Pattern, bool Verified, bool Formatted, double Seconds,
                                 IReadOnlyList<char> Letters);

public sealed record DeletedFileInfo(string Path, long Size, RecoveryChance Chance, bool NameRecovered, DateTime? Modified,
                                     uint FirstCluster, string? Output);

public sealed record RecoverSummary(string Source, string? Destination, int Found, int Recovered, long RecoveredBytes,
                                    List<DeletedFileInfo> Files);

/// <summary>
/// データ系コマンド: backup / restore / wipe / recover。
/// 物理ディスクやボリュームの生 I/O には管理者権限が必要なので、権限が無ければ UAC 昇格した自分自身に引数を渡して実行させる。
/// </summary>
internal static class DataCommands
{
    public static readonly string[] Commands = ["backup", "restore", "wipe", "recover"];

    public static int Run(string[] args)
    {
        var o = DataOptions.Parse(args);
        Preflight(o);

        if (o.IsDestructive && !o.Yes)
        {
            if (o.Json) throw new UsageException("--json を使う場合は --yes も指定してください（対話確認ができません）。");
            if (!Confirm(o)) { Console.WriteLine("中止しました。"); return ElevatedFormat.ExitCancelled; }
        }

        DataOutcome outcome;
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.Error.WriteLine();
            Console.Error.WriteLine("中止しています...");
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            if (o.NeedsElevation && !VolumeFormatService.IsElevated())
            {
                string exe = Environment.ProcessPath ?? throw new VolumeFormatException("実行ファイルのパスを取得できません。");
                if (!o.Json) Console.Error.WriteLine("管理者権限が必要です。UAC の確認ダイアログで「はい」を選択してください...");
                var progress = o.Json ? null : Program.ConsoleProgress();
                outcome = ElevatedCli.RunData(exe, ChildArgs(o), progress == null ? null : progress.Report,
                    o.Json ? null : Console.Error.WriteLine, cts.Token);
            }
            else
            {
                using var cancelFile = ElevatedCli.WatchCancelFile(o.CancelFile);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancelFile.Token);
                var writer = ElevatedCli.CreateProgressWriter(o.ProgressFile);
                IProgress<OperationProgress>? progress = writer ?? (o.Json ? null : Program.ConsoleProgress());
                Action<string> log = writer != null ? writer.Log : o.Json ? _ => { } : Console.Error.WriteLine;
                outcome = Execute(o, progress, log, linked.Token);
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

        if (o.Json) Console.WriteLine(JsonSerializer.Serialize(outcome, JsonDefaults.Options));
        else if (outcome.Success && outcome.Result is { } r) PrintResult(o, r);
        else Console.Error.WriteLine($"エラー: {outcome.Error}");

        return outcome.Success ? ElevatedFormat.ExitOk
             : outcome.Error?.Contains("キャンセル") == true || outcome.Error?.Contains("中止") == true ? ElevatedFormat.ExitCancelled
             : ElevatedFormat.ExitError;
    }

    // ---- 事前確認（管理者権限不要） ------------------------------------------------

    /// <summary>対象を物理ディスクに解決し、パスを絶対パスにして、書き込み前に分かる問題を検出する。</summary>
    public static void Preflight(DataOptions o)
    {
        switch (o.Command)
        {
            case "backup":
                Require(o.Positionals.Count == 2, "使い方: fat32fmt backup <X: | diskN> <出力.img>");
                o.Disk = ResolveDisk(o, o.Positionals[0]);
                o.ImagePath = Path.GetFullPath(o.Positionals[1]);
                if (File.Exists(o.ImagePath) && !o.Overwrite)
                    throw new UsageException($"{o.ImagePath} は既に存在します。上書きする場合は --overwrite を指定してください。");
                if (Directory.Exists(o.ImagePath)) throw new UsageException($"{o.ImagePath} はフォルダです。イメージのファイル名を指定してください。");
                if (DiskTargets.DiskNumberOfPath(o.ImagePath) == o.Disk.DiskNumber)
                    throw new VolumeFormatException("イメージの保存先がバックアップ元と同じディスク上にあります。別のドライブを指定してください。");
                CheckFreeSpace(o.ImagePath, o.Disk.Length);
                break;

            case "restore":
                Require(o.Positionals.Count == 2, "使い方: fat32fmt restore <入力.img> <X: | diskN>");
                o.ImagePath = Path.GetFullPath(o.Positionals[0]);
                if (!File.Exists(o.ImagePath)) throw new UsageException($"イメージが見つかりません: {o.ImagePath}");
                o.Disk = ResolveDisk(o, o.Positionals[1]);
                if (DiskTargets.DiskNumberOfPath(o.ImagePath) == o.Disk.DiskNumber)
                    throw new VolumeFormatException("イメージが書き戻し先と同じディスク上にあります。別のドライブにコピーしてから実行してください。");
                long imageBytes = new FileInfo(o.ImagePath).Length;
                if (imageBytes % o.Disk.SectorSize != 0)
                    throw new VolumeFormatException($"イメージのサイズ ({imageBytes:N0} B) がセクタサイズ ({o.Disk.SectorSize} B) の倍数ではありません。");
                break;

            case "wipe":
                Require(o.Positionals.Count == 1, "使い方: fat32fmt wipe <X: | diskN> [--passes N] [--random] [--verify] [--format]");
                o.Disk = ResolveDisk(o, o.Positionals[0]);
                if (o.Passes is < 1 or > SecureEraser.MaxPasses) throw new UsageException($"--passes は 1～{SecureEraser.MaxPasses} で指定してください。");
                if (!VolumeLabel.TryValidate(o.Label, out string? labelError)) throw new UsageException(labelError!);
                if (o.Label != null && !o.FormatAfter) throw new UsageException("--label は --format と一緒に指定してください。");
                break;

            case "recover":
                Require(o.Positionals.Count == 1, "使い方: fat32fmt recover <X: | イメージ.img> --to <保存先フォルダ> [--list] [--all] [--match *.jpg]");
                string src = o.Positionals[0];
                if (DiskTargets.ParseDriveLetter(src) is char letter && !File.Exists(src))
                {
                    var entry = DriveEnumerator.Describe(letter);
                    if (entry.Kind == DriveKind.NoRootDir) throw new VolumeFormatException($"ドライブ {letter}: は存在しません。");
                    if (entry.IsSystemDrive) throw new VolumeFormatException($"ドライブ {letter}: はシステムドライブです。");
                    o.SourceLetter = letter;
                    o.SourceDiskNumber = entry.DiskNumber;
                }
                else
                {
                    o.ImagePath = Path.GetFullPath(src);
                    if (!File.Exists(o.ImagePath)) throw new UsageException($"ファイルまたはドライブが見つかりません: {src}");
                }
                if (!o.ListOnly)
                {
                    if (o.Destination == null) throw new UsageException("復旧したファイルの保存先を --to <フォルダ> で指定してください（一覧だけなら --list）。");
                    o.Destination = Path.GetFullPath(o.Destination);
                    if (o.SourceLetter is char sl && char.ToUpperInvariant(o.Destination[0]) == sl)
                        throw new VolumeFormatException("保存先が復旧元と同じドライブです。上書きで削除データが失われるため、別のドライブを指定してください。");
                    if (o.SourceDiskNumber >= 0 && DiskTargets.DiskNumberOfPath(o.Destination) == o.SourceDiskNumber)
                        throw new VolumeFormatException("保存先が復旧元と同じ物理ディスク上にあります。別のドライブを指定してください。");
                }
                break;
        }
    }

    private static DiskInfo ResolveDisk(DataOptions o, string spec)
    {
        var disk = DiskTargets.Resolve(spec);
        DiskTargets.Validate(disk, o.AllowFixed);
        if (o.ExpectSize is long expected && expected != disk.Length)
            throw new VolumeFormatException($"Disk {disk.DiskNumber} のサイズ ({disk.Length:N0} B) が確認時 ({expected:N0} B) と異なります。カードが差し替えられた可能性があるため中止します。");
        return disk;
    }

    private static void CheckFreeSpace(string path, long needed)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            if (drive.AvailableFreeSpace >= needed) return;
            bool sparse = drive.DriveFormat is "NTFS" or "ReFS";
            string msg = $"保存先 {drive.Name} の空き ({SizeFormat.Both(drive.AvailableFreeSpace)}) がディスク容量 ({SizeFormat.Both(needed)}) より少ないです。";
            if (!sparse) throw new VolumeFormatException(msg);
            Console.Error.WriteLine($"警告: {msg} 0 だけの領域はスパースファイルの穴になるので収まる場合もありますが、途中で容量不足になる可能性があります。");
        }
        catch (ArgumentException) { }
        catch (IOException) { }
    }

    private static void Require(bool ok, string usage)
    {
        if (!ok) throw new UsageException(usage);
    }

    private static bool Confirm(DataOptions o)
    {
        var d = o.Disk!;
        Console.WriteLine(d.Display);
        Console.WriteLine(o.Command == "restore"
            ? $"警告: Disk {d.DiskNumber} の内容はすべてイメージ {o.ImagePath} の内容で置き換えられます。"
            : $"警告: Disk {d.DiskNumber} のデータはパーティション情報も含めてすべて消去されます（{o.Passes} 回上書き、{(o.Random ? "乱数" : "0")}）。");
        string expected = d.Letters.Count > 0 ? d.Letters[0].ToString() : $"disk{d.DiskNumber}";
        Console.Write($"続行するには '{expected}' を入力してください: ");
        string input = Console.ReadLine()?.Trim().TrimEnd(':') ?? "";
        return string.Equals(input, expected, StringComparison.OrdinalIgnoreCase)
               || string.Equals(input, $"disk{d.DiskNumber}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>昇格した子プロセスに渡す引数。対象はディスク番号とサイズで固定し、パスは絶対パスにする（昇格プロセスのカレントディレクトリは異なる）。</summary>
    public static List<string> ChildArgs(DataOptions o)
    {
        var a = new List<string> { o.Command };
        string disk = o.Disk is { } d ? $"disk{d.DiskNumber}" : "";
        switch (o.Command)
        {
            case "backup": a.Add(disk); a.Add(o.ImagePath!); break;
            case "restore": a.Add(o.ImagePath!); a.Add(disk); break;
            case "wipe": a.Add(disk); break;
            case "recover": a.Add(o.SourceLetter is char l ? $"{l}:" : o.ImagePath!); break;
        }
        if (o.Disk is { } dd) { a.Add("--expect-size"); a.Add(dd.Length.ToString()); }
        a.Add("--yes");
        if (o.AllowFixed) a.Add("--allow-fixed");
        if (o.Overwrite) a.Add("--overwrite");
        if (o.Command == "wipe")
        {
            a.Add("--passes"); a.Add(o.Passes.ToString());
            if (o.Random) a.Add("--random");
            if (o.Verify) a.Add("--verify");
            if (o.FormatAfter) a.Add("--format");
            if (o.Label != null) { a.Add("--label"); a.Add(o.Label); }
        }
        if (o.Command == "recover")
        {
            if (o.Destination != null) { a.Add("--to"); a.Add(o.Destination); }
            if (o.ListOnly) a.Add("--list");
            if (o.IncludeOverwritten) a.Add("--all");
            if (o.Match != null) { a.Add("--match"); a.Add(o.Match); }
        }
        return a;
    }

    // ---- 実行（管理者権限のあるプロセス、またはイメージ相手） -----------------------------

    /// <summary>このプロセスで操作を実行する。例外は DataOutcome のエラーにまとめる。</summary>
    public static DataOutcome Execute(DataOptions o, IProgress<OperationProgress>? progress, Action<string> log, CancellationToken ct)
    {
        var lines = new List<string>();
        void Log(string s) { lines.Add(s); log(s); }
        try
        {
            if (o.NeedsElevation && !VolumeFormatService.IsElevated())
                throw new VolumeFormatException("この操作には管理者権限が必要です。");
            object result = o.Command switch
            {
                "backup" => DoBackup(o, progress, Log, ct),
                "restore" => DoRestore(o, progress, Log, ct),
                "wipe" => DoWipe(o, progress, Log, ct),
                "recover" => DoRecover(o, progress, Log, ct),
                _ => throw new UsageException($"不明なコマンドです: {o.Command}"),
            };
            return new DataOutcome { Success = true, Result = JsonSerializer.SerializeToElement(result, result.GetType(), JsonDefaults.Options), Log = lines };
        }
        catch (OperationCanceledException)
        {
            return DataOutcome.Fail("ユーザーにより中止されました。", lines);
        }
        catch (Exception ex)
        {
            bool friendly = ex is VolumeFormatException or Fat32LayoutException or ArgumentException or IOException
                or InvalidDataException or UsageException or UnauthorizedAccessException;
            return DataOutcome.Fail(friendly ? ex.Message : $"{ex.GetType().Name}: {ex.Message}", lines);
        }
    }

    private static BackupSummary DoBackup(DataOptions o, IProgress<OperationProgress>? progress, Action<string> log, CancellationToken ct)
    {
        var info = o.Disk!;
        string path = o.ImagePath!;
        log($"バックアップ元: {info.Display}");
        using var disk = PhysicalDisk.Open(info.DiskNumber, writable: false, log);
        if (o.Overwrite && File.Exists(path)) File.Delete(path);
        try
        {
            ImageResult r;
            using (var image = SparseImage.Create(path, disk.Length, disk.SectorSize))
            {
                r = DiskImager.Backup(disk, image, imageIsZeroFilled: true, progress, ct);
            }
            ImageHashFile.Write(path, r.Sha256);
            log($"SHA-256: {r.Sha256}");
            return new BackupSummary(info.DiskNumber, path, r.Bytes, r.Sha256, ImageHashFile.PathFor(path), r.ZeroBytesSkipped, r.Elapsed.TotalSeconds);
        }
        catch
        {
            try { File.Delete(path); } catch { } // 作りかけのイメージを残さない
            throw;
        }
    }

    private static RestoreSummary DoRestore(DataOptions o, IProgress<OperationProgress>? progress, Action<string> log, CancellationToken ct)
    {
        var info = o.Disk!;
        string? recorded = ImageHashFile.TryRead(o.ImagePath!);
        log($"書き戻し先: {info.Display}");
        log(recorded != null ? $"記録されたハッシュ: {recorded}" : "ハッシュファイル (.sha256) が無いため、イメージ自体の改変は確認できません。");
        RestoreResult r;
        using (var image = StreamBlockDevice.OpenImage(o.ImagePath!, writable: false, info.SectorSize))
        using (var disk = PhysicalDisk.Open(info.DiskNumber, writable: true, log))
        {
            r = DiskImager.Restore(image, disk, recorded, progress, log, ct);
        }
        log($"照合 OK: 書き込んだ {SizeFormat.Both(r.Bytes)} の SHA-256 が一致しました ({r.ReadbackSha256})。");
        if (!r.MatchesRecordedHash)
            log($"警告: イメージの SHA-256 ({r.ImageSha256}) がバックアップ時の記録 ({recorded}) と異なります。イメージファイルが変更・破損している可能性があります。");
        log("Windows がボリュームを認識するのを待っています...");
        var letters = DiskTargets.WaitForLetters(info.DiskNumber, TimeSpan.FromSeconds(15));
        return new RestoreSummary(info.DiskNumber, o.ImagePath!, r.Bytes, r.ImageSha256, r.ReadbackSha256, r.Verified, recorded,
            r.MatchesRecordedHash, r.TruncatedZeroBytes, r.Elapsed.TotalSeconds, letters);
    }

    private static WipeSummary DoWipe(DataOptions o, IProgress<OperationProgress>? progress, Action<string> log, CancellationToken ct)
    {
        var info = o.Disk!;
        log($"消去対象: {info.Display}");
        var opt = new WipeOptions { Passes = o.Passes, Pattern = o.Random ? WipePattern.Random : WipePattern.Zero, Verify = o.Verify };
        WipeResult r;
        using (var disk = PhysicalDisk.Open(info.DiskNumber, writable: true, log))
        {
            r = SecureEraser.Wipe(disk, opt, progress, log, ct);
            if (o.FormatAfter)
            {
                var init = DiskInitializer.InitializeMbrFat32(disk, o.Label, progress, ct);
                log($"MBR + FAT32 パーティションを作成しました（開始 {SizeFormat.Binary(init.Partition.StartBytes)}、クラスタ {SizeFormat.Cluster(init.Format.Layout.BytesPerCluster)}）。");
            }
            else disk.DeletePartitionTable(log);
        }
        IReadOnlyList<char> letters = Array.Empty<char>();
        if (o.FormatAfter)
        {
            log("Windows がボリュームを認識するのを待っています...");
            letters = DiskTargets.WaitForLetters(info.DiskNumber, TimeSpan.FromSeconds(15));
        }
        else
        {
            // パーティションの無いリムーバブルディスクは、Windows がディスク全体を 1 つのボリューム（スーパーフロッピー）として見せる
            letters = DiskTargets.WaitForLetters(info.DiskNumber, TimeSpan.FromSeconds(5));
            if (letters.Count > 0) log(DiskTargets.SuperfloppyNote(letters));
        }
        return new WipeSummary(info.DiskNumber, r.Bytes, r.Passes, r.Pattern, r.Verified, o.FormatAfter, r.Elapsed.TotalSeconds, letters);
    }

    private static RecoverSummary DoRecover(DataOptions o, IProgress<OperationProgress>? progress, Action<string> log, CancellationToken ct)
    {
        IBlockDevice root;
        IBlockDevice volumeDevice;
        string source;
        if (o.SourceLetter is char letter)
        {
            root = ReadOnlyVolume.Open(letter);
            volumeDevice = root;
            source = $"{letter}:";
        }
        else
        {
            root = StreamBlockDevice.OpenImage(o.ImagePath!, writable: false);
            volumeDevice = Fat32Verifier.LocateVolume(root, out _);
            source = o.ImagePath!;
        }

        using (root)
        {
            var volume = Fat32Volume.Open(volumeDevice);
            log($"{source} の削除ファイルを探しています...");
            var found = DeletedFileRecovery.Scan(volume, ct);
            var regex = o.Match != null ? WildcardRegex(o.Match) : null;
            var selected = found
                .Where(f => f.Chance != RecoveryChance.Invalid)
                .Where(f => o.IncludeOverwritten || f.Chance != RecoveryChance.Overwritten)
                .Where(f => regex == null || regex.IsMatch(f.Name))
                .ToList();
            log($"削除ファイル {found.Count} 個（取り出し対象 {selected.Count} 個）。");

            // Recover は渡した順に結果を返す（selected には取り出し不可のものが無いので 1 対 1 に対応する）
            var outputs = new Dictionary<DeletedFile, string>(ReferenceEqualityComparer.Instance);
            if (!o.ListOnly)
            {
                var recovered = DeletedFileRecovery.Recover(volume, selected, o.Destination!, progress, log, ct);
                for (int i = 0; i < recovered.Count; i++) outputs[selected[i]] = recovered[i].OutputPath;
            }

            var files = found.Select(f => new DeletedFileInfo(f.Path, f.Size, f.Chance, f.NameRecovered, f.Entry.Modified,
                f.Entry.FirstCluster, outputs.GetValueOrDefault(f))).ToList();
            return new RecoverSummary(source, o.ListOnly ? null : o.Destination, found.Count, outputs.Count,
                outputs.Keys.Sum(f => f.Size), files);
        }
    }

    internal static Regex WildcardRegex(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ---- 出力 ----------------------------------------------------------------

    private static void PrintResult(DataOptions o, JsonElement r)
    {
        switch (o.Command)
        {
            case "backup":
            {
                var s = r.Deserialize<BackupSummary>(JsonDefaults.Options)!;
                Console.WriteLine($"完了: Disk {s.Disk} を {s.Image} に保存しました（{SizeFormat.Both(s.Bytes)}、{s.Seconds:0} 秒）。");
                Console.WriteLine($"SHA-256: {s.Sha256}  → {s.HashFile}");
                if (s.ZeroBytesSkipped > 0) Console.WriteLine($"0 だけの領域 {SizeFormat.Binary(s.ZeroBytesSkipped)} はスパースファイルの穴として保存しました。");
                break;
            }
            case "restore":
            {
                var s = r.Deserialize<RestoreSummary>(JsonDefaults.Options)!;
                Console.WriteLine($"完了: {s.Image} を Disk {s.Disk} に書き戻しました（{SizeFormat.Both(s.Bytes)}、{s.Seconds:0} 秒）。");
                Console.WriteLine($"照合: 読み戻した SHA-256 がイメージと一致 ({s.ReadbackSha256})");
                if (s.RecordedSha256 != null)
                    Console.WriteLine(s.MatchesRecordedHash ? "イメージはバックアップ時の記録と一致しました。" : "警告: イメージがバックアップ時の記録と一致しません。");
                Console.WriteLine(s.Letters.Count > 0 ? $"再マウント: {string.Join(", ", s.Letters.Select(l => l + ":"))}" : "ドライブ文字は割り当てられていません（カードの抜き差しで認識される場合があります）。");
                break;
            }
            case "wipe":
            {
                var s = r.Deserialize<WipeSummary>(JsonDefaults.Options)!;
                Console.WriteLine($"完了: Disk {s.Disk} の {SizeFormat.Both(s.Bytes)} を {s.Passes} 回上書きしました（{(s.Pattern == WipePattern.Random ? "乱数" : "0")}、{s.Seconds:0} 秒）。");
                if (s.Verified) Console.WriteLine("最後のパスの読み戻し確認 OK。");
                if (s.Formatted)
                    Console.WriteLine(s.Letters.Count > 0 ? $"FAT32 で初期化しました: {string.Join(", ", s.Letters.Select(l => l + ":"))}" : "FAT32 で初期化しました（ドライブ文字はまだ割り当てられていません）。");
                else
                    Console.WriteLine(
                        $"パーティションも消えています{(s.Letters.Count > 0 ? $"（Windows はディスク全体を未フォーマットのボリューム {string.Join(", ", s.Letters.Select(l => l + ":"))} として表示します。正常です）" : "")}。" +
                        $"使うには `fat32fmt restore <img> disk{s.Disk}` で書き戻すか、`fat32fmt wipe disk{s.Disk} --format` で MBR + FAT32 に初期化してください（次回からは wipe に --format を付けるのがおすすめです）。");
                break;
            }
            case "recover":
            {
                var s = r.Deserialize<RecoverSummary>(JsonDefaults.Options)!;
                foreach (var f in s.Files)
                {
                    string mark = f.Output != null ? "復旧" : "    ";
                    string name = f.NameRecovered ? f.Path : f.Path + "  (先頭文字は推定不可)";
                    Console.WriteLine($"{mark} {DeletedFileRecovery.ChanceName(f.Chance),-6} {SizeFormat.Binary(f.Size),12}  {f.Modified:yyyy-MM-dd HH:mm}  {name}");
                }
                Console.WriteLine(s.Destination != null
                    ? $"{s.Found} 個中 {s.Recovered} 個（{SizeFormat.Binary(s.RecoveredBytes)}）を {s.Destination} に保存しました。連続クラスタを仮定しているため、断片化していたファイルは後半が壊れている場合があります。"
                    : $"削除ファイル {s.Found} 個。取り出すには --to <フォルダ> を指定してください。");
                break;
            }
        }
    }
}

internal sealed class DataOptions
{
    public string Command = "";
    public List<string> Positionals = new();
    public bool Yes, Json, AllowFixed, Overwrite, Random, Verify, FormatAfter, ListOnly, IncludeOverwritten;
    public int Passes = 1;
    public string? Label, Destination, Match, ResultFile, ProgressFile, CancelFile;
    public long? ExpectSize;

    // Preflight で決まる値
    public DiskInfo? Disk;
    public string? ImagePath;
    public char? SourceLetter;
    public int SourceDiskNumber = -1;

    public bool IsDestructive => Command is "restore" or "wipe";

    /// <summary>イメージファイルからの復旧以外は生ディスク I/O なので管理者権限が必要。</summary>
    public bool NeedsElevation => Command != "recover" || SourceLetter != null;

    public static DataOptions Parse(string[] args)
    {
        var o = new DataOptions { Command = args[0].ToLowerInvariant() };
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new UsageException($"{a} には値が必要です。");
            switch (a.ToLowerInvariant())
            {
                case "--yes": case "-y": o.Yes = true; break;
                case "--json": o.Json = true; break;
                case "--allow-fixed": case "--force": o.AllowFixed = true; break;
                case "--overwrite": o.Overwrite = true; break;
                case "--passes":
                    o.Passes = int.TryParse(Next(), out int p) ? p : throw new UsageException("--passes には回数を指定してください。");
                    break;
                case "--random": o.Random = true; break;
                case "--zero": o.Random = false; break;
                case "--verify": o.Verify = true; break;
                case "--format": o.FormatAfter = true; break;
                case "--label": o.Label = Next(); break;
                case "--to": o.Destination = Next(); break;
                case "--list": o.ListOnly = true; break;
                case "--all": o.IncludeOverwritten = true; break;
                case "--match": o.Match = Next(); break;
                case "--expect-size":
                    o.ExpectSize = long.TryParse(Next(), out long es) ? es : throw new UsageException("--expect-size の値が不正です。");
                    break;
                case ElevatedCli.ResultFileOption: o.ResultFile = Next(); break;
                case ElevatedCli.ProgressFileOption: o.ProgressFile = Next(); break;
                case ElevatedCli.CancelFileOption: o.CancelFile = Next(); break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"不明なオプションです: {a}");
                    o.Positionals.Add(a);
                    break;
            }
        }
        return o;
    }
}
