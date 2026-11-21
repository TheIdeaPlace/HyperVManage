using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HyperVManage.Helpers;

namespace HyperVManage.ViewModels;

/// <summary>One column in the Columns dialog's lists. ToString is what the list shows and speaks.</summary>
public sealed record ColumnItem(string Id, string Heading)
{
    public override string ToString() => Heading;
}

/// <summary>
/// View, Columns…: which columns the VM list shows, their order, and whether their names are
/// spoken. The rules, and the sentence each action says, are <see cref="ColumnLayoutEditor"/>'s;
/// this keeps the dialog's two lists in step with it, moving items rather than rebuilding the
/// lists, so the one that moved keeps focus.
/// </summary>
public sealed partial class ColumnsViewModel : ObservableObject
{
    private readonly ColumnLayoutEditor _editor;
    private readonly Func<IReadOnlyList<ColumnDef>, bool, string> _previewOf;

    /// <param name="previewOf">The row as it would be read with these columns and names on or off:
    /// the same code that names the list's rows, never a second copy.</param>
    public ColumnsViewModel(ColumnSet set, IEnumerable<string?> shown, bool speakNames, Func<IReadOnlyList<ColumnDef>, bool, string> previewOf)
    {
        _editor = new ColumnLayoutEditor(set, shown);
        _previewOf = previewOf;
        _speakNames = speakNames;
        Shown = [.. _editor.Shown.Select(Item)];
        Available = [.. _editor.Available.Select(Item)];
        UpdatePreview();
    }

    public ObservableCollection<ColumnItem> Shown { get; }
    public ObservableCollection<ColumnItem> Available { get; }

    [ObservableProperty] private bool _speakNames;
    [ObservableProperty] private string _preview = "";

    /// <summary>The sentence the last action said.</summary>
    public string Said => _editor.Said;

    public IReadOnlyList<string> ShownIds => _editor.Shown;

    private ColumnItem Item(string id) => new(id, _editor.Set.Column(id).Heading);

    partial void OnSpeakNamesChanged(bool value) => UpdatePreview();

    private void UpdatePreview() => Preview = _previewOf(_editor.Set.Defs(_editor.Shown), SpeakNames);

    public bool Add(int availableIndex)
    {
        if (!_editor.Add(availableIndex)) return false;
        var item = Available[availableIndex];
        Available.RemoveAt(availableIndex);
        Shown.Add(item);
        UpdatePreview();
        return true;
    }

    public bool Remove(int shownIndex)
    {
        if (!_editor.Remove(shownIndex)) return false;
        var item = Shown[shownIndex];
        Shown.RemoveAt(shownIndex);
        // Back in its place in the set's order.
        var at = _editor.Available.ToList().IndexOf(item.Id);
        Available.Insert(Math.Clamp(at, 0, Available.Count), item);
        UpdatePreview();
        return true;
    }

    /// <summary>Moves a column; where it ended up is <see cref="MovedTo"/>, which can differ from the
    /// target asked for: nothing goes above Name, which always stays first.</summary>
    public bool Move(int index, int target)
    {
        var id = index >= 0 && index < Shown.Count ? Shown[index].Id : null;
        if (!_editor.Move(index, target)) return false;
        MovedTo = _editor.Shown.ToList().IndexOf(id!);
        Shown.Move(index, MovedTo);
        UpdatePreview();
        return true;
    }

    public int MovedTo { get; private set; } = -1;

    public void Reset()
    {
        _editor.Reset();
        Shown.Clear();
        foreach (var id in _editor.Shown) Shown.Add(Item(id));
        Available.Clear();
        foreach (var id in _editor.Available) Available.Add(Item(id));
        UpdatePreview();
    }
}
