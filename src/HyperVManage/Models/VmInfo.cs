using CommunityToolkit.Mvvm.ComponentModel;

namespace HyperVManage.Models;

/// <summary>
/// One virtual machine as the list shows it. An ObservableObject updated in place on every
/// refresh, rather than replaced, so the selected row, and the screen reader's place in the
/// list, survive the ten-second auto-refresh.
/// </summary>
public sealed partial class VmInfo : ObservableObject
{
    public VmInfo(string id) => Id = id;

    /// <summary>Hyper-V's VM id. Stable across renames, so it is what a refresh matches on.</summary>
    public string Id { get; }

    // Everything a column can show also changes the row's name, which is made of the columns.
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AccessibleName))] private string _name = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AccessibleName), nameof(StateText))] private string _state = "Off";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AccessibleName), nameof(ProcessorsText))] private int _processorCount;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AccessibleName), nameof(MemoryText))] private long _memoryStartupMB;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AccessibleName), nameof(MemoryInUseText))] private long _memoryAssignedMB;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AccessibleName), nameof(MemoryText))] private bool _dynamicMemory;
    [ObservableProperty] private long _uptimeSeconds;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AccessibleName), nameof(StartsWithPcText))] private string _automaticStartAction = "";
    [ObservableProperty] private bool _automaticCheckpoints;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AccessibleName))] private string _switchName = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AccessibleName), nameof(AddressText))] private string[] _ipAddresses = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AccessibleName), nameof(CheckpointsText))] private int _checkpointCount;
    [ObservableProperty] private int _generation;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AccessibleName))] private bool _isBusy;

    /// <summary>The folder Hyper-V keeps the VM's configuration in.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AccessibleName))] private string _location = "";

    /// <summary>The VM's virtual hard disk files, in the order Hyper-V lists them.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AccessibleName), nameof(DisksText))] private string[] _diskPaths = [];

    /// <summary>The columns shown and whether their names are spoken; the list sets it on every row.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AccessibleName))] private VmRowFormat _rowFormat = VmRowFormat.Default;

    public string StateText => VmStates.Describe(State);

    public string ProcessorsText => ProcessorCount > 0 ? ProcessorCount.ToString(System.Globalization.CultureInfo.CurrentCulture) : "";

    public string DisksText => string.Join("; ", DiskPaths);

    public string CheckpointsText => CheckpointCount switch
    {
        0 => "No checkpoints",
        1 => "1 checkpoint",
        var n => $"{n} checkpoints",
    };

    public string AddressText => IpAddresses.Length > 0 ? string.Join(", ", IpAddresses) : "";

    /// <summary>The memory the VM starts with: steady, so it can be in the row's name without the
    /// focused row being read again as dynamic memory moves.</summary>
    public string MemoryText => MemoryStartupMB > 0 ? FormatMB(MemoryStartupMB) : "";

    /// <summary>What a running VM is using now. It moves every few seconds with dynamic memory, so
    /// it's a column of its own, off to begin with.</summary>
    public string MemoryInUseText => MemoryAssignedMB > 0 ? $"{FormatMB(MemoryAssignedMB)} in use" : "";

    public string StartsWithPcText => AutomaticStartAction switch
    {
        "Start" => "Always",
        "StartIfRunning" => "If it was running",
        "Nothing" => "No",
        _ => AutomaticStartAction,
    };

    /// <summary>
    /// What a screen reader says for the row: the columns shown, in their order on screen, with
    /// names when Speak column names is on, then "busy" while an operation runs. Set on the row
    /// container, and returned by ToString, which is where a list item's name comes from.
    /// </summary>
    public string AccessibleName => RowFormat.Compose(this);

    public override string ToString() => AccessibleName;

    /// <summary>Copies every field from a fresh read, raising change notifications only for
    /// those that changed.</summary>
    public void UpdateFrom(VmInfo other)
    {
        Name = other.Name;
        State = other.State;
        ProcessorCount = other.ProcessorCount;
        MemoryStartupMB = other.MemoryStartupMB;
        MemoryAssignedMB = other.MemoryAssignedMB;
        DynamicMemory = other.DynamicMemory;
        UptimeSeconds = other.UptimeSeconds;
        AutomaticStartAction = other.AutomaticStartAction;
        AutomaticCheckpoints = other.AutomaticCheckpoints;
        SwitchName = other.SwitchName;
        if (!IpAddresses.SequenceEqual(other.IpAddresses)) IpAddresses = other.IpAddresses;
        CheckpointCount = other.CheckpointCount;
        Generation = other.Generation;
        Location = other.Location;
        if (!DiskPaths.SequenceEqual(other.DiskPaths)) DiskPaths = other.DiskPaths;
    }

    internal static string FormatMB(long mb) =>
        mb >= 1024 && mb % 1024 == 0 ? $"{mb / 1024} GB"
        : mb >= 1024 ? $"{mb / 1024.0:0.#} GB"
        : $"{mb} MB";
}

/// <summary>Hyper-V VM states (the names of Microsoft.HyperV.PowerShell.VMState) and what each allows.</summary>
public static class VmStates
{
    public static string Describe(string state) => state switch
    {
        "Off" => "Off",
        "Running" => "Running",
        "Paused" => "Paused",
        "Saved" => "Saved",
        "Starting" => "Starting",
        "Stopping" => "Shutting down",
        "Saving" => "Saving",
        "Pausing" => "Pausing",
        "Resuming" => "Resuming",
        "Reset" => "Restarting",
        _ => state,
    };

    public static bool CanStart(string s) => s is "Off" or "Saved";
    public static bool CanShutDown(string s) => s is "Running";
    public static bool CanTurnOff(string s) => s is "Running" or "Paused";
    public static bool CanSave(string s) => s is "Running" or "Paused";
    public static bool CanPause(string s) => s is "Running";
    public static bool CanResume(string s) => s is "Paused";
    public static bool CanRestart(string s) => s is "Running";
    public static bool CanConnect(string s) => s is "Running";

    /// <summary>Hyper-V has a screen to show only while the VM is running or paused.</summary>
    public static bool CanScreenshot(string s) => s is "Running" or "Paused";

    /// <summary>Settled states, where configuration, checkpoints, cloning and deleting are safe.
    /// A VM partway through starting or saving is left alone until it gets there.</summary>
    public static bool IsSettled(string s) => s is "Off" or "Running" or "Paused" or "Saved";

    public static bool CanClone(string s) => s is "Off" or "Saved";

    /// <summary>Processor count, startup memory and dynamic memory can only change while off.</summary>
    public static bool CanChangeHardware(string s) => s is "Off";
}
