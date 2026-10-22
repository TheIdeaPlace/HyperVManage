using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HyperVManage.Models;
using HyperVManage.Services;

namespace HyperVManage.ViewModels;

/// <summary>One row of the checkpoint list. Kept and updated in place, like the VM rows, so a
/// screen reader stays where it was when the list is read again.</summary>
public sealed partial class CheckpointRow : ObservableObject
{
    public CheckpointRow(CheckpointInfo info) => _info = info;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(TakenText), nameof(NowText), nameof(AccessibleName))]
    private CheckpointInfo _info;

    public string Id => Info.Id;
    public string Name => Info.Name;
    public string TakenText => Info.Created.ToString("f");
    public string NowText => Info.IsCurrent ? "The VM is now based on this" : "";

    /// <summary>What a screen reader reads for the row: its name, when, and whether it's the
    /// one the VM is now based on.</summary>
    public string AccessibleName => Info.ToString();

    public override string ToString() => AccessibleName;
}

/// <summary>The checkpoint list under the VM list: the selected VM's checkpoints, newest first,
/// and what can be done to the ones chosen there.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<CheckpointRow> Checkpoints { get; } = [];

    /// <summary>The checkpoint with the list's selection; the one Apply and Rename act on.</summary>
    [ObservableProperty] private CheckpointRow? _selectedCheckpoint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckpointsListName))]
    private string _checkpointsHeading = "Checkpoints";

    /// <summary>Shown in the list while it's empty: being read, none, or why it couldn't be read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckpointsListName))]
    private string _checkpointsNote = "";

    /// <summary>True while the list has nothing in it, when the note shows in its place.</summary>
    public bool IsCheckpointListEmpty => Checkpoints.Count == 0;

    /// <summary>The list's name. While it's empty the note is part of it, so tabbing to the list
    /// says why there's nothing in it.</summary>
    public string CheckpointsListName =>
        Checkpoints.Count == 0 && CheckpointsNote.Length > 0 ? $"{CheckpointsHeading}. {CheckpointsNote}" : CheckpointsHeading;

    private IReadOnlyList<CheckpointRow> _selectedCheckpoints = [];

    /// <summary>Every checkpoint chosen in the list; Delete acts on them all. The window keeps
    /// this up to date. Without it, the selected checkpoint alone.</summary>
    public IReadOnlyList<CheckpointRow> SelectedCheckpoints
    {
        get => _selectedCheckpoints;
        set { _selectedCheckpoints = value; RefreshCheckpointCommandStates(); }
    }

    private IReadOnlyList<CheckpointRow> Chosen =>
        SelectedCheckpoints.Count > 0 ? SelectedCheckpoints : SelectedCheckpoint is { } one ? [one] : [];

    /// <summary>Raised when the selected checkpoint left the list and another was selected in its
    /// place, so the window can move keyboard focus to it.</summary>
    public event Action? CheckpointSelectionReplaced;

    /// <summary>Asked for a checkpoint's new name. Null means the user cancelled.</summary>
    public Func<VmInfo, CheckpointInfo, string?>? RequestCheckpointRename { get; set; }

    /// <summary>Asked before deleting checkpoints, with every one that would go; withLater when
    /// they're one checkpoint and those taken after it.</summary>
    public Func<VmInfo, IReadOnlyList<CheckpointInfo>, bool, bool>? ConfirmDeleteCheckpoints { get; set; }

    partial void OnSelectedCheckpointChanged(CheckpointRow? value) => RefreshCheckpointCommandStates();

    private void RefreshCheckpointCommandStates()
    {
        foreach (var c in new IRelayCommand[] { ApplySelectedCheckpointCommand, RenameCheckpointCommand,
                     DeleteCheckpointsCommand, DeleteCheckpointAndLaterCommand })
            c.NotifyCanExecuteChanged();
    }

    // ── Loading ──────────────────────────────────────────────────────────────

    private int _checkpointLoad;
    private string? _checkpointsVmId;
    private CancellationTokenSource? _checkpointRead;

    /// <summary>Raised when rows arrive in a list that was empty, as they do a moment after
    /// moving to another VM, so the window can move focus from the list onto its selected row.</summary>
    public event Action? CheckpointsArrived;

    /// <summary>Reads the selected VM's checkpoints into the list. Only the newest read is used,
    /// and the one before it is stopped, so moving quickly through the VMs neither leaves one
    /// VM's checkpoints under another nor piles up PowerShell processes.</summary>
    public async Task LoadCheckpointsAsync()
    {
        var load = ++_checkpointLoad;
        _checkpointRead?.Cancel();
        _checkpointRead?.Dispose();
        _checkpointRead = null;
        var vm = Selected;
        if (vm is null)
        {
            _checkpointsVmId = null;
            Checkpoints.Clear();
            SelectedCheckpoint = null;
            SelectedCheckpoints = [];
            CheckpointsHeading = "Checkpoints";
            CheckpointsNote = "";
            return;
        }
        if (_checkpointsVmId != vm.Id)
        {
            _checkpointsVmId = vm.Id;
            Checkpoints.Clear();
            SelectedCheckpoint = null;
            SelectedCheckpoints = [];
            CheckpointsNote = $"Reading {vm.Name}'s checkpoints.";
        }
        CheckpointsHeading = $"Checkpoints of {vm.Name}";
        var read = _checkpointRead = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            var fresh = await _hyperV.GetCheckpointsAsync(vm.Id, read.Token);
            if (load != _checkpointLoad) return;
            var wasEmpty = Checkpoints.Count == 0;
            MergeCheckpoints(fresh);
            if (wasEmpty && Checkpoints.Count > 0) CheckpointsArrived?.Invoke();
            CheckpointsNote = fresh.Count == 0 ? $"{vm.Name} has no checkpoints. Ctrl+K makes one." : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (load != _checkpointLoad) return;
            var text = $"Couldn't read {vm.Name}'s checkpoints: {ex.Message}";
            if (Checkpoints.Count == 0) CheckpointsNote = text;
            // With some already shown, the list stays as it was, and the status bar says why.
            else StatusText = text;
        }
        OnPropertyChanged(nameof(CheckpointsListName));
    }

    /// <summary>Updates rows in place, in the order given, newest first: rows that went are
    /// removed and new ones put where they belong.</summary>
    internal void MergeCheckpoints(IReadOnlyList<CheckpointInfo> fresh)
    {
        var ids = fresh.Select(c => c.Id).ToHashSet();
        var selected = SelectedCheckpoint;
        var selectedIndex = selected is null ? -1 : Checkpoints.IndexOf(selected);
        var selectedGone = selected is not null && !ids.Contains(selected.Id);
        for (var i = Checkpoints.Count - 1; i >= 0; i--)
            if (!ids.Contains(Checkpoints[i].Id)) Checkpoints.RemoveAt(i);
        for (var i = 0; i < fresh.Count; i++)
        {
            var at = -1;
            for (var j = i; j < Checkpoints.Count; j++)
                if (Checkpoints[j].Id == fresh[i].Id) { at = j; break; }
            if (at < 0) Checkpoints.Insert(i, new CheckpointRow(fresh[i]));
            else
            {
                if (at != i) Checkpoints.Move(at, i);
                if (Checkpoints[i].Info != fresh[i]) Checkpoints[i].Info = fresh[i];
            }
        }
        SelectedCheckpoints = SelectedCheckpoints.Where(Checkpoints.Contains).ToList();
        if (selectedGone)
        {
            // The row that took its place, or the one before it at the end of the list.
            SelectedCheckpoint = Checkpoints.Count == 0 ? null : Checkpoints[Math.Clamp(selectedIndex, 0, Checkpoints.Count - 1)];
            CheckpointSelectionReplaced?.Invoke();
        }
        else if (SelectedCheckpoint is null || !Checkpoints.Contains(SelectedCheckpoint))
            SelectedCheckpoint = Checkpoints.FirstOrDefault();
    }

    // ── Actions ──────────────────────────────────────────────────────────────

    /// <summary>Apply, for the checkpoint chosen in the list. The same window as Apply Checkpoint
    /// asks first, with that checkpoint already chosen.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnOneCheckpoint))]
    private Task ApplySelectedCheckpoint() => ApplyAsync(Chosen[0].Id);

    [RelayCommand(CanExecute = nameof(CanActOnOneCheckpoint))]
    private Task RenameCheckpoint()
    {
        var vm = Selected!;
        var checkpoint = Chosen[0].Info;
        var name = RequestCheckpointRename?.Invoke(vm, checkpoint)?.Trim();
        if (string.IsNullOrEmpty(name) || name == checkpoint.Name) return Task.CompletedTask;
        return RunAsync(vm, $"Renaming checkpoint {checkpoint.Name} of {vm.Name} to {name}.",
            v => _hyperV.RenameCheckpointAsync(v.Id, checkpoint.Id, name),
            v => $"Checkpoint {checkpoint.Name} of {v.Name} is now called {name}.", checkpoints: true);
    }

    private bool CanActOnOneCheckpoint() => Can(VmStates.IsSettled) && Chosen.Count == 1;

    /// <summary>Deletes every checkpoint chosen in the list, after asking.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteCheckpoints))]
    private Task DeleteCheckpoints()
    {
        // In the list's order, newest first, whatever order they were chosen in.
        var going = Checkpoints.Where(Chosen.Contains).Select(r => r.Info).ToList();
        return DeleteAsync(going, withLater: false);
    }

    private bool CanDeleteCheckpoints() => Can(VmStates.IsSettled) && Chosen.Count > 0;

    /// <summary>Deletes the chosen checkpoint and every one taken after it, after asking.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteCheckpointAndLater))]
    private Task DeleteCheckpointAndLater()
    {
        var root = Chosen[0].Info;
        var later = LaterThan(root.Id);
        var going = new[] { root }.Concat(Checkpoints.Select(r => r.Info).Where(c => later.Contains(c.Id))).ToList();
        return DeleteAsync(going, withLater: true);
    }

    private bool CanDeleteCheckpointAndLater() =>
        CanActOnOneCheckpoint() && Checkpoints.Any(r => r.Info.ParentId == Chosen[0].Id);

    /// <summary>The ids of every checkpoint taken after this one, at any depth.</summary>
    private HashSet<string> LaterThan(string id)
    {
        var later = new HashSet<string>();
        var next = new Queue<string>([id]);
        while (next.TryDequeue(out var parent))
            foreach (var child in Checkpoints.Where(r => r.Info.ParentId == parent && later.Add(r.Id)))
                next.Enqueue(child.Id);
        return later;
    }

    private Task DeleteAsync(IReadOnlyList<CheckpointInfo> going, bool withLater)
    {
        var vm = Selected!;
        if (going.Count == 0 || ConfirmDeleteCheckpoints?.Invoke(vm, going, withLater) != true) return Task.CompletedTask;
        var what = going.Count == 1 ? $"checkpoint {going[0].Name}" : $"{going.Count} checkpoints";
        // With later ones, every one asked about goes along, first the one they follow, so the
        // delete can refuse if Hyper-V now has others after it.
        IReadOnlyList<string> ids = going.Select(c => c.Id).ToList();
        return RunAsync(vm, $"Deleting {what} of {vm.Name}.",
            v => _hyperV.DeleteCheckpointsAsync(v.Id, ids, withLater),
            v => $"Deleted {what} of {v.Name}.", checkpoints: true);
    }

    /// <summary>Why a checkpoint action can't run now, for a key pressed in the list that would
    /// otherwise do nothing and say nothing.</summary>
    internal string CheckpointActionUnavailable(IRelayCommand command)
    {
        if (Selected is not { } vm) return "No virtual machine is selected.";
        if (vm.IsBusy || !VmStates.IsSettled(vm.State)) return $"{vm.Name} is {vm.StateText.ToLowerInvariant()}. Wait for it to finish.";
        if (Checkpoints.Count == 0) return $"{vm.Name} has no checkpoints.";
        if (Chosen.Count == 0) return "No checkpoint is chosen.";
        var needsOne = ReferenceEquals(command, ApplySelectedCheckpointCommand) || ReferenceEquals(command, RenameCheckpointCommand)
            || ReferenceEquals(command, DeleteCheckpointAndLaterCommand);
        if (needsOne && Chosen.Count > 1) return $"That works on one checkpoint, and {Chosen.Count} are chosen.";
        if (ReferenceEquals(command, DeleteCheckpointAndLaterCommand)) return $"No checkpoints were taken after {Chosen[0].Name}.";
        return "That can't be done now.";
    }

    /// <summary>Says why a checkpoint action can't run, in the status bar and aloud.</summary>
    public void ExplainCheckpointAction(IRelayCommand command)
    {
        StatusText = CheckpointActionUnavailable(command);
        Announce?.Invoke(StatusText);
    }

    /// <summary>What the Delete confirmation asks: which checkpoints go, and that the VM itself
    /// stays as it is.</summary>
    internal static string DeleteCheckpointsQuestion(VmInfo vm, IReadOnlyList<CheckpointInfo> going, bool withLater)
    {
        // Says what is lost, going back to those points, and what isn't: the VM as it is now and
        // its other checkpoints. Hyper-V merging the disks is how, which nobody needs to hear.
        var (question, after) = going.Count == 1
            ? ($"Delete checkpoint {going[0].Name} from {vm.Name}?",
               $"You won't be able to take {vm.Name} back to that point any more. {vm.Name} itself won't change, and neither will its other checkpoints.")
            : withLater
                ? ($"Delete checkpoint {going[0].Name} from {vm.Name}, and the {(going.Count == 2 ? "checkpoint" : $"{going.Count - 1} checkpoints")} taken after it?\n\n"
                   + string.Join("\n", going.Skip(1).Select(c => c.Name)),
                   $"You won't be able to take {vm.Name} back to any of those points. {vm.Name} itself won't change.")
                : ($"Delete these {going.Count} checkpoints from {vm.Name}?\n\n" + string.Join("\n", going.Select(c => c.Name)),
                   $"You won't be able to take {vm.Name} back to those points any more. {vm.Name} itself won't change, and neither will its other checkpoints.");
        return $"{question}\n\n{after}\n\nThis can't be undone.";
    }
}
