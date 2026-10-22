using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using HyperVManage.Models;
using HyperVManage.Services;
using HyperVManage.ViewModels;
using HyperVManage.Views;
using Xunit;

namespace HyperVManage.Tests;

/// <summary>The checkpoint list under the VM list, and what it can do (issue 13).</summary>
public class CheckpointListTests
{
    private static async Task<(MainViewModel vm, DemoHyperVService demo, List<string> said)> Make(string vmName = "Build Agent")
    {
        var demo = new DemoHyperVService { Delay = TimeSpan.Zero };
        var vm = new MainViewModel(demo);
        await vm.RefreshAsync();
        vm.Selected = vm.Vms.Single(v => v.Name == vmName);
        var said = new List<string>();
        vm.Announce += said.Add;
        return (vm, demo, said);
    }

    private static string[] Names(MainViewModel vm) => vm.Checkpoints.Select(c => c.Name).ToArray();

    [Fact]
    public async Task SelectingAVm_ListsItsCheckpointsNewestFirst_AndSaysWhose()
    {
        var (vm, _, _) = await Make();
        Assert.Equal(["Before the October update", "Tools installed", "Fresh agent"], Names(vm));
        Assert.Equal("Checkpoints of Build Agent", vm.CheckpointsHeading);
        Assert.Equal("Checkpoints of Build Agent", vm.CheckpointsListName);
        Assert.Same(vm.Checkpoints[0], vm.SelectedCheckpoint);
        Assert.True(vm.Checkpoints[0].Info.IsCurrent);
        Assert.Equal("The VM is now based on this", vm.Checkpoints[0].NowText);
        Assert.Equal("", vm.Checkpoints[1].NowText);

        vm.Selected = vm.Vms.Single(v => v.Name == "Test2");
        Assert.Equal(["Clean install"], Names(vm));
        Assert.Equal("Checkpoints of Test2", vm.CheckpointsHeading);
    }

    [Fact]
    public async Task AVmWithNone_SaysSoInTheListsName_SoTabbingThereExplainsTheEmptyList()
    {
        var (vm, _, _) = await Make("Win11-RDP");
        Assert.Empty(vm.Checkpoints);
        Assert.True(vm.IsCheckpointListEmpty);
        Assert.Equal("Checkpoints of Win11-RDP. Win11-RDP has no checkpoints. Ctrl+K makes one.", vm.CheckpointsListName);
        Assert.False(vm.DeleteCheckpointsCommand.CanExecute(null));
        Assert.False(vm.ApplySelectedCheckpointCommand.CanExecute(null));

        vm.RequestCheckpointName = _ => "First";
        await vm.CheckpointCommand.ExecuteAsync(null);
        Assert.Equal(["First"], Names(vm));
        Assert.False(vm.IsCheckpointListEmpty);
        Assert.Equal("Checkpoints of Win11-RDP", vm.CheckpointsListName);
    }

    [Fact]
    public async Task DeleteOne_AsksAboutJustThatOne_LeavesTheVmAsItIs_AndSelectsTheNextRow()
    {
        var (vm, _, said) = await Make();
        var agent = vm.Selected!;
        var state = agent.State;
        var middle = vm.Checkpoints[1];
        vm.SelectedCheckpoint = middle;
        IReadOnlyList<CheckpointInfo>? asked = null;
        bool? askedWithLater = null;
        vm.ConfirmDeleteCheckpoints = (_, going, withLater) => { asked = going; askedWithLater = withLater; return true; };
        var replaced = 0;
        vm.CheckpointSelectionReplaced += () => replaced++;

        await vm.DeleteCheckpointsCommand.ExecuteAsync(null);

        Assert.Equal("Tools installed", Assert.Single(asked!).Name);
        Assert.False(askedWithLater);
        Assert.Equal(["Before the October update", "Fresh agent"], Names(vm));
        Assert.Equal(2, agent.CheckpointCount);
        Assert.Equal(state, agent.State);
        // The row that took its place, so pressing Delete again goes on down the list.
        Assert.Equal("Fresh agent", vm.SelectedCheckpoint!.Name);
        Assert.Equal(1, replaced);
        Assert.Contains("Deleted checkpoint Tools installed of Build Agent.", said);
        // What came after it now follows from the one before it.
        Assert.Equal(vm.Checkpoints[1].Id, vm.Checkpoints[0].Info.ParentId);
    }

    [Fact]
    public async Task DeleteAnswerNo_DeletesNothing()
    {
        var (vm, _, _) = await Make();
        vm.ConfirmDeleteCheckpoints = (_, _, _) => false;
        await vm.DeleteCheckpointsCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Checkpoints.Count);
        Assert.Equal(3, vm.Selected!.CheckpointCount);
    }

    [Fact]
    public async Task DeleteSeveral_DeletesEveryOneChosen_AskingOnce_InTheListsOrder()
    {
        var (vm, _, said) = await Make();
        // Chosen bottom first, as Shift+Up would.
        vm.SelectedCheckpoints = [vm.Checkpoints[2], vm.Checkpoints[1]];
        var asks = 0;
        IReadOnlyList<CheckpointInfo>? asked = null;
        vm.ConfirmDeleteCheckpoints = (_, going, _) => { asks++; asked = going; return true; };

        await vm.DeleteCheckpointsCommand.ExecuteAsync(null);

        Assert.Equal(1, asks);
        Assert.Equal(["Tools installed", "Fresh agent"], asked!.Select(c => c.Name));
        Assert.Equal(["Before the October update"], Names(vm));
        Assert.Contains("Deleted 2 checkpoints of Build Agent.", said);
        Assert.Empty(vm.SelectedCheckpoints);
        // Several chosen: Apply and Rename need just one.
        vm.SelectedCheckpoints = [vm.Checkpoints[0]];
        Assert.True(vm.RenameCheckpointCommand.CanExecute(null));
    }

    [Fact]
    public async Task ApplyAndRename_NeedExactlyOneChosen()
    {
        var (vm, _, _) = await Make();
        vm.SelectedCheckpoints = [vm.Checkpoints[0], vm.Checkpoints[1]];
        Assert.False(vm.ApplySelectedCheckpointCommand.CanExecute(null));
        Assert.False(vm.RenameCheckpointCommand.CanExecute(null));
        Assert.False(vm.DeleteCheckpointAndLaterCommand.CanExecute(null));
        Assert.True(vm.DeleteCheckpointsCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeleteWithThoseAfterIt_OnlyWhereThereAreSome_AndTakesThemAll()
    {
        var (vm, _, said) = await Make();
        vm.SelectedCheckpoint = vm.Checkpoints[0]; // the newest: nothing after it
        Assert.False(vm.DeleteCheckpointAndLaterCommand.CanExecute(null));

        vm.SelectedCheckpoint = vm.Checkpoints[2]; // the oldest
        Assert.True(vm.DeleteCheckpointAndLaterCommand.CanExecute(null));
        IReadOnlyList<CheckpointInfo>? asked = null;
        vm.ConfirmDeleteCheckpoints = (_, going, withLater) => { Assert.True(withLater); asked = going; return true; };

        await vm.DeleteCheckpointAndLaterCommand.ExecuteAsync(null);

        Assert.Equal(["Fresh agent", "Before the October update", "Tools installed"], asked!.Select(c => c.Name));
        Assert.Empty(vm.Checkpoints);
        Assert.Equal(0, vm.Selected!.CheckpointCount);
        Assert.Contains("Deleted 3 checkpoints of Build Agent.", said);
        Assert.Contains("Build Agent has no checkpoints", vm.CheckpointsListName);
    }

    [Fact]
    public async Task Rename_UpdatesTheRowInPlace_SoAScreenReaderStaysOnIt()
    {
        var (vm, demo, said) = await Make();
        var row = vm.Checkpoints[1];
        vm.SelectedCheckpoint = row;
        CheckpointInfo? offered = null;
        vm.RequestCheckpointRename = (_, cp) => { offered = cp; return "  Tools and SDKs  "; };

        await vm.RenameCheckpointCommand.ExecuteAsync(null);

        Assert.Equal("Tools installed", offered!.Name);
        Assert.Same(row, vm.Checkpoints[1]);
        Assert.Equal("Tools and SDKs", row.Name);
        Assert.StartsWith("Tools and SDKs, taken ", row.AccessibleName);
        Assert.Contains("Checkpoint Tools installed of Build Agent is now called Tools and SDKs.", said);
        var stored = await demo.GetCheckpointsAsync(vm.Selected!.Id, TestContext.Current.CancellationToken);
        Assert.Equal("Tools and SDKs", stored[1].Name);
    }

    [Fact]
    public async Task Rename_CancelledOrUnchanged_DoesNothing()
    {
        var (vm, _, said) = await Make();
        vm.RequestCheckpointRename = (_, _) => null;
        await vm.RenameCheckpointCommand.ExecuteAsync(null);
        vm.RequestCheckpointRename = (_, cp) => cp.Name;
        await vm.RenameCheckpointCommand.ExecuteAsync(null);
        Assert.Empty(said);
    }

    [Fact]
    public async Task ApplyFromTheList_StartsTheApplyWindowOnTheChosenCheckpoint()
    {
        var (vm, _, said) = await Make();
        vm.SelectedCheckpoint = vm.Checkpoints[2];
        CheckpointInfo? startedOn = null;
        vm.RequestCheckpointToApply = (_, list, chosen) => { startedOn = chosen; return new CheckpointChoice(chosen!, SaveCurrentFirst: false); };

        await vm.ApplySelectedCheckpointCommand.ExecuteAsync(null);

        Assert.Equal("Fresh agent", startedOn!.Name);
        // The list shows which one the VM is now based on.
        Assert.True(vm.Checkpoints.Single(c => c.Name == "Fresh agent").Info.IsCurrent);
        Assert.False(vm.Checkpoints.Single(c => c.Name == "Before the October update").Info.IsCurrent);
        Assert.Contains(said, s => s.StartsWith("Build Agent is back at checkpoint Fresh agent"));
    }

    [Fact]
    public async Task TheVmMenusApply_StillStartsOnTheNewest()
    {
        var (vm, _, _) = await Make();
        vm.SelectedCheckpoint = vm.Checkpoints[2];
        var asked = false;
        vm.RequestCheckpointToApply = (_, _, chosen) => { asked = true; Assert.Null(chosen); return null; };
        await vm.ApplyCheckpointCommand.ExecuteAsync(null);
        Assert.True(asked);
    }

    [Fact]
    public async Task ApplyFromTheList_OfOneDeletedElsewhere_SaysSo_AndReadsTheListAgain()
    {
        var (vm, demo, said) = await Make();
        var gone = vm.Checkpoints[2];
        vm.SelectedCheckpoint = gone;
        await demo.DeleteCheckpointsAsync(vm.Selected!.Id, [gone.Id], includeLater: false, TestContext.Current.CancellationToken);
        var asked = false;
        vm.RequestCheckpointToApply = (_, _, _) => { asked = true; return null; };

        await vm.ApplySelectedCheckpointCommand.ExecuteAsync(null);

        Assert.False(asked);
        Assert.Contains("That checkpoint no longer exists.", said);
        Assert.DoesNotContain(vm.Checkpoints, c => c.Id == gone.Id);
    }

    [Fact]
    public async Task Refresh_ReadsTheCheckpointsAgain_KeepingTheRowsThatStayed()
    {
        var (vm, demo, _) = await Make();
        var rows = vm.Checkpoints.ToList();
        await demo.RenameCheckpointAsync(vm.Selected!.Id, rows[0].Id, "Renamed in Hyper-V Manager", TestContext.Current.CancellationToken);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(rows, vm.Checkpoints);
        Assert.Equal("Renamed in Hyper-V Manager", rows[0].Name);
    }

    [Fact]
    public void Merge_PutsNewOnesInOrder_AndMovesNoRowThatStayed()
    {
        var vm = new MainViewModel(new DemoHyperVService { Delay = TimeSpan.Zero });
        var a = new CheckpointInfo("a", "A", new DateTime(2026, 1, 1), false);
        var b = new CheckpointInfo("b", "B", new DateTime(2026, 2, 1), false, "a");
        vm.MergeCheckpoints([b, a]);
        var rowA = vm.Checkpoints[1];

        var c = new CheckpointInfo("c", "C", new DateTime(2026, 3, 1), true, "b");
        vm.MergeCheckpoints([c, b, a]);

        Assert.Equal(["C", "B", "A"], Names(vm));
        Assert.Same(rowA, vm.Checkpoints[2]);
    }

    [Fact]
    public void TheDeleteQuestion_NamesWhatGoes_AndSaysTheVmItselfStaysAsItIs()
    {
        var vm = new VmInfo("x") { Name = "Build Agent" };
        CheckpointInfo Cp(string name) => new(name, name, new DateTime(2026, 1, 1), false);

        Assert.Equal(
            "Delete checkpoint Old from Build Agent?\n\n" +
            "You won't be able to take Build Agent back to that point any more. Build Agent itself won't change, and neither will its other checkpoints.\n\n" +
            "This can't be undone.",
            MainViewModel.DeleteCheckpointsQuestion(vm, [Cp("Old")], withLater: false));

        Assert.Equal(
            "Delete these 2 checkpoints from Build Agent?\n\nOld\nOlder\n\n" +
            "You won't be able to take Build Agent back to those points any more. Build Agent itself won't change, and neither will its other checkpoints.\n\n" +
            "This can't be undone.",
            MainViewModel.DeleteCheckpointsQuestion(vm, [Cp("Old"), Cp("Older")], withLater: false));

        var later = MainViewModel.DeleteCheckpointsQuestion(vm, [Cp("Root"), Cp("Next")], withLater: true);
        Assert.StartsWith("Delete checkpoint Root from Build Agent, and the checkpoint taken after it?\n\nNext\n\n", later);
        Assert.Equal(
            "Delete checkpoint Root from Build Agent, and the 2 checkpoints taken after it?\n\nNext\nLast\n\n" +
            "You won't be able to take Build Agent back to any of those points. Build Agent itself won't change.\n\n" +
            "This can't be undone.",
            MainViewModel.DeleteCheckpointsQuestion(vm, [Cp("Root"), Cp("Next"), Cp("Last")], withLater: true));
    }

    [Fact]
    public void TheVmDeleteQuestion_SaysWhatGoes_AndMentionsTurningOffOnlyForAVmThatIsOn()
    {
        var running = new VmInfo("x") { Name = "Win11-RDP", State = "Running" };
        Assert.Equal(
            "Delete Win11-RDP?\n\n" +
            "This permanently deletes the virtual machine, its checkpoints, and this disk file:\nC:\\VMs\\Win11-RDP.vhdx\n\n" +
            "Win11-RDP is running, so it's turned off first. The desktop connection file Win11-RDP.rdp and its saved sign-in " +
            "are deleted too, if they connect to this VM.\n\nThis can't be undone.",
            MainWindow.DeleteVmQuestion(running, [@"C:\VMs\Win11-RDP.vhdx"]));

        var off = MainWindow.DeleteVmQuestion(new VmInfo("y") { Name = "Test2", State = "Off" }, ["a.vhdx", "b.vhdx"]);
        Assert.Contains("and these disk files:\na.vhdx\nb.vhdx\n\nThe desktop connection file Test2.rdp", off);
        Assert.DoesNotContain("turned off", off);
        Assert.Contains("It has no disk files.", MainWindow.DeleteVmQuestion(new VmInfo("z") { Name = "Z", State = "Off" }, []));
    }

    [Fact]
    public async Task AKeyThatCantActNow_SaysWhy()
    {
        var (vm, _, _) = await Make();
        vm.SelectedCheckpoints = [vm.Checkpoints[0], vm.Checkpoints[1]];
        Assert.Equal("That works on one checkpoint, and 2 are chosen.", vm.CheckpointActionUnavailable(vm.RenameCheckpointCommand));
        vm.SelectedCheckpoints = [vm.Checkpoints[0]];
        Assert.Equal("No checkpoints were taken after Before the October update.", vm.CheckpointActionUnavailable(vm.DeleteCheckpointAndLaterCommand));
        vm.Selected!.IsBusy = true;
        Assert.StartsWith("Build Agent is saved. Wait", vm.CheckpointActionUnavailable(vm.DeleteCheckpointsCommand));
        vm.Selected.IsBusy = false;

        var said = new List<string>();
        vm.Announce += said.Add;
        vm.Selected = vm.Vms.Single(v => v.Name == "Win11-RDP");
        vm.ExplainCheckpointAction(vm.DeleteCheckpointsCommand);
        Assert.Equal(["Win11-RDP has no checkpoints."], said);
        Assert.Equal("Win11-RDP has no checkpoints.", vm.StatusText);
    }

    [Fact]
    public async Task OnlyCheckpointActions_ReadTheCheckpointsAgain()
    {
        var service = new GatedCheckpoints();
        var vm = new MainViewModel(service);
        await vm.RefreshAsync();
        vm.Selected = vm.Vms.Single(v => v.Name == "Test2");
        service.Release();
        var reads = service.Reads;

        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(reads, service.Reads);
    }

    [Fact]
    public async Task MovingOnBeforeARead_Finishes_StopsIt_AndNeverShowsItsCheckpoints()
    {
        var service = new GatedCheckpoints();
        var vm = new MainViewModel(service);
        await vm.RefreshAsync();
        vm.Selected = vm.Vms.Single(v => v.Name == "Build Agent");
        var first = service.Pending.Last();
        Assert.Equal("Reading Build Agent's checkpoints.", vm.CheckpointsNote);

        vm.Selected = vm.Vms.Single(v => v.Name == "Test2");
        Assert.True(first.Token.IsCancellationRequested);
        var arrived = 0;
        vm.CheckpointsArrived += () => arrived++;
        service.Release();
        for (var i = 0; i < 200 && vm.Checkpoints.Count == 0; i++) await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.Equal(["Clean install"], Names(vm));
        Assert.Equal("Checkpoints of Test2", vm.CheckpointsHeading);
        Assert.Equal(1, arrived);
    }

    /// <summary>The demo, with each checkpoint read held until released, and stopped when its
    /// token is cancelled, as PowerShellRunner stops its process.</summary>
    private sealed class GatedCheckpoints : IHyperVService
    {
        private readonly DemoHyperVService _demo = new() { Delay = TimeSpan.Zero };
        public List<(TaskCompletionSource Gate, CancellationToken Token)> Pending { get; } = [];
        public int Reads { get; private set; }

        public void Release() { foreach (var p in Pending.ToList()) p.Gate.TrySetResult(); }

        public async Task<IReadOnlyList<CheckpointInfo>> GetCheckpointsAsync(string vmId, CancellationToken ct = default)
        {
            Reads++;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending.Add((gate, ct));
            await gate.Task.WaitAsync(ct);
            return await _demo.GetCheckpointsAsync(vmId, ct);
        }

        public Task<IReadOnlyList<VmInfo>> GetVmsAsync(CancellationToken ct = default) => _demo.GetVmsAsync(ct);
        public Task<IReadOnlyList<SwitchInfo>> GetSwitchesAsync(CancellationToken ct = default) => _demo.GetSwitchesAsync(ct);
        public Task RunActionAsync(VmAction action, string vmId, CancellationToken ct = default) => _demo.RunActionAsync(action, vmId, ct);
        public Task ApplySettingsAsync(string vmId, VmSettings current, VmSettings wanted, CancellationToken ct = default) => _demo.ApplySettingsAsync(vmId, current, wanted, ct);
        public Task CreateCheckpointAsync(string vmId, string checkpointName, CancellationToken ct = default) => _demo.CreateCheckpointAsync(vmId, checkpointName, ct);
        public Task ApplyCheckpointAsync(string vmId, string checkpointId, string? saveCurrentAs, CancellationToken ct = default) => _demo.ApplyCheckpointAsync(vmId, checkpointId, saveCurrentAs, ct);
        public Task DeleteCheckpointsAsync(string vmId, IReadOnlyList<string> checkpointIds, bool includeLater, CancellationToken ct = default) => _demo.DeleteCheckpointsAsync(vmId, checkpointIds, includeLater, ct);
        public Task RenameCheckpointAsync(string vmId, string checkpointId, string newName, CancellationToken ct = default) => _demo.RenameCheckpointAsync(vmId, checkpointId, newName, ct);
        public Task CloneAsync(string vmId, string newName, CancellationToken ct = default) => _demo.CloneAsync(vmId, newName, ct);
        public Task<IReadOnlyList<string>> GetDiskPathsAsync(string vmId, CancellationToken ct = default) => _demo.GetDiskPathsAsync(vmId, ct);
        public Task<DeleteResult> DeleteAsync(string vmId, CancellationToken ct = default) => _demo.DeleteAsync(vmId, ct);
        public Task<string> CreateExternalSwitchAsync(CancellationToken ct = default) => _demo.CreateExternalSwitchAsync(ct);
        public Task<ScreenPicture> TakeScreenshotAsync(string vmId, CancellationToken ct = default) => _demo.TakeScreenshotAsync(vmId, ct);
        public Task<SessionScreenshot> TakeSessionScreenshotAsync(string vmId, GuestCredential credential, CancellationToken ct = default) => _demo.TakeSessionScreenshotAsync(vmId, credential, ct);
        public Task ConnectAsync(VmInfo vm, CancellationToken ct = default) => _demo.ConnectAsync(vm, ct);
        public void OpenConsole(VmInfo vm) => _demo.OpenConsole(vm);
        public Task<SavedConnection> SaveConnectionFileAsync(VmInfo vm, CancellationToken ct = default) => _demo.SaveConnectionFileAsync(vm, ct);
    }

    [Fact]
    public async Task ADeleteThatFails_SaysWhy_AndTheListStays()
    {
        var (vm, demo, said) = await Make();
        var row = vm.Checkpoints[1];
        vm.SelectedCheckpoint = row;
        // Deleted in Hyper-V Manager while this list still showed it.
        await demo.DeleteCheckpointsAsync(vm.Selected!.Id, [row.Id], includeLater: false, TestContext.Current.CancellationToken);
        vm.ConfirmDeleteCheckpoints = (_, _, _) => true;

        await vm.DeleteCheckpointsCommand.ExecuteAsync(null);

        Assert.Contains(said, s => s.Contains("A checkpoint you chose no longer exists, so none were deleted."));
        Assert.Equal(["Before the October update", "Fresh agent"], Names(vm));
    }
}

public class CheckpointScriptTests
{
    [Fact]
    public void DeleteScript_FindsEveryOneByIdBeforeRemovingAny()
    {
        var script = PowerShellHyperVService.BuildDeleteCheckpointsScript("id-1", ["cp-1", "cp-'2"], includeLater: false);
        Assert.Contains("$vm = Get-VM -Id 'id-1'", script);
        Assert.Contains("$ids = @('cp-1', 'cp-''2')", script);
        var check = script.IndexOf("no longer exists, so none were deleted", StringComparison.Ordinal);
        var remove = script.IndexOf("Remove-VMSnapshot -VMSnapshot $c -Confirm:$false", StringComparison.Ordinal);
        Assert.True(check > 0 && remove > check);
        Assert.DoesNotContain("IncludeAllChildSnapshots", script);
    }

    [Fact]
    public void DeleteScript_WithThoseAfterIt_GoesAheadOnlyIfTheyAreTheOnesAskedAbout()
    {
        var script = PowerShellHyperVService.BuildDeleteCheckpointsScript("id-1", ["root", "child", "grandchild"], includeLater: true);
        var check = script.IndexOf("have changed since the list was read, so none were deleted", StringComparison.Ordinal);
        var remove = script.IndexOf("Remove-VMSnapshot -VMSnapshot $found[0] -IncludeAllChildSnapshots -Confirm:$false", StringComparison.Ordinal);
        Assert.True(check > 0 && remove > check);
        Assert.DoesNotContain("foreach ($c in $found)", script);
        Assert.Throws<ArgumentException>(() => PowerShellHyperVService.BuildDeleteCheckpointsScript("id-1", [], includeLater: false));
    }

    [Fact]
    public async Task Demo_WithThoseAfterIt_RefusesWhenTheyAreNotTheOnesAskedAbout()
    {
        var demo = new DemoHyperVService { Delay = TimeSpan.Zero };
        var ct = TestContext.Current.CancellationToken;
        var agent = (await demo.GetVmsAsync(ct)).Single(v => v.Name == "Build Agent");
        var list = await demo.GetCheckpointsAsync(agent.Id, ct); // newest first: October, Tools, Fresh

        // One taken since the list was read is missing from what was asked about.
        var ex = await Assert.ThrowsAsync<HyperVException>(() => demo.DeleteCheckpointsAsync(agent.Id, [list[2].Id, list[1].Id], includeLater: true, ct));
        Assert.Contains("have changed since the list was read", ex.Message);
        Assert.Equal(3, (await demo.GetCheckpointsAsync(agent.Id, ct)).Count);

        await demo.DeleteCheckpointsAsync(agent.Id, [list[1].Id, list[0].Id], includeLater: true, ct);
        Assert.Equal(["Fresh agent"], (await demo.GetCheckpointsAsync(agent.Id, ct)).Select(c => c.Name));
    }

    [Fact]
    public void RenameScript_FindsTheCheckpointById_AndQuotesTheName()
    {
        var script = PowerShellHyperVService.BuildRenameCheckpointScript("id-1", "cp-9", "Bob's build");
        Assert.Contains("$_.Id.ToString() -eq 'cp-9'", script);
        Assert.Contains("throw 'That checkpoint no longer exists.'", script);
        Assert.EndsWith("Rename-VMSnapshot -VMSnapshot $checkpoint -NewName 'Bob''s build'", script);
    }

    [Fact]
    public void TheList_ReadsEachCheckpointsParent()
    {
        Assert.Contains("ParentId = ", PowerShellHyperVService.CheckpointsScript);
        var list = PowerShellHyperVService.ParseCheckpoints(
            """[{"Id":"b","Name":"B","Created":"2026-10-03T09:00:00.0000000-05:00","IsCurrent":true,"ParentId":"a"},{"Id":"a","Name":"A","Created":"2026-10-02T09:00:00.0000000-05:00","IsCurrent":false,"ParentId":""}]""");
        Assert.Equal("a", list[0].ParentId);
        Assert.Equal("", list[1].ParentId);
    }

    [Fact]
    public async Task Demo_DeletingTheOneTheVmIsBasedOn_MakesItsParentTheCurrentOne()
    {
        var demo = new DemoHyperVService { Delay = TimeSpan.Zero };
        var ct = TestContext.Current.CancellationToken;
        var agent = (await demo.GetVmsAsync(ct)).Single(v => v.Name == "Build Agent");
        var before = await demo.GetCheckpointsAsync(agent.Id, ct);
        Assert.True(before[0].IsCurrent);

        await demo.DeleteCheckpointsAsync(agent.Id, [before[0].Id], includeLater: false, ct);

        var after = await demo.GetCheckpointsAsync(agent.Id, ct);
        Assert.Equal(["Tools installed", "Fresh agent"], after.Select(c => c.Name));
        Assert.True(after[0].IsCurrent);
    }
}

/// <summary>The Help menu's Ubuntu download (issue 11).</summary>
public class UbuntuDownloadTests
{
    [Theory]
    [InlineData(true, "https://cdimage.ubuntu.com/ubuntu/releases/26.04/release/", "Arm64")]
    [InlineData(false, "https://releases.ubuntu.com/26.04/", "x64")]
    public void ThePage_IsWhereNewUbuntuVmDownloadsFrom_ForThisPcsProcessor(bool arm64, string page, string kind)
    {
        var download = IsoDownloads.UbuntuForThisPc(arm64);
        Assert.Equal(page, download.Page.ToString());
        Assert.StartsWith($"Download Ubuntu 26.04 LTS for {kind} PCs", download.Text);
        Assert.EndsWith(", which this PC needs", download.Text);
    }

    [Fact]
    public void TheRelease_IsTheOneTheScriptInstalls()
    {
        var script = File.ReadAllText(Path.Combine(ScriptBuildingTests.RepoRoot(), "hyperv-rdp-vm", "New-HyperVRdpVM.ps1"));
        Assert.Contains($"$ubuntuSeries = '{IsoDownloads.UbuntuSeries}'", script);
        // The script's two download sites, the same as the menu's.
        Assert.Contains("https://cdimage.ubuntu.com/ubuntu/releases/$ubuntuSeries/release", script);
        Assert.Contains("https://releases.ubuntu.com/$ubuntuSeries", script);
        Assert.Contains($"Ubuntu {IsoDownloads.UbuntuSeries} LTS", new NewVmViewModel([], ubuntu: true).Intro);
    }
}

[Collection("Wpf")]
public class CheckpointWindowTests
{
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static async Task<(MainWindow window, MainViewModel vm)> Open(string vmName = "Build Agent")
    {
        TestApp.Ensure();
        var vm = new MainViewModel(new DemoHyperVService { Delay = TimeSpan.Zero });
        await vm.RefreshAsync();
        vm.Selected = vm.Vms.Single(v => v.Name == vmName);
        var window = new MainWindow(vm, demo: true)
        {
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
        };
        window.Show();
        Pump();
        return (window, vm);
    }

    [StaFact]
    public async Task TheCheckpointList_IsNamedForTheVm_AndEachRowIsNamedOnItsContainer()
    {
        var (window, vm) = await Open();
        try
        {
            var list = (ListView)window.FindName("CheckpointList");
            Assert.Equal("Checkpoints of Build Agent", UIElementAutomationPeer.CreatePeerForElement(list).GetName());
            Assert.Equal(SelectionMode.Extended, list.SelectionMode);
            var row = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.Equal(vm.Checkpoints[0].AccessibleName, AutomationProperties.GetName(row));
            Assert.EndsWith(", the one the VM is now based on", AutomationProperties.GetName(row));

            vm.Selected = vm.Vms.Single(v => v.Name == "Win11-RDP");
            Pump();
            Assert.Equal("Checkpoints of Win11-RDP. Win11-RDP has no checkpoints. Ctrl+K makes one.",
                UIElementAutomationPeer.CreatePeerForElement(list).GetName());
        }
        finally { window.Close(); }
    }

    [StaFact]
    public async Task ChoosingSeveralRowsInTheList_ReachesTheViewModel()
    {
        var (window, vm) = await Open();
        try
        {
            var list = (ListView)window.FindName("CheckpointList");
            list.SelectedItems.Add(vm.Checkpoints[2]);
            Pump();
            Assert.Equal(2, vm.SelectedCheckpoints.Count);
            Assert.False(vm.RenameCheckpointCommand.CanExecute(null));
            Assert.True(vm.DeleteCheckpointsCommand.CanExecute(null));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public async Task ACheckpointRowsContextMenu_ReachesTheWindowsCommands()
    {
        var (window, vm) = await Open();
        try
        {
            var list = (ListView)window.FindName("CheckpointList");
            var row = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var menu = row.ContextMenu;
            menu.PlacementTarget = row;
            menu.IsOpen = true;
            Pump();
            try
            {
                Assert.Same(vm, menu.DataContext);
                var commands = menu.Items.OfType<MenuItem>().Select(i => i.Command).ToList();
                Assert.Same(vm.ApplySelectedCheckpointCommand, commands[0]);
                Assert.Contains(vm.RenameCheckpointCommand, commands);
                Assert.Contains(vm.DeleteCheckpointsCommand, commands);
                Assert.Contains(vm.DeleteCheckpointAndLaterCommand, commands);
            }
            finally { menu.IsOpen = false; }
        }
        finally { window.Close(); }
    }

    [StaFact]
    public async Task TheMenuBar_HasACheckpointsMenu_AndHelpHasUbuntusDownload()
    {
        var (window, vm) = await Open();
        try
        {
            var bar = (Menu)LogicalTreeHelper.GetChildren((DockPanel)window.Content).OfType<Menu>().Single();
            var top = bar.Items.OfType<MenuItem>().ToList();
            var checkpoints = top.Single(m => (string)m.Header == "_Checkpoints");
            Assert.Contains(checkpoints.Items.OfType<MenuItem>(), i => ReferenceEquals(i.Command, vm.DeleteCheckpointAndLaterCommand));

            var help = top.Single(m => (string)m.Header == "_Help");
            var ubuntu = (MenuItem)window.FindName("IsoMenuUbuntu");
            Assert.Contains(ubuntu, help.Items.OfType<MenuItem>());
            Assert.StartsWith("Download Ubu_ntu 26.04 LTS for ", (string)ubuntu.Header);
            Assert.Equal(IsoDownloads.UbuntuForThisPc(IsoFinder.HostIsArm64).Page, ubuntu.Tag);
            // Each of Help's access keys is its own, so one press goes straight there.
            var keys = help.Items.OfType<MenuItem>().Select(i => (string)i.Header).Select(h => char.ToUpperInvariant(h[h.IndexOf('_') + 1])).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
            // And the top-level menus' too.
            var topKeys = top.Select(m => char.ToUpperInvariant(((string)m.Header)[((string)m.Header).IndexOf('_') + 1])).ToList();
            Assert.Equal(topKeys.Count, topKeys.Distinct().Count());
        }
        finally { window.Close(); }
    }

    private static ConfirmWindow ShowConfirm(bool yesFirst = false)
    {
        TestApp.Ensure();
        var w = ConfirmWindow.Create("Delete Test2?\n\nThis can't be undone.", "Delete virtual machine", yesFirst, warning: !yesFirst);
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = w.Top = -10000;
        w.Show();
        Pump();
        return w;
    }

    [StaFact]
    public void ConfirmWindow_StartsOnNo_WhichEscapeAndEnterAnswer_AndSpeaksTheQuestion()
    {
        var w = ShowConfirm();
        try
        {
            var no = (Button)w.FindName("NoButton");
            var yes = (Button)w.FindName("YesButton");
            Assert.True(no.IsKeyboardFocused);
            Assert.True(no.IsCancel);   // Escape, and the close button, answer No
            Assert.True(no.IsDefault);  // and so does Enter
            Assert.False(yes.IsDefault);
            Assert.Equal("Delete virtual machine", w.Title);
            Assert.Equal("Delete Test2?\n\nThis can't be undone.", UIElementAutomationPeer.CreatePeerForElement(no).GetHelpText());
            Assert.Equal("", AutomationProperties.GetHelpText(yes));
            // Plain text, as a message box's is, which screen readers read as the dialog opens.
            var text = (TextBlock)w.FindName("QuestionText");
            Assert.Equal("Delete Test2?\n\nThis can't be undone.", text.Text);
            var icon = (Image)w.FindName("WarningIcon");
            Assert.Equal(Visibility.Visible, icon.Visibility);
            Assert.NotNull(icon.Source);
        }
        finally { w.Close(); }
    }

    [StaFact]
    public void ConfirmWindow_ForAQuestionWhoseUsualAnswerIsYes_StartsOnYes()
    {
        var w = ShowConfirm(yesFirst: true);
        try
        {
            var yes = (Button)w.FindName("YesButton");
            Assert.True(yes.IsKeyboardFocused);
            Assert.True(yes.IsDefault);
            Assert.True(((Button)w.FindName("NoButton")).IsCancel);
            Assert.Equal(Visibility.Collapsed, ((Image)w.FindName("WarningIcon")).Visibility);
        }
        finally { w.Close(); }
    }

    /// <summary>Shows the window as a dialog, does something to it once it's up, and returns its
    /// answer. Closes it after five seconds rather than hang, and fails if it had to.</summary>
    private static bool? Answer(Action<ConfirmWindow> act)
    {
        TestApp.Ensure();
        var w = ConfirmWindow.Create("Delete Test2?", "Delete virtual machine");
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = w.Top = -10000;
        var timedOut = false;
        var guard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        guard.Tick += (_, _) => { guard.Stop(); timedOut = true; w.Close(); };
        w.Loaded += (_, _) => { guard.Start(); w.Dispatcher.BeginInvoke(() => act(w), DispatcherPriority.Input); };
        var answer = w.ShowDialog();
        guard.Stop();
        Assert.False(timedOut, "the window didn't close");
        return answer;
    }

    [StaFact]
    public void ConfirmWindow_Yes_AnswersYes_AndClosingIt_AnswersNo()
    {
        Assert.True(Answer(w => ((Button)w.FindName("YesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
        Assert.NotEqual(true, Answer(w => w.Close()));
    }

    [StaFact(Skip = InputTests.SkipReason, SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public void ConfirmWindow_Escape_AnswersNo()
    {
        var answer = Answer(w =>
        {
            w.Activate();
            var source = PresentationSource.FromVisual(w)!;
            System.Windows.Input.InputManager.Current.ProcessInput(new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Escape)
            { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
        });
        Assert.NotEqual(true, answer);
    }

    [StaFact]
    public void ApplyCheckpointWindow_StartsOnTheOneChosenInTheList()
    {
        TestApp.Ensure();
        var vm = new VmInfo("a") { Name = "Build Agent", State = "Off" };
        CheckpointInfo[] checkpoints =
        [
            new("3", "Newest", new DateTime(2026, 10, 3), true, "2"),
            new("2", "Middle", new DateTime(2026, 10, 2), false, "1"),
            new("1", "Oldest", new DateTime(2026, 10, 1), false),
        ];
        var window = ApplyCheckpointWindow.Create(vm, checkpoints, checkpoints[2]);
        try
        {
            window.ShowActivated = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = window.Top = -10000;
            window.Show();
            Pump();
            Pump();
            var list = (ListBox)window.FindName("CheckpointList");
            Assert.Same(checkpoints[2], list.SelectedItem);
            Assert.True(((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(2)).IsKeyboardFocused);
        }
        finally { window.Close(); }
    }
}
