using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Fat32Fmt.Core.Windows;

/// <summary>
/// 管理者権限で読み書きするファイルの場所を検査する。
/// backup / restore / recover は UAC 昇格したプロセスで動くため、場所を制限しないと
/// 「カードの中身を C:\Windows や Program Files のプログラムとして書き込む」ことができてしまう
/// （AI エージェントが指示文にだまされた場合など）。
/// ジャンクションやシンボリックリンクを経由した指定も、実際の場所に解決してから判定する。
/// </summary>
[SupportedOSPlatform("windows")]
public static class SafePaths
{
    /// <summary>バックアップイメージに許す拡張子。実行ファイルや DLL として読み込まれる名前を作らせない。</summary>
    public const string ImageExtension = ".img";

    private const uint FILE_SHARE_ALL = 0x7; // READ | WRITE | DELETE
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000; // ディレクトリも開けるようにする
    private const uint VOLUME_NAME_DOS = 0;

    /// <summary>書き込み・読み出しを禁止する場所（Windows、Program Files、ProgramData）。</summary>
    public static IReadOnlyList<string> ProtectedRoots { get; } = BuildProtectedRoots();

    /// <summary>backup の出力先として使えるか検査する。問題があれば例外。</summary>
    public static void EnsureImageOutput(string path)
    {
        if (!path.EndsWith(ImageExtension, StringComparison.OrdinalIgnoreCase))
            throw new VolumeFormatException($"イメージのファイル名は拡張子 {ImageExtension} にしてください（指定値: {path}）。");
        EnsureAllowedLocation(path, "イメージの保存先");
    }

    /// <summary>restore で読み込むイメージとして使えるか検査する。</summary>
    public static void EnsureImageInput(string path) => EnsureAllowedLocation(path, "書き戻すイメージ");

    /// <summary>recover の保存先フォルダとして使えるか検査する。</summary>
    public static void EnsureOutputDirectory(string path) => EnsureAllowedLocation(path, "復旧ファイルの保存先");

    /// <summary>ネットワークパス・デバイスパス・保護された場所でないことを確認する。</summary>
    public static void EnsureAllowedLocation(string path, string what)
    {
        string resolved = ResolveFinalPath(path, what);
        if (ProtectedReason(resolved) is { } reason)
            throw new VolumeFormatException($"{what}に {reason} は指定できません（{resolved}）。別のフォルダを指定してください。");
    }

    /// <summary>保護された場所の中なら、その説明を返す。</summary>
    public static string? ProtectedReason(string resolvedPath)
    {
        foreach (string root in ProtectedRoots)
        {
            if (IsUnder(resolvedPath, root)) return $"システムの保護されたフォルダ ({root})";
        }
        return null;
    }

    /// <summary>path が root 自身か、その中にあるか（大文字小文字を区別しない）。</summary>
    public static bool IsUnder(string path, string root)
    {
        string p = Path.TrimEndingDirectorySeparator(path);
        string r = Path.TrimEndingDirectorySeparator(root);
        return p.Equals(r, StringComparison.OrdinalIgnoreCase)
               || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 絶対パスにし、存在する部分についてはジャンクション・シンボリックリンクを実体まで解決したパスを返す。
    /// まだ存在しない末尾の部分はそのまま連結する。ネットワークパスとデバイスパスは拒否する。
    /// </summary>
    public static string ResolveFinalPath(string path, string what = "パス")
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new VolumeFormatException($"{what}にネットワーク上のパスやデバイスのパスは指定できません（{full}）。ドライブ文字で始まるパスを指定してください。");

        string existing = full;
        var rest = new Stack<string>();
        while (!Directory.Exists(existing) && !File.Exists(existing))
        {
            string? parent = Path.GetDirectoryName(existing);
            if (parent == null) throw new VolumeFormatException($"{what}のドライブが存在しません（{full}）。");
            rest.Push(Path.GetFileName(existing));
            existing = parent;
        }

        string resolved = FinalPathOf(existing);
        while (rest.Count > 0) resolved = Path.Combine(resolved, rest.Pop());
        return resolved;
    }

    /// <summary>既存のファイル・フォルダの実体のパス（リンクを解決済み、\\?\ を除いた形）。</summary>
    public static string FinalPathOf(string existingPath)
    {
        using var h = CreateFileW(existingPath, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException($"{existingPath} を開けません（Win32 エラー {Marshal.GetLastWin32Error()}）。");
        return FinalPathOf(h);
    }

    /// <summary>開いたハンドルが実際に指しているファイルのパス。書き込む前の最終確認に使う。</summary>
    public static string FinalPathOf(SafeFileHandle handle)
    {
        var sb = new StringBuilder(1024);
        uint n = GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, VOLUME_NAME_DOS);
        if (n > sb.Capacity)
        {
            sb = new StringBuilder((int)n + 1);
            n = GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, VOLUME_NAME_DOS);
        }
        if (n == 0) throw new IOException($"ファイルの実際の場所を取得できません（Win32 エラー {Marshal.GetLastWin32Error()}）。");

        string s = sb.ToString();
        if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new VolumeFormatException($"ネットワーク上の場所には書き込めません（{s}）。");
        return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }

    /// <summary>
    /// 作成直後のファイルについて、実体が保護された場所や想定外の場所に無いか確認する。
    /// 問題があれば閉じて削除し、例外を投げる（作ったのはこのプロセスなので削除してよい）。
    /// </summary>
    /// <param name="mustBeUnder">指定時は、実体がこのフォルダ（解決済みのパス）の中になければならない。</param>
    public static void VerifyCreatedFile(FileStream stream, string path, string? mustBeUnder = null)
    {
        string actual = FinalPathOf(stream.SafeFileHandle);
        string? problem = ProtectedReason(actual) is { } reason ? reason
                        : mustBeUnder != null && !IsUnder(actual, mustBeUnder) ? $"保存先フォルダの外 ({actual})"
                        : null;
        if (problem == null) return;

        stream.Dispose();
        try { File.Delete(path); } catch { }
        throw new VolumeFormatException($"{path} の実体が {problem} を指していたため、書き込みを中止しました。");
    }

    private static List<string> BuildProtectedRoots()
    {
        var roots = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try
            {
                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
                if (Path.GetPathRoot(full) is { } r && Path.TrimEndingDirectorySeparator(r).Length == full.Length) return; // ドライブ直下は対象外
                if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
                if (Directory.Exists(full))
                {
                    string final = Path.TrimEndingDirectorySeparator(FinalPathOf(full));
                    if (!roots.Contains(final, StringComparer.OrdinalIgnoreCase)) roots.Add(final);
                }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add(Environment.GetEnvironmentVariable("SystemRoot"));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(Environment.GetEnvironmentVariable("ProgramW6432"));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)); // C:\ProgramData
        return roots;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);
}
