using System.Runtime.InteropServices;

namespace Fat32Fmt.Core.Diagnostics;

/// <summary>
/// 位置ごとに異なる疑似乱数の検査パターン（H2testw / F3 と同じ考え方）。
/// 512 バイトのブロックごとに、先頭 8 バイトへ「そのブロックの位置」を埋め込み、残りは位置と種から作る乱数で埋める。
/// 書き込みが折り返す偽装カードでは、ある位置を読むと別の位置のデータが返るので、埋め込んだ位置から折り返し幅が分かる。
/// 種は実行ごとに変えるので、前回の検査で書いたデータが残っていても合格扱いにはならない。
/// </summary>
public sealed class TestPattern
{
    public const int BlockSize = 512;
    private const int WordsPerBlock = BlockSize / sizeof(ulong);
    private const ulong Golden = 0x9E3779B97F4A7C15;

    private readonly ulong _tagKey;

    public TestPattern(ulong seed)
    {
        Seed = seed;
        _tagKey = Mix(seed ^ 0xD1B54A32D192ED03);
    }

    public ulong Seed { get; }

    public static TestPattern CreateRandom() => new((ulong)Random.Shared.NextInt64() ^ ((ulong)Random.Shared.NextInt64() << 1));

    /// <summary>デバイス上の位置 <paramref name="offset"/> から始まる範囲のパターンを作る。offset と長さは 512 の倍数。</summary>
    public void Fill(long offset, Span<byte> buffer)
    {
        Check(offset, buffer.Length);
        var words = MemoryMarshal.Cast<byte, ulong>(buffer);
        long block = offset / BlockSize;
        for (int w = 0; w < words.Length; w += WordsPerBlock, block++)
            FillBlock(block, words.Slice(w, WordsPerBlock));
    }

    /// <summary>
    /// 読み戻したブロックが、どの位置のパターンかを調べる。
    /// 先頭 8 バイトから位置を復元し、ブロック全体がその位置のパターンと一致すれば true。
    /// </summary>
    public bool TryIdentify(ReadOnlySpan<byte> block, out long sourceOffset)
    {
        sourceOffset = -1;
        if (block.Length < BlockSize) return false;
        ulong index = MemoryMarshal.Read<ulong>(block) ^ _tagKey;
        if (index > long.MaxValue / BlockSize) return false;
        Span<ulong> expected = stackalloc ulong[WordsPerBlock];
        FillBlock((long)index, expected);
        if (!MemoryMarshal.AsBytes(expected).SequenceEqual(block[..BlockSize])) return false;
        sourceOffset = (long)index * BlockSize;
        return true;
    }

    private void FillBlock(long block, Span<ulong> words)
    {
        words[0] = (ulong)block ^ _tagKey;
        ulong state = Mix(Seed ^ ((ulong)block * Golden));
        for (int i = 1; i < words.Length; i++)
        {
            state += Golden;
            words[i] = Mix(state);
        }
    }

    /// <summary>SplitMix64 の出力関数。</summary>
    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }

    private static void Check(long offset, int length)
    {
        if (offset < 0 || offset % BlockSize != 0 || length % BlockSize != 0)
            throw new ArgumentException($"検査パターンの位置と長さは {BlockSize} の倍数である必要があります (offset={offset}, length={length})。");
    }
}
