using System.IO;
using System.Text.Json;
using HyperVManage.Services;
using Xunit;

namespace HyperVManage.Tests;

/// <summary>
/// Against real Hyper-V, on a real drive: HYPERVMANAGE_LIVE_LOCATION names a folder to make VMs in
/// (an external SSD, say), and HYPERVMANAGE_LIVE_CLONE_SOURCE an off or saved VM to copy. It makes
/// a small VM laid out as the script lays one out, and a clone, both named HVM-Live-…, reads them
/// back through the list's own script, and deletes them through Delete's own script. The source VM
/// is only read. Needs a member of Hyper-V Administrators.
/// </summary>
public class LiveLocationTests
{
    public static string? Location => Environment.GetEnvironmentVariable("HYPERVMANAGE_LIVE_LOCATION");
    public static string? CloneSource => Environment.GetEnvironmentVariable("HYPERVMANAGE_LIVE_CLONE_SOURCE");
    public static bool HasLocation => !string.IsNullOrEmpty(Location);
    public static bool HasCloneSource => HasLocation && !string.IsNullOrEmpty(CloneSource);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> VmNamed(string name)
    {
        var all = PowerShellHyperVService.ParseVms(await PowerShellRunner.RunAsync(PowerShellHyperVService.ListScript, Ct));
        var vm = all.SingleOrDefault(v => v.Name == name);
        Assert.NotNull(vm);
        return JsonSerializer.SerializeToElement(new { vm!.Id, vm.Location, vm.DiskPaths });
    }

    private static async Task<string> IdOf(string name) =>
        (await PowerShellRunner.RunAsync($"(Get-VM | Where-Object Name -eq {Ps.Quote(name)}).Id.ToString()", Ct)).Trim();

    [Fact(Skip = "Set HYPERVMANAGE_LIVE_LOCATION to a folder to run it against real Hyper-V.", SkipUnless = nameof(HasLocation))]
    public async Task AVmMadeTheScriptsWay_IsListedWhereItIs_AndDeleteRemovesItsFolder()
    {
        var location = LocationText.Normalize(Location);
        Assert.Null(NewVmScript.LocationProblem(location));
        var name = $"HVM-Live-{Guid.NewGuid():N}"[..20];
        var own = NewVmScript.VmFolder(location, name);

        // The precheck looks where the script will build.
        using (var pre = JsonDocument.Parse(await PowerShellRunner.RunAsync(NewVmScript.BuildPrecheckScript(name, "", location), Ct)))
        {
            Assert.Equal(Path.Combine(own, "Virtual Hard Disks", name + ".vhdx"), pre.RootElement.GetProperty("Vhd").GetString());
            Assert.False(pre.RootElement.GetProperty("OwnFolderExisted").GetBoolean());
        }

        // The script's own layout: the disk in the VM's folder's Virtual Hard Disks, the VM made with
        // -Path, which puts its settings in a folder named after it.
        await PowerShellRunner.RunAsync($$"""
            $Location = {{Ps.Quote(location)}}
            $VMName = {{Ps.Quote(name)}}
            $VhdFolder = Join-Path (Join-Path $Location $VMName) 'Virtual Hard Disks'
            New-Item -ItemType Directory -Path $VhdFolder -Force | Out-Null
            $vhdPath = Join-Path $VhdFolder "$VMName.vhdx"
            New-VHD -Path $vhdPath -SizeBytes 1GB -Dynamic | Out-Null
            $where = @{ Path = $Location }
            New-VM -Name $VMName -Generation 2 -MemoryStartupBytes 1GB -VHDPath $vhdPath @where | Out-Null
            Set-VM -Name $VMName -AutomaticStartAction Nothing
            """, Ct);
        try
        {
            var listed = await VmNamed(name);
            Assert.Equal(own, listed.GetProperty("Location").GetString());
            Assert.Equal([Path.Combine(own, "Virtual Hard Disks", name + ".vhdx")],
                listed.GetProperty("DiskPaths").EnumerateArray().Select(e => e.GetString()));
        }
        finally
        {
            var result = PowerShellHyperVService.ParseDeleteResult(
                await PowerShellRunner.RunAsync(PowerShellHyperVService.BuildDeleteScript(await IdOf(name)), Ct));
            Assert.Empty(result.Failed);
            Assert.Contains(own, result.Deleted);
        }
        Assert.False(Directory.Exists(own));
    }

    [Fact(Skip = "Set HYPERVMANAGE_LIVE_LOCATION and HYPERVMANAGE_LIVE_CLONE_SOURCE to run it against real Hyper-V.", SkipUnless = nameof(HasCloneSource))]
    public async Task Clone_ToALocation_PutsTheCopyThere_AndLeavesNoExportBehind()
    {
        var location = LocationText.Normalize(Location);
        var name = $"HVM-Live-Clone-{Guid.NewGuid():N}"[..24];
        var own = NewVmScript.VmFolder(location, name);
        var sourceId = await IdOf(CloneSource!);
        Assert.NotEmpty(sourceId);
        var sourceBefore = await VmNamed(CloneSource!);

        await PowerShellRunner.RunAsync(PowerShellHyperVService.BuildCloneScript(sourceId, name, location), Ct);
        try
        {
            var copy = await VmNamed(name);
            Assert.Equal(own, copy.GetProperty("Location").GetString());
            Assert.All(copy.GetProperty("DiskPaths").EnumerateArray(),
                d => Assert.StartsWith(Path.Combine(own, "Virtual Hard Disks") + "\\", d.GetString()));
            Assert.Empty(Directory.GetDirectories(location, ".HyperVManage-clone-*"));
            // The source is only read.
            Assert.Equal(sourceBefore.GetProperty("DiskPaths").ToString(), (await VmNamed(CloneSource!)).GetProperty("DiskPaths").ToString());
        }
        finally
        {
            var result = PowerShellHyperVService.ParseDeleteResult(
                await PowerShellRunner.RunAsync(PowerShellHyperVService.BuildDeleteScript(await IdOf(name)), Ct));
            Assert.Empty(result.Failed);
        }
        Assert.False(Directory.Exists(own));
    }
}
