using System.IO;
using System.Text.Json;

namespace HyperVManage.Services;

/// <summary>The Windows ISOs VMs have been built from, most recent first, so New Virtual Machine
/// can offer them again.</summary>
public interface IIsoHistory
{
    /// <summary>The remembered ISOs that still exist, most recent first.</summary>
    IReadOnlyList<string> Recent();

    /// <summary>Puts an ISO first in the list.</summary>
    void Remember(string path);
}

/// <summary>
/// Kept in %AppData%\HyperVManage\recent-isos.json: outside the install folder, so updates and
/// reinstalls keep it. Only paths are stored. A file that can't be read or written is treated as
/// an empty list, since remembering is a convenience and must never stop a build.
/// </summary>
public sealed class IsoHistory(string? file = null) : IIsoHistory
{
    public const int MaxRemembered = 10;

    private readonly string _file = file ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HyperVManage", "recent-isos.json");

    public IReadOnlyList<string> Recent() => Read().Where(StillThere).ToList();

    /// <summary>A file on this PC that has gone isn't offered. One on a network share or a mapped
    /// network drive is offered unchecked: asking a server that isn't there would hold up the
    /// window for its timeout, and Create checks the file anyway.</summary>
    internal static bool StillThere(string path) => IsOnNetwork(path) || File.Exists(path);

    private static bool IsOnNetwork(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try
        {
            // Asks Windows what kind of drive the letter is, without touching the drive itself.
            return Path.GetPathRoot(path) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Remember(string path)
    {
        string? temp = null;
        try
        {
            var list = Add(Read(), path);
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            // A name of its own, so two copies of the app building at once don't share one; then
            // moved over the list in one step, so it's never half written.
            temp = $"{_file}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(list));
            File.Move(temp, _file, overwrite: true);
            temp = null;
        }
        catch (Exception) { }
        finally
        {
            if (temp is not null) try { File.Delete(temp); } catch (Exception) { }
        }
    }

    /// <summary>The list with an ISO put first, once, and no more than <see cref="MaxRemembered"/>.</summary>
    internal static List<string> Add(IEnumerable<string> list, string path)
    {
        var full = Path.GetFullPath(path);
        return list.Where(p => !p.Equals(full, StringComparison.OrdinalIgnoreCase)).Prepend(full).Take(MaxRemembered).ToList();
    }

    private static bool IsIsoPath(string? p) =>
        !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p) && p.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);

    private List<string> Read()
    {
        try
        {
            if (!File.Exists(_file)) return [];
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_file))?
                .Where(IsIsoPath).ToList() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }
}

/// <summary>For demo mode and tests: the same list, kept only until the app closes.</summary>
public sealed class InMemoryIsoHistory : IIsoHistory
{
    private List<string> _paths = [];
    public IReadOnlyList<string> Recent() => _paths.Where(IsoHistory.StillThere).ToList();
    public void Remember(string path) => _paths = IsoHistory.Add(_paths, path);
}
