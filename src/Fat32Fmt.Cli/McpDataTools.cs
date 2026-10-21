using System.Text.Json;
using System.Text.Json.Nodes;
using Fat32Fmt.Core.Windows;

namespace Fat32Fmt.Cli;

/// <summary>MCP のデータ系ツール: backup_disk / restore_image / wipe_disk / recover_deleted。CLI の DataCommands と同じ処理を呼ぶ。</summary>
internal static partial class McpServer
{
    private static readonly HashSet<string> DataToolNames = new() { "backup_disk", "restore_image", "wipe_disk", "recover_deleted" };

    private static JsonArray AddDataTools(JsonArray tools)
    {
        tools.Add(Tool("backup_disk",
            "Save the WHOLE card (physical disk: MBR + all partitions) to an image file, e.g. before wipe_disk or format_drive. " +
            "The image is written as an NTFS sparse file and its SHA-256 is stored next to it as <image>.sha256. Does not modify the card. " +
            "Requires administrator rights (UAC prompt when not elevated). Takes ~30 min for a 128GB card. カード全体を .img に保存します。",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("drive", "image"),
                ["properties"] = DataProps(
                    ("image", Prop("string", "Output image path on a DIFFERENT drive, e.g. \"D:\\\\backup\\\\card.img\".")),
                    ("overwrite", Prop("boolean", "Overwrite an existing image file. Default false."))),
            }));
        tools.Add(Tool("restore_image",
            "DESTRUCTIVE: write an image file back to the whole card, replacing everything on it, then read the card back and compare SHA-256. " +
            "If <image>.sha256 exists it also reports whether the image changed since backup. " +
            "Requires `confirm` to be exactly \"RESTORE <target>\" where target is the drive letter (\"RESTORE E\") or \"DISK<n>\" when `drive` was given as diskN. " +
            "Pass it only after the human user approved. UAC prompt when not elevated. イメージをカードへ書き戻して照合します。",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("image", "drive", "confirm"),
                ["properties"] = DataProps(
                    ("image", Prop("string", "Image file to write.")),
                    ("confirm", Prop("string", "Exactly \"RESTORE <target>\", e.g. \"RESTORE E\" or \"RESTORE DISK2\"."))),
            }));
        tools.Add(Tool("wipe_disk",
            "DESTRUCTIVE: securely erase the whole card by overwriting every sector `passes` times with zeros (default) or random data. " +
            "The partition table is erased too. Normally set format=true (recommended) to recreate an MBR + FAT32 partition afterwards; " +
            "without it Windows shows the whole unpartitioned card as one unformatted volume (superfloppy), which is expected. " +
            "Note: flash wear levelling may keep old data in spare blocks. " +
            "Requires `confirm` to be exactly \"WIPE <target>\" (\"WIPE E\" or \"WIPE DISK2\"). UAC prompt when not elevated. カード全体を上書き消去します。",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("drive", "confirm"),
                ["properties"] = DataProps(
                    ("passes", Prop("integer", "Number of overwrite passes, 1-35. Default 1.")),
                    ("random", Prop("boolean", "Overwrite with random data instead of zeros. Default false.")),
                    ("verify", Prop("boolean", "Read back the last pass and check it. Default false.")),
                    ("format", Prop("boolean", "Create an MBR + FAT32 partition after wiping so the card is usable again (recommended). Default false.")),
                    ("label", Prop("string", "Volume label when format=true.")),
                    ("confirm", Prop("string", "Exactly \"WIPE <target>\", e.g. \"WIPE E\" or \"WIPE DISK2\"."))),
            }));
        tools.Add(Tool("recover_deleted",
            "Find deleted files on a FAT32 drive (or in a backup image) and copy them to a folder on a DIFFERENT drive. " +
            "Read-only on the source. Long file names are restored; for 8.3-only names the first character is unknown and shown as '_'. " +
            "Files are assumed to be stored in contiguous clusters, so fragmented files may be corrupted after the first fragment. " +
            "Each file has a chance: Good / Partial (some clusters reused) / Overwritten / Empty. listOnly=true just lists. " +
            "Reading a drive needs administrator rights (UAC); reading an image file does not. 削除ファイルを復旧します。",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("source"),
                ["properties"] = new JsonObject
                {
                    ["source"] = Prop("string", "Drive letter (\"E\") or path to an image file made by backup_disk."),
                    ["destination"] = Prop("string", "Folder to save recovered files to, on another drive. Required unless listOnly."),
                    ["listOnly"] = Prop("boolean", "Only list deleted files. Default false."),
                    ["includeOverwritten"] = Prop("boolean", "Also extract files whose first cluster was reused (almost certainly garbage). Default false."),
                    ["match"] = Prop("string", "Wildcard on the file name, e.g. \"*.jpg\"."),
                },
            }));
        return tools;
    }

    private static JsonObject DataProps(params (string Name, JsonObject Schema)[] extra)
    {
        var o = new JsonObject
        {
            ["drive"] = Prop("string", "Card to operate on: drive letter (\"E\" / \"E:\") or physical disk (\"disk2\") when the card has no drive letter."),
            ["allowFixed"] = Prop("boolean", "Allow a disk that Windows reports as fixed (not removable). Dangerous; default false."),
        };
        foreach (var (name, schema) in extra) o[name] = schema;
        return o;
    }

    private static JsonObject CallDataTool(string name, JsonObject args)
    {
        string? Str(string key) => args[key]?.GetValue<string>();
        bool Flag(string key) => args[key]?.GetValue<bool>() ?? false;
        string Required(string key) => Str(key) is { Length: > 0 } s ? s : throw new UsageException($"{key} を指定してください。");

        var cli = new List<string>();
        switch (name)
        {
            case "backup_disk":
                cli.AddRange(["backup", Required("drive"), Required("image")]);
                if (Flag("overwrite")) cli.Add("--overwrite");
                break;
            case "restore_image":
            {
                string drive = Required("drive");
                if (ConfirmError("RESTORE", drive, Str("confirm")) is { } err) return ToolError(err);
                cli.AddRange(["restore", Required("image"), drive]);
                break;
            }
            case "wipe_disk":
            {
                string drive = Required("drive");
                if (ConfirmError("WIPE", drive, Str("confirm")) is { } err) return ToolError(err);
                cli.AddRange(["wipe", drive, "--passes", (args["passes"]?.GetValue<int>() ?? 1).ToString()]);
                if (Flag("random")) cli.Add("--random");
                if (Flag("verify")) cli.Add("--verify");
                if (Flag("format")) cli.Add("--format");
                if (Str("label") is { } label) { cli.Add("--label"); cli.Add(label); }
                break;
            }
            case "recover_deleted":
                cli.AddRange(["recover", Required("source")]);
                if (Flag("listOnly")) cli.Add("--list");
                if (Str("destination") is { } dest) { cli.Add("--to"); cli.Add(dest); }
                if (Flag("includeOverwritten")) cli.Add("--all");
                if (Str("match") is { } m) { cli.Add("--match"); cli.Add(m); }
                break;
        }
        if (Flag("allowFixed")) cli.Add("--allow-fixed");
        cli.Add("--yes");

        DataOutcome outcome;
        try
        {
            var o = DataOptions.Parse(cli.ToArray());
            DataCommands.Preflight(o);
            if (o.NeedsElevation && !VolumeFormatService.IsElevated())
            {
                string exe = Environment.ProcessPath ?? throw new VolumeFormatException("実行ファイルのパスを取得できません。");
                outcome = ElevatedCli.RunData(exe, DataCommands.ChildArgs(o));
            }
            else outcome = DataCommands.Execute(o, null, _ => { }, CancellationToken.None);
        }
        catch (UsageException ex) { return ToolError(ex.Message); }
        catch (IOException ex) { return ToolError(ex.Message); }

        var json = JsonSerializer.Serialize(outcome, JsonDefaults.Options);
        return outcome.Success ? ToolText(json) : ToolError(json);
    }

    /// <summary>confirm が「動詞 対象」と完全一致しなければエラーメッセージを返す。</summary>
    private static string? ConfirmError(string verb, string drive, string? confirm)
    {
        string target = DiskTargets.ParseDiskNumber(drive) is int n ? $"DISK{n}"
                      : DiskTargets.ParseDriveLetter(drive) is char c ? c.ToString()
                      : drive.Trim().ToUpperInvariant();
        string expected = $"{verb} {target}";
        return string.Equals(confirm?.Trim(), expected, StringComparison.Ordinal) ? null
            : $"confirm must be exactly \"{expected}\". Ask the human user to approve this destructive operation on {drive} before calling this tool.";
    }
}
