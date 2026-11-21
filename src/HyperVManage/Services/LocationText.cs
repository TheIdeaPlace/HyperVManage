namespace HyperVManage.Services;

/// <summary>The Location box shared by New VM and Clone: what's typed, tidied, and what it means.</summary>
public static class LocationText
{
    /// <summary>
    /// What was typed or pasted, as the location to use: spaces and the quotes File Explorer's
    /// Copy as Path adds taken off, and a trailing backslash dropped except from a drive's root
    /// (D:\), so D:\Hyper-V and D:\Hyper-V\ are one location.
    /// </summary>
    public static string Normalize(string? typed)
    {
        var text = (typed ?? "").Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1].Trim();
        while (text.Length > 3 && text.EndsWith('\\') && !text.EndsWith(@":\", StringComparison.Ordinal)) text = text[..^1];
        if (text.Length == 2 && char.IsAsciiLetter(text[0]) && text[1] == ':') text += '\\';
        return text;
    }

    /// <summary>Where a VM of this name will go, in a sentence.</summary>
    /// <param name="subject">What is being placed: "The VM", or "The copy" for a clone.</param>
    public static string Describe(string? location, string? vmName, string subject = "The VM")
    {
        var where = Normalize(location);
        var name = (vmName ?? "").Trim();
        if (where.Length == 0) return "Empty means Hyper-V's own default folders on this PC.";
        return name.Length == 0
            ? $"{subject} goes in a folder named after it in {where}, with its disk in Virtual Hard Disks there."
            : $"{subject} goes in {NewVmScript.VmFolder(where, name)}, with its disk in Virtual Hard Disks there.";
    }
}
