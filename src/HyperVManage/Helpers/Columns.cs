namespace HyperVManage.Helpers;

/// <summary>
/// How a column's value is spoken when column names are on. Text is named ("Network: External
/// Wi-Fi"); State and Count already say what they are ("Running", "3 checkpoints") and never are.
/// See the baseline's columns.md.
/// </summary>
public enum ColumnKind { Text, State, Count }

/// <summary>One column a list can show.</summary>
/// <param name="Id">Saved in the settings file; never shown, never renamed.</param>
/// <param name="Heading">The column heading, and the name spoken before a value.</param>
/// <param name="Property">The row's property the column shows, for the column's binding.</param>
/// <param name="SpokenLabel">The name to speak, when it should differ from the heading.</param>
public sealed record ColumnDef(string Id, string Heading, string Property, ColumnKind Kind = ColumnKind.Text,
    double Width = 120, string SpokenLabel = "")
{
    public string Label => SpokenLabel.Length > 0 ? SpokenLabel : Heading;
}

/// <summary>The columns one kind of list can show, and those it shows at first.</summary>
/// <param name="Pinned">A column that is always shown and always first, such as a name: it is the
/// row's name, read first, and typing a letter in the list jumps by it, so a state or a count must
/// never take its place. Null for none.</param>
public sealed record ColumnSet(string Id, IReadOnlyList<ColumnDef> Columns, IReadOnlyList<string> Defaults, string? Pinned = null)
{
    public IEnumerable<string> Ids => Columns.Select(c => c.Id);

    public ColumnDef? Find(string id) => Columns.FirstOrDefault(c => c.Id == id);

    public ColumnDef Column(string id) => Find(id) ?? new ColumnDef(id, id, id);

    public IReadOnlyList<ColumnDef> Defs(IEnumerable<string> ids) => ids.Select(Column).ToList();

    /// <summary>
    /// A saved layout made safe to use: ids the set no longer has, and repeats, are dropped, the
    /// saved order kept. Columns added since stay off until chosen. Nothing usable left means the
    /// defaults.
    /// </summary>
    public IReadOnlyList<string> Reconcile(IEnumerable<string?>? saved)
    {
        var known = Ids.ToHashSet(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var id in saved ?? [])
            if (id is not null && known.Contains(id) && !result.Contains(id)) result.Add(id);
        if (result.Count == 0) result.AddRange(Defaults);
        if (Pinned is not null && known.Contains(Pinned))
        {
            result.Remove(Pinned);
            result.Insert(0, Pinned);
        }
        return result;
    }
}

/// <summary>A row read as one line: what the row's accessible name and the Columns Preview say.</summary>
public static class RowText
{
    /// <summary>
    /// The values in column order, empty ones left out. The first column is never named: it is the
    /// row's name, and typing a letter in a list jumps by it. Others are named only when
    /// <paramref name="speakNames"/> is on and they are text. After a value that already ends a
    /// phrase, only a space follows.
    /// </summary>
    public static string Compose(IReadOnlyList<ColumnDef> columns, Func<ColumnDef, string?> valueOf, bool speakNames)
    {
        var parts = new List<string>();
        for (var i = 0; i < columns.Count; i++)
        {
            var value = (valueOf(columns[i]) ?? "").Trim();
            if (value.Length == 0) continue;
            parts.Add(speakNames && i > 0 && columns[i].Kind == ColumnKind.Text ? $"{columns[i].Label}: {value}" : value);
        }
        if (parts.Count == 0) return "";
        var text = new System.Text.StringBuilder(parts[0]);
        foreach (var part in parts.Skip(1))
            text.Append(".?!:;".Contains(text[^1]) ? " " : ", ").Append(part);
        return text.ToString();
    }
}

/// <summary>
/// The columns a list shows, being changed: what the Columns dialog does, without the dialog, so
/// the rules can be tested without windows. Each action returns false when it could do nothing,
/// and <see cref="Said"/> is always the sentence to tell the user. A move names the column it is
/// now next to, in the direction it went ("Location moved above Memory").
/// </summary>
public sealed class ColumnLayoutEditor
{
    private readonly List<string> _shown;

    public ColumnLayoutEditor(ColumnSet set, IEnumerable<string?>? shown)
    {
        Set = set;
        _shown = set.Reconcile(shown).ToList();
    }

    public ColumnSet Set { get; }

    public IReadOnlyList<string> Shown => _shown;

    public string Said { get; private set; } = "";

    /// <summary>The columns not shown, in the set's order.</summary>
    public IReadOnlyList<string> Available => Set.Ids.Where(id => !_shown.Contains(id)).ToList();

    private string Name(string id) => Set.Column(id).Heading;

    private bool HasPinned => Set.Pinned is not null && _shown.Count > 0 && _shown[0] == Set.Pinned;

    private bool IsPinned(int index) => index == 0 && HasPinned;

    /// <summary>Shows an available column, at the end.</summary>
    public bool Add(int availableIndex)
    {
        var rest = Available;
        if (availableIndex < 0 || availableIndex >= rest.Count)
        {
            Said = rest.Count == 0 ? "Every column is shown." : "No column is selected.";
            return false;
        }
        _shown.Add(rest[availableIndex]);
        Said = $"{Name(rest[availableIndex])} added at the end, {_shown.Count} of {_shown.Count}.";
        return true;
    }

    /// <summary>Stops showing a column. The last one always stays.</summary>
    public bool Remove(int index)
    {
        if (index < 0 || index >= _shown.Count) { Said = "No column is selected."; return false; }
        if (IsPinned(index)) { Said = $"{Name(_shown[index])} always stays first, so it can't be removed."; return false; }
        if (_shown.Count == 1) { Said = "At least one column has to be shown."; return false; }
        var name = Name(_shown[index]);
        _shown.RemoveAt(index);
        Said = $"{name} removed, {_shown.Count} column{(_shown.Count == 1 ? "" : "s")} shown.";
        return true;
    }

    public bool Move(int index, int target)
    {
        var last = _shown.Count - 1;
        if (index < 0 || index > last) { Said = "No column is selected."; return false; }
        var name = Name(_shown[index]);
        if (IsPinned(index)) { Said = $"{name} always stays first."; return false; }
        // Nothing goes above a pinned column: the top is just below it.
        var top = HasPinned ? 1 : 0;
        if (target < top && target < index && index > top) target = top;
        if (target < top || target > last || target == index)
        {
            Said = last == top ? (top == 0 ? $"{name} is the only column shown." : $"{name} is the only column that can move.")
                : target <= index && index == top ? (top == 0 ? $"{name} is already at the top." : $"{name} is already at the top, under {Name(_shown[0])}.")
                : target >= index && index == last ? $"{name} is already at the bottom."
                : $"{name} didn't move.";
            return false;
        }
        var id = _shown[index];
        _shown.RemoveAt(index);
        _shown.Insert(target, id);
        Said = target == top ? (top == 0 ? $"{name} moved to the top." : $"{name} moved to the top, under {Name(_shown[0])}.")
            : target == last ? $"{name} moved to the bottom."
            : target < index ? $"{name} moved above {Name(_shown[target + 1])}."
            : $"{name} moved below {Name(_shown[target - 1])}.";
        return true;
    }

    public void Reset()
    {
        _shown.Clear();
        _shown.AddRange(Set.Defaults);
        Said = "Columns reset to the default.";
    }
}
