using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HyperVManage.Services;

namespace HyperVManage.ViewModels;

/// <summary>
/// The New VM window: the options of New-HyperVRdpVM.ps1, then the script's own output as it runs.
/// Each line the script prints is also spoken, which is how its console reads with a screen reader.
/// It builds Windows or, opened for Ubuntu, an Ubuntu desktop; that is fixed when it opens, so the
/// window's title always says which.
/// </summary>
public sealed partial class NewVmViewModel : ObservableObject
{
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly StringBuilder _log = new();
    private CancellationTokenSource? _stop;

    /// <summary>Runs the build. Swapped for the demo, and in tests, so nothing real runs.</summary>
    internal Func<NewVmOptions, Action<string>, CancellationToken, Task<BuildOutcome>> RunScript { get; set; } = NewVmScript.RunAsync;

    private readonly IIsoHistory _isoHistory;

    /// <summary>Where each build's output is saved, so it can be read after the window has closed
    /// or sent with a bug report. Null, the default, saves nothing: the window sets it for real
    /// builds, so neither the demo nor the tests write into the user's folder.</summary>
    internal string? LogFolder { get; set; }

    public static string DefaultLogFolder { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyperVManage", "Logs");

    /// <summary>The file the last build's output was saved to; null until one has been.</summary>
    public string? LogPath { get; private set; }

    /// <summary>The last entry in the Windows ISO list: choosing it opens the file dialog.</summary>
    public const string BrowseChoice = "Browse for an ISO…";

    /// <param name="isoHistory">The ISOs built from before; none if not given.</param>
    /// <param name="ubuntu">Build an Ubuntu desktop rather than Windows.</param>
    public NewVmViewModel(IEnumerable<string> existingNames, IIsoHistory? isoHistory = null, bool ubuntu = false)
    {
        IsUbuntu = ubuntu;
        _isoHistory = isoHistory ?? new InMemoryIsoHistory();
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        // This PC's name first, as the script does, so VMs made on different PCs on one network
        // don't share a name: two computers with one name confuse Remote Desktop.
        _vmName = SuggestName($"{Environment.MachineName}-{(ubuntu ? "Ubuntu" : "Win11")}", taken);
        // Windows 11 needs 64 GB; the Ubuntu desktop far less. The disk grows only as it's used.
        _diskGB = ubuntu ? "64" : "128";
        // The ISOs used before, most recent first, then the newest in Downloads if it isn't one of
        // them; the most recent is chosen. Then Browse.
        // Ubuntu's ISO is the script's own download, so Downloads isn't searched for one.
        var newest = ubuntu ? null : IsoFinder.FindNewest(IsoFinder.DownloadsFolder, IsoFinder.HostIsArm64);
        var choices = _isoHistory.Recent().ToList();
        if (newest is not null && !choices.Contains(newest, StringComparer.OrdinalIgnoreCase)) choices.Add(newest);
        _isoPath = choices.FirstOrDefault() ?? "";
        IsoChoices = [.. choices, BrowseChoice];
    }

    /// <summary>Whether this builds Ubuntu; otherwise Windows.</summary>
    public bool IsUbuntu { get; }
    public bool IsWindows => !IsUbuntu;

    public string Title => IsUbuntu ? "New Ubuntu Virtual Machine" : "New Windows Virtual Machine";

    /// <summary>The line at the top of the form, saying what the build does.</summary>
    public string Intro => IsUbuntu
        ? "Builds an Ubuntu 26.04 LTS desktop VM with an unattended install, and leaves it ready to connect to with Remote Desktop, which goes straight to the desktop with Orca talking. Ubuntu is downloaded from ubuntu.com the first time, about 4 GB on Arm PCs and 6.5 GB on others, and kept for next time. The install takes 20 to 40 minutes and needs nothing from you once it starts."
        : "Builds a Windows VM with an unattended install and leaves it ready to sign in to with Remote Desktop. It takes 15 to 30 minutes and needs nothing from you once it starts.";

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
    [ObservableProperty] private string _diskGB;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HostOnly))] private bool _onYourNetwork = true;
    [ObservableProperty] private bool _autoStart = true;
    [ObservableProperty] private bool _connectWhenDone = true;
    /// <summary>Ubuntu only: the Orca screen reader on from the start.</summary>
    [ObservableProperty] private bool _orca = true;

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
        // Ubuntu's ISO is the script's to find or download.
        var iso = IsUbuntu ? "" : IsoPath.Trim();
        var browseShowing = iso == BrowseChoice;
        // The ISO is always passed explicitly, so a stopped build knows which one to dismount.
        if (browseShowing) iso = "";
        else if (iso.Length == 0 && IsWindows) iso = IsoFinder.FindNewest(IsoFinder.DownloadsFolder, IsoFinder.HostIsArm64) ?? "";

        // The script mounts the full path, so the cleanup after a stop must dismount that same path.
        if (iso.Length > 0) { try { iso = System.IO.Path.GetFullPath(iso); } catch (Exception) { } }

        // The same limits as the script's own checks, asked here so they show in the form rather
        // than as a failed build.
        var computer = RemoteDesktop.ComputerName(name);
        var user = UserName.Trim();
        var maxProcessors = Math.Min(Environment.ProcessorCount, 64);
        var system = IsUbuntu ? "Ubuntu" : "Windows";
        if (NewVmScript.NameProblem(name) is { } nameProblem) problem = nameProblem;
        else if (computer.Length == 0) problem = $"The name needs at least one letter or digit, since {system} names the computer after it.";
        else if (computer.All(char.IsAsciiDigit)) problem = $"The name needs at least one letter: {system} can't use a computer name made only of digits.";
        else if (IsUbuntu && NewVmScript.UbuntuUserNameProblem(user) is { } userProblem) problem = userProblem;
        else if (IsWindows && (user.Length > 20 || (user.Length > 0 && user.Trim('.', ' ').Length == 0) || user.IndexOfAny(['"', '/', '\\', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>', '@']) >= 0))
            problem = "Windows can't use that user name. Use up to 20 letters, digits, spaces, dots, hyphens or underscores.";
        // Remote Desktop signs in with the password, so Ubuntu's account needs one.
        else if (IsUbuntu && Password.Length == 0) problem = "Give the Ubuntu account a password: Remote Desktop signs in with it.";
        else if (Password.Contains('"')) problem = "The password can't contain a double quote (\").";
        else if (browseShowing) problem = "Choose a Windows ISO: press Enter on Browse for an ISO, or pick one from the list.";
        else if (IsWindows && iso.Length == 0) problem = "There's no Windows ISO in your Downloads folder. Choose Browse for an ISO in the Windows ISO list.";
        else if (IsWindows && !System.IO.File.Exists(iso)) problem = $"Can't find the ISO {iso}.";
        else if (!int.TryParse(Processors.Trim(), out var c) || c < 1 || c > maxProcessors)
            problem = $"Processors must be a whole number from 1 to {maxProcessors}.";
        else if (!int.TryParse(MemoryGB.Trim(), out var m) || m < 2 || m > 512) problem = "Memory must be a whole number of gigabytes, from 2 to 512.";
        else if (IsWindows && (!int.TryParse(DiskGB.Trim(), out var d) || d < 64)) problem = "Disk size must be a whole number of gigabytes, at least 64. Windows 11 needs that much.";
        else if (IsUbuntu && (!int.TryParse(DiskGB.Trim(), out var u) || u < NewVmScript.UbuntuMinimumDiskGB))
            problem = $"Disk size must be a whole number of gigabytes, at least {NewVmScript.UbuntuMinimumDiskGB}. The Ubuntu desktop needs that much.";
        else if (IsWindows && Edition.Contains("Home", StringComparison.OrdinalIgnoreCase)) problem = "Home editions can't accept Remote Desktop connections. Use Pro, Enterprise or Education.";

        Error = problem ?? "";
        if (problem is not null) return null;
        return new NewVmOptions(name, iso, IsUbuntu ? "" : Edition.Trim(), UserName.Trim(), Password,
            int.Parse(Processors.Trim()), int.Parse(MemoryGB.Trim()), int.Parse(DiskGB.Trim()),
            HostOnly, AutoStart, ConnectWhenDone, IsUbuntu, Orca);
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task Create()
    {
        var options = Validate();
        if (options is null) { Announce?.Invoke(Error); return; }

        // Remembered as the build starts, so it's offered first next time, even if this one fails.
        // Ubuntu's ISO is the script's own download, so there is none to remember.
        if (IsWindows) _isoHistory.Remember(options.IsoPath);
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
        Post(() => End(result, options));
    }
    private bool CanCreate() => !HasStarted && !IsRunning;

    /// <summary>Stops the build. The runner cleans up what it had made, then Finished is raised.</summary>
    public void Stop() => _stop?.Cancel();

    private void End(BuildOutcome result, NewVmOptions options)
    {
        var name = options.VMName;
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
        // The whole path is in the box, to arrow to and copy; spoken, it's a string of backslashes.
        if (SaveLog(options) is { } path)
            Append($"This output is saved in {path}", spoken: "This output is saved in the Hyper-V Manage Logs folder.");
        Finished?.Invoke(result);
    }

    /// <summary>Writes the output to a file named for the VM and the time. The script prints the
    /// password at the end, so that line is blanked out: only that line, since a short password
    /// blanked wherever its letters turn up would garble the log and give the password away.
    /// Null if there's nowhere to save it.</summary>
    private string? SaveLog(NewVmOptions options)
    {
        if (LogFolder is null) return null;
        try
        {
            System.IO.Directory.CreateDirectory(LogFolder);
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var safeName = new string(options.VMName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var path = System.IO.Path.Combine(LogFolder, $"New VM {safeName} {stamp}.log");
            var passwordLine = $"Password: {options.Password}";
            var lines = LogText.Split(Environment.NewLine)
                .Select(l => options.Password.Length > 0 && l.Trim() == passwordLine ? l.Replace(passwordLine, "Password: (hidden)") : l);
            System.IO.File.WriteAllLines(path, lines);
            return LogPath = path;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Append($"Couldn't save this output to a file: {ex.Message}");
            return null;
        }
    }

    // PowerShell's error records add lines like "At C:\...ps1:123 char:5", "+ throw ..." and
    // "    + CategoryInfo ...". Useful in the log; noise when spoken one by one.
    private static readonly Regex ErrorRecordDetail = new(@"^\s*(At [A-Za-z]:\\|At line:|\+)", RegexOptions.Compiled);

    /// <param name="spoken">What a screen reader says instead of the line; by default, the line.</param>
    internal void Append(string line, string? spoken = null)
    {
        if (_log.Length > 0) _log.AppendLine();
        _log.Append(line);
        LineAppended?.Invoke(line);
        if (spoken is not null) Announce?.Invoke(spoken);
        else if (!string.IsNullOrWhiteSpace(line) && !ErrorRecordDetail.IsMatch(line)) Announce?.Invoke(line);
    }

    private void Post(Action a)
    {
        if (_ui is null) a();
        else _ui.Post(_ => a(), null);
    }
}
