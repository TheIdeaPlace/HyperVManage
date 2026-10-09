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

    public IReadOnlyList<string> Recent() => Read().Where(File.Exists).ToList();

    public void Remember(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var list = Read().Where(p => !p.Equals(full, StringComparison.OrdinalIgnoreCase)).Prepend(full)
                .Take(MaxRemembered).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(list));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception) { }
    }

    private List<string> Read()
    {
        try
        {
            if (!File.Exists(_file)) return [];
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_file))?
                .Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p)).ToList() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }
}

/// <summary>For demo mode and tests: remembers nothing past the window.</summary>
public sealed class InMemoryIsoHistory : IIsoHistory
{
    private readonly List<string> _paths = [];
    public IReadOnlyList<string> Recent() => _paths.ToList();
    public void Remember(string path)
    {
        _paths.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        _paths.Insert(0, path);
    }
}
