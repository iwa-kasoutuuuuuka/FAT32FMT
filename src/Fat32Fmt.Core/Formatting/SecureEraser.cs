using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Fat32Fmt.Core.Imaging;
using Fat32Fmt.Core.IO;

namespace Fat32Fmt.Core.Formatting;

public enum WipePattern
{
    /// <summary>0 で埋める。</summary>
    Zero,

    /// <summary>乱数で埋める(パスごとに別の乱数列)。</summary>
    Random,
}

public sealed record WipeOptions
{
    /// <summary>上書き回数(1 以上)。</summary>
    public int Passes { get; init; } = 1;

    public WipePattern Pattern { get; init; } = WipePattern.Zero;

    /// <summary>最後のパスの内容を読み戻して確認する。</summary>
    public bool Verify { get; init; }
}

public sealed record WipeResult(long Bytes, int Passes, WipePattern Pattern, bool Verified, TimeSpan Elapsed);

/// <summary>
/// デバイス全体を指定回数上書きして消去する。
/// 注意: SD カードはウェアレベリングのため、論理的に上書きしても予備ブロックに古いデータが残る可能性がある。
/// </summary>
public static class SecureEraser
{
    public const int MaxPasses = 35;

    public static WipeResult Wipe(IBlockDevice device, WipeOptions options, IProgress<OperationProgress>? progress = null,
        Action<string>? log = null, CancellationToken ct = default)
    {
        if (options.Passes is < 1 or > MaxPasses)
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"上書き回数は 1～{MaxPasses} で指定してください。");
        }

        var started = DateTime.UtcNow;
        var buf = new byte[BlockDeviceExtensions.ChunkSize];
        ulong lastSeed = 0;
        for (int pass = 1; pass <= options.Passes; pass++)
        {
            string name = options.Pattern == WipePattern.Zero ? "0" : "乱数";
            string phase = options.Passes > 1 ? $"上書き {pass}/{options.Passes} ({name})" : $"上書き ({name})";
            log?.Invoke($"{phase} を開始します ({SizeFormat.Both(device.Length)})。");
            lastSeed = NewSeed();
            var rng = new Xoshiro256(lastSeed);
            if (options.Pattern == WipePattern.Zero)
            {
                Array.Clear(buf);
            }

            var tracker = new ProgressTracker(progress, phase, device.Length);
            for (long off = 0; off < device.Length;)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buf.Length, device.Length - off);
                if (options.Pattern == WipePattern.Random)
                {
                    rng.Fill(buf.AsSpan(0, n));
                }

                device.Write(off, buf.AsSpan(0, n));
                off += n;
                tracker.Report(off);
            }

            device.Flush();
            tracker.Complete();
        }

        if (options.Verify)
        {
            VerifyLastPass(device, options.Pattern, lastSeed, progress, ct);
            log?.Invoke("最後のパスの読み戻し確認 OK。");
        }

        return new WipeResult(device.Length, options.Passes, options.Pattern, options.Verify, DateTime.UtcNow - started);
    }

    private static void VerifyLastPass(IBlockDevice device, WipePattern pattern, ulong seed,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var expected = new byte[BlockDeviceExtensions.ChunkSize];
        var actual = new byte[BlockDeviceExtensions.ChunkSize];
        var rng = new Xoshiro256(seed);
        var tracker = new ProgressTracker(progress, "読み戻し確認", device.Length);
        for (long off = 0; off < device.Length;)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(actual.Length, device.Length - off);
            device.Read(off, actual.AsSpan(0, n));
            int bad;
            if (pattern == WipePattern.Zero)
            {
                bad = actual.AsSpan(0, n).IndexOfAnyExcept((byte)0);
            }
            else
            {
                rng.Fill(expected.AsSpan(0, n));
                bad = expected.AsSpan(0, n).SequenceEqual(actual.AsSpan(0, n)) ? -1 : FirstDifference(expected, actual, n);
            }

            if (bad >= 0)
            {
                long at = off + bad;
                throw new DataVerificationException(
                    $"消去後の読み戻しで、オフセット {at:N0} ({SizeFormat.Binary(at)}) の内容が書いたものと異なります。カードの不良や容量偽装の可能性があります。", at);
            }

            off += n;
            tracker.Report(off);
        }

        tracker.Complete();
    }

    private static int FirstDifference(byte[] a, byte[] b, int n)
    {
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return -1;
    }

    private static ulong NewSeed()
    {
        Span<byte> s = stackalloc byte[8];
        RandomNumberGenerator.Fill(s);
        return MemoryMarshal.Read<ulong>(s);
    }
}

/// <summary>
/// xoshiro256** 擬似乱数。暗号用途ではないが高速で、同じシードから同じ列を再生成できるので読み戻し確認に使える。
/// </summary>
internal struct Xoshiro256
{
    private ulong _s0, _s1, _s2, _s3;

    public Xoshiro256(ulong seed)
    {
        // splitmix64 で状態を初期化する(全 0 の状態を避ける)
        _s0 = SplitMix(ref seed);
        _s1 = SplitMix(ref seed);
        _s2 = SplitMix(ref seed);
        _s3 = SplitMix(ref seed);
    }

    public ulong Next()
    {
        ulong result = ulong.RotateLeft(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = ulong.RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>バッファを埋める。長さは 8 の倍数であること(セクタ単位なので常に満たす)。</summary>
    public void Fill(Span<byte> buffer)
    {
        var words = MemoryMarshal.Cast<byte, ulong>(buffer);
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = Next();
        }
    }

    private static ulong SplitMix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }
}
