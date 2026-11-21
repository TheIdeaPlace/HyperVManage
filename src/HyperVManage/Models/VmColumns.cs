using HyperVManage.Helpers;

namespace HyperVManage.Models;

/// <summary>The columns the VM list can show (View, Columns…), and the value each shows.</summary>
public static class VmColumns
{
    public static readonly ColumnSet Set = new("vms",
    [
        new("name", "Name", nameof(VmInfo.Name), Width: 170),
        new("state", "State", nameof(VmInfo.StateText), ColumnKind.State, 100),
        new("address", "Address", nameof(VmInfo.AddressText), Width: 120),
        new("network", "Network", nameof(VmInfo.SwitchName), Width: 140),
        new("memory", "Memory", nameof(VmInfo.MemoryText), Width: 100),
        new("processors", "Processors", nameof(VmInfo.ProcessorsText), Width: 80),
        new("startsWithPc", "Starts with PC", nameof(VmInfo.StartsWithPcText), Width: 140),
        new("location", "Location", nameof(VmInfo.Location), Width: 280),
        new("disks", "Disks", nameof(VmInfo.DisksText), Width: 360),
        new("checkpoints", "Checkpoints", nameof(VmInfo.CheckpointsText), ColumnKind.Count, 120),
    ],
    ["name", "state", "address", "network", "memory", "processors", "startsWithPc"]);

    /// <summary>What a column shows for a VM: the same text on screen and in the row's name.</summary>
    public static string Value(VmInfo vm, string id) => id switch
    {
        "name" => vm.Name,
        "state" => vm.StateText,
        "address" => vm.AddressText,
        "network" => vm.SwitchName,
        "memory" => vm.MemoryText,
        "processors" => vm.ProcessorsText,
        "startsWithPc" => vm.StartsWithPcText,
        "location" => vm.Location,
        "disks" => vm.DisksText,
        "checkpoints" => vm.CheckpointsText,
        _ => "",
    };
}

/// <summary>
/// The columns the list shows and whether their names are spoken: one layout for both the screen
/// and the row's accessible name, so what is heard is what is shown, in the same order.
/// </summary>
public sealed record VmRowFormat(IReadOnlyList<ColumnDef> Columns, bool SpeakNames)
{
    public static VmRowFormat Default { get; } = new(VmColumns.Set.Defs(VmColumns.Set.Defaults), true);

    public string Compose(VmInfo vm)
    {
        var text = RowText.Compose(Columns, c => VmColumns.Value(vm, c.Id), SpeakNames);
        // Not a column: it says the row can't be acted on just now, so it's always last.
        return vm.IsBusy ? text + (text.Length > 0 && ".?!:;".Contains(text[^1]) ? " busy" : ", busy") : text;
    }
}
