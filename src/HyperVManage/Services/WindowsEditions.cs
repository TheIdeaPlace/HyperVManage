namespace HyperVManage.Services;

/// <summary>
/// The editions inside a Windows ISO, read with the build script's own code, so the Edition list
/// in the New VM window offers exactly the names the script will accept.
/// </summary>
public static class WindowsEditions
{
    /// <summary>
    /// The edition names in the ISO's install.wim or install.esd, in the ISO's own order: each
    /// image's display name, which for Windows Server is the friendly one. An ISO
    /// already open in File Explorer is read where it is and left open; one mounted here is
    /// dismounted again. Throws <see cref="HyperVException"/> if it isn't a Windows install ISO.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReadAsync(string isoPath, CancellationToken ct = default)
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
                Get-WimImages $wim | ForEach-Object { $_.DisplayName }
            }
            finally {
                if ($mounted) { Dismount-DiskImage -ImagePath $iso | Out-Null }
            }
            """;
        var output = await PowerShellRunner.RunAsync(script, ct).ConfigureAwait(false);
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

    internal static IReadOnlyList<string> Parse(string output) =>
        output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Whether the edition is Windows Server rather than Windows 11 or 10.</summary>
    public static bool IsServer(string edition) => edition.Contains("Server", StringComparison.OrdinalIgnoreCase);

    /// <summary>Server Core: Windows Server with no desktop, which is every Server edition whose
    /// name doesn't end in (Desktop Experience).</summary>
    public static bool IsServerCore(string edition) =>
        IsServer(edition) && !edition.Contains("Desktop Experience", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The edition to choose from a freshly read list: <paramref name="current"/> if the ISO has it,
    /// then Pro, the one most people want on a Windows 11 ISO. On a Server ISO, Datacenter with
    /// the desktop, which can run Hyper-V with any number of VMs, then Standard with the desktop.
    /// Never Home, which can't accept Remote Desktop, unless that's all there is.
    /// </summary>
    public static string? Choose(IReadOnlyList<string> editions, string current)
    {
        if (editions.Count == 0) return null;
        static bool Has(string name, string word) => name.Contains(word, StringComparison.OrdinalIgnoreCase);
        return editions.FirstOrDefault(e => e.Equals(current.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? editions.FirstOrDefault(e => e.EndsWith(" Pro", StringComparison.OrdinalIgnoreCase))
            ?? editions.FirstOrDefault(e => Has(e, "Datacenter") && Has(e, "Desktop Experience"))
            ?? editions.FirstOrDefault(e => Has(e, "Desktop Experience"))
            ?? editions.FirstOrDefault(e => !Has(e, "Home"))
            ?? editions[0];
    }
}
