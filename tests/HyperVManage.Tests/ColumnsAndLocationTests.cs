using System.IO;
using HyperVManage.Helpers;
using HyperVManage.Models;
using HyperVManage.Services;
using HyperVManage.ViewModels;
using Xunit;

namespace HyperVManage.Tests;

/// <summary>The rules of the spoken row and the Columns editor, from the baseline's columns.md.</summary>
public class ColumnRuleTests
{
    private static readonly ColumnSet Set = new("t",
    [
        new("title", "Title", "Title"),
        new("state", "State", "State", ColumnKind.State),
        new("author", "Author", "Author"),
        new("count", "Comments", "Count", ColumnKind.Count),
        new("where", "Location", "Where", SpokenLabel: "Kept in"),
    ],
    ["title", "state", "author"]);

    private static string Row(IEnumerable<string> ids, bool names, Dictionary<string, string> values) =>
        RowText.Compose(Set.Defs(ids), c => values.GetValueOrDefault(c.Id), names);

    [Fact]
    public void TheFirstColumn_IsNeverNamed_AndStateAndCountNeverAre()
    {
        var values = new Dictionary<string, string> { ["title"] = "Fix crash", ["state"] = "open", ["author"] = "Kelly", ["count"] = "3 comments" };
        Assert.Equal("Fix crash, open, Author: Kelly, 3 comments", Row(["title", "state", "author", "count"], true, values));
        // Even a text column put first.
        Assert.Equal("Kelly, Title: Fix crash", Row(["author", "title"], true, values));
        Assert.Equal("Fix crash, open, Kelly, 3 comments", Row(["title", "state", "author", "count"], false, values));
    }

    [Fact]
    public void EmptyValues_AreLeftOut_AndASpokenLabelIsUsed()
    {
        var values = new Dictionary<string, string> { ["title"] = "A", ["author"] = "  ", ["where"] = @"D:\VMs" };
        Assert.Equal(@"A, Kept in: D:\VMs", Row(["title", "author", "where"], true, values));
        Assert.Equal("", Row(["author"], true, values));
    }

    [Fact]
    public void AfterAValueThatEndsAPhrase_OnlyASpaceFollows()
    {
        var values = new Dictionary<string, string> { ["title"] = "Is it fixed?", ["state"] = "open" };
        Assert.Equal("Is it fixed? open", Row(["title", "state"], true, values));
    }

    [Fact]
    public void Reconcile_DropsUnknownAndRepeated_KeepsOrder_AndFallsBackToTheDefaults()
    {
        Assert.Equal(["author", "title"], Set.Reconcile(["author", "gone", "title", "author", null]));
        Assert.Equal(Set.Defaults, Set.Reconcile(["gone"]));
        Assert.Equal(Set.Defaults, Set.Reconcile(null));
        Assert.Equal(Set.Defaults, Set.Reconcile([]));
    }

    [Fact]
    public void TheEditor_SaysWhatEachActionDid_NamingTheNeighbour()
    {
        var e = new ColumnLayoutEditor(Set, ["title", "state", "author"]);
        Assert.Equal(["count", "where"], e.Available);

        Assert.True(e.Add(1));
        Assert.Equal("Location added at the end, 4 of 4.", e.Said);

        Assert.True(e.Move(3, 1));
        Assert.Equal("Location moved above State.", e.Said);
        Assert.True(e.Move(1, 2));
        Assert.Equal("Location moved below State.", e.Said);
        Assert.True(e.Move(2, 0));
        Assert.Equal("Location moved to the top.", e.Said);
        Assert.True(e.Move(0, 3));
        Assert.Equal("Location moved to the bottom.", e.Said);
        Assert.Equal(["title", "state", "author", "where"], e.Shown);

        Assert.False(e.Move(0, -1));
        Assert.Equal("Title is already at the top.", e.Said);
        Assert.False(e.Move(3, 4));
        Assert.Equal("Location is already at the bottom.", e.Said);
        Assert.False(e.Move(-1, 0));
        Assert.Equal("No column is selected.", e.Said);

        Assert.True(e.Remove(1));
        Assert.Equal("State removed, 3 columns shown.", e.Said);
        Assert.Equal(["state", "count"], e.Available);

        e.Reset();
        Assert.Equal(Set.Defaults, e.Shown);
        Assert.Equal("Columns reset to the default.", e.Said);
    }

    [Fact]
    public void TheEditor_KeepsAtLeastOneColumn_AndSaysWhenEveryOneIsShown()
    {
        var e = new ColumnLayoutEditor(Set, ["title"]);
        Assert.False(e.Remove(0));
        Assert.Equal("At least one column has to be shown.", e.Said);
        Assert.False(e.Move(0, 1));
        Assert.Equal("Title is the only column shown.", e.Said);
        for (var i = 0; i < 4; i++) Assert.True(e.Add(0));
        Assert.False(e.Add(0));
        Assert.Equal("Every column is shown.", e.Said);
        Assert.True(e.Remove(4));
        Assert.True(e.Remove(3));
        Assert.True(e.Remove(2));
        Assert.True(e.Remove(1));
        Assert.Equal("State removed, 1 column shown.", e.Said);
    }

    [Fact]
    public void TheColumnsDialogsModel_KeepsBothListsInStep_AndThePreviewIsTheRow()
    {
        var vm = new VmInfo("1") { Name = "Lab", State = "Off", Location = @"D:\Hyper-V\Lab" };
        var editor = new ColumnsViewModel(VmColumns.Set, ["name", "state"], speakNames: true,
            (columns, speak) => new VmRowFormat(columns, speak).Compose(vm));
        Assert.Equal("Lab, Off", editor.Preview);

        var location = editor.Available.ToList().FindIndex(c => c.Id == "location");
        Assert.True(editor.Add(location));
        Assert.Equal(["name", "state", "location"], editor.Shown.Select(c => c.Id));
        Assert.DoesNotContain(editor.Available, c => c.Id == "location");
        Assert.Equal(@"Lab, Off, Location: D:\Hyper-V\Lab", editor.Preview);

        var moved = editor.Shown[2];
        Assert.True(editor.Move(2, 1));
        Assert.Same(moved, editor.Shown[1]);
        Assert.Equal("Location moved above State.", editor.Said);

        editor.SpeakNames = false;
        Assert.Equal(@"Lab, D:\Hyper-V\Lab, Off", editor.Preview);

        // Back in Available in the set's order, not at the end.
        Assert.True(editor.Remove(1));
        Assert.Equal(VmColumns.Set.Ids.Where(id => id is not ("name" or "state")), editor.Available.Select(c => c.Id));
        Assert.Equal("Name", editor.Shown[0].ToString());
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Settings_RoundTrip_AndAMissingFileGivesTheDefaults()
    {
        var folder = Directory.CreateTempSubdirectory("hvm-settings-").FullName;
        try
        {
            var store = new SettingsStore(Path.Combine(folder, "settings.json"));
            var fresh = store.Load();
            Assert.Null(fresh.VmListColumns);
            Assert.Null(fresh.SpeakColumnNames);

            Assert.True(store.Save(new SettingsData { VmListColumns = ["name", "location"], SpeakColumnNames = false, VmLocation = @"D:\Hyper-V" }));
            var back = new SettingsStore(store.FilePath).Load();
            Assert.Equal(["name", "location"], back.VmListColumns);
            Assert.False(back.SpeakColumnNames);
            Assert.Equal(@"D:\Hyper-V", back.VmLocation);
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void AFileThatCantBeRead_IsSetAside_NotOverwritten()
    {
        var folder = Directory.CreateTempSubdirectory("hvm-settings-").FullName;
        try
        {
            var file = Path.Combine(folder, "settings.json");
            File.WriteAllText(file, "{ this isn't json");
            var store = new SettingsStore(file);
            Assert.Null(store.Load().VmListColumns);
            Assert.False(File.Exists(file));
            var aside = Assert.Single(Directory.GetFiles(folder, "settings.json.unreadable-*"));
            Assert.Equal("{ this isn't json", File.ReadAllText(aside));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ASavedLayout_IsCleanedUp_AndColumnsAddedLaterStayOff()
    {
        var store = new InMemorySettingsStore();
        store.Save(new SettingsData { VmListColumns = ["state", "retired-column", "name"] });
        var vm = new MainViewModel(new DemoHyperVService(), settings: store);
        Assert.Equal(["state", "name"], vm.RowFormat.Columns.Select(c => c.Id));
        // Never chosen: names are spoken.
        Assert.True(vm.RowFormat.SpeakNames);
    }
}

public class LocationTests
{
    private static string? Ntfs(string root) => root == @"D:\" ? "NTFS" : root == @"E:\" ? "exFAT" : null;

    [Theory]
    [InlineData("", null)]
    [InlineData(@"D:\Hyper-V", null)]
    [InlineData(@"\\server\share\VMs", null)]
    [InlineData(@"\\server", "server and the share")]
    [InlineData(@"Hyper-V", "full path")]
    [InlineData(@"D:Hyper-V", "full path")]
    [InlineData(@"E:\VMs", "NTFS or ReFS, and E:\\ is exFAT")]
    [InlineData(@"F:\VMs", "no drive F:\\")]
    [InlineData(@"D:\VMs?", "can't contain")]
    public void LocationProblem_SaysWhyAFolderCantHoldAVm(string location, string? problem)
    {
        var said = NewVmScript.LocationProblem(location, Ntfs);
        if (problem is null) Assert.Null(said);
        else Assert.Contains(problem, said);
    }

    [Theory]
    [InlineData("  D:\\Hyper-V\\  ", @"D:\Hyper-V")]
    [InlineData("\"D:\\Hyper-V\"", @"D:\Hyper-V")]
    [InlineData(@"D:\", @"D:\")]
    [InlineData("D:", @"D:\")]
    [InlineData("", "")]
    public void TheLocationBox_IsTidied(string typed, string location) => Assert.Equal(location, LocationText.Normalize(typed));

    [Fact]
    public void TheSentenceUnderTheBox_SaysWhereTheVmGoes()
    {
        Assert.Equal(@"The VM goes in D:\Hyper-V\Lab, with its disk in Virtual Hard Disks there.", LocationText.Describe(@"D:\Hyper-V\", "Lab"));
        Assert.StartsWith("Empty means Hyper-V's own default folders", LocationText.Describe("", "Lab"));
        Assert.StartsWith("The copy goes in a folder named after it", LocationText.Describe(@"D:\X", "", "The copy"));
    }

    [Fact]
    public void NewVm_PassesTheLocation_RefusesABadOne_AndSaysWhichWasChosen()
    {
        string? chosen = null;
        var n = new NewVmViewModel([], new InMemoryIsoHistory(), ubuntu: true, location: @"E:\VMs")
        {
            CheckLocation = l => NewVmScript.LocationProblem(l, Ntfs),
            LocationChosen = l => chosen = l,
        };
        n.Password = "pw";
        Assert.Null(n.Validate());
        Assert.Contains("exFAT", n.Error);

        n.Location = "  D:\\Hyper-V\\ ";
        var options = n.Validate();
        Assert.NotNull(options);
        Assert.Equal(@"D:\Hyper-V", options!.Location);
        Assert.Contains(@"-Location 'D:\Hyper-V'", NewVmScript.BuildCommand(options, "p"));
        Assert.Contains(@"D:\Hyper-V\" + n.VmName.Trim(), n.LocationDescription);

        n.Location = "";
        Assert.Equal("", n.Validate()!.Location);
        Assert.DoesNotContain("-Location", NewVmScript.BuildCommand(n.Validate()!, "p"));
        Assert.Null(chosen);
    }

    [Fact]
    public void ThePrecheck_LooksWhereTheScriptWillPutTheDisks()
    {
        var precheck = NewVmScript.BuildPrecheckScript("Lab", "", @"D:\Hyper-V");
        Assert.Contains(@"$location = 'D:\Hyper-V'", precheck);
        Assert.Contains("Join-Path $own 'Virtual Hard Disks'", precheck);
        Assert.Contains("OwnFolderExisted", precheck);
        // The script puts them in the same place.
        var script = NewVmScript.ScriptText();
        Assert.Contains("Join-Path (Join-Path $Location $VMName) 'Virtual Hard Disks'", script);
        Assert.Contains("@{ Path = $Location }", script);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task TheCleanupAfterAStop_RemovesTheVmsOwnFolder_OnlyOnceItsEmpty(bool somethingElseThere, bool removed)
    {
        var location = Directory.CreateTempSubdirectory("hvm-location-").FullName;
        try
        {
            var own = Path.Combine(location, "Lab");
            var disks = Directory.CreateDirectory(Path.Combine(own, "Virtual Hard Disks")).FullName;
            var vhd = Path.Combine(disks, "Lab.vhdx");
            File.WriteAllText(vhd, "disk");
            if (somethingElseThere) File.WriteAllText(Path.Combine(own, "notes.txt"), "mine");
            var stubs = """
                function Get-VM { }
                function Dismount-VHD { param($Path, $ErrorAction) }
                function Dismount-DiskImage { param($ImagePath, $ErrorAction) }

                """;
            var said = await UbuntuBuildTests.RunPsForTests(stubs + NewVmScript.BuildCleanupScript("Lab", "", vhd, ownFolder: own));
            Assert.False(File.Exists(vhd));
            Assert.Equal(!removed, Directory.Exists(own));
            if (removed) Assert.Contains("Removed the empty folder", said);
        }
        finally { Directory.Delete(location, true); }
    }

    /// <summary>The script's own Location checks, run in Windows PowerShell 5.1 as the build runs
    /// them, against this PC's system drive (NTFS on any Windows PC) and a temporary file.</summary>
    [Fact]
    public async Task TheScriptsLocationChecks_RefuseWhatHyperVWould()
    {
        var script = NewVmScript.ScriptText().Replace("\r\n", "\n");
        var start = script.IndexOf("# --- Location (begin)", StringComparison.Ordinal);
        var end = script.IndexOf("# --- Location (end)", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "The script's Location checks weren't found.");
        var checks = script[start..end];
        var file = Path.GetTempFileName();
        try
        {
            string Try(string location) => $$"""
                $Location = {{Ps.Quote(location)}}
                try { {{checks}} 'fine' } catch { $_.Exception.Message }

                """;
            var system = Path.GetPathRoot(Environment.SystemDirectory)!;
            var said = await UbuntuBuildTests.RunPsForTests(
                Try(Path.Combine(system, "HyperVManage-no-such-folder")) + Try("relative\\path") + Try(file) + Try(@"Q:\VMs"));
            var lines = said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal("fine", lines[0]);
            Assert.Contains("must be a full path", lines[1]);
            Assert.Contains("is a file, not a folder", lines[2]);
            // No PC in CI has a Q: drive.
            Assert.Contains("Can't reach Q:\\", lines[3]);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Clone_ToALocation_KeepsTheCopyAndItsExportThere_AndChecksForRoom()
    {
        var clone = PowerShellHyperVService.BuildCloneScript("id", "Copy", @"D:\Hyper-V");
        Assert.Contains(@"$location = 'D:\Hyper-V'", clone);
        Assert.Contains("$vmFolder = Join-Path $location $newName", clone);
        Assert.Contains("$diskFolder = Join-Path $vmFolder 'Virtual Hard Disks'", clone);
        Assert.Contains("$exportParent = $location", clone);
        Assert.Contains("function Get-FreeSpace(", clone);
        Assert.Contains("for the copy and a temporary export of it", clone);

        // Without one, Hyper-V's folders, as before.
        var plain = PowerShellHyperVService.BuildCloneScript("id", "Copy");
        Assert.Contains("$location = ''", plain);
        Assert.Contains("Join-Path $vmHost.VirtualMachinePath $newName", plain);
    }

    [Fact]
    public void Delete_RemovesTheFolderAVmWasGivenInALocation_ButNeverHyperVsOwn()
    {
        var delete = PowerShellHyperVService.BuildDeleteScript("id");
        Assert.Contains("(Split-Path -Leaf $own.TrimEnd('\\')) -eq $name", delete);
        Assert.Contains("$vmHost.VirtualMachinePath, $vmHost.VirtualHardDiskPath", delete);
    }

    [Fact]
    public void TheDemo_PutsAVmGivenALocationInAFolderOfItsOwn()
    {
        var demo = new DemoHyperVService();
        var vms = demo.GetVmsAsync(TestContext.Current.CancellationToken).Result;
        var agent = vms.Single(v => v.Name == "Build Agent");
        Assert.Equal(@"D:\Hyper-V\Build Agent", agent.Location);
        Assert.Equal([@"D:\Hyper-V\Build Agent\Virtual Hard Disks\Build Agent.vhdx"], agent.DiskPaths);
    }
}
