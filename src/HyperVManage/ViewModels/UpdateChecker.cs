using HyperVManage.Services;

namespace HyperVManage.ViewModels;

/// <summary>
/// Checking for updates, at start and from Help, Check for Updates, and saying what was found.
/// The window answers the questions through the callbacks and shows what is reported.
/// </summary>
public sealed class UpdateChecker
{
    private readonly IUpdateService _updates;
    private readonly string _current;
    private Task<UpdateCheck>? _checking;
    private bool _asking;

    public UpdateChecker(IUpdateService updates, string? currentVersion = null)
    {
        _updates = updates;
        _current = currentVersion ?? AppInfo.Version;
    }

    /// <summary>Text for the status bar, which is also spoken.</summary>
    public Action<string>? Report { get; set; }

    /// <summary>Asks a yes or no question; true for yes.</summary>
    public Func<string, bool>? Confirm { get; set; }

    /// <summary>True while something is being done that closing the app would cut short: New
    /// Virtual Machine building one, or a VM being cloned, checkpointed, deleted or changed.</summary>
    public Func<bool>? Busy { get; set; }

    /// <summary>Opens a release's page in the browser.</summary>
    public Action<Uri>? OpenPage { get; set; }

    /// <summary>One check at a time: asked again while one runs, its answer is shared.</summary>
    private Task<UpdateCheck> Check() =>
        _checking is { IsCompleted: false } running ? running : _checking = _updates.CheckAsync();

    /// <summary>At start: says nothing unless there is an update, and never asks anything.</summary>
    public async Task CheckAtStartAsync()
    {
        var found = await Check();
        if (found.Status != UpdateStatus.Available) return;
        Report?.Invoke(_updates.InstallsItself
            ? $"Hyper-V Manage {found.Version} is available. It downloads while you work and is installed when you close Hyper-V Manage, or now from Help, Check for Updates."
            : $"Hyper-V Manage {found.Version} is available. Help, Check for Updates opens its download page.");
    }

    /// <summary>From the Help menu: always says what it found, and offers to install.</summary>
    public async Task CheckNowAsync()
    {
        if (_asking) { Report?.Invoke("Already checking for updates."); return; }
        _asking = true;
        try
        {
            Report?.Invoke("Checking for updates.");
            var found = await Check();
            switch (found.Status)
            {
                case UpdateStatus.UpToDate:
                    Report?.Invoke($"Hyper-V Manage {_current} is the newest version.");
                    return;
                case UpdateStatus.Failed:
                    Report?.Invoke($"Couldn't check for updates. {found.Problem}");
                    return;
            }

            if (!_updates.InstallsItself)
            {
                if (Confirm?.Invoke($"Hyper-V Manage {found.Version} is available. You have {_current}.\n\nOpen its download page?") == true
                    && found.ReleasePage is { } page)
                {
                    OpenPage?.Invoke(page);
                    Report?.Invoke("Opening the download page in your browser.");
                }
                return;
            }

            var later = $"Hyper-V Manage {found.Version} is installed when you close Hyper-V Manage, once it has downloaded.";
            // Installing closes the app at once, so not while it's in the middle of something.
            if (Busy?.Invoke() == true)
            {
                Report?.Invoke($"Hyper-V Manage {found.Version} is available, but can't be installed while a virtual machine is being built or changed. {later}");
                return;
            }
            if (Confirm?.Invoke($"Hyper-V Manage {found.Version} is available. You have {_current}.\n\n" +
                                "Install it now? Hyper-V Manage closes and opens again with the new version.") != true)
            {
                Report?.Invoke(later);
                return;
            }

            Report?.Invoke($"Downloading Hyper-V Manage {found.Version}.");
            var problem = await _updates.DownloadAsync();
            if (problem.Length > 0)
            {
                Report?.Invoke($"Couldn't download the update. {problem}");
                return;
            }
            // The download can take minutes, and something may have been started meanwhile.
            if (Busy?.Invoke() == true)
            {
                Report?.Invoke($"Hyper-V Manage {found.Version} has downloaded, and is installed when you close Hyper-V Manage: " +
                               "a virtual machine is being built or changed, which installing now would stop.");
                return;
            }
            Report?.Invoke($"Installing Hyper-V Manage {found.Version}.");
            problem = _updates.InstallAndRestart();
            // Only reached when it couldn't: on success the app has closed.
            Report?.Invoke($"Couldn't install the update. {problem}");
        }
        finally { _asking = false; }
    }
}
