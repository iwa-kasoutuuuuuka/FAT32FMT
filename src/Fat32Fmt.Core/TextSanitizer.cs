using System.Text;

namespace Fat32Fmt.Core;

/// <summary>
/// カードやデバイスから読んだ文字列（ファイル名・ボリュームラベル・デバイス名）は攻撃者が自由に作れる。
/// 画面・ログ・AI エージェントに渡す前に、端末を操作する制御文字や表示順を入れ替える双方向制御文字を取り除く。
/// </summary>
public static class TextSanitizer
{
    /// <summary>制御文字（C0 / C1）と双方向制御文字を <paramref name="replacement"/> に置き換える。</summary>
    public static string Clean(string? text, char replacement = '_')
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }

        StringBuilder? sb = null;
        for (int i = 0; i < text.Length; i++)
        {
            if (!IsUnsafe(text[i]))
            {
                sb?.Append(text[i]);
                continue;
            }

            sb ??= new StringBuilder(text, 0, i, text.Length);
            sb.Append(replacement);
        }

        return sb?.ToString() ?? text;
    }

    /// <summary>
    /// 端末のエスケープシーケンスになり得る制御文字と、表示上の並びを偽装できる双方向制御文字
    /// （例: "photo‮gpj.exe" はエクスプローラーで "photoexe.jpg" と表示される）。
    /// </summary>
    public static bool IsUnsafe(char c) =>
        char.IsControl(c)                       // U+0000-001F, U+007F-009F（ESC, BEL, CSI など）
        || c is '؜'                        // ARABIC LETTER MARK
        || c is >= '​' and <= '‏'     // ゼロ幅文字、LRM / RLM
        || c is >= '‪' and <= '‮'     // LRE / RLE / PDF / LRO / RLO
        || c is >= '⁦' and <= '⁩'     // LRI / RLI / FSI / PDI
        || c is '﻿';                       // ゼロ幅ノーブレークスペース (BOM)
}
