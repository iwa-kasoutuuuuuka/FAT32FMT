using Fat32Fmt.Core;

namespace Fat32Fmt.Gui;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // コマンドラインで初期値を渡せる:  FAT32FMT.exe E: --label MYCARD --cluster 32K
        char? letter = null;
        string? label = null;
        int cluster = 0;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.Equals("--label", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) label = args[++i];
            else if (a.Equals("--cluster", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                cluster = SizeFormat.TryParse(args[++i], out long v) && v <= 65536 ? (int)v : 0;
            else if (a.Length is 1 or 2 && char.IsAsciiLetter(a[0]) && (a.Length == 1 || a[1] == ':')) letter = char.ToUpperInvariant(a[0]);
        }

        Application.Run(new MainForm(letter, label, cluster));
    }
}
