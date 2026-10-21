using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Fat32Fmt.Core.Windows;

/// <summary>物理ディスク上のボリュームを探すための Win32 API（<see cref="Native"/> の補完）。</summary>
[SupportedOSPlatform("windows")]
internal static class DiskNative
{
    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    /// <summary>MBR の署名を消し、パーティションマネージャのレイアウトを「パーティションなし」にする（diskpart clean と同じ）。</summary>
    public const uint IOCTL_DISK_DELETE_DRIVE_LAYOUT = 0x0007C100;
    private static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr FindFirstVolumeW(StringBuilder lpszVolumeName, uint cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool FindNextVolumeW(IntPtr hFindVolume, StringBuilder lpszVolumeName, uint cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool FindVolumeClose(IntPtr hFindVolume);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool GetVolumePathNamesForVolumeNameW(string lpszVolumeName, char[] lpszVolumePathNames,
        uint cchBufferLength, out uint lpcchReturnLength);

    /// <summary>システム上の全ボリュームの GUID パス（\\?\Volume{...}\ 形式）。</summary>
    public static List<string> EnumerateVolumeGuidPaths()
    {
        var result = new List<string>();
        var sb = new StringBuilder(260);
        IntPtr h = FindFirstVolumeW(sb, (uint)sb.Capacity);
        if (h == InvalidHandle) return result;
        try
        {
            do
            {
                result.Add(sb.ToString());
                sb.Clear();
            }
            while (FindNextVolumeW(h, sb, (uint)sb.Capacity));
        }
        finally
        {
            FindVolumeClose(h);
        }
        return result;
    }

    /// <summary>ボリュームに割り当てられたドライブ文字。</summary>
    public static List<char> DriveLettersOf(string volumeGuidPath)
    {
        var buf = new char[1024];
        if (!GetVolumePathNamesForVolumeNameW(volumeGuidPath, buf, (uint)buf.Length, out _)) return new();
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Length == 3 && p[1] == ':' && char.IsAsciiLetter(p[0]))
            .Select(p => char.ToUpperInvariant(p[0]))
            .ToList();
    }

    /// <summary>ボリュームが載っている物理ディスク番号（複数ディスクにまたがる場合はすべて）。取得できなければ空。</summary>
    public static List<int> DisksOfVolume(string volumeGuidPath)
    {
        try
        {
            using var h = RawHandleIo.Open(volumeGuidPath.TrimEnd('\\'), 0, 0);
            var buf = new byte[8 + 24 * 16];
            if (!Native.IoctlOut(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, buf, out _)) return new();
            // VOLUME_DISK_EXTENTS: NumberOfDiskExtents(4) pad(4) DISK_EXTENT[] { DiskNumber(4) pad(4) StartingOffset(8) ExtentLength(8) }
            int n = (int)Math.Min(16, BinaryPrimitives.ReadUInt32LittleEndian(buf));
            var disks = new List<int>();
            for (int i = 0; i < n; i++) disks.Add((int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(8 + 24 * i)));
            return disks;
        }
        catch
        {
            return new();
        }
    }
}
