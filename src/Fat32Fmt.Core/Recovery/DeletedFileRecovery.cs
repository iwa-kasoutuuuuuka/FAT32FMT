using Fat32Fmt.Core.Fat32;
using Fat32Fmt.Core.IO;

namespace Fat32Fmt.Core.Recovery;

/// <summary>削除ファイルが取り出せる見込み。</summary>
public enum RecoveryChance
{
    /// <summary>データがあるはずの連続クラスタがすべて未使用。断片化していなければ元の内容が残っている。</summary>
    Good,

    /// <summary>一部のクラスタがすでに別のファイルに使われている。後半などが壊れている可能性が高い。</summary>
    Partial,

    /// <summary>先頭クラスタが別のファイルに使われている。ほぼ確実に上書き済み。</summary>
    Overwritten,

    /// <summary>サイズ 0 のファイル(空ファイルとして復元できる)。</summary>
    Empty,

    /// <summary>先頭クラスタやサイズがボリュームの範囲外で取り出せない。</summary>
    Invalid,
}

/// <summary>見つかった削除ファイル。</summary>
/// <param name="Path">ボリューム内のパス(区切りは '/')。削除済みディレクトリの中にあった場合はそのディレクトリ名を含む。</param>
/// <param name="NameRecovered">LFN のチェックサムから短い名前の先頭文字を確定できた(false なら先頭は '_' で代用)。</param>
public sealed record DeletedFile(string Path, FatDirectoryEntry Entry, RecoveryChance Chance, bool NameRecovered)
{
    public string Name => Entry.Name;

    public long Size => Entry.Size;
}

public sealed record RecoveredFile(string SourcePath, string OutputPath, long Bytes, RecoveryChance Chance);

/// <summary>
/// FAT32 の削除ファイルを探して取り出す。
/// FAT32 では削除時にクラスタチェーンが消えるため、ファイルは先頭クラスタから連続して置かれていたと仮定して読む。
/// 断片化していたファイルは後半が別の内容になる。
/// </summary>
public static class DeletedFileRecovery
{
    private const int MaxDepth = 64;

    /// <summary>ルートから全ディレクトリをたどり、削除ファイルを列挙する(読み取りのみ)。</summary>
    public static List<DeletedFile> Scan(Fat32Volume volume, CancellationToken ct = default)
    {
        var result = new List<DeletedFile>();
        var visited = new HashSet<uint>();
        Walk(volume, volume.Chain(volume.RootCluster), "", false, 0, visited, result, ct);
        return result;
    }

    /// <summary>ファイルの内容を読み、連続クラスタを仮定した見込みを判定する。</summary>
    public static RecoveryChance Assess(Fat32Volume volume, FatDirectoryEntry entry)
    {
        if (entry.Size == 0)
        {
            return RecoveryChance.Empty;
        }

        var l = volume.Layout;
        long clusters = ((long)entry.Size + l.BytesPerCluster - 1) / l.BytesPerCluster;
        if (!volume.IsValidCluster(entry.FirstCluster) || entry.FirstCluster + clusters - 1 > l.ClusterCount + 1L)
        {
            return RecoveryChance.Invalid;
        }

        if (volume.ReadFatEntry(entry.FirstCluster) != 0)
        {
            return RecoveryChance.Overwritten;
        }

        for (long i = 1; i < clusters; i++)
        {
            if (volume.ReadFatEntry((uint)(entry.FirstCluster + i)) != 0)
            {
                return RecoveryChance.Partial;
            }
        }

        return RecoveryChance.Good;
    }

    /// <summary>
    /// 削除ファイルを destination 以下に書き出す。元のディレクトリ構成を再現し、同名ファイルがあれば「名前 (2).拡張子」にする。
    /// destination は復旧元とは別のドライブであること(呼び出し側で確認する)。
    /// </summary>
    public static List<RecoveredFile> Recover(Fat32Volume volume, IEnumerable<DeletedFile> files, string destination,
        IProgress<OperationProgress>? progress = null, Action<string>? log = null, CancellationToken ct = default)
    {
        var list = files.Where(f => f.Chance != RecoveryChance.Invalid).ToList();
        Directory.CreateDirectory(destination);
        long total = list.Sum(f => f.Size);
        var tracker = new ProgressTracker(progress, "削除ファイルの復旧", total);
        long done = 0;
        var result = new List<RecoveredFile>();
        var l = volume.Layout;
        var buf = new byte[BlockDeviceExtensions.ChunkSize];
        foreach (var f in list)
        {
            ct.ThrowIfCancellationRequested();
            string output = UniquePath(Path.Combine(destination, SafeRelativePath(f.Path)));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using (var fs = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
            {
                long offset = l.ClusterOffset(f.Entry.FirstCluster);
                long left = f.Size;
                while (left > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(buf.Length, left);
                    int aligned = (want + l.BytesPerSector - 1) / l.BytesPerSector * l.BytesPerSector;
                    volume.Device.Read(offset, buf.AsSpan(0, aligned));
                    fs.Write(buf, 0, want);
                    offset += aligned;
                    left -= want;
                    done += want;
                    tracker.Report(done);
                }
            }

            if (f.Entry.Modified is DateTime m)
            {
                File.SetLastWriteTime(output, m);
            }

            log?.Invoke($"{f.Path} → {output} ({SizeFormat.Binary(f.Size)}, {ChanceName(f.Chance)})");
            result.Add(new RecoveredFile(f.Path, output, f.Size, f.Chance));
        }

        tracker.Complete();
        return result;
    }

    public static string ChanceName(RecoveryChance c) => c switch
    {
        RecoveryChance.Good => "良好",
        RecoveryChance.Partial => "一部上書き",
        RecoveryChance.Overwritten => "上書き済み",
        RecoveryChance.Empty => "空ファイル",
        _ => "取り出し不可",
    };

    private static void Walk(Fat32Volume volume, IEnumerable<uint> clusters, string path, bool insideDeleted, int depth,
        HashSet<uint> visited, List<DeletedFile> result, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > MaxDepth)
        {
            return;
        }

        foreach (var e in volume.ReadDirectory(clusters, includeDeleted: true))
        {
            if (e.IsDotEntry || e.IsVolumeLabel)
            {
                continue;
            }

            bool deleted = e.IsDeleted || insideDeleted;
            string childPath = path.Length == 0 ? e.Name : $"{path}/{e.Name}";
            if (e.IsDirectory)
            {
                if (!volume.IsValidCluster(e.FirstCluster) || !visited.Add(e.FirstCluster))
                {
                    continue;
                }

                if (!deleted)
                {
                    Walk(volume, volume.Chain(e.FirstCluster), childPath, false, depth + 1, visited, result, ct);
                }
                else if (volume.ReadFatEntry(e.FirstCluster) == 0 && LooksLikeDirectory(volume, e.FirstCluster))
                {
                    // 削除済みディレクトリはチェーンが消えているので先頭クラスタだけを読む
                    Walk(volume, [e.FirstCluster], childPath, true, depth + 1, visited, result, ct);
                }

                continue;
            }

            if (!deleted)
            {
                continue;
            }

            // LFN のチェックサムが合えば Name は長い名前になる。合わなければ短い名前の先頭が '_' のまま
            bool nameRecovered = !e.IsDeleted || e.Name != e.ShortName;
            result.Add(new DeletedFile(childPath, e, Assess(volume, e), nameRecovered));
        }
    }

    /// <summary>先頭エントリが「.」なら、まだ別のデータで上書きされていないディレクトリとみなす。</summary>
    private static bool LooksLikeDirectory(Fat32Volume volume, uint cluster)
    {
        var data = new byte[volume.Layout.BytesPerSector];
        volume.Device.Read(volume.Layout.ClusterOffset(cluster), data);
        return data[0] == (byte)'.' && data[1] == (byte)' ' && (data[11] & 0x10) != 0;
    }

    private static string SafeRelativePath(string path)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p =>
            {
                var chars = p.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
                string s = new string(chars).TrimEnd('.', ' ');
                return s.Length == 0 || s is "." or ".." ? "_" : s;
            });
        return Path.Combine(parts.ToArray());
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p))
            {
                return p;
            }
        }
    }
}
