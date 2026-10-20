using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Fat32Fmt.Core.Windows;

/// <summary>昇格した CLI の実行結果。</summary>
/// <param name="Json">子プロセスが --result-file に書いた JSON。起動できなかった・結果が無い場合は null。</param>
/// <param name="Error">起動やタイムアウトなど、子プロセスの外で起きたエラー。</param>
public sealed record ElevatedRun(string? Json, string? Error, int? ExitCode, bool Cancelled);

/// <summary>昇格プロセスで実行したデータ操作（backup / restore / wipe / recover）の結果。</summary>
public sealed class DataOutcome
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public JsonElement? Result { get; set; }
    public List<string> Log { get; set; } = new();

    public static DataOutcome Fail(string error, IEnumerable<string>? log = null) =>
        new() { Success = false, Error = error, Log = log?.ToList() ?? new() };
}

/// <summary>
/// 管理者権限の無いプロセスから、UAC 昇格した CLI を起動して結果ファイル経由で受け取る（format / backup / restore / wipe / recover 共通）。
/// 昇格した子プロセスのウィンドウは見えないので、進捗とログはファイル経由で受け取り、中止もファイルで伝える。
/// </summary>
[SupportedOSPlatform("windows")]
public static class ElevatedCli
{
    public const string ResultFileOption = "--result-file";
    public const string ProgressFileOption = "--progress-file";
    public const string CancelFileOption = "--cancel-file";

    /// <summary>
    /// CLI を UAC 昇格して起動し、終了まで待つ。args には --result-file を含めないこと（ここで付ける）。
    /// --progress-file は onProgress / onLog を渡したときだけ、--cancel-file は ct がキャンセル可能なときだけ付ける。
    /// </summary>
    /// <param name="onProgress">子プロセスの進捗。指定すると --progress-file を付ける。</param>
    /// <param name="onLog">子プロセスのログ 1 行ごと。--progress-file 指定時のみ届く。</param>
    /// <param name="ct">キャンセルすると --cancel-file を作り、子プロセスに中止を伝える。</param>
    public static ElevatedRun Run(string cliExePath, IEnumerable<string> args, Action<OperationProgress>? onProgress = null,
                                  Action<string>? onLog = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        if (!File.Exists(cliExePath)) return new(null, $"CLI 実行ファイルが見つかりません: {cliExePath}", null, false);

        string baseName = Path.Combine(Path.GetTempPath(), $"fat32fmt-{Guid.NewGuid():N}");
        string resultFile = baseName + ".json";
        string progressFile = baseName + ".progress";
        string cancelFile = baseName + ".cancel";
        bool watch = onProgress != null || onLog != null;

        var psi = new ProcessStartInfo(cliExePath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(ResultFileOption);
        psi.ArgumentList.Add(resultFile);
        if (watch)
        {
            psi.ArgumentList.Add(ProgressFileOption);
            psi.ArgumentList.Add(progressFile);
        }
        if (ct.CanBeCanceled)
        {
            psi.ArgumentList.Add(CancelFileOption);
            psi.ArgumentList.Add(cancelFile);
        }

        Process? p;
        try
        {
            p = Process.Start(psi);
            if (p == null) return new(null, "昇格プロセスを起動できませんでした。", null, false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new(null, "UAC の昇格がユーザーによってキャンセルされました。操作は実行されていません。", null, true);
        }
        catch (Exception ex)
        {
            return new(null, $"昇格プロセスの起動に失敗しました: {ex.Message}", null, false);
        }

        var reader = watch ? new ProgressFileReader(progressFile) : null;
        try
        {
            using (p)
            {
                var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromHours(48));
                bool cancelSent = false;
                while (!p.WaitForExit(500))
                {
                    reader?.Poll(onProgress, onLog);
                    if (ct.IsCancellationRequested && !cancelSent)
                    {
                        try { File.WriteAllText(cancelFile, "cancel"); } catch { }
                        cancelSent = true;
                    }
                    if (DateTime.UtcNow >= deadline)
                    {
                        try { File.WriteAllText(cancelFile, "timeout"); } catch { }
                        p.WaitForExit(30_000);
                        return new(null, "処理がタイムアウトしました。", null, false);
                    }
                }
                reader?.Poll(onProgress, onLog);
                if (!File.Exists(resultFile))
                    return new(null, $"昇格プロセスが結果を返しませんでした（終了コード {p.ExitCode}）。", p.ExitCode, cancelSent);
                return new(File.ReadAllText(resultFile), null, p.ExitCode, cancelSent);
            }
        }
        catch (Exception ex)
        {
            return new(null, $"結果の取得に失敗しました: {ex.Message}", null, false);
        }
        finally
        {
            foreach (var f in new[] { resultFile, progressFile, progressFile + ".log", cancelFile })
            {
                try { File.Delete(f); } catch { }
            }
        }
    }

    /// <summary>昇格した CLI でデータ操作を実行し、<see cref="DataOutcome"/> を返す。</summary>
    public static DataOutcome RunData(string cliExePath, IEnumerable<string> args, Action<OperationProgress>? onProgress = null,
                                      Action<string>? onLog = null, CancellationToken ct = default)
    {
        var run = Run(cliExePath, args, onProgress, onLog, ct);
        if (run.Json == null) return DataOutcome.Fail(run.Error ?? "不明なエラー");
        try
        {
            return JsonSerializer.Deserialize<DataOutcome>(run.Json, JsonDefaults.Options) ?? DataOutcome.Fail("結果ファイルの解析に失敗しました。");
        }
        catch (JsonException ex)
        {
            return DataOutcome.Fail($"結果ファイルの解析に失敗しました: {ex.Message}");
        }
    }

    /// <summary>
    /// 子プロセス側: --cancel-file が作られたらキャンセルする CancellationToken を作る。
    /// 返したオブジェクトを Dispose すると監視を止める。
    /// </summary>
    public static CancelFileWatcher WatchCancelFile(string? cancelFile) => new(cancelFile);

    /// <summary>子プロセス側: 進捗とログを --progress-file に書く。</summary>
    public static ProgressFileWriter? CreateProgressWriter(string? progressFile) =>
        progressFile == null ? null : new ProgressFileWriter(progressFile);
}

/// <summary>キャンセル用ファイルの出現を監視する。</summary>
public sealed class CancelFileWatcher : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer? _timer;

    internal CancelFileWatcher(string? path)
    {
        if (path != null)
            _timer = new Timer(_ => { if (File.Exists(path)) _cts.Cancel(); }, null, 500, 500);
    }

    public CancellationToken Token => _cts.Token;

    public void Dispose()
    {
        _timer?.Dispose();
        _cts.Dispose();
    }
}

/// <summary>子プロセスが進捗（最新 1 件を上書き）とログ（追記）をファイルに書く。</summary>
public sealed class ProgressFileWriter : IProgress<OperationProgress>
{
    private readonly string _path;
    private readonly object _gate = new();

    internal ProgressFileWriter(string path) => _path = path;

    public void Report(OperationProgress value)
    {
        string json = JsonSerializer.Serialize(value, JsonDefaults.Options);
        lock (_gate)
        {
            try
            {
                // 書きかけを読まれないよう一時ファイルから置き換える
                File.WriteAllText(_path + ".tmp", json, new UTF8Encoding(false));
                File.Move(_path + ".tmp", _path, overwrite: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Log(string line)
    {
        lock (_gate)
        {
            try { File.AppendAllText(_path + ".log", line.Replace('\n', ' ') + "\n", new UTF8Encoding(false)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

internal sealed class ProgressFileReader
{
    private readonly string _path;
    private string? _lastProgress;
    private long _logPosition;

    public ProgressFileReader(string path) => _path = path;

    public void Poll(Action<OperationProgress>? onProgress, Action<string>? onLog)
    {
        if (onLog != null) ReadNewLogLines(onLog);
        if (onProgress == null) return;
        try
        {
            if (!File.Exists(_path)) return;
            string json = File.ReadAllText(_path);
            if (json == _lastProgress) return;
            _lastProgress = json;
            if (JsonSerializer.Deserialize<OperationProgress>(json, JsonDefaults.Options) is { } p) onProgress(p);
        }
        catch (IOException) { }
        catch (JsonException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ReadNewLogLines(Action<string> onLog)
    {
        try
        {
            string logPath = _path + ".log";
            if (!File.Exists(logPath)) return;
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length <= _logPosition) return;
            fs.Position = _logPosition;
            var bytes = new byte[fs.Length - _logPosition];
            fs.ReadExactly(bytes);
            int lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
            if (lastNewline < 0) return; // 行の途中まで書かれている
            _logPosition += lastNewline + 1;
            foreach (var line in Encoding.UTF8.GetString(bytes, 0, lastNewline).Split('\n')) onLog(line);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
