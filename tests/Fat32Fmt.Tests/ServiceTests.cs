using System.Text.Json;
using Fat32Fmt.Cli;
using Fat32Fmt.Core.Fat32;
using Fat32Fmt.Core.Windows;
using Xunit;

namespace Fat32Fmt.Tests;

/// <summary>Windows 層・CLI のうち、実デバイス無しで検証できる部分。</summary>
public class ServiceTests
{
    [Fact]
    public void BuildCliArgs_ReflectsRequest()
    {
        var req = new FormatRequest
        {
            DriveLetter = 'e',
            ClusterBytes = 65536,
            Label = "MY CARD",
            FullFormat = true,
            FixPartitionType = false,
            AllowFixedDisk = true,
            AlignmentBytes = 0,
        };
        var args = ElevatedFormat.BuildCliArgs(req, @"C:\tmp\r.json");
        Assert.Equal(new[] { "format", "E:", "--yes", "--result-file", @"C:\tmp\r.json", "--cluster", "65536", "--label", "MY CARD",
                             "--full", "--no-ptype", "--allow-fixed", "--align", "0" }, args);

        // CLI 側のパーサで元に戻る
        var parsed = Options.Parse(args.ToArray()).ToRequest();
        Assert.Equal('E', parsed.DriveLetter);
        Assert.Equal(65536, parsed.ClusterBytes);
        Assert.Equal("MY CARD", parsed.Label);
        Assert.True(parsed.FullFormat);
        Assert.False(parsed.FixPartitionType);
        Assert.True(parsed.AllowFixedDisk);
        Assert.Equal(0L, parsed.AlignmentBytes);
    }

    [Fact]
    public void BuildCliArgs_Defaults_AreMinimal()
    {
        var args = ElevatedFormat.BuildCliArgs(new FormatRequest { DriveLetter = 'E' }, null);
        Assert.Equal(new[] { "format", "E:", "--yes" }, args);
        var parsed = Options.Parse(args.ToArray()).ToRequest();
        Assert.Null(parsed.AlignmentBytes); // 既定 = SD 境界単位を自動
        Assert.True(parsed.FixPartitionType);
    }

    [Theory]
    [InlineData("32K", 32768)]
    [InlineData("4096", 4096)]
    [InlineData("64KB", 65536)]
    [InlineData("1M", 1048576)]
    public void Options_ParsesSizes(string text, long expected)
    {
        var o = Options.Parse(new[] { "format", "E:", "--cluster", text, "--align", text });
        Assert.Equal((int)expected, o.ClusterSize);
        Assert.Equal(expected, o.Align);
    }

    [Fact]
    public void Options_RejectsUnknownOption()
    {
        Assert.Throws<UsageException>(() => Options.Parse(new[] { "format", "E:", "--bogus" }));
        Assert.Throws<UsageException>(() => Options.Parse(new[] { "format", "E:", "--cluster" }));
        Assert.Throws<UsageException>(() => Options.Parse(new[] { "format", "EF:" }).DriveLetter());
    }

    [Fact]
    public void FormatOutcome_JsonRoundTrip()
    {
        var layout = Fat32LayoutCalculator.Calculate(64L << 30, 512, 32768, 1 << 20, 16 << 20);
        var outcome = new FormatOutcome
        {
            Success = true,
            Result = new FormatResult('E', layout, 0xDEADBEEF, new List<uint> { 7, 9 }, true, "FAT32", "テスト", TimeSpan.FromSeconds(1.5)),
            Log = new List<string> { "a", "日本語" },
        };
        string json = JsonSerializer.Serialize(outcome, JsonDefaults.Options);
        Assert.Contains("\"driveLetter\": \"E\"", json);
        Assert.Contains("日本語", json); // エスケープされない

        var back = JsonSerializer.Deserialize<FormatOutcome>(json, JsonDefaults.Options)!;
        Assert.True(back.Success);
        Assert.Equal('E', back.Result!.DriveLetter);
        Assert.Equal(layout, back.Result.Layout);
        Assert.Equal(0xDEADBEEFu, back.Result.VolumeId);
        Assert.Equal(new uint[] { 7, 9 }, back.Result.BadClusters);
        Assert.True(back.Result.PartitionTypeChanged);
        Assert.Equal("FAT32", back.Result.MountedFileSystem);
        Assert.Equal(TimeSpan.FromSeconds(1.5), back.Result.Elapsed);
    }

    [Fact]
    public void LayoutDescriber_MentionsAlignment()
    {
        var layout = Fat32LayoutCalculator.Calculate(64L << 30, 512, 32768, 1 << 20, 16 << 20);
        string s = LayoutDescriber.Describe(layout, 16 << 20);
        Assert.Contains("32 KB", s);
        Assert.Contains("整列", s);
        Assert.DoesNotContain("未整列", s);
    }

    [Fact]
    public void DriveEnumerator_SystemDriveIsFlaggedAndExcluded()
    {
        char sys = DriveEnumerator.SystemDriveLetter;
        var entry = DriveEnumerator.Describe(sys);
        Assert.True(entry.IsSystemDrive);
        Assert.Equal(DriveKind.Fixed, entry.Kind);
        Assert.DoesNotContain(DriveEnumerator.Enumerate(includeFixed: true), d => d.Letter == sys);
    }

    [Fact]
    public void ValidateTarget_RejectsSystemDrive()
    {
        var ex = Assert.Throws<VolumeFormatException>(() =>
            VolumeFormatService.ValidateTarget(new FormatRequest { DriveLetter = DriveEnumerator.SystemDriveLetter, AllowFixedDisk = true }));
        Assert.Contains("システムドライブ", ex.Message);
    }
}
