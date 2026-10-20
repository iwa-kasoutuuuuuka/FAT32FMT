using System.Runtime.Versioning;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fat32Fmt.Core.Windows;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 日本語をエスケープせずそのまま出す
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>フォーマット実行の結果（JSON で CLI / MCP / 昇格プロセス間を受け渡す）。</summary>
public sealed class FormatOutcome
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public FormatResult? Result { get; set; }
    public List<string> Log { get; set; } = new();

    public static FormatOutcome Fail(string error, IEnumerable<string>? log = null) =>
        new() { Success = false, Error = error, Log = log?.ToList() ?? new() };
}

/// <summary>
/// 管理者権限の無いプロセス（MCP サーバー、通常の CLI 呼び出し）から、
/// UAC 昇格した CLI を起動してフォーマットを実行し、結果ファイル経由で受け取る。
/// </summary>
[SupportedOSPlatform("windows")]
public static class ElevatedFormat
{
    public const int ExitOk = 0;
    public const int ExitError = 1;
    public const int ExitUsage = 2;
    public const int ExitCancelled = 3;

    public static List<string> BuildCliArgs(FormatRequest req, string? resultFile)
    {
        var a = new List<string> { "format", $"{char.ToUpperInvariant(req.DriveLetter)}:", "--yes" };
        if (resultFile != null) { a.Add("--result-file"); a.Add(resultFile); }
        if (req.ClusterBytes != 0) { a.Add("--cluster"); a.Add(req.ClusterBytes.ToString()); }
        if (!string.IsNullOrEmpty(req.Label)) { a.Add("--label"); a.Add(req.Label); }
        if (req.FullFormat) a.Add("--full");
        if (!req.FixPartitionType) a.Add("--no-ptype");
        if (req.AllowFixedDisk) a.Add("--allow-fixed");
        if (req.AlignmentBytes is long al) { a.Add("--align"); a.Add(al.ToString()); }
        return a;
    }

    /// <summary>CLI 実行ファイルを UAC 昇格して起動し、完了まで待って結果を返す。</summary>
    public static FormatOutcome RunViaElevatedCli(string cliExePath, FormatRequest req, TimeSpan? timeout = null)
    {
        // 起動・待機・結果ファイルの受け渡しは backup / restore / wipe / recover と共通の ElevatedCli で行う
        var run = ElevatedCli.Run(cliExePath, BuildCliArgs(req, null), timeout: timeout ?? TimeSpan.FromHours(12));
        if (run.Json == null)
            return FormatOutcome.Fail(run.Cancelled
                ? "UAC の昇格がユーザーによってキャンセルされました。フォーマットは実行されていません。"
                : run.Error ?? "不明なエラー");
        try
        {
            return JsonSerializer.Deserialize<FormatOutcome>(run.Json, JsonDefaults.Options)
                   ?? FormatOutcome.Fail("結果ファイルの解析に失敗しました。");
        }
        catch (JsonException ex)
        {
            return FormatOutcome.Fail($"結果ファイルの解析に失敗しました: {ex.Message}");
        }
    }
}
