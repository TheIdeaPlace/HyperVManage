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
    private bool _checking;

    public UpdateChecker(IUpdateService updates, string? currentVersion = null)
    {
        _updates = updates;
        _current = currentVersion ?? AppInfo.Version;
    }

    /// <summary>Text for the status bar, which is also spoken.</summary>
    public Action<string>? Report { get; set; }

    /// <summary>Asks a yes or no question; true for yes.</summary>
    public Func<string, bool>? Confirm { get; set; }

    /// <summary>True while New Virtual Machine is building one, which restarting would stop.</summary>
    public Func<bool>? BuildRunning { get; set; }

    /// <summary>Opens a release's page in the browser.</summary>
    public Action<Uri>? OpenPage { get; set; }

    /// <summary>At start: says nothing unless there is an update, and never asks anything.</summary>
    public async Task CheckAtStartAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            var found = await _updates.CheckAsync();
            if (found.Status != UpdateStatus.Available) return;
            Report?.Invoke(_updates.InstallsItself
                ? $"Hyper-V Manage {found.Version} is available. It's installed when you close Hyper-V Manage, or now from Help, Check for Updates."
                : $"Hyper-V Manage {found.Version} is available. Help, Check for Updates opens its download page.");
        }
        finally { _checking = false; }
    }

    /// <summary>From the Help menu: always says what it found, and offers to install.</summary>
    public async Task CheckNowAsync()
    {
        if (_checking) { Report?.Invoke("Already checking for updates."); return; }
        _checking = true;
        try
        {
            Report?.Invoke("Checking for updates.");
            var found = await _updates.CheckAsync();
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

            if (Confirm?.Invoke($"Hyper-V Manage {found.Version} is available. You have {_current}.\n\n" +
                                "Install it now? Hyper-V Manage closes and opens again with the new version.") != true)
            {
                Report?.Invoke($"Hyper-V Manage {found.Version} is installed when you close Hyper-V Manage.");
                return;
            }
            if (BuildRunning?.Invoke() == true)
            {
                Report?.Invoke("A virtual machine is being built, and restarting would stop it. " +
                               "The update is installed when you close Hyper-V Manage after the build.");
                return;
            }
            Report?.Invoke($"Installing Hyper-V Manage {found.Version}.");
            var problem = await _updates.InstallAndRestartAsync();
            // Only reached when it couldn't: on success the app has closed.
            Report?.Invoke($"Couldn't install the update. {problem}");
        }
        finally { _checking = false; }
    }
}
