using System.Globalization;
using System.IO;
using HyperVManage.Services;
using Xunit;

namespace HyperVManage.Tests;

/// <summary>A build checks there's room for the VM's disk before making it, rather than failing 15
/// minutes in with "The device is not ready" when the drive fills while Windows is copied.</summary>
public class DiskSpaceTests
{
    private static string Script() =>
        File.ReadAllText(Path.Combine(ScriptBuildingTests.RepoRoot(), "hyperv-rdp-vm", "New-HyperVRdpVM.ps1")).Replace("\r\n", "\n");

    /// <summary>The script's own space functions, then <paramref name="call"/>.</summary>
    private static Task<string> Run(string call, string? location = null)
    {
        var script = Script();
        const string begin = "# --- Assert-RoomForWindows (begin)", end = "# --- Assert-RoomForWindows (end)";
        var from = script.IndexOf(begin, StringComparison.Ordinal);
        var to = script.IndexOf(end, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, "The space functions have moved.");
        var cd = location is null ? "" : $"Set-Location -LiteralPath {Ps.Quote(location)}\n";
        return PowerShellRunner.RunAsync(
            "function Say([string]$Message) { Write-Output $Message }\n" + script[from..to] + "\n" + cd + call,
            TestContext.Current.CancellationToken);
    }

    private static string GB(double gb) => $"([uint64]({gb.ToString(CultureInfo.InvariantCulture)} * 1GB))";

    private static Task<string> Check(double imageGB, double freeGB, string folder = @"C:\ProgramData\Microsoft\Windows\Virtual Hard Disks") =>
        Run($"Assert-RoomForWindows -ImageSize {GB(imageGB)} -Edition 'Windows Server 2025 Datacenter Evaluation (Desktop Experience)' -Folder {Ps.Quote(folder)} -Free {GB(freeGB)}");

    [Fact]
    public async Task NotEnoughRoom_StopsTheBuild_SayingWhatNeedsHowMuchAndWhere()
    {
        // What happened on a host with 9.9 GB free, building Windows Server with the desktop (23 GB).
        var ex = await Assert.ThrowsAsync<HyperVException>(() => Check(23, 9.9));
        Assert.StartsWith("Windows Server 2025 Datacenter Evaluation (Desktop Experience) needs about 33 GB free for the VM's disk, " +
            @"in C:\ProgramData\Microsoft\Windows\Virtual Hard Disks, and there's only 9 GB there.", ex.Message);
        // Where an app user can change the folder, as well as the script's own parameter.
        Assert.Contains("Hyper-V Settings", ex.Message);
        Assert.Contains("-VhdFolder", ex.Message);
    }

    [Fact]
    public async Task JustShort_NeverSoundsAsIfThereIsEnough()
    {
        var ex = await Assert.ThrowsAsync<HyperVException>(() => Check(23, 32.97));
        Assert.Contains("needs about 33 GB", ex.Message);
        Assert.Contains("there's only 32 GB", ex.Message);
    }

    [Fact]
    public async Task EnoughRoom_GoesAhead_SayingNothing()
    {
        Assert.Equal("", (await Check(23, 120)).Trim());
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
    public async Task UbuntuUsesTheSameCheck()
    {
        var ex = await Assert.ThrowsAsync<HyperVException>(() =>
            Run($"Assert-Room -Needed 20GB -What 'The Ubuntu desktop' -Folder 'D:\\VMs' -Free {GB(5)}"));
        Assert.StartsWith(@"The Ubuntu desktop needs about 20 GB free for the VM's disk, in D:\VMs, and there's only 5 GB there.", ex.Message);
        var script = Script();
        Assert.Contains("Assert-Room -Needed 20GB -What 'The Ubuntu desktop' -Folder $VhdFolder", script);
        Assert.DoesNotContain(".PSDrive.Free", script);
    }

    // --- Measuring a real folder -------------------------------------------------------------

    private static async Task<double?> Measure(string folder, string? location = null)
    {
        var output = (await Run($"$f = Get-FreeSpace {Ps.Quote(folder)}; if ($null -eq $f) {{ 'none' }} else {{ [string]$f }}", location)).Trim();
        return output == "none" ? null : double.Parse(output, CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task AFolderIsMeasured_WhereverItIs()
    {
        var temp = Path.GetTempPath();
        var expected = new DriveInfo(Path.GetPathRoot(temp)!).AvailableFreeSpace;
        var measured = await Measure(temp);
        Assert.NotNull(measured);
        Assert.InRange(measured!.Value, expected - 2e9, expected + 2e9); // other programs write meanwhile
    }

    [Fact]
    public async Task ARelativeFolder_IsMeasured_NotAnError()
    {
        var temp = Path.GetTempPath();
        Assert.NotNull(await Measure(".", temp));
        // With plenty of room the check goes ahead, quietly, for a relative folder too.
        Assert.Equal("", (await Run($"Assert-Room -Needed 1MB -What 'x' -Folder '.'", temp)).Trim());
    }

    [Fact]
    public async Task AShare_IsMeasured_NotAnError()
    {
        var share = $@"\\localhost\{Path.GetPathRoot(Path.GetTempPath())!.TrimEnd('\\', ':')}$\";
        if (!Directory.Exists(share)) Assert.Skip($"{share} can't be opened here (it needs administrator rights).");
        Assert.NotNull(await Measure(share));
    }

    [Fact]
    public async Task AFolderThatCantBeMeasured_DoesntStopTheBuild()
    {
        var said = await Run($"Assert-Room -Needed 20GB -What 'x' -Folder {Ps.Quote(@"Q:\no\such\folder")}");
        Assert.Contains("Couldn't tell how much room there is", said);
    }

    // --- Where the check is ------------------------------------------------------------------

    [Fact]
    public void TheWindowsCheck_IsInStepOne_WhereAFailureDismountsTheIso_BeforeTheDiskIsMade()
    {
        var script = Script();
        var stepOne = script.IndexOf("try {\n    Step 'Step 1 of 5: Reading the ISO.'", StringComparison.Ordinal);
        var check = script.IndexOf("    Assert-RoomForWindows -ImageSize ([uint64]$info.ImageSize) -Edition $info.ImageName -Folder $VhdFolder", StringComparison.Ordinal);
        var newVhd = script.IndexOf("Step \"Step 2 of 5: Creating a $DiskGB GB virtual disk", StringComparison.Ordinal);
        Assert.True(stepOne > 0 && check > stepOne && newVhd > check,
            "The room must be checked inside step 1's try, so the ISO is dismounted, and before the disk is made.");
        // Defined before both builds, Ubuntu's included, which comes first and returns.
        Assert.True(script.IndexOf("function Assert-Room(", StringComparison.Ordinal) < script.IndexOf("Step 'Step 1 of 5: Getting Ubuntu.'", StringComparison.Ordinal));
    }
}
