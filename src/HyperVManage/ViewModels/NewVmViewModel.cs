using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HyperVManage.Services;

namespace HyperVManage.ViewModels;

/// <summary>
/// The New VM window: the options of New-HyperVRdpVM.ps1, then the script's own output as it runs.
/// Each line the script prints is also spoken, which is how its console reads with a screen reader.
/// </summary>
public sealed partial class NewVmViewModel : ObservableObject
{
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly StringBuilder _log = new();
    private CancellationTokenSource? _stop;

    /// <summary>Runs the build. Swapped for the demo, and in tests, so nothing real runs.</summary>
    internal Func<NewVmOptions, Action<string>, CancellationToken, Task<BuildOutcome>> RunScript { get; set; } = NewVmScript.RunAsync;

    private readonly IIsoHistory _isoHistory;

    /// <summary>The last entry in the Windows ISO list: choosing it opens the file dialog.</summary>
    public const string BrowseChoice = "Browse for an ISO…";

    /// <param name="isoHistory">The ISOs built from before; none if not given.</param>
    public NewVmViewModel(IEnumerable<string> existingNames, IIsoHistory? isoHistory = null)
    {
        _isoHistory = isoHistory ?? new InMemoryIsoHistory();
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        // This PC's name first, as the script does, so VMs made on different PCs on one network
        // don't share a name: two computers with one name confuse Remote Desktop.
        _vmName = SuggestName($"{Environment.MachineName}-Win11", taken);
        // The ISOs used before, most recent first, then the newest in Downloads if it isn't one of
        // them; the most recent is chosen. Then Browse.
        var newest = IsoFinder.FindNewest(IsoFinder.DownloadsFolder, IsoFinder.HostIsArm64);
        var choices = _isoHistory.Recent().ToList();
        if (newest is not null && !choices.Contains(newest, StringComparer.OrdinalIgnoreCase)) choices.Add(newest);
        _isoPath = choices.FirstOrDefault() ?? "";
        IsoChoices = [.. choices, BrowseChoice];
    }

    /// <summary>What the Windows ISO list offers: remembered ISOs, Downloads' newest, then <see cref="BrowseChoice"/>.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> IsoChoices { get; }

    /// <summary>A file chosen with Browse: added to the list, after the remembered ones, and chosen.</summary>
    public void ChooseBrowsedIso(string path)
    {
        if (!IsoChoices.Contains(path, StringComparer.OrdinalIgnoreCase)) IsoChoices.Insert(IsoChoices.Count - 1, path);
        IsoPath = IsoChoices.First(c => c.Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The base name, for example SURFACEPRO7-Win11, or -2, -3 ... after it if that is taken.</summary>
    internal static string SuggestName(string baseName, ISet<string> taken)
    {
        if (!taken.Contains(baseName)) return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName}-{i}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    [ObservableProperty] private string _vmName;
    [ObservableProperty] private string _isoPath;
    [ObservableProperty] private string _edition = "Windows 11 Pro";
    [ObservableProperty] private string _userName = "vmuser";
    [ObservableProperty] private string _password = "vmadmin";
    [ObservableProperty] private string _processors = "4";
    [ObservableProperty] private string _memoryGB = "4";
    [ObservableProperty] private string _diskGB = "128";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HostOnly))] private bool _onYourNetwork = true;
    [ObservableProperty] private bool _autoStart = true;
    [ObservableProperty] private bool _connectWhenDone = true;

    public bool HostOnly
    {
        get => !OnYourNetwork;
        set => OnYourNetwork = !value;
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowForm), nameof(ShowProgress))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private bool _hasStarted;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CreateCommand))] private bool _isRunning;
    [ObservableProperty] private string _outcome = "";

    public bool HasError => Error.Length > 0;
    public bool ShowForm => !HasStarted;
    public bool ShowProgress => HasStarted;

    /// <summary>Everything printed so far, for tests and the log. The window appends lines as they come (see
    /// <see cref="LineAppended"/>) rather than binding to this, so the caret isn't reset.</summary>
    public string LogText => _log.ToString();

    /// <summary>A new line for the progress box.</summary>
    public event Action<string>? LineAppended;

    /// <summary>Text for a screen reader: the script's lines, and how it ended.</summary>
    public event Action<string>? Announce;

    /// <summary>Raised when the build ends, however it ended.</summary>
    public event Action<BuildOutcome>? Finished;

    internal NewVmOptions? Validate()
    {
        string? problem = null;
        var name = VmName.Trim();
        var iso = IsoPath.Trim();
        var browseShowing = iso == BrowseChoice;
        // The ISO is always passed explicitly, so a stopped build knows which one to dismount.
        if (browseShowing) iso = "";
        else if (iso.Length == 0) iso = IsoFinder.FindNewest(IsoFinder.DownloadsFolder, IsoFinder.HostIsArm64) ?? "";

        // The script mounts the full path, so the cleanup after a stop must dismount that same path.
        if (iso.Length > 0) { try { iso = System.IO.Path.GetFullPath(iso); } catch (Exception) { } }

        // The same limits as the script's own checks, asked here so they show in the form rather
        // than as a failed build.
        var computer = RemoteDesktop.ComputerName(name);
        var user = UserName.Trim();
        var maxProcessors = Math.Min(Environment.ProcessorCount, 64);
        if (NewVmScript.NameProblem(name) is { } nameProblem) problem = nameProblem;
        else if (computer.Length == 0) problem = "The name needs at least one letter or digit, since Windows names the computer after it.";
        else if (computer.All(char.IsAsciiDigit)) problem = "The name needs at least one letter: Windows can't use a computer name made only of digits.";
        else if (user.Length > 20 || (user.Length > 0 && user.Trim('.', ' ').Length == 0) || user.IndexOfAny(['"', '/', '\\', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>', '@']) >= 0)
            problem = "Windows can't use that user name. Use up to 20 letters, digits, spaces, dots, hyphens or underscores.";
        else if (Password.Contains('"')) problem = "The password can't contain a double quote (\").";
        else if (browseShowing) problem = "Choose a Windows ISO: press Enter on Browse for an ISO, or pick one from the list.";
        else if (iso.Length == 0) problem = "There's no Windows ISO in your Downloads folder. Choose Browse for an ISO in the Windows ISO list.";
        else if (!System.IO.File.Exists(iso)) problem = $"Can't find the ISO {iso}.";
        else if (!int.TryParse(Processors.Trim(), out var c) || c < 1 || c > maxProcessors)
            problem = $"Processors must be a whole number from 1 to {maxProcessors}.";
        else if (!int.TryParse(MemoryGB.Trim(), out var m) || m < 2 || m > 512) problem = "Memory must be a whole number of gigabytes, from 2 to 512.";
        else if (!int.TryParse(DiskGB.Trim(), out var d) || d < 64) problem = "Disk size must be a whole number of gigabytes, at least 64. Windows 11 needs that much.";
        else if (Edition.Contains("Home", StringComparison.OrdinalIgnoreCase)) problem = "Home editions can't accept Remote Desktop connections. Use Pro, Enterprise or Education.";

        Error = problem ?? "";
        if (problem is not null) return null;
        return new NewVmOptions(name, iso, Edition.Trim(), UserName.Trim(), Password,
            int.Parse(Processors.Trim()), int.Parse(MemoryGB.Trim()), int.Parse(DiskGB.Trim()),
            HostOnly, AutoStart, ConnectWhenDone);
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task Create()
    {
        var options = Validate();
        if (options is null) { Announce?.Invoke(Error); return; }

        // Remembered as the build starts, so it's offered first next time, even if this one fails.
        _isoHistory.Remember(options.IsoPath);
        HasStarted = true;
        IsRunning = true;
        _stop = new CancellationTokenSource();
        Append("Starting New-HyperVRdpVM.ps1.");
        BuildOutcome result;
        try
        {
            result = await RunScript(options, line => Post(() => Append(line)), _stop.Token);
        }
        catch (Exception ex)
        {
            Append($"Couldn't run the script: {ex.Message}");
            result = BuildOutcome.Failed;
        }
        // Lines posted from the runner's thread land before this, which is posted after them.
        Post(() => End(result, options.VMName));
    }
    private bool CanCreate() => !HasStarted && !IsRunning;

    /// <summary>Stops the build. The runner cleans up what it had made, then Finished is raised.</summary>
    public void Stop() => _stop?.Cancel();

    private void End(BuildOutcome result, string name)
    {
        IsRunning = false;
        _stop?.Dispose();
        _stop = null;
        Outcome = result switch
        {
            BuildOutcome.Succeeded => $"Finished. {name} is ready.",
            BuildOutcome.Stopped => "Stopped.",
            _ => "The script stopped with an error. The lines above say what went wrong.",
        };
        Append(Outcome);
        Finished?.Invoke(result);
    }

    // PowerShell's error records add lines like "At C:\...ps1:123 char:5", "+ throw ..." and
    // "    + CategoryInfo ...". Useful in the log; noise when spoken one by one.
    private static readonly Regex ErrorRecordDetail = new(@"^\s*(At [A-Za-z]:\\|At line:|\+)", RegexOptions.Compiled);

    internal void Append(string line)
    {
        if (_log.Length > 0) _log.AppendLine();
        _log.Append(line);
        LineAppended?.Invoke(line);
        if (!string.IsNullOrWhiteSpace(line) && !ErrorRecordDetail.IsMatch(line)) Announce?.Invoke(line);
    }

    private void Post(Action a)
    {
        if (_ui is null) a();
        else _ui.Post(_ => a(), null);
    }
}
