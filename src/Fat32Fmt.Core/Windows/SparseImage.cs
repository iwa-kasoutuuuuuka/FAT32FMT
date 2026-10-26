using System.Runtime.Versioning;
using Fat32Fmt.Core.IO;

namespace Fat32Fmt.Core.Windows;

/// <summary>
/// イメージファイルを NTFS のスパースファイルとして作る。
/// 通常のファイルだと SetLength の時点で全容量を確保するため、空き容量より大きいイメージ（64 GB など）を作れない。
/// </summary>
[SupportedOSPlatform("windows")]
public static class SparseImage
{
    public static StreamBlockDevice Create(string path, long size, int sectorSize = 512)
    {
        size -= size % sectorSize;
        var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1 << 20, FileOptions.RandomAccess);
        try
        {
            // 作ったファイルの実体が保護された場所（リンク経由で C:\Windows など）なら、書き込む前に消して中止する
            SafePaths.VerifyCreatedFile(fs, path);
            Native.Ioctl(fs.SafeFileHandle, Native.FSCTL_SET_SPARSE); // FAT/exFAT 上などでは失敗するが、その場合は通常ファイルのまま続ける
            fs.SetLength(size);
            return new StreamBlockDevice(fs, sectorSize);
        }
        catch
        {
            fs.Dispose();
            try { File.Delete(path); } catch { }
            throw;
        }
    }
}
