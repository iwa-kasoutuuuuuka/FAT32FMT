using System.Text;

namespace Fat32Fmt.Core.Imaging;

/// <summary>
/// イメージの横に置くハッシュファイル (&lt;image&gt;.sha256)。sha256sum と同じ「ハッシュ *ファイル名」形式なので、
/// Linux の <c>sha256sum -c</c> や PowerShell の Get-FileHash でも照合できる。
/// </summary>
public static class ImageHashFile
{
    public static string PathFor(string imagePath) => imagePath + ".sha256";

    public static void Write(string imagePath, string sha256)
    {
        string line = $"{sha256.ToLowerInvariant()} *{Path.GetFileName(imagePath)}\n";
        File.WriteAllText(PathFor(imagePath), line, new UTF8Encoding(false));
    }

    /// <summary>ハッシュファイルがあれば中のハッシュを返す。無い・読めない場合は null。</summary>
    public static string? TryRead(string imagePath)
    {
        try
        {
            string path = PathFor(imagePath);
            if (!File.Exists(path))
            {
                return null;
            }

            string text = File.ReadAllText(path).Trim();
            string token = text.Split([' ', '\t', '\r', '\n'], 2)[0];
            return token.Length == 64 && token.All(Uri.IsHexDigit) ? token.ToLowerInvariant() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
