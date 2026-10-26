using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Fat32Fmt.Cli;
using Fat32Fmt.Core;
using Fat32Fmt.Core.Fat32;
using Fat32Fmt.Core.IO;
using Fat32Fmt.Core.Recovery;
using Fat32Fmt.Core.Windows;
using Xunit;

namespace Fat32Fmt.Tests;

/// <summary>脆弱性検証で見つかった問題（v1.0.1 で修正）の回帰テスト。</summary>
public class SecurityTests : IDisposable
{
    private const long MiB = 1L << 20;

    private readonly string _tmp = Directory.CreateTempSubdirectory("fat32fmt-sec-").FullName;
    private readonly List<string> _junctions = new();

    public void Dispose()
    {
        // ジャンクションはリンクだけを消す（再帰削除するとリンク先の中身まで消えてしまう）
        foreach (var j in _junctions)
        {
            try { Directory.Delete(j); } catch { }
        }

        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    // ---- カード由来の文字列の無害化 ----------------------------------------------

    [Theory]
    [InlineData("evil\u001b]0;HACKED\u0007.txt", "evil_]0;HACKED_.txt")] // 端末のタイトル書き換え (OSC)
    [InlineData("photo\u202Egpj.exe", "photo_gpj.exe")]                 // RLO で .jpg に見せかける
    [InlineData("a\u2066b\u2069c\u200Fd", "a_b_c_d")]                    // 双方向分離・RLM
    [InlineData("tab\there\nnew", "tab_here_new")]
    [InlineData("写真 2026-10-20.jpg", "写真 2026-10-20.jpg")]           // 通常の名前は変えない
    public void TextSanitizer_RemovesControlAndBidiCharacters(string input, string expected)
    {
        Assert.Equal(expected, TextSanitizer.Clean(input));
    }

    [Theory]
    [InlineData("CON", true)]
    [InlineData("con.txt", true)]
    [InlineData("NUL.tar.gz", true)]
    [InlineData("COM1.jpg", true)]
    [InlineData("LPT9", true)]
    [InlineData("CONOUT$", true)]
    [InlineData("CONSOLE.txt", false)]
    [InlineData("COM10", false)]
    [InlineData("photo.jpg", false)]
    public void ReservedDeviceNames_AreDetected(string name, bool reserved)
    {
        Assert.Equal(reserved, DeletedFileRecovery.IsReservedDeviceName(name));
    }

    [Fact]
    public void Recovery_HostileNames_StayInsideDestination_AndAreNeutralized()
    {
        using var dev = new StreamBlockDevice(new SparseMemoryStream(512 * MiB), 512);
        Fat32Formatter.Format(dev, Fat32LayoutCalculator.Calculate(dev.Length, 512, 4096), new Fat32FormatOptions());
        var w = new FatImageWriter(dev);
        string[] names =
        [
            @"..\..\..\pwned_traversal.txt",
            "../../pwned_slash.txt",
            @"C:\Windows\abs.txt",
            "ads.txt:hidden",
            "CON",
            "NUL.txt",
            "evil\u001b]0;HACKED\u0007name.txt",
            "photo\u202Egpj.exe",
        ];
        for (int i = 0; i < names.Length; i++)
        {
            var e = w.AddFile(w.Root, names[i], $"X{i:D7}TXT", [(byte)('0' + i)]);
            w.Delete(e);
        }

        var volume = Fat32Volume.Open(dev);
        var found = DeletedFileRecovery.Scan(volume);
        Assert.Equal(names.Length, found.Count);
        Assert.All(found, f => Assert.DoesNotContain(f.Name, c => TextSanitizer.IsUnsafe(c))); // 一覧・JSON に制御文字を出さない

        string dest = Path.Combine(_tmp, "out", "a", "b");
        var recovered = DeletedFileRecovery.Recover(volume, found, dest);
        Assert.Equal(names.Length, recovered.Count);
        string destFull = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        foreach (var r in recovered)
        {
            Assert.StartsWith(destFull, Path.GetFullPath(r.OutputPath), StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(r.OutputPath));
            string fileName = Path.GetFileName(r.OutputPath);
            Assert.DoesNotContain(fileName, c => TextSanitizer.IsUnsafe(c));
            Assert.False(DeletedFileRecovery.IsReservedDeviceName(fileName), fileName);
        }

        Assert.Contains(recovered, r => Path.GetFileName(r.OutputPath) == "_CON");
        Assert.Contains(recovered, r => Path.GetFileName(r.OutputPath) == "_NUL.txt");
        Assert.Contains(recovered, r => Path.GetFileName(r.OutputPath) == "photo_gpj.exe");
        Assert.Empty(Directory.GetFiles(Path.Combine(_tmp, "out"), "pwned*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Recovery_RefusesToWriteThroughJunctionOutsideDestination()
    {
        using var dev = new StreamBlockDevice(new SparseMemoryStream(512 * MiB), 512);
        Fat32Formatter.Format(dev, Fat32LayoutCalculator.Calculate(dev.Length, 512, 4096), new Fat32FormatOptions());
        var w = new FatImageWriter(dev);
        uint dir = w.AddDirectory(w.Root, "sub", "SUB        ", out var dirEntry);
        var file = w.AddFile(dir, "x.txt", "X       TXT", [1, 2, 3]);
        w.Delete(file);
        w.Delete(dirEntry);

        // 保存先の中の "sub" を、保存先の外のフォルダへのジャンクションにしておく
        string dest = Path.Combine(_tmp, "dest");
        string outside = Path.Combine(_tmp, "outside");
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(outside);
        MakeJunction(Path.Combine(dest, "sub"), outside);

        var volume = Fat32Volume.Open(dev);
        var found = DeletedFileRecovery.Scan(volume);
        // 新しい Windows はジャンクション経由の新規作成自体を拒否する (IOException)。拒否しない環境では
        // 作成直後の実体確認 (VolumeFormatException) で止まる。どちらでも外には何も残らないこと
        var ex = Record.Exception(() => DeletedFileRecovery.Recover(volume, found, dest));
        Assert.True(ex is VolumeFormatException or IOException, $"想定外: {ex}");
        Assert.Empty(Directory.GetFiles(outside));
    }

    [Fact]
    public void VerifyCreatedFile_DeletesAndThrows_WhenFileIsOutsideAllowedFolder()
    {
        string allowed = Directory.CreateDirectory(Path.Combine(_tmp, "allowed")).FullName;
        string elsewhere = Path.Combine(Directory.CreateDirectory(Path.Combine(_tmp, "elsewhere")).FullName, "x.txt");
        var fs = new FileStream(elsewhere, FileMode.CreateNew, FileAccess.Write);
        var ex = Assert.Throws<VolumeFormatException>(() =>
            SafePaths.VerifyCreatedFile(fs, elsewhere, SafePaths.ResolveFinalPath(allowed)));
        Assert.Contains("保存先フォルダの外", ex.Message);
        Assert.False(File.Exists(elsewhere)); // 作ったファイルは消されている

        string inside = Path.Combine(allowed, "ok.txt");
        using var ok = new FileStream(inside, FileMode.CreateNew, FileAccess.Write);
        SafePaths.VerifyCreatedFile(ok, inside, SafePaths.ResolveFinalPath(allowed)); // 中なら何もしない
    }

    // ---- ディレクトリの読み込み上限 ---------------------------------------------

    [Fact]
    public void ReadDirectory_StopsAtSpecLimit_OnHugeCraftedChain()
    {
        const int Clusters = 6000; // 512 B クラスタ × 6000 = 約 3 MiB（仕様上限 2 MiB を超える）
        using var dev = new StreamBlockDevice(new SparseMemoryStream(512 * MiB), 512);
        var layout = Fat32LayoutCalculator.Calculate(dev.Length, 512, 512);
        Fat32Formatter.Format(dev, layout, new Fat32FormatOptions());

        // ルート (2) → 3 → ... と長いチェーンを作り、全クラスタを使用中のエントリで埋める（終端の 0x00 を置かない）
        var fat = new byte[(Clusters + 3) * 4 + 511 & ~511];
        dev.Read(layout.FatStartSector * 512, fat);
        for (uint c = 2; c < 2 + Clusters; c++)
            BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan((int)c * 4), c == 1 + Clusters ? Fat32Formatter.EndOfChain : c + 1);
        dev.Write(layout.FatStartSector * 512, fat);

        var cluster = new byte[512];
        for (int i = 0; i < 16; i++)
        {
            "FILE    BIN"u8.CopyTo(cluster.AsSpan(i * 32));
            cluster[i * 32 + 11] = 0x20;
        }
        for (uint c = 2; c < 2 + Clusters; c++) dev.Write(layout.ClusterOffset(c), cluster);

        var entries = Fat32Volume.Open(dev).ReadDirectory(2);
        Assert.Equal(Fat32Volume.MaxDirectoryBytes / 32, entries.Count); // 65,536 エントリで打ち切る
    }

    // ---- 管理者権限で書く場所の制限 ---------------------------------------------

    [Fact]
    public void ImageOutput_RequiresImgExtension()
    {
        var ex = Assert.Throws<VolumeFormatException>(() => SafePaths.EnsureImageOutput(Path.Combine(_tmp, "service.exe")));
        Assert.Contains(".img", ex.Message);
        SafePaths.EnsureImageOutput(Path.Combine(_tmp, "card.img")); // 通常の場所は可
        SafePaths.EnsureImageOutput(Path.Combine(_tmp, "new", "folder", "card.IMG"));
    }

    [Fact]
    public void ProtectedFolders_AreRejected()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        Assert.Throws<VolumeFormatException>(() => SafePaths.EnsureImageOutput(Path.Combine(windows, "System32", "x.img")));
        Assert.Throws<VolumeFormatException>(() => SafePaths.EnsureImageOutput(Path.Combine(programFiles, "App", "x.img")));
        Assert.Throws<VolumeFormatException>(() => SafePaths.EnsureOutputDirectory(Path.Combine(programData, "x")));
        Assert.Throws<VolumeFormatException>(() => SafePaths.EnsureImageInput(Path.Combine(windows, "win.ini")));
        // 大文字小文字や「..」を混ぜても同じ
        Assert.Throws<VolumeFormatException>(() =>
            SafePaths.EnsureOutputDirectory(Path.Combine(windows.ToLowerInvariant(), "..", Path.GetFileName(windows).ToUpperInvariant(), "Temp")));
    }

    [Theory]
    [InlineData(@"\\localhost\C$\Windows\x.img")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\x.img")]
    [InlineData(@"\\.\PhysicalDrive0")]
    public void NetworkAndDevicePaths_AreRejected(string path)
    {
        Assert.Throws<VolumeFormatException>(() => SafePaths.EnsureAllowedLocation(path, "テスト"));
    }

    [Fact]
    public void JunctionIntoWindows_IsResolvedAndRejected()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string link = Path.Combine(_tmp, "innocent");
        MakeJunction(link, windows);

        Assert.Throws<VolumeFormatException>(() => SafePaths.EnsureImageOutput(Path.Combine(link, "System32", "evil.img")));
        Assert.Throws<VolumeFormatException>(() => SafePaths.EnsureOutputDirectory(Path.Combine(link, "Temp")));
    }

    // ---- システムディスクの保護（フェイルクローズ） --------------------------------

    [Fact]
    public void ProtectedDisks_FailClosed_WhenWindowsDiskUnknown()
    {
        var unknown = new ProtectedDisks.Snapshot([], []);
        Assert.Throws<VolumeFormatException>(() => ProtectedDisks.Check(3, unknown));
        Assert.True(ProtectedDisks.IsProtected(3, unknown));
    }

    [Fact]
    public void ProtectedDisks_ProtectsWindowsAndBootDisks()
    {
        var s = new ProtectedDisks.Snapshot([0], [1]);
        Assert.Throws<VolumeFormatException>(() => ProtectedDisks.Check(0, s));
        var boot = Assert.Throws<VolumeFormatException>(() => ProtectedDisks.Check(1, s));
        Assert.Contains("起動用", boot.Message);
        ProtectedDisks.Check(2, s); // それ以外は可
        Assert.False(ProtectedDisks.IsProtected(2, s));
    }

    [Fact]
    public void ProtectedDisks_OnThisMachine_IncludesSystemDrive()
    {
        var s = ProtectedDisks.Query();
        Assert.NotEmpty(s.WindowsDisks);
        foreach (int d in s.WindowsDisks) Assert.Throws<VolumeFormatException>(() => ProtectedDisks.EnsureNotProtected(d));
    }

    private void MakeJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0 && Directory.Exists(link), $"ジャンクションを作れません: {p.StandardError.ReadToEnd()}");
        _junctions.Add(link);
    }
}

/// <summary>MCP サーバーの安全装置。FixedDisksEnabled は静的状態なので、このクラスの中だけで切り替える。</summary>
public class McpSecurityTests
{
    private static JsonObject Call(string tool, JsonObject args) =>
        McpServer.CallTool(new JsonObject { ["name"] = tool, ["arguments"] = args });

    private static string Text(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();

    [Theory]
    [InlineData("plan_format")]
    [InlineData("format_drive")]
    [InlineData("wipe_disk")]
    [InlineData("scan_drive")]
    public void AllowFixed_FromAgent_IsRejected_UnlessServerStartedWithFlag(string tool)
    {
        bool saved = McpServer.FixedDisksEnabled;
        try
        {
            McpServer.FixedDisksEnabled = false;
            var args = new JsonObject { ["drive"] = "Z", ["allowFixed"] = true };
            args["confirm"] = tool switch { "format_drive" => "FORMAT Z", "wipe_disk" => "WIPE Z", _ => null };
            var r = Call(tool, args);
            Assert.True(r["isError"]!.GetValue<bool>());
            Assert.Contains("allowFixed is disabled", Text(r));
        }
        finally
        {
            McpServer.FixedDisksEnabled = saved;
        }
    }

    [Fact]
    public void ToolSchemas_HideAllowFixed_UnlessServerStartedWithFlag()
    {
        bool saved = McpServer.FixedDisksEnabled;
        try
        {
            McpServer.FixedDisksEnabled = false;
            Assert.All(McpServer.ListTools(), t => Assert.Null(t!["inputSchema"]!["properties"]!["allowFixed"]));

            McpServer.FixedDisksEnabled = true;
            Assert.Contains(McpServer.ListTools(), t => t!["inputSchema"]!["properties"]!["allowFixed"] != null);
        }
        finally
        {
            McpServer.FixedDisksEnabled = saved;
        }
    }

    [Fact]
    public void BackupDisk_RequiresConfirm()
    {
        var r = Call("backup_disk", new JsonObject { ["drive"] = "E", ["image"] = @"D:\backup\card.img" });
        Assert.True(r["isError"]!.GetValue<bool>());
        Assert.Contains("\"BACKUP E\"", Text(r));
    }

    [Fact]
    public void BackupDisk_SchemaListsConfirmAsRequired()
    {
        var backup = McpServer.ListTools().Single(t => t!["name"]!.GetValue<string>() == "backup_disk")!;
        var required = backup["inputSchema"]!["required"]!.AsArray().Select(n => n!.GetValue<string>());
        Assert.Contains("confirm", required);
    }
}
