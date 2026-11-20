using System.IO;
using HyperVManage.Services;
using Xunit;

namespace HyperVManage.Tests;

/// <summary>A Windows build checks there's room for the VM's disk before copying Windows onto it,
/// rather than failing 15 minutes in with "The device is not ready" when the drive fills.</summary>
public class DiskSpaceTests
{
    private static string Script() =>
        File.ReadAllText(Path.Combine(ScriptBuildingTests.RepoRoot(), "hyperv-rdp-vm", "New-HyperVRdpVM.ps1")).Replace("\r\n", "\n");

    /// <summary>The script's own Assert-RoomForWindows, run with the free space given.</summary>
    private static Task<string> Check(double imageGB, double freeGB, string folder = @"C:\ProgramData\Microsoft\Windows\Virtual Hard Disks")
    {
        var script = Script();
        const string begin = "# --- Assert-RoomForWindows (begin)", end = "# --- Assert-RoomForWindows (end)";
        var from = script.IndexOf(begin, StringComparison.Ordinal);
        var to = script.IndexOf(end, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, "Assert-RoomForWindows has moved.");
        return PowerShellRunner.RunAsync(
            "function Say([string]$Message) { Write-Output $Message }\n" + script[from..to] +
            $"\nAssert-RoomForWindows -ImageSize ([uint64]({imageGB} * 1GB)) -Folder {Ps.Quote(folder)} -Free ([uint64]({freeGB} * 1GB))",
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NotEnoughRoom_StopsTheBuild_SayingHowMuchIsNeededAndWhere()
    {
        // What happened on a host with 9.9 GB free, building Windows Server with the desktop (23 GB).
        var ex = await Assert.ThrowsAsync<HyperVException>(() => Check(23, 9.9));
        Assert.Contains("needs about 33 GB free for the VM's disk in C:\\ProgramData\\Microsoft\\Windows\\Virtual Hard Disks", ex.Message);
        Assert.Contains("C: has 9.9 GB", ex.Message);
        Assert.Contains("-VhdFolder", ex.Message);
    }

    [Fact]
    public async Task EnoughRoom_GoesAhead_AndSaysHowMuchThereIs()
    {
        var said = await Check(23, 120);
        Assert.Contains("Room for the VM's disk: 120.0 GB free on C:, and it needs about 33 GB.", said);
    }

    [Theory]
    [InlineData(19, 28.9, false)] // Windows 11 Pro, just short
    [InlineData(19, 29.1, true)]
    [InlineData(10.5, 20.6, true)] // Server Core needs less
    public async Task TheRoomNeeded_IsTheImagePlus10GB(double imageGB, double freeGB, bool goesAhead)
    {
        if (goesAhead) await Check(imageGB, freeGB);
        else await Assert.ThrowsAsync<HyperVException>(() => Check(imageGB, freeGB));
    }

    [Fact]
    public async Task ADriveOtherThanC_IsTheOneNamed()
    {
        var ex = await Assert.ThrowsAsync<HyperVException>(() => Check(19, 5, @"D:\VMs"));
        Assert.Contains("in D:\\VMs, and D: has 5.0 GB", ex.Message);
    }

    [Fact]
    public void TheCheck_ComesBeforeTheDiskIsMade()
    {
        var script = Script();
        var check = script.IndexOf("    Assert-RoomForWindows -ImageSize ([uint64]$info.ImageSize) -Folder $VhdFolder", StringComparison.Ordinal);
        var newVhd = script.IndexOf("Step \"Step 2 of 5: Creating a $DiskGB GB virtual disk", StringComparison.Ordinal);
        Assert.True(check > 0 && newVhd > check, "The room must be checked in step 1, before the disk is made and Windows copied.");
        Assert.True(script.IndexOf("function Assert-RoomForWindows", StringComparison.Ordinal) < check);
    }
}
