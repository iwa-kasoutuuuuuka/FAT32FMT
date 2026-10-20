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
