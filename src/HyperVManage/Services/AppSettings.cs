using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HyperVManage.Services;

/// <summary>What the app remembers between runs, apart from the recent ISOs (IsoHistory).</summary>
public sealed class SettingsData
{
    /// <summary>The VM list's columns, by id, in order. Cleaned up against the column set on load.</summary>
    [JsonPropertyName("vmListColumns")] public List<string?>? VmListColumns { get; set; }

    /// <summary>Whether a row's text columns are spoken with their names ("Network: External Wi-Fi").
    /// Null, never chosen, means on: without names, values like "4" or "Always" say nothing.</summary>
    [JsonPropertyName("speakColumnNames")] public bool? SpeakColumnNames { get; set; }

    /// <summary>Column widths the user dragged, by column id.</summary>
    [JsonPropertyName("vmListColumnWidths")] public Dictionary<string, double>? VmListColumnWidths { get; set; }

    /// <summary>The folder new VMs and clones were last put in; empty for Hyper-V's own default folders.</summary>
    [JsonPropertyName("vmLocation")] public string? VmLocation { get; set; }
}

/// <summary>Reads and saves <see cref="SettingsData"/>.</summary>
public interface ISettingsStore
{
    SettingsData Load();

    /// <summary>Saves; returns false if it couldn't. A failed save never undoes the change for the session.</summary>
    bool Save(SettingsData data);
}

/// <summary>
/// %AppData%\HyperVManage\settings.json, beside recent-isos.json, so updates and reinstalls keep it.
/// A file that can't be read is set aside as settings.json.unreadable-&lt;time&gt; rather than
/// overwritten, so nothing someone cared about is lost; the app then starts from the defaults.
/// </summary>
public sealed class SettingsStore(string? file = null) : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _file = file ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HyperVManage", "settings.json");

    public string FilePath => _file;

    public SettingsData Load()
    {
        try
        {
            if (!File.Exists(_file)) return new SettingsData();
            return JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(_file)) ?? new SettingsData();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            SetAside();
            return new SettingsData();
        }
        catch (Exception)
        {
            // Can't be opened just now (locked by antivirus or a sync, no access): use the defaults
            // for this run, and leave the file alone, never saving over it.
            _couldNotRead = true;
            return new SettingsData();
        }
    }

    /// <summary>Set when the file was there but couldn't be opened: this run mustn't save over it.</summary>
    private bool _couldNotRead;

    private void SetAside()
    {
        try { File.Move(_file, $"{_file}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true); }
        catch (Exception) { }
    }

    public bool Save(SettingsData data)
    {
        if (_couldNotRead) return false;
        string? temp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            // Written beside it, then moved over it in one step, so it's never half written.
            temp = $"{_file}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, Options));
            File.Move(temp, _file, overwrite: true);
            temp = null;
            return true;
        }
        catch (Exception) { return false; }
        finally
        {
            if (temp is not null) try { File.Delete(temp); } catch (Exception) { }
        }
    }
}

/// <summary>For demo mode and tests: settings kept only until the app closes.</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    private string _json = "{}";
    public int Saves { get; private set; }
    public SettingsData Load() => JsonSerializer.Deserialize<SettingsData>(_json) ?? new SettingsData();
    public bool Save(SettingsData data) { _json = JsonSerializer.Serialize(data); Saves++; return true; }
}
