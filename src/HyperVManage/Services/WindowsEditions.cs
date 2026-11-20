namespace HyperVManage.Services;

/// <summary>One edition in a Windows ISO.</summary>
/// <param name="DisplayName">The name shown in the Edition list, which is translated on a non-English ISO.</param>
/// <param name="Name">The image's own name, which isn't translated: "Windows 11 Pro", or for Windows Server an
/// internal one such as "Windows Server 2025 SERVERDATACENTER". The build accepts either name.</param>
/// <param name="Type">Client, Server or Server Core.</param>
public sealed record WindowsEdition(string DisplayName, string Name, string Type)
{
    public bool IsServer => Type.StartsWith("Server", StringComparison.OrdinalIgnoreCase);

    /// <summary>Windows Server with no desktop, Narrator or sound.</summary>
    public bool IsServerCore => Type.Equals("Server Core", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="edition"/> names this one, by either name.</summary>
    public bool Matches(string edition) =>
        DisplayName.Equals(edition.Trim(), StringComparison.OrdinalIgnoreCase) || Name.Equals(edition.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The editions inside a Windows ISO, read with the build script's own code, so the Edition list
/// in the New VM window offers exactly the names the script will accept.
/// </summary>
public static class WindowsEditions
{
    /// <summary>
    /// The editions in the ISO's install.wim or install.esd, in the ISO's own order. An ISO already
    /// open in File Explorer is read where it is and left open; one mounted here is dismounted
    /// again. Throws <see cref="HyperVException"/> if it isn't a Windows install ISO.
    /// </summary>
    /// <remarks>Deliberately not cancellable: an ISO mount outlives PowerShell, so stopping the
    /// read between mounting and dismounting would leave the ISO mounted until Windows restarts.
    /// A caller that no longer wants the answer lets it finish and ignores it.</remarks>
    public static async Task<IReadOnlyList<WindowsEdition>> ReadAsync(string isoPath)
    {
        var script = ReadNamesFunction + $$"""

            $iso = {{Ps.Quote(isoPath)}}
            $image = Get-DiskImage -ImagePath $iso
            $mounted = $false
            if (-not $image.Attached) { $image = Mount-DiskImage -ImagePath $iso -PassThru; $mounted = $true }
            try {
                $letter = ($image | Get-Volume).DriveLetter
                if (-not $letter) { throw "$iso is mounted but has no drive letter." }
                $wim = @("${letter}:\sources\install.wim", "${letter}:\sources\install.esd") |
                    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
                if (-not $wim) { throw "$iso isn't a Windows install ISO." }
                Get-WimImages $wim | ForEach-Object { "$($_.Type)`t$($_.Name)`t$($_.DisplayName)" }
            }
            finally {
                if ($mounted) { Dismount-DiskImage -ImagePath $iso | Out-Null }
            }
            """;
        var output = await PowerShellRunner.RunAsync(script).ConfigureAwait(false);
        return Parse(output);
    }

    /// <summary>
    /// The build script's own Get-WimImages, so the Edition list and the build read an ISO the same
    /// way, from one copy of the code: see the comment on it in New-HyperVRdpVM.ps1.
    /// </summary>
    internal static string ReadNamesFunction => _readNamesFunction ??= FunctionFrom(NewVmScript.ScriptText());
    private static string? _readNamesFunction;

    internal static string FunctionFrom(string script)
    {
        const string begin = "# --- Get-WimImages (begin)", end = "# --- Get-WimImages (end)";
        var from = script.IndexOf(begin, StringComparison.Ordinal);
        var to = from < 0 ? -1 : script.IndexOf(end, from, StringComparison.Ordinal);
        if (to < 0) throw new HyperVException("The build script has no Get-WimImages function.");
        return script[from..to];
    }

    /// <summary>Lines of type, name and display name, separated by tabs; repeats of a display name dropped.</summary>
    internal static IReadOnlyList<WindowsEdition> Parse(string output) =>
        output.Split('\n')
            .Select(l => l.Trim('\r', ' ').Split('\t'))
            .Where(f => f.Length == 3 && f[2].Trim().Length > 0)
            .Select(f => new WindowsEdition(f[2].Trim(), f[1].Trim(), f[0].Trim()))
            .DistinctBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The edition to choose from a freshly read list: <paramref name="current"/> if the ISO has it,
    /// then Pro, the one most people want on a Windows 11 ISO. On a Server ISO, Datacenter with the
    /// desktop, which can run Hyper-V with any number of VMs, then any Server with the desktop.
    /// Never Home, which can't accept Remote Desktop, unless that's all there is. Decided by the
    /// image's type and its own untranslated name, so it works on an ISO in any language.
    /// </summary>
    public static WindowsEdition? Choose(IReadOnlyList<WindowsEdition> editions, string current)
    {
        if (editions.Count == 0) return null;
        static bool Has(string name, string word) => name.Contains(word, StringComparison.OrdinalIgnoreCase);
        return editions.FirstOrDefault(e => e.Matches(current))
            ?? editions.FirstOrDefault(e => !e.IsServer && e.Name.EndsWith(" Pro", StringComparison.OrdinalIgnoreCase))
            ?? editions.FirstOrDefault(e => e.IsServer && !e.IsServerCore && Has(e.Name, "Datacenter"))
            ?? editions.FirstOrDefault(e => e.IsServer && !e.IsServerCore)
            ?? editions.FirstOrDefault(e => !Has(e.Name, "Home"))
            ?? editions[0];
    }
}
