using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace HyperVManage.Services;

public enum UpdateStatus { UpToDate, Available, Failed }

/// <summary>What a check for updates found. <see cref="Version"/> and <see cref="ReleasePage"/>
/// are set when an update is available; <see cref="Problem"/> when the check failed.</summary>
public sealed record UpdateCheck(UpdateStatus Status, string? Version = null, Uri? ReleasePage = null, string? Problem = null);

public interface IUpdateService : IDisposable
{
    /// <summary>True for a copy installed with Setup, which can download and install its own
    /// updates; false for the single exe run from wherever it was saved, which can only say a
    /// new version is out.</summary>
    bool InstallsItself { get; }

    /// <summary>Checks for a newer version. Never throws: a failure comes back as
    /// <see cref="UpdateStatus.Failed"/> with the reason. For an installed copy, an update that is
    /// found starts downloading at once and is installed when the app closes.</summary>
    Task<UpdateCheck> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Finishes downloading the update the last check found, installs it and starts the
    /// new version; on success the process ends and this never returns. Returns the reason when
    /// it can't.</summary>
    Task<string> InstallAndRestartAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Updates through Velopack, from this repository's GitHub releases. An installed copy (in
/// %LocalAppData%\HyperVManage) downloads an update in the background and installs it when the
/// app closes, or straight away when asked. The single exe asks GitHub's API for the newest
/// release instead, and can only point at its download page.
/// </summary>
public sealed class UpdateService : IUpdateService
{
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string? _feed;
    private readonly Lazy<UpdateManager?> _manager;

    private Velopack.UpdateInfo? _pending;
    private Task? _download;
    private volatile bool _downloaded;
    private bool _restarting;
    private bool _disposed;

    /// <param name="feed">A folder or address holding Velopack packages, used instead of GitHub
    /// (--update-feed), so updating can be tried without publishing a release.</param>
    public UpdateService(string? feed = null, HttpMessageHandler? handler = null)
    {
        _feed = feed;
        _http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HyperVManage", AppInfo.Version));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _manager = new Lazy<UpdateManager?>(CreateManager);
    }

    public bool InstallsItself => _manager.Value is not null;

    private UpdateManager? CreateManager()
    {
        try
        {
            // Every release here is Hyper-V Manage's, and while its version starts 0. each is a
            // GitHub pre-release, so pre-releases count.
            var manager = _feed is null
                ? new UpdateManager(new GithubSource(AppInfo.RepoUrl, accessToken: null, prerelease: true))
                : new UpdateManager(_feed);
            return manager.IsInstalled ? manager : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<UpdateCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            // Finding the install looks at the disk; not on the window's thread.
            var manager = await Task.Run(() => _manager.Value, linked.Token).ConfigureAwait(false);
            return manager is null
                ? await CheckGitHubAsync(linked.Token).ConfigureAwait(false)
                : await CheckInstalledAsync(manager, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheck(UpdateStatus.Failed, Problem: "GitHub didn't answer in time.");
        }
        catch (Exception ex)
        {
            return new UpdateCheck(UpdateStatus.Failed, Problem: ex.Message);
        }
    }

    private async Task<UpdateCheck> CheckInstalledAsync(UpdateManager manager, CancellationToken ct)
    {
        // Velopack's check takes no token; stop waiting for it instead.
        var update = await manager.CheckForUpdatesAsync().WaitAsync(ct).ConfigureAwait(false);
        if (update is null) return new UpdateCheck(UpdateStatus.UpToDate);

        var version = update.TargetFullRelease.Version.ToString();
        if (_pending?.TargetFullRelease.Version.ToString() != version)
        {
            _pending = update;
            _downloaded = false;
            // On the app's lifetime, not the check's short timeout: the package is large.
            _download = Task.Run(async () =>
            {
                await manager.DownloadUpdatesAsync(update, cancelToken: _lifetime.Token).ConfigureAwait(false);
                _downloaded = true;
            });
        }
        return new UpdateCheck(UpdateStatus.Available, version, AppInfo.ReleasePage(version));
    }

    private async Task<UpdateCheck> CheckGitHubAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(AppInfo.ReleasesApiUrl, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return new UpdateCheck(UpdateStatus.Failed, Problem: $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return NewestRelease(json, AppInfo.Version) is { } newer
            ? new UpdateCheck(UpdateStatus.Available, newer.Version, newer.Page)
            : new UpdateCheck(UpdateStatus.UpToDate);
    }

    /// <summary>
    /// The newest release in GitHub's list of releases that is newer than <paramref name="current"/>,
    /// or null. Drafts are skipped, and so is any tag that isn't v and a version.
    /// </summary>
    internal static (string Version, Uri Page)? NewestRelease(string releasesJson, string current)
    {
        if (!TryParseVersion(current, out var mine)) return null;
        using var doc = JsonDocument.Parse(releasesJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        (Version Parsed, string Text, Uri Page)? best = null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            if (!release.TryGetProperty("tag_name", out var tagElement) || tagElement.GetString() is not { } tag) continue;
            if (!tag.StartsWith('v') || !TryParseVersion(tag[1..], out var theirs)) continue;
            if (theirs <= mine || (best is { } b && theirs <= b.Parsed)) continue;
            var page = release.TryGetProperty("html_url", out var url) && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var u)
                       && u.Scheme == Uri.UriSchemeHttps && u.Host == "github.com"
                ? u
                : AppInfo.ReleasePage(tag[1..]);
            best = (theirs, tag[1..], page);
        }
        return best is { } found ? (found.Text, found.Page) : null;
    }

    /// <summary>Major.minor.patch only. A build's own version may carry "-test.N", which is
    /// older than the release of the same number, so it compares as one patch lower.</summary>
    internal static bool TryParseVersion(string text, out Version version)
    {
        var dash = text.IndexOf('-');
        var core = dash < 0 ? text : text[..dash];
        if (!System.Version.TryParse(core, out var parsed) || parsed.Build < 0 || parsed.Revision >= 0)
        {
            version = new Version();
            return false;
        }
        version = dash < 0 || parsed.Build == 0 ? parsed : new Version(parsed.Major, parsed.Minor, parsed.Build - 1, int.MaxValue);
        return true;
    }

    public async Task<string> InstallAndRestartAsync(CancellationToken cancellationToken = default)
    {
        var manager = _manager.Value;
        var update = _pending;
        if (manager is null || update is null) return "There's no update to install.";
        try
        {
            if (_download is { } download)
            {
                try { await download.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
            }
            if (!_downloaded)
            {
                // The background download failed, most likely the network; try once more now
                // that someone is waiting for it.
                await manager.DownloadUpdatesAsync(update, cancelToken: cancellationToken).ConfigureAwait(false);
                _downloaded = true;
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Never reset: if this throws, Update.exe may already be on its way, and arming a
            // second one on exit is the worse outcome.
            _restarting = true;
            manager.ApplyUpdatesAndRestart(update, Environment.GetCommandLineArgs()[1..]);
            return "";
        }
        catch (OperationCanceledException)
        {
            return "Installing the update was stopped.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Closing the app installs a downloaded update, so the next start is the new version.
        if (_downloaded && !_restarting && _manager.IsValueCreated && _manager.Value is { } manager && _pending is { } update)
        {
            try { manager.WaitExitThenApplyUpdates(update, silent: true, restart: false); }
            catch (Exception) { }
        }
        _lifetime.Cancel();
        _lifetime.Dispose();
        _http.Dispose();
    }
}
