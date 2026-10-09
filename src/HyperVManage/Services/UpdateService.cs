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
    /// found starts downloading at once, and once downloaded is installed when the app closes.</summary>
    Task<UpdateCheck> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Waits for the update the last check found to finish downloading, downloading it
    /// again if the background download failed. Returns "" when it's ready, or the reason it isn't.</summary>
    Task<string> DownloadAsync(CancellationToken cancellationToken = default);

    /// <summary>Installs the downloaded update and starts the new version: on success the process
    /// ends at once and this never returns. Returns the reason when it can't.</summary>
    string InstallAndRestart();
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

    /// <summary>The update found, and its download. Replaced as one object, so a finished download
    /// always belongs to the update beside it, and a check on another thread never sees half.</summary>
    private sealed record Pending(Velopack.UpdateInfo Update, Task Download);
    private volatile Pending? _pending;
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
            return new UpdateCheck(UpdateStatus.Failed, Problem: "There was no answer in 30 seconds.");
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
        // A new version, or a download that failed (the network, or another Velopack process
        // holding its lock), starts again; one still going, or done, is kept.
        if (_pending is not { } p || p.Update.TargetFullRelease.Version.ToString() != version || p.Download.IsFaulted || p.Download.IsCanceled)
            _pending = new Pending(update, StartDownload(manager, update));
        return new UpdateCheck(UpdateStatus.Available, version, AppInfo.ReleasePage(version));
    }

    // On the app's lifetime, not the check's short timeout: the package is large.
    private Task StartDownload(UpdateManager manager, Velopack.UpdateInfo update) =>
        Task.Run(() => manager.DownloadUpdatesAsync(update, cancelToken: _lifetime.Token));

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
        if (!SemanticVersion.TryParse(current, out var mine)) return null;
        using var doc = JsonDocument.Parse(releasesJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        (SemanticVersion Parsed, string Text, Uri Page)? best = null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object) continue;
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            if (!release.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String) continue;
            var tag = tagElement.GetString()!;
            if (!tag.StartsWith('v') || !IsReleaseVersion(tag[1..]) || !SemanticVersion.TryParse(tag[1..], out var theirs)) continue;
            if (theirs <= mine || (best is { } b && theirs <= b.Parsed)) continue;
            var page = release.TryGetProperty("html_url", out var url) && url.ValueKind == JsonValueKind.String
                       && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var u)
                       && u.Scheme == Uri.UriSchemeHttps && u.Host == "github.com"
                ? u
                : AppInfo.ReleasePage(tag[1..]);
            best = (theirs, tag[1..], page);
        }
        return best is { } found ? (found.Text, found.Page) : null;
    }

    /// <summary>Major.minor.patch, as the release workflow insists on, and perhaps -test.N.</summary>
    private static bool IsReleaseVersion(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$");

    public async Task<string> DownloadAsync(CancellationToken cancellationToken = default)
    {
        var manager = _manager.Value;
        if (manager is null || _pending is not { } pending) return "There's no update to install.";
        try
        {
            try { await pending.Download.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The background download failed; try once more now that someone is waiting.
                var again = StartDownload(manager, pending.Update);
                if (_pending == pending) _pending = pending with { Download = again };
                await again.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return "";
        }
        catch (OperationCanceledException)
        {
            return "Downloading the update was stopped.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public string InstallAndRestart()
    {
        if (_manager.Value is not { } manager || _pending is not { } pending || !pending.Download.IsCompletedSuccessfully)
            return "The update hasn't finished downloading.";
        try
        {
            // Never reset: if this throws, Update.exe may already be on its way, and arming a
            // second one on exit is the worse outcome.
            _restarting = true;
            manager.ApplyUpdatesAndRestart(pending.Update, Environment.GetCommandLineArgs()[1..]);
            return "";
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
        // Closing the app installs a downloaded update, so the next start is the new version. One
        // still downloading is left; the next start finds it again.
        if (!_restarting && _manager.IsValueCreated && _manager.Value is { } manager
            && _pending is { } pending && pending.Download.IsCompletedSuccessfully)
        {
            try { manager.WaitExitThenApplyUpdates(pending.Update, silent: true, restart: false); }
            catch (Exception) { }
        }
        _lifetime.Cancel();
        _lifetime.Dispose();
        _http.Dispose();
    }
}
