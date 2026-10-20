using System.Globalization;

namespace Fat32Fmt.Core;

public static class SizeFormat
{
    /// <summary>2 進接頭辞で表示する(例: 29.72 GiB)。</summary>
    public static string Binary(long bytes) => Scale(bytes, 1024, ["B", "KiB", "MiB", "GiB", "TiB"]);

    /// <summary>カード表記と同じ 10 進接頭辞で表示する(例: 31.91 GB)。</summary>
    public static string Decimal(long bytes) => Scale(bytes, 1000, ["B", "KB", "MB", "GB", "TB"]);

    /// <summary>「31.91 GB (29.72 GiB)」形式。</summary>
    public static string Both(long bytes) => $"{Decimal(bytes)} ({Binary(bytes)})";

    /// <summary>転送速度(10 進 MB/s。SD のスピードクラスと同じ単位)。</summary>
    public static string Speed(double bytesPerSecond) =>
        (bytesPerSecond / 1_000_000).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";

    /// <summary>クラスタサイズ表示(例: 32 KB)。</summary>
    public static string Cluster(int bytes) => bytes >= 1024 ? $"{bytes / 1024} KB" : $"{bytes} B";

    /// <summary>
    /// "512", "32K", "4M", "1.5G", "64KiB", "64KB" などを解析する。接尾辞は 2 進(1K = 1024)として扱う。
    /// </summary>
    public static long Parse(string text) =>
        TryParse(text, out long value) ? value : throw new FormatException($"サイズ指定を解釈できません: {text}");

    public static bool TryParse(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string s = text.Trim().ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal);
        if (s.EndsWith("IB", StringComparison.Ordinal))
        {
            s = s[..^2];
        }
        else if (s.EndsWith('B'))
        {
            s = s[..^1];
        }

        long mul = 1;
        if (s.Length > 0 && char.IsLetter(s[^1]))
        {
            mul = s[^1] switch
            {
                'K' => 1L << 10,
                'M' => 1L << 20,
                'G' => 1L << 30,
                'T' => 1L << 40,
                _ => 0,
            };
            s = s[..^1];
        }

        if (mul == 0 || !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double num) || num < 0)
        {
            return false;
        }

        value = (long)Math.Round(num * mul);
        return true;
    }

    private static string Scale(long bytes, double step, string[] units)
    {
        double v = bytes;
        int i = 0;
        while (Math.Abs(v) >= step && i < units.Length - 1)
        {
            v /= step;
            i++;
        }

        return i == 0
            ? $"{bytes} B"
            : v.ToString(v >= 100 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + " " + units[i];
    }
}
