using System.Text.Json;
using System.Text.Json.Nodes;
using Fat32Fmt.Core.Windows;

namespace Fat32Fmt.Cli;

/// <summary>MCP の検査ツール: test_capacity / scan_drive / benchmark_drive。</summary>
internal static partial class McpServer
{
    private static readonly HashSet<string> DiagnosticToolNames = new() { "test_capacity", "scan_drive", "benchmark_drive" };

    private static JsonArray AddDiagnosticTools(JsonArray tools)
    {
        tools.Add(Tool("test_capacity",
            "DESTRUCTIVE: fake-capacity check like H2testw/F3. Writes position-dependent pseudo-random patterns and reads them back to find the real capacity " +
            "and corrupted ranges. mode \"quick\" samples ~500 blocks on a power-of-two grid (minutes); \"full\" writes and verifies the whole partition (hours for large cards). " +
            "Erases all data on the drive (format it afterwards). Requires `confirm` to be exactly \"ERASE <letter>\" (e.g. \"ERASE E\"); pass it only after the human user approved. " +
            "Triggers a UAC prompt when not elevated. 容量偽装（フェイクカード）チェック。全データ消去。",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("drive", "confirm"),
                ["properties"] = DiagnosticProps(
                    ("mode", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("quick", "full"), ["description"] = "quick (sampled, default) or full (whole partition)." }),
                    ("confirm", Prop("string", "Must be exactly \"ERASE <letter>\", e.g. \"ERASE E\"."))),
            }));
        tools.Add(Tool("scan_drive",
            "Bad-sector scan. Default (write=false) is NON-DESTRUCTIVE: reads the whole card (physical disk) and reports unreadable sectors and slow areas. " +
            "write=true is DESTRUCTIVE: writes a pattern to every sector of the partition and verifies it; then `confirm` must be \"ERASE <letter>\". " +
            "Requires administrator rights (UAC prompt when not elevated). Takes a long time on large cards. 不良セクタスキャン。",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("drive"),
                ["properties"] = DiagnosticProps(
                    ("write", Prop("boolean", "Destructive write+verify scan instead of read-only. Default false.")),
                    ("confirm", Prop("string", "Required only when write=true: exactly \"ERASE <letter>\"."))),
            }));
        tools.Add(Tool("benchmark_drive",
            "Measure real sequential and random 4K speed and compare with SD speed classes (Class 10 / U1 / U3 / V30 / V60 / V90 / A1 / A2). " +
            "Default is read-only and NON-DESTRUCTIVE (speed classes need write speed, so they are reported as unmeasured). " +
            "write=true also measures writes and is DESTRUCTIVE (overwrites the first ~1 GiB); then `confirm` must be \"ERASE <letter>\". " +
            "Requires administrator rights (UAC prompt when not elevated). 実測の読み書き速度テスト。",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("drive"),
                ["properties"] = DiagnosticProps(
                    ("write", Prop("boolean", "Also measure write speed (destructive). Default false.")),
                    ("confirm", Prop("string", "Required only when write=true: exactly \"ERASE <letter>\"."))),
            }));
        return tools;
    }

    private static JsonObject DiagnosticProps(params (string Name, JsonObject Schema)[] extra)
    {
        var o = new JsonObject
        {
            ["drive"] = Prop("string", "Drive letter, e.g. \"E\" or \"E:\"."),
        };
        AddAllowFixedProp(o);
        foreach (var (name, schema) in extra) o[name] = schema;
        return o;
    }

    private static JsonObject CallDiagnosticTool(string name, JsonObject args)
    {
        string drive = args["drive"]?.GetValue<string>() ?? "";
        if (DiskTargets.ParseDriveLetter(drive) is not char letter)
            return ToolError($"drive には \"E\" のようなドライブ文字を指定してください（指定値: \"{drive}\"）。");
        bool write = args["write"]?.GetValue<bool>() ?? false;
        var kind = name switch
        {
            "test_capacity" => (args["mode"]?.GetValue<string>() ?? "quick") switch
            {
                "quick" => DiagnosticKind.CapacityQuick,
                "full" => DiagnosticKind.CapacityFull,
                var m => throw new ArgumentException($"mode は \"quick\" か \"full\" を指定してください（指定値: \"{m}\"）。"),
            },
            "scan_drive" => write ? DiagnosticKind.ScanWrite : DiagnosticKind.ScanRead,
            _ => write ? DiagnosticKind.BenchWrite : DiagnosticKind.Bench,
        };
        var req = new DiagnosticRequest { DriveLetter = letter, Kind = kind, AllowFixedDisk = AllowFixedFrom(args) };

        if (req.IsDestructive)
        {
            string confirm = args["confirm"]?.GetValue<string>()?.Trim() ?? "";
            if (!string.Equals(confirm, req.ConfirmPhrase, StringComparison.Ordinal))
                return ToolError($"{DiagnosticRequest.KindName(kind)} erases all data on {letter}:. confirm must be exactly \"{req.ConfirmPhrase}\". " +
                                 $"Ask the human user to approve erasing drive {letter}: before calling this tool.");
        }

        DiagnosticsService.ValidateTarget(req); // 事前検証（権限不要）
        DiagnosticOutcome outcome;
        if (VolumeFormatService.IsElevated())
        {
            outcome = DiagnosticCommands.Execute(req, null, _ => { }, CancellationToken.None);
        }
        else
        {
            string exe = Environment.ProcessPath ?? throw new VolumeFormatException("実行ファイルのパスを取得できません。");
            outcome = DiagnosticsService.RunViaElevatedCli(exe, req);
        }

        string text = outcome.Describe() + "\n\n" + JsonSerializer.Serialize(outcome, JsonDefaults.Options);
        return outcome.Success ? ToolText(text) : ToolError(text);
    }
}
