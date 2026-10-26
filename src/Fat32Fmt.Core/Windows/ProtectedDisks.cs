using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Fat32Fmt.Core.Windows;

/// <summary>
/// 書き込み対象にしてはいけない物理ディスク（Windows のあるディスクと、起動用のシステムパーティションがあるディスク）。
/// 判定に失敗したときは「守れない」ので、対象にさせない（フェイルクローズ）。
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProtectedDisks
{
    /// <summary>保護対象のディスクと、判定できたかどうか。</summary>
    public sealed record Snapshot(IReadOnlyCollection<int> WindowsDisks, IReadOnlyCollection<int> BootDisks);

    /// <summary>現在のシステムの保護対象ディスクを調べる。</summary>
    public static Snapshot Query() => new(WindowsDiskNumbers(), BootDiskNumbers());

    /// <summary>ディスクが保護対象なら例外を投げる。Windows のあるディスクを特定できない場合も例外（フェイルクローズ）。</summary>
    public static void EnsureNotProtected(int diskNumber) => Check(diskNumber, Query());

    /// <summary>判定ロジック本体（テスト用に分離）。</summary>
    public static void Check(int diskNumber, Snapshot s)
    {
        if (s.WindowsDisks.Count == 0)
            throw new VolumeFormatException(
                "Windows がインストールされている物理ディスクを特定できなかったため、安全のため操作を中止しました。" +
                "管理者として実行しているか、ディスクの状態を確認してください。");
        if (s.WindowsDisks.Contains(diskNumber))
            throw new VolumeFormatException($"Disk {diskNumber} は Windows がインストールされている物理ディスクのため対象にできません。");
        if (s.BootDisks.Contains(diskNumber))
            throw new VolumeFormatException($"Disk {diskNumber} には Windows の起動用パーティション（システムパーティション）があるため対象にできません。");
    }

    /// <summary>ディスクが保護対象か（一覧表示用。例外は投げず、判定できないときは true）。</summary>
    public static bool IsProtected(int diskNumber, Snapshot s) =>
        s.WindowsDisks.Count == 0 || s.WindowsDisks.Contains(diskNumber) || s.BootDisks.Contains(diskNumber);

    /// <summary>Windows フォルダ（通常 C:\Windows）があるボリュームの物理ディスク。</summary>
    private static List<int> WindowsDiskNumbers()
    {
        var disks = new List<int>();
        foreach (string dir in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
        {
            if (dir.Length < 2 || dir[1] != ':') continue;
            disks.AddRange(DiskNative.DisksOfVolume($@"\\.\{char.ToUpperInvariant(dir[0])}:"));
        }
        return disks.Distinct().ToList();
    }

    /// <summary>
    /// 起動用のシステムパーティション（EFI システムパーティションや MBR の「システムで予約済み」）がある物理ディスク。
    /// Windows はその場所を HKLM\SYSTEM\Setup の SystemPartition（例: \Device\HarddiskVolume1）に記録している。
    /// </summary>
    private static List<int> BootDiskNumbers()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\Setup");
            if (key?.GetValue("SystemPartition") is not string device || !device.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
                return new();
            return DiskNative.DisksOfVolume(@"\\?\GLOBALROOT" + device);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new();
        }
    }
}
