using System.Globalization;
using System.Text;

namespace Fat32Fmt.Core.Fat32;

/// <summary>FAT のボリュームラベル(11 バイト、OEM コードページ)。</summary>
public static class VolumeLabel
{
    public const int MaxBytes = 11;

    private const string Forbidden = "\"*+,./:;<=>?[\\]|";

    static VolumeLabel()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Windows と同じく OEM コードページ(日本語環境では CP932)を使う。</summary>
    public static Encoding OemEncoding { get; } = GetOemEncoding();

    /// <summary>ラベル未指定時に BPB に書く値。</summary>
    public static ReadOnlySpan<byte> NoName => "NO NAME    "u8;

    public static bool TryValidate(string? label, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(label))
        {
            return true;
        }

        foreach (char c in label)
        {
            if (c < 0x20 || Forbidden.Contains(c))
            {
                error = $"ボリュームラベルに使えない文字が含まれています: '{c}'(使用不可: {Forbidden})";
                return false;
            }
        }

        byte[] bytes;
        try
        {
            bytes = Encoder.GetBytes(Normalize(label));
        }
        catch (EncoderFallbackException)
        {
            error = $"ボリュームラベルに {OemEncoding.WebName} で表せない文字が含まれています。";
            return false;
        }

        if (bytes.Length > MaxBytes)
        {
            error = $"ボリュームラベルが長すぎます({bytes.Length} バイト。最大 {MaxBytes} バイト、全角文字は 2 バイト)。";
            return false;
        }

        return true;
    }

    /// <summary>11 バイトの空白埋めラベルにエンコードする。空なら null。</summary>
    public static byte[]? Encode(string? label)
    {
        if (string.IsNullOrEmpty(label))
        {
            return null;
        }

        if (!TryValidate(label, out string? error))
        {
            throw new ArgumentException(error, nameof(label));
        }

        byte[] raw = Encoder.GetBytes(Normalize(label));
        var result = new byte[MaxBytes];
        result.AsSpan().Fill((byte)' ');
        raw.CopyTo(result, 0);
        if (result[0] == 0xE5)
        {
            result[0] = 0x05; // 0xE5 は削除済みマークなので規定どおり 0x05 に置き換える
        }

        return result;
    }

    public static string Decode(ReadOnlySpan<byte> raw)
    {
        Span<byte> tmp = stackalloc byte[raw.Length];
        raw.CopyTo(tmp);
        if (tmp.Length > 0 && tmp[0] == 0x05)
        {
            tmp[0] = 0xE5;
        }

        return OemEncoding.GetString(tmp).TrimEnd(' ', '\0');
    }

    /// <summary>Windows と同様に ASCII 英字を大文字にする(全角文字はそのまま)。</summary>
    private static string Normalize(string label)
    {
        var sb = new StringBuilder(label.Length);
        foreach (char c in label)
        {
            sb.Append(c is >= 'a' and <= 'z' ? char.ToUpperInvariant(c) : c);
        }

        return sb.ToString();
    }

    private static Encoding Encoder { get; } = Encoding.GetEncoding(
        OemEncoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);

    private static Encoding GetOemEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        int cp = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
        try
        {
            return Encoding.GetEncoding(cp);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.GetEncoding(437);
        }
    }
}
