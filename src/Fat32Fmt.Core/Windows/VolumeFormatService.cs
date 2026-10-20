using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Fat32Fmt.Core.Diagnostics;
using Fat32Fmt.Core.Fat32;

namespace Fat32Fmt.Core.Windows;

/// <summary>ドライブのフォーマット処理（サービス層）で発生する、利用者に見せられるエラー。</summary>
public sealed class VolumeFormatException : Exception
{
    public VolumeFormatException(string message) : base(message) { }
    public VolumeFormatException(string message, Exception inner) : base(message, inner) { }
}

public sealed class FormatRequest
{
    public required char DriveLetter { get; init; }
    /// <summary>クラスタサイズ（バイト）。0 で自動（Windows 既定表。32GB 超は 32 KB）。</summary>
    public int ClusterBytes { get; init; }
    /// <summary>ボリュームラベル（11 バイト以内。日本語は CP932 で 2 バイト/文字）。</summary>
    public string? Label { get; init; }
    /// <summary>true で全データ領域を書き込み検査する通常フォーマット。false でクイック。</summary>
    public bool FullFormat { get; init; }
    /// <summary>MBR のパーティション種別を 0x0C に修正する。</summary>
    public bool FixPartitionType { get; init; } = true;
    /// <summary>固定ディスク扱いのドライブも許可する（危険）。</summary>
    public bool AllowFixedDisk { get; init; }
    /// <summary>データ領域の整列境界（バイト）。null で SD 規格の境界単位 (BU) を容量から自動選択。0 で整列しない。</summary>
    public long? AlignmentBytes { get; init; }
}

/// <summary>書き込み前に分かる、フォーマットの計画。</summary>
public sealed record FormatPlan(DriveEntry Drive, Fat32Layout Layout, int ClusterBytes, long AlignmentBytes,
                                IReadOnlyList<int> ValidClusterSizes)
{
    public string Describe() => LayoutDescriber.Describe(Layout, AlignmentBytes);
}

public sealed record FormatResult(char DriveLetter, Fat32Layout Layout, uint VolumeId, IReadOnlyList<uint> BadClusters,
                                  bool PartitionTypeChanged, string? MountedFileSystem, string? MountedLabel, TimeSpan Elapsed);

/// <summary>Windows 上でドライブ文字を指定して FAT32 フォーマットを実行するサービス層。GUI / CLI / MCP が共用する。</summary>
[SupportedOSPlatform("windows")]
public static class VolumeFormatService
{
    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>対象ドライブを検証し、その情報を返す。問題があれば例外。</summary>
    /// <param name="allowWriteProtected">読み取りしかしない操作（非破壊の検査など）では書き込み禁止でも許可する。</param>
    public static DriveEntry ValidateTarget(FormatRequest req, bool allowWriteProtected = false)
    {
        char letter = char.ToUpperInvariant(req.DriveLetter);
        if (letter < 'A' || letter > 'Z') throw new VolumeFormatException($"ドライブ文字が不正です: '{req.DriveLetter}'");

        var entry = DriveEnumerator.Describe(letter);
        if (entry.Kind == DriveKind.NoRootDir) throw new VolumeFormatException($"ドライブ {letter}: は存在しません。");
        if (entry.IsSystemDrive) throw new VolumeFormatException($"ドライブ {letter}: はシステムドライブのためフォーマットできません。");
        if (entry.IsOnSystemDisk)
            throw new VolumeFormatException($"ドライブ {letter}: はシステムドライブと同じ物理ディスク (Disk {entry.DiskNumber}) 上にあるためフォーマットできません。");
        if (entry.Kind is DriveKind.Cdrom or DriveKind.Remote or DriveKind.RamDisk)
            throw new VolumeFormatException($"ドライブ {letter}: ({DriveEntry.KindName(entry.Kind)}) はフォーマット対象にできません。");
        if (entry.Kind == DriveKind.Fixed && !req.AllowFixedDisk)
            throw new VolumeFormatException($"ドライブ {letter}: はリムーバブルではなく固定ディスクとして認識されています。本当にフォーマットする場合は「固定ディスクを許可」を指定してください。");
        if (entry.Kind == DriveKind.Unknown && !req.AllowFixedDisk)
            throw new VolumeFormatException($"ドライブ {letter}: の種類を判別できません。");
        if (entry.SizeBytes <= 0) throw new VolumeFormatException($"ドライブ {letter}: のサイズを取得できません。メディアが挿入されているか確認してください。");
        if (entry.IsWriteProtected && !allowWriteProtected) throw new VolumeFormatException(WindowsVolume.WriteProtectedMessage(letter));

        string cwd = Environment.CurrentDirectory;
        if (cwd.Length >= 2 && char.ToUpperInvariant(cwd[0]) == letter && cwd[1] == ':')
            throw new VolumeFormatException($"カレントディレクトリが {letter}: 上にあります。別のドライブから実行してください。");
        return entry;
    }

    /// <summary>書き込みは行わず、レイアウトだけ計算する（管理者権限不要）。</summary>
    public static FormatPlan Plan(FormatRequest req)
    {
        var entry = ValidateTarget(req);
        return PlanFor(req, entry, entry.SizeBytes, entry.SectorSize, entry.PartitionOffset);
    }

    private static FormatPlan PlanFor(FormatRequest req, DriveEntry entry, long volumeBytes, int sectorSize, long partitionOffset)
    {
        if (!VolumeLabel.TryValidate(req.Label, out string? labelError))
            throw new VolumeFormatException(labelError!);

        long alignment = req.AlignmentBytes ?? SdCardGeometry.BoundaryUnitBytes(partitionOffset + volumeBytes);
        if (alignment < 0 || alignment % sectorSize != 0)
            throw new VolumeFormatException($"整列境界はセクタサイズ ({sectorSize}) の倍数で指定してください。");
        if (partitionOffset % sectorSize != 0)
            throw new VolumeFormatException($"パーティション開始位置 ({partitionOffset}) がセクタ境界にありません。");

        try
        {
            int cluster = req.ClusterBytes == 0 ? ClusterSizePolicy.Default(volumeBytes, sectorSize) : req.ClusterBytes;
            var layout = Fat32LayoutCalculator.Calculate(volumeBytes, sectorSize, cluster, partitionOffset, alignment);
            return new FormatPlan(entry, layout, cluster, alignment, ClusterSizePolicy.ValidSizes(volumeBytes, sectorSize));
        }
        catch (Fat32LayoutException ex)
        {
            throw new VolumeFormatException(ex.Message, ex);
        }
    }

    /// <summary>フォーマットを実行する。管理者権限が必要。</summary>
    public static FormatResult Format(FormatRequest req, IProgress<OperationProgress>? progress = null,
                                      Action<string>? log = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (!IsElevated()) throw new VolumeFormatException("フォーマットには管理者権限が必要です。管理者として実行してください。");

        var entry = ValidateTarget(req);
        PlanFor(req, entry, entry.SizeBytes, entry.SectorSize, entry.PartitionOffset); // 事前検証（ラベル・クラスタサイズ）
        char letter = entry.Letter;

        log?.Invoke($"対象: {entry.Display}");
        log?.Invoke($"ボリューム {letter}: をロックしてマウント解除します...");

        Fat32FormatResult fmt;
        FormatPlan plan;
        bool ptypeChanged = false;
        var vol = WindowsVolume.OpenForFormat(letter, log);
        try
        {
            var g = vol.Geometry;
            log?.Invoke($"ディスク {g.DiskNumber} パーティション {g.PartitionNumber}: オフセット {g.PartitionOffset:N0} B, サイズ {g.Length:N0} B, セクタ {g.SectorSize} B, {g.Style}");

            // ロック後に取得した正確なジオメトリで最終的なレイアウトを決める
            plan = PlanFor(req, entry, g.Length, g.SectorSize, g.PartitionOffset);
            log?.Invoke(plan.Describe());

            var opt = new Fat32FormatOptions { Label = req.Label, FullFormat = req.FullFormat };
            fmt = Fat32Formatter.Format(vol, plan.Layout, opt, progress, ct);
            log?.Invoke(fmt.BadClusters.Count == 0
                ? "FAT32 の書き込みが完了しました。"
                : $"FAT32 の書き込みが完了しました（不良クラスタ {fmt.BadClusters.Count} 個を FAT に登録）。");

            // ロックしたまま書いた構造を読み戻して検証する
            var report = Fat32Verifier.Verify(vol, ct);
            foreach (var w in report.Warnings) log?.Invoke($"検証の警告: {w}");
            if (!report.Ok)
                throw new VolumeFormatException("フォーマット後の読み戻し検証に失敗しました: " + string.Join(" / ", report.Errors));
            log?.Invoke($"読み戻し検証 OK（空きクラスタ {report.FreeClusters:N0} 個）。");

            if (req.FixPartitionType)
            {
                if (g.Style == PartitionStyle.Mbr && g.DiskNumber >= 0)
                    ptypeChanged = MbrTools.EnsureFat32PartitionType(g.DiskNumber, g.PartitionOffset, g.SectorSize, log);
                else
                    log?.Invoke("MBR ディスクではないため、パーティション種別の修正はスキップします。");
            }
        }
        finally
        {
            vol.Dispose();
        }

        log?.Invoke("ボリュームを再マウントしています...");
        var (fs, label) = WaitForMount(letter, TimeSpan.FromSeconds(10));
        log?.Invoke(fs != null
            ? $"再マウント確認: {fs} ラベル \"{label}\""
            : "再マウントの確認がタイムアウトしました（カードの抜き差しで認識される場合があります）。");

        return new FormatResult(letter, fmt.Layout, fmt.VolumeId, fmt.BadClusters, ptypeChanged, fs, label, sw.Elapsed);
    }

    private static (string? FileSystem, string? Label) WaitForMount(char letter, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string root = $"{letter}:\\";
        while (true)
        {
            var lbl = new StringBuilder(261);
            var fs = new StringBuilder(261);
            if (Native.GetVolumeInformationW(root, lbl, 261, out _, out _, out _, fs, 261))
                return (fs.ToString(), lbl.ToString());
            if (DateTime.UtcNow >= deadline) return (null, null);
            Thread.Sleep(300);
        }
    }
}

/// <summary>レイアウトの人間向け説明（GUI / CLI 共用）。</summary>
public static class LayoutDescriber
{
    public static string Describe(Fat32Layout l, long alignmentBytes)
    {
        var ci = CultureInfo.InvariantCulture;
        string align = alignmentBytes > 0
            ? $"{SizeFormat.Binary(alignmentBytes)} 境界に{(l.AbsoluteDataStartOffset % alignmentBytes == 0 ? "整列" : "未整列")}"
            : "整列なし";
        return string.Join(Environment.NewLine, new[]
        {
            $"ボリュームサイズ   : {SizeFormat.Both(l.VolumeBytes)} ({l.TotalSectors.ToString("N0", ci)} セクタ × {l.BytesPerSector} B)",
            $"クラスタサイズ     : {SizeFormat.Cluster(l.BytesPerCluster)} ({l.SectorsPerCluster} セクタ)",
            $"クラスタ数         : {l.ClusterCount.ToString("N0", ci)}",
            $"予約セクタ         : {l.ReservedSectors.ToString("N0", ci)}",
            $"FAT サイズ         : {l.FatSizeSectors.ToString("N0", ci)} セクタ ({SizeFormat.Binary((long)l.FatSizeSectors * l.BytesPerSector)}) × {l.NumberOfFats}",
            $"データ領域開始     : セクタ {l.DataStartSector.ToString("N0", ci)}（ディスク先頭から {SizeFormat.Binary(l.AbsoluteDataStartOffset)}、{align}）",
            $"利用可能容量(概算) : {SizeFormat.Both(l.DataBytes)}",
        });
    }
}
