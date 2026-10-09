using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace HyperVManage.Services;

/// <summary>What someone typed into Report a Bug.</summary>
public sealed record BugReport(string Summary, string WhatHappened, string WhatExpected, string Steps);

/// <summary>The issue it became, or why it couldn't be sent.</summary>
public sealed record BugReportResult(Uri? IssueUrl, string? Problem)
{
    public bool Sent => IssueUrl is not null;
}

public interface IBugReportService
{
    /// <summary>True when this build can send reports itself; otherwise a report goes through
    /// the browser, to a GitHub issue form filled in with it.</summary>
    bool CanSend { get; }

    /// <summary>Files the report as an issue. Never throws.</summary>
    Task<BugReportResult> SendAsync(BugReport report, CancellationToken cancellationToken = default);

    /// <summary>The report as it is sent: what was typed, then about this PC.</summary>
    string BuildText(BugReport report);

    /// <summary>GitHub's new issue form with the report filled in, for sending it yourself.</summary>
    Uri BuildIssueFormUrl(BugReport report);
}

/// <summary>
/// Files bug reports as GitHub issues on kellylford/HyperVManage through the bug-report relay
/// (relay/README.md), so nobody needs a GitHub account and nothing that can write to the
/// repository ships in the app. The relay key compiled in only lets a report be filed, and can
/// be changed without touching any account. A build without one, or a relay that fails, falls
/// back to GitHub's issue form in the browser, with the report on the clipboard as well.
/// </summary>
public sealed partial class BugReportService : IBugReportService, IDisposable
{
    private const string IssueFormUrl = AppInfo.RepoUrl + "/issues/new";

    // Long addresses fail in places: Windows' internet shortcuts stop near 2,000 characters,
    // and servers refuse very long ones. The clipboard has the whole report.
    internal const int MaxFormUrlLength = 2000;

    private const string CutShort = "\n\n(Cut short here. The whole report is on your clipboard: paste it over this.)";

    // The title has its own limit, so a long one can never crowd the report out entirely.
    private const int MaxFormTitleLength = 100;

    private readonly string _relayUrl;
    private readonly string _relayKey;
    private readonly HttpClient _http;
    // Worked out once: the preview is rebuilt on every keystroke, and finding the screen reader
    // looks through every running process.
    private readonly Lazy<string> _environment;

    /// <param name="installed">Whether this copy was installed with Setup.</param>
    public BugReportService(bool demo, bool installed) : this(new HttpClientHandler(), null, null, () => DescribeThisPc(demo, installed)) { }

    internal BugReportService(HttpMessageHandler handler, string? relayUrl, string? relayKey, Func<string> environment)
    {
        _relayUrl = relayUrl ?? RelayUrl;
        _relayKey = relayKey ?? RelayKey;
        _environment = new Lazy<string>(environment);
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HyperVManage", AppInfo.Version));
    }

    public bool CanSend => _relayUrl.StartsWith("https://", StringComparison.Ordinal) && _relayKey.Length > 0;

    public async Task<BugReportResult> SendAsync(BugReport report, CancellationToken cancellationToken = default)
    {
        if (!CanSend) return new BugReportResult(null, "This copy of Hyper-V Manage can't send reports itself.");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _relayUrl);
            request.Headers.Add("X-HyperVManage-Key", _relayKey);
            // Labels are the relay's to choose: a key taken out of the app mustn't pick them.
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { title = report.Summary.Trim(), body = BuildText(report) }),
                Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new BugReportResult(null, response.StatusCode == HttpStatusCode.TooManyRequests
                    ? "Too many reports were sent from this network just now. Try again in a minute."
                    : $"The report service answered {(int)response.StatusCode}.");

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            // Only ever an issue on this repository: it is opened in the browser if asked.
            // Whatever answers (a proxy, a sign-in page) may not send what the relay does.
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("issueUrl", out var url)
                && url.ValueKind == JsonValueKind.String
                && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var issue)
                && issue.Scheme == Uri.UriSchemeHttps
                && issue.AbsoluteUri.StartsWith(AppInfo.RepoUrl + "/issues/", StringComparison.OrdinalIgnoreCase))
                return new BugReportResult(issue, null);
            return new BugReportResult(null, "The report service didn't say which issue it made.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BugReportResult(null, "The report service didn't answer in time.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            return new BugReportResult(null, $"Couldn't reach the report service. {ex.Message}");
        }
    }

    public string BuildText(BugReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("### What happened").AppendLine(report.WhatHappened.Trim());
        if (!string.IsNullOrWhiteSpace(report.WhatExpected))
            text.AppendLine().AppendLine("### What you expected").AppendLine(report.WhatExpected.Trim());
        if (!string.IsNullOrWhiteSpace(report.Steps))
            text.AppendLine().AppendLine("### Steps to reproduce").AppendLine(report.Steps.Trim());
        text.AppendLine().AppendLine("### About this PC").Append(_environment.Value);
        return text.ToString();
    }

    public Uri BuildIssueFormUrl(BugReport report)
    {
        var title = Shorten(report.Summary.Trim(), MaxFormTitleLength);
        var body = BuildText(report);
        string Url(string b) => $"{IssueFormUrl}?labels=bug&title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(b)}";
        var url = Url(body);
        if (url.Length <= MaxFormUrlLength) return new Uri(url);
        // Escaping makes a character anything from 1 to 12 long, so find the most of the report
        // that fits by halving.
        int low = 0, high = body.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (Url(Shorten(body, mid) + CutShort).Length <= MaxFormUrlLength) low = mid;
            else high = mid - 1;
        }
        return new Uri(Url(Shorten(body, low) + CutShort));
    }

    /// <summary>The first <paramref name="length"/> characters at most, never half of a pair
    /// that makes one character, which can't be escaped.</summary>
    private static string Shorten(string text, int length)
    {
        if (text.Length <= length) return text;
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }

    /// <summary>
    /// Versions, processor, how it was installed and which screen reader is running: the things
    /// that change how the app behaves. Nothing about the VMs, the network or the person; the
    /// report becomes a public issue.
    /// </summary>
    internal static string DescribeThisPc(bool demo, bool installed)
    {
        var text = new StringBuilder();
        text.AppendLine($"- Hyper-V Manage {AppInfo.Version}{(demo ? ", demo mode" : "")}");
        text.AppendLine($"- {RuntimeInformation.OSDescription.Trim()} ({Environment.OSVersion.Version})");
        text.AppendLine($"- Processor: {RuntimeInformation.OSArchitecture}, app built for {RuntimeInformation.ProcessArchitecture}");
        text.AppendLine($"- Installed: {(installed ? "with Setup" : "no, run from the exe")}");
        text.AppendLine($"- Screen reader running: {ScreenReaders()}");
        return text.ToString();
    }

    private static readonly (string Process, string Name)[] KnownScreenReaders =
        [("jfw", "JAWS"), ("nvda", "NVDA"), ("Narrator", "Narrator"), ("zt", "ZoomText"), ("fusion", "Fusion")];

    internal static string ScreenReaders()
    {
        var running = KnownScreenReaders.Where(r =>
        {
            var found = Process.GetProcessesByName(r.Process);
            foreach (var p in found) p.Dispose();
            return found.Length > 0;
        }).Select(r => r.Name).ToList();
        return running.Count == 0 ? "none found" : string.Join(", ", running);
    }

    public void Dispose() => _http.Dispose();
}
