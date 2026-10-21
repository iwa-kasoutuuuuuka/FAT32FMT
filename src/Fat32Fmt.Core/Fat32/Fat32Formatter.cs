using System.Buffers.Binary;
using System.Diagnostics;
using Fat32Fmt.Core.IO;

namespace Fat32Fmt.Core.Fat32;

public sealed class Fat32FormatOptions
{
    public string? Label { get; init; }

    /// <summary>
    /// true ならデータ領域全体に 0 を書いて読み戻し、読み書きできないクラスタを不良クラスタとして FAT に登録する。
    /// </summary>
    public bool FullFormat { get; init; }

    public uint? VolumeId { get; init; }

    public DateTime? Timestamp { get; init; }

    public ushort SectorsPerTrack { get; init; } = 63;

    public ushort Heads { get; init; } = 255;

    /// <summary>
    /// フルフォーマットで、失敗するまでにこれ以上かかった区間は切り分けずに丸ごと不良にする。
    /// 不良ブロックではドライバーが 1 回の I/O に 1 分以上リトライすることがあり、クラスタ単位で試すと何時間もかかるため。
    /// </summary>
    public TimeSpan SlowFailureThreshold { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>失敗に時間がかかる区間がこの回数続いたら、カードが応答しないとみなして中止する。</summary>
    public int MaxConsecutiveSlowFailures { get; init; } = 8;
}

public sealed record Fat32FormatResult(Fat32Layout Layout, IReadOnlyList<uint> BadClusters, uint VolumeId)
{
    public long FreeBytes => ((long)Layout.ClusterCount - 1 - BadClusters.Count) * Layout.BytesPerCluster;
}

/// <summary>
/// ブロックデバイス(パーティション)に FAT32 ファイルシステムを書き込む。
/// Windows の format コマンドと違い、32 GB を超えるボリュームも FAT32 にできる。
/// </summary>
public static class Fat32Formatter
{
    public const uint BadClusterMark = 0x0FFFFFF7;
    public const uint EndOfChain = 0x0FFFFFFF;

    public static Fat32FormatResult Format(IBlockDevice volume, Fat32Layout layout, Fat32FormatOptions options,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        int bps = layout.BytesPerSector;
        if (volume.SectorSize != bps)
        {
            throw new ArgumentException($"デバイスのセクタサイズ ({volume.SectorSize}) とレイアウト ({bps}) が一致しません。");
        }

        if (volume.Length < layout.VolumeBytes)
        {
            throw new ArgumentException("デバイスがレイアウトより小さいです。");
        }

        if (!volume.CanWrite)
        {
            throw new IOException("デバイスが書き込み可能な状態で開かれていません。");
        }

        byte[]? label = VolumeLabel.Encode(options.Label);
        DateTime now = options.Timestamp ?? DateTime.Now;
        uint volumeId = options.VolumeId ?? Fat32BootSector.MakeVolumeId(now);

        // フル: 先にデータ領域を検査して不良クラスタを集める(FAT に反映するため)
        var badClusters = options.FullFormat
            ? ScanDataArea(volume, layout, options, progress, ct)
            : new List<uint>();
        if (badClusters.Contains(Fat32BootSector.RootCluster))
        {
            throw new IOException("ルートディレクトリを置くクラスタ 2 が不良です。このデバイスは FAT32 でフォーマットできません。");
        }

        // 1. 予約領域と FAT を 0 で初期化(古いブートセクタも消える)
        volume.Zero(0, layout.DataStartOffset, progress, "システム領域の初期化", ct);

        // 2. ルートディレクトリのクラスタを初期化し、ボリュームラベルを置く
        var root = new byte[layout.BytesPerCluster];
        if (label is not null)
        {
            label.CopyTo(root, 0);
            root[11] = 0x08; // ATTR_VOLUME_ID
            BinaryPrimitives.WriteUInt16LittleEndian(root.AsSpan(22), Fat32BootSector.DosTime(now));
            BinaryPrimitives.WriteUInt16LittleEndian(root.AsSpan(24), Fat32BootSector.DosDate(now));
        }

        volume.Write(layout.ClusterOffset(Fat32BootSector.RootCluster), root);

        // 3. FAT の先頭エントリと不良クラスタを各 FAT に書く
        WriteFats(volume, layout, badClusters);

        // 4. FSInfo とバックアップ
        uint free = layout.ClusterCount - 1 - (uint)badClusters.Count;
        uint nextFree = badClusters.Contains(3) ? 0xFFFFFFFF : 3;
        byte[] fsInfo = Fat32BootSector.BuildFsInfo(bps, free, nextFree);
        volume.Write((long)Fat32BootSector.FsInfoSector * bps, fsInfo);
        volume.Write((long)(Fat32BootSector.BackupBootSector + Fat32BootSector.FsInfoSector) * bps, fsInfo);

        // 5. ブートセクタはバックアップを先に、本体は最後に書く(途中で失敗したとき中途半端な FAT32 と認識させない)
        byte[] boot = Fat32BootSector.Build(layout, label ?? [], volumeId, options.SectorsPerTrack, options.Heads);
        volume.Write((long)Fat32BootSector.BackupBootSector * bps, boot);
        volume.Flush();
        volume.Write(0, boot);
        volume.Flush();

        // 6. 書き込んだブートセクタを読み戻して確認
        var check = new byte[bps];
        volume.Read(0, check);
        if (!check.AsSpan().SequenceEqual(boot))
        {
            throw new IOException("ブートセクタの読み戻し結果が一致しません。デバイスへの書き込みが反映されていない可能性があります。");
        }

        return new Fat32FormatResult(layout, badClusters, volumeId);
    }

    private static void WriteFats(IBlockDevice volume, Fat32Layout layout, List<uint> badClusters)
    {
        int bps = layout.BytesPerSector;
        int entriesPerSector = bps / 4;
        var sectors = new SortedDictionary<long, byte[]>();

        byte[] Sector(long index)
        {
            if (!sectors.TryGetValue(index, out byte[]? s))
            {
                s = new byte[bps];
                sectors[index] = s;
            }

            return s;
        }

        void SetEntry(uint cluster, uint value)
        {
            byte[] s = Sector(cluster / entriesPerSector);
            BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan((int)(cluster % entriesPerSector) * 4), value);
        }

        SetEntry(0, 0x0FFFFF00 | 0xF8); // メディアディスクリプタ
        SetEntry(1, EndOfChain);        // クリーンシャットダウン・エラーなし
        SetEntry(Fat32BootSector.RootCluster, EndOfChain);
        foreach (uint c in badClusters)
        {
            SetEntry(c, BadClusterMark);
        }

        for (int fat = 0; fat < layout.NumberOfFats; fat++)
        {
            long fatStart = (layout.FatStartSector + (long)fat * layout.FatSizeSectors) * bps;
            foreach (var (index, data) in sectors)
            {
                volume.Write(fatStart + index * bps, data);
            }
        }
    }

    /// <summary>データ領域に 0 を書いて読み戻し、異常のあるクラスタ番号を返す。</summary>
    private static List<uint> ScanDataArea(IBlockDevice volume, Fat32Layout layout, Fat32FormatOptions options,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        int clusterBytes = layout.BytesPerCluster;
        int chunk = Math.Max(clusterBytes, BlockDeviceExtensions.ChunkSize / clusterBytes * clusterBytes);
        var zeros = new byte[chunk];
        var read = new byte[chunk];
        var bad = new List<uint>();
        long dataLen = layout.DataBytes;
        var tracker = new ProgressTracker(progress, "データ領域の書き込み検査", dataLen);

        int consecutiveSlow = 0;
        for (long pos = 0; pos < dataLen; pos += chunk)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(chunk, dataLen - pos);
            long offset = layout.DataStartOffset + pos;
            var t = Stopwatch.StartNew();
            if (TryWriteVerify(volume, offset, zeros.AsSpan(0, n), read.AsSpan(0, n)))
            {
                consecutiveSlow = 0;
            }
            else if (t.Elapsed >= options.SlowFailureThreshold)
            {
                // 失敗に時間がかかる（ドライバーが長くリトライする）領域は、細かく切り分けると
                // 1 区間で何時間もかかるため、区間全体を不良として次へ進む
                MarkBad(bad, pos, n, clusterBytes);
                if (++consecutiveSlow >= options.MaxConsecutiveSlowFailures)
                {
                    throw new IOException(
                        $"データ領域の {SizeFormat.Binary(pos)} 付近から先が応答しません（失敗に時間がかかる区間が {consecutiveSlow} 回続きました）。" +
                        "カードかカードリーダーの故障が疑われます。");
                }
            }
            else
            {
                consecutiveSlow = 0;
                // すぐ失敗する場合は二分探索でクラスタ単位まで切り分ける（不良 1 個あたり約 log2 回の試行で済む）
                Bisect(volume, layout, pos, n, clusterBytes, zeros, read, bad, options.SlowFailureThreshold);
            }

            tracker.Report(pos + n);
        }

        tracker.Complete();
        bad.Sort();
        return bad;
    }

    private static void Bisect(IBlockDevice volume, Fat32Layout layout, long pos, int length, int clusterBytes,
        byte[] zeros, byte[] read, List<uint> bad, TimeSpan slow)
    {
        if (length <= clusterBytes)
        {
            MarkBad(bad, pos, length, clusterBytes);
            return;
        }

        int half = length / 2 / clusterBytes * clusterBytes;
        foreach (var (p, n) in new[] { (pos, half), (pos + half, length - half) })
        {
            var t = Stopwatch.StartNew();
            if (TryWriteVerify(volume, layout.DataStartOffset + p, zeros.AsSpan(0, n), read.AsSpan(0, n))) continue;
            if (t.Elapsed >= slow) MarkBad(bad, p, n, clusterBytes);
            else Bisect(volume, layout, p, n, clusterBytes, zeros, read, bad, slow);
        }
    }

    private static void MarkBad(List<uint> bad, long pos, int length, int clusterBytes)
    {
        for (long c = 0; c < length; c += clusterBytes)
        {
            bad.Add((uint)(2 + (pos + c) / clusterBytes));
        }
    }

    private static bool TryWriteVerify(IBlockDevice volume, long offset, ReadOnlySpan<byte> data, Span<byte> read)
    {
        try
        {
            volume.Write(offset, data);
            volume.Read(offset, read);
            return read.SequenceEqual(data);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
