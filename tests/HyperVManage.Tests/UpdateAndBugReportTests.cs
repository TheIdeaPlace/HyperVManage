using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HyperVManage.Services;
using HyperVManage.ViewModels;
using HyperVManage.Views;
using Xunit;

namespace HyperVManage.Tests;

public class UpdateServiceTests
{
    private static string Releases(params object[] releases) => JsonSerializer.Serialize(releases);

    private static object Release(string tag, bool draft = false, string? url = null) =>
        new { tag_name = tag, draft, html_url = url ?? $"https://github.com/TheIdeaPlace/HyperVManage/releases/tag/{tag}" };

    [Fact]
    public void NewestRelease_IsTheHighestVersionAboveThisOne_NotTheFirstListed()
    {
        var json = Releases(Release("v0.9.4"), Release("v0.10.1"), Release("v0.9.2"), Release("v0.10.0"));
        var found = UpdateService.NewestRelease(json, "0.9.3");
        Assert.Equal("0.10.1", found?.Version);
        Assert.Equal("https://github.com/TheIdeaPlace/HyperVManage/releases/tag/v0.10.1", found?.Page.AbsoluteUri);
    }

    [Theory]
    [InlineData("0.9.3")]
    [InlineData("0.9.4")]
    public void NewestRelease_IsNull_WhenNothingIsNewer(string current) =>
        Assert.Null(UpdateService.NewestRelease(Releases(Release("v0.9.3"), Release("v0.9.1")), current));

    [Fact]
    public void NewestRelease_IsNull_WhenThereAreNoReleasesYet() =>
        Assert.Null(UpdateService.NewestRelease("[]", "0.9.3"));

    [Fact]
    public void NewestRelease_SkipsDrafts_AndTagsThatArentVersions()
    {
        var json = Releases(Release("v2.0.0", draft: true), Release("hyperv-manage-v3.0.0"), Release("v1.2"), Release("v1.2.3.4"),
            Release("latest"), Release("v0.9.5"));
        Assert.Equal("0.9.5", UpdateService.NewestRelease(json, "0.9.3")?.Version);
    }

    [Fact]
    public void NewestRelease_NeverOpensAPageOffGitHub()
    {
        var json = Releases(Release("v0.9.5", url: "https://example.com/HyperVManage.exe"), Release("v0.9.6", url: "http://github.com/x"));
        var found = UpdateService.NewestRelease(json, "0.9.3");
        Assert.Equal(AppInfo.ReleasePage("0.9.6"), found?.Page);
    }

    [Fact]
    public void ATestBuild_IsOlderThanTheReleaseOfItsNumber_AndNewerThanTheOneBefore()
    {
        Assert.Equal("0.9.3", UpdateService.NewestRelease(Releases(Release("v0.9.3")), "0.9.3-test.12.1")?.Version);
        Assert.Null(UpdateService.NewestRelease(Releases(Release("v0.9.2")), "0.9.3-test.12.1"));
    }

    [Fact]
    public void ThisBuildsOwnVersion_IsReadable() => Assert.True(Velopack.SemanticVersion.TryParse(AppInfo.Version, out _), AppInfo.Version);

    [Fact]
    public void ATestBuildOfANewMinorVersion_StillUpdatesToItsRelease() =>
        Assert.Equal("1.0.0", UpdateService.NewestRelease(Releases(Release("v1.0.0")), "1.0.0-test.4.1")?.Version);

    [Fact]
    public void NewestRelease_IgnoresEntriesOfTheWrongShape() =>
        Assert.Equal("0.9.9", UpdateService.NewestRelease("""[1, "x", {"tag_name": 5}, {"tag_name": "v0.9.9", "html_url": 7}]""", "0.9.3")?.Version);

    [Fact]
    public async Task TheSingleExe_SaysWhenGitHubCantBeReached()
    {
        using var service = new UpdateService(handler: new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { ReasonPhrase = "rate limit exceeded" }));
        Assert.False(service.InstallsItself);
        var check = await service.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateStatus.Failed, check.Status);
        Assert.Contains("403", check.Problem);
    }

    [Fact]
    public async Task TheSingleExe_ReadsGitHubsListOfReleases()
    {
        Uri? asked = null;
        using var service = new UpdateService(handler: new StubHandler(r =>
        {
            asked = r.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Releases(Release("v99.0.0"))) };
        }));
        var check = await service.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateStatus.Available, check.Status);
        Assert.Equal("99.0.0", check.Version);
        Assert.Equal(AppInfo.ReleasesApiUrl, asked?.AbsoluteUri);
        Assert.Equal("There's no update to install.", await service.DownloadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("The update hasn't finished downloading.", service.InstallAndRestart());
    }
}

public class UpdateCheckerTests
{
    private sealed class FakeUpdates(UpdateCheck result, bool installsItself) : IUpdateService
    {
        public bool InstallsItself => installsItself;
        public int Checks;
        public int Downloads;
        public int Installs;
        public string DownloadProblem = "";
        public TaskCompletionSource<UpdateCheck>? Pending;
        public Action? WhileDownloading;
        public Task<UpdateCheck> CheckAsync(CancellationToken cancellationToken = default)
        {
            Checks++;
            return Pending?.Task ?? Task.FromResult(result);
        }
        public Task<string> DownloadAsync(CancellationToken cancellationToken = default)
        {
            Downloads++;
            WhileDownloading?.Invoke();
            return Task.FromResult(DownloadProblem);
        }
        public string InstallAndRestart()
        {
            Installs++;
            return "the disk is full";
        }
        public void Dispose() { }
    }

    private static readonly UpdateCheck Available = new(UpdateStatus.Available, "0.9.9", AppInfo.ReleasePage("0.9.9"));

    private sealed class Harness
    {
        public required UpdateChecker Checker;
        public List<string> Said = [];
        public List<string> Asked = [];
        public List<Uri> Opened = [];
        public bool Busy;
    }

    private static Harness Make(FakeUpdates updates, bool answer = true)
    {
        var h = new Harness { Checker = new UpdateChecker(updates, "0.9.3") };
        h.Checker.Report = h.Said.Add;
        h.Checker.Confirm = q => { h.Asked.Add(q); return answer; };
        h.Checker.Busy = () => h.Busy;
        h.Checker.OpenPage = h.Opened.Add;
        return h;
    }

    [Theory]
    [InlineData(UpdateStatus.UpToDate)]
    [InlineData(UpdateStatus.Failed)]
    public async Task AtStart_NothingIsSaid_UnlessThereIsAnUpdate(UpdateStatus status)
    {
        var h = Make(new FakeUpdates(new UpdateCheck(status, Problem: "offline"), true));
        await h.Checker.CheckAtStartAsync();
        Assert.Empty(h.Said);
        Assert.Empty(h.Asked);
    }

    [Fact]
    public async Task AtStart_AnUpdateIsAnnounced_ButNothingIsAsked()
    {
        var h = Make(new FakeUpdates(Available, installsItself: true));
        await h.Checker.CheckAtStartAsync();
        Assert.Equal(["Hyper-V Manage 0.9.9 is available. It downloads while you work and is installed when you close Hyper-V Manage, or now from Help, Check for Updates."], h.Said);
        Assert.Empty(h.Asked);

        h = Make(new FakeUpdates(Available, installsItself: false));
        await h.Checker.CheckAtStartAsync();
        Assert.Equal(["Hyper-V Manage 0.9.9 is available. Help, Check for Updates opens its download page."], h.Said);
    }

    [Fact]
    public async Task FromHelp_UpToDate_SaysWhichVersionThisIs()
    {
        var h = Make(new FakeUpdates(new UpdateCheck(UpdateStatus.UpToDate), true));
        await h.Checker.CheckNowAsync();
        Assert.Equal(["Checking for updates.", "Hyper-V Manage 0.9.3 is the newest version."], h.Said);
        Assert.Empty(h.Asked);
    }

    [Fact]
    public async Task FromHelp_AFailureSaysWhy()
    {
        var h = Make(new FakeUpdates(new UpdateCheck(UpdateStatus.Failed, Problem: "There was no answer in 30 seconds."), true));
        await h.Checker.CheckNowAsync();
        Assert.Equal("Couldn't check for updates. There was no answer in 30 seconds.", h.Said[^1]);
    }

    [Fact]
    public async Task FromHelp_TheSingleExe_OffersTheDownloadPage()
    {
        var updates = new FakeUpdates(Available, installsItself: false);
        var h = Make(updates);
        await h.Checker.CheckNowAsync();
        Assert.Contains("Open its download page?", Assert.Single(h.Asked));
        Assert.Equal([AppInfo.ReleasePage("0.9.9")], h.Opened);
        Assert.Equal(0, updates.Installs);

        h = Make(updates, answer: false);
        await h.Checker.CheckNowAsync();
        Assert.Empty(h.Opened);
    }

    [Fact]
    public async Task FromHelp_AnInstalledCopy_DownloadsThenInstalls_AndSaysWhyItCouldnt()
    {
        var updates = new FakeUpdates(Available, installsItself: true);
        var h = Make(updates);
        await h.Checker.CheckNowAsync();
        Assert.Contains("Install it now?", Assert.Single(h.Asked));
        Assert.Equal((1, 1), (updates.Downloads, updates.Installs));
        Assert.Equal(["Checking for updates.", "Downloading Hyper-V Manage 0.9.9.", "Installing Hyper-V Manage 0.9.9.",
            "Couldn't install the update. the disk is full"], h.Said);
    }

    [Fact]
    public async Task FromHelp_AFailedDownload_IsSaid_AndNothingIsInstalled()
    {
        var updates = new FakeUpdates(Available, installsItself: true) { DownloadProblem = "No such host is known." };
        var h = Make(updates);
        await h.Checker.CheckNowAsync();
        Assert.Equal(0, updates.Installs);
        Assert.Equal("Couldn't download the update. No such host is known.", h.Said[^1]);
    }

    [Fact]
    public async Task FromHelp_NotNow_SaysWhenItWillBeInstalled()
    {
        var updates = new FakeUpdates(Available, installsItself: true);
        var h = Make(updates, answer: false);
        await h.Checker.CheckNowAsync();
        Assert.Equal((0, 0), (updates.Downloads, updates.Installs));
        Assert.Equal("Hyper-V Manage 0.9.9 is installed when you close Hyper-V Manage, once it has downloaded.", h.Said[^1]);
    }

    [Fact]
    public async Task FromHelp_WhileBusy_DoesntOfferToInstall()
    {
        var updates = new FakeUpdates(Available, installsItself: true);
        var h = Make(updates);
        h.Busy = true;
        await h.Checker.CheckNowAsync();
        Assert.Empty(h.Asked);
        Assert.Equal(0, updates.Installs);
        Assert.StartsWith("Hyper-V Manage 0.9.9 is available, but can't be installed while a virtual machine is being built or changed.", h.Said[^1]);
    }

    [Fact]
    public async Task FromHelp_SomethingStartedDuringTheDownload_StopsTheInstall()
    {
        var updates = new FakeUpdates(Available, installsItself: true);
        var h = Make(updates);
        updates.WhileDownloading = () => h.Busy = true;
        await h.Checker.CheckNowAsync();
        Assert.Equal((1, 0), (updates.Downloads, updates.Installs));
        Assert.StartsWith("Hyper-V Manage 0.9.9 has downloaded, and is installed when you close Hyper-V Manage", h.Said[^1]);
    }

    [Fact]
    public async Task FromHelp_DuringTheStartCheck_SharesItsAnswer()
    {
        var updates = new FakeUpdates(Available, installsItself: true) { Pending = new() };
        var h = Make(updates, answer: false);
        var atStart = h.Checker.CheckAtStartAsync();
        var fromHelp = h.Checker.CheckNowAsync();
        await h.Checker.CheckNowAsync();
        Assert.Equal("Already checking for updates.", h.Said[^1]);
        updates.Pending.SetResult(new UpdateCheck(UpdateStatus.UpToDate));
        await Task.WhenAll(atStart, fromHelp);
        Assert.Equal(1, updates.Checks);
        Assert.Equal("Hyper-V Manage 0.9.3 is the newest version.", h.Said[^1]);
    }
}

public class BugReportServiceTests
{
    private const string Relay = "https://appkit-bug-relay.example.workers.dev/report";

    private static BugReportService Make(Func<HttpRequestMessage, HttpResponseMessage> answer, string relayUrl = Relay, string relayKey = "key-123") =>
        new(new StubHandler(answer), relayUrl, relayKey, () => "- Hyper-V Manage 0.9.3\n");

    private static readonly BugReport Report = new("Clone fails", "It said access denied.", "A copy", "1. Clone\n2. Watch");

    private static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json) };

    [Fact]
    public async Task Send_PostsTheReportWithTheKey_AndGivesBackTheIssue()
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        using var service = Make(r =>
        {
            sent = r;
            body = r.Content!.ReadAsStringAsync().Result;
            return Json(HttpStatusCode.Created, """{"issueUrl":"https://github.com/TheIdeaPlace/HyperVManage/issues/12","number":12}""");
        });
        Assert.True(service.CanSend);
        var result = await service.SendAsync(Report, TestContext.Current.CancellationToken);

        Assert.True(result.Sent);
        Assert.Equal("https://github.com/TheIdeaPlace/HyperVManage/issues/12", result.IssueUrl!.AbsoluteUri);
        Assert.Equal(HttpMethod.Post, sent!.Method);
        Assert.Equal(Relay, sent.RequestUri!.AbsoluteUri);
        Assert.Equal("hypervmanage", Assert.Single(sent.Headers.GetValues("X-AppKit-App")));
        Assert.Equal("key-123", Assert.Single(sent.Headers.GetValues("X-AppKit-Key")));
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("Clone fails", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(service.BuildText(Report), doc.RootElement.GetProperty("body").GetString());
        // The relay chooses labels; the app never sends any.
        Assert.False(doc.RootElement.TryGetProperty("labels", out _));
    }

    [Theory]
    [InlineData("https://github.com/someone-else/repo/issues/1")]
    [InlineData("http://github.com/TheIdeaPlace/HyperVManage/issues/1")]
    [InlineData("https://evil.example/TheIdeaPlace/HyperVManage/issues/1")]
    [InlineData("not a url")]
    public async Task Send_RefusesAnIssueThatIsntOnThisRepository(string url)
    {
        using var service = Make(_ => Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { issueUrl = url })));
        var result = await service.SendAsync(Report, TestContext.Current.CancellationToken);
        Assert.False(result.Sent);
        Assert.Equal("The report service didn't say which issue it made.", result.Problem);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"ok\"")]
    [InlineData("{\"issueUrl\": 12}")]
    [InlineData("{\"issueUrl\": null}")]
    public async Task Send_AnAnswerOfAnotherShape_IsAFailureNotACrash(string json)
    {
        using var service = Make(_ => Json(HttpStatusCode.OK, json));
        var result = await service.SendAsync(Report, TestContext.Current.CancellationToken);
        Assert.False(result.Sent);
        Assert.Equal("The report service didn't say which issue it made.", result.Problem);
    }

    [Fact]
    public void AboutThisPc_IsWorkedOutOnce()
    {
        var calls = 0;
        using var service = new BugReportService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), "", "", () => { calls++; return "- x\n"; });
        service.BuildText(Report);
        service.BuildText(Report);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "Too many reports")]
    [InlineData(HttpStatusCode.Unauthorized, "answered 401")]
    [InlineData(HttpStatusCode.BadGateway, "answered 502")]
    public async Task Send_SaysWhyItFailed(HttpStatusCode code, string expected)
    {
        using var service = Make(_ => new HttpResponseMessage(code));
        var result = await service.SendAsync(Report, TestContext.Current.CancellationToken);
        Assert.False(result.Sent);
        Assert.Contains(expected, result.Problem);
    }

    [Fact]
    public async Task Send_SurvivesNoNetwork_AndNonsense()
    {
        using var offline = Make(_ => throw new HttpRequestException("No such host is known."));
        Assert.Contains("No such host is known.", (await offline.SendAsync(Report, TestContext.Current.CancellationToken)).Problem);
        using var garbled = Make(_ => Json(HttpStatusCode.OK, "<html>"));
        Assert.False((await garbled.SendAsync(Report, TestContext.Current.CancellationToken)).Sent);
    }

    [Theory]
    [InlineData("", "key")]
    [InlineData(Relay, "")]
    [InlineData("http://appkit-bug-relay.example.workers.dev/report", "key")]
    public async Task WithoutARelay_NothingIsSent(string url, string key)
    {
        var asked = false;
        using var service = Make(_ => { asked = true; return new HttpResponseMessage(HttpStatusCode.OK); }, url, key);
        Assert.False(service.CanSend);
        Assert.False((await service.SendAsync(Report, TestContext.Current.CancellationToken)).Sent);
        Assert.False(asked);
    }

    [Fact]
    public void TheShippedSource_HasNoRelayKey()
    {
        // CI writes the real one into the file before a release build; the repository's copy is empty.
        var file = File.ReadAllText(Path.Combine(ScriptBuildingTests.RepoRoot(), "src", "HyperVManage", "Services", "BugReportService.Relay.cs"));
        Assert.Contains("private const string RelayKey = \"\";", file);
    }

    [Fact]
    public void Text_LeavesOutWhatWasntFilledIn()
    {
        using var service = Make(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var text = service.BuildText(new BugReport("Crash", "  It closed.  ", " ", ""));
        Assert.Equal("### What happened\r\nIt closed.\r\n\r\n### About this PC\r\n- Hyper-V Manage 0.9.3\n", text);
        Assert.Contains("### Steps to reproduce\r\n1. Clone\n2. Watch", service.BuildText(Report));
    }

    [Fact]
    public void IssueForm_IsThisRepositorysAndCarriesTheReport()
    {
        using var service = Make(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var url = service.BuildIssueFormUrl(new BugReport("A & B?", "x", "", ""));
        Assert.StartsWith("https://github.com/TheIdeaPlace/HyperVManage/issues/new?labels=bug&title=A%20%26%20B%3F&body=", url.AbsoluteUri);
        Assert.Contains(Uri.EscapeDataString("### What happened"), url.AbsoluteUri);
    }

    [Theory]
    [InlineData("Long", 20000, 'x')]
    [InlineData("Long", 3000, 'é')]
    [InlineData("A very long title that goes on and on and on and on and on and on and on and on and on and on and on and on and on", 5000, 'x')]
    public void IssueForm_StaysShortEnoughForExplorerToOpen_AndSaysTheClipboardHasItAll(string title, int length, char fill)
    {
        using var service = Make(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var url = service.BuildIssueFormUrl(new BugReport(title, "start" + new string(fill, length), "", ""));
        Assert.True(url.AbsoluteUri.Length <= BugReportService.MaxFormUrlLength, url.AbsoluteUri.Length.ToString());
        var body = Uri.UnescapeDataString(url.Query[(url.Query.IndexOf("&body=", StringComparison.Ordinal) + 6)..]);
        Assert.StartsWith("### What happened", body);
        Assert.EndsWith("The whole report is on your clipboard: paste it over this.)", body);
    }

    [Fact]
    public void IssueForm_NeverSplitsAnEmoji()
    {
        using var service = Make(_ => new HttpResponseMessage(HttpStatusCode.OK));
        // Escaping half of a surrogate pair throws; every cut point must be safe.
        for (var n = 300; n < 700; n += 7)
        {
            var url = service.BuildIssueFormUrl(new BugReport(string.Concat(Enumerable.Repeat("😀", 80)), string.Concat(Enumerable.Repeat("😀", n)), "", ""));
            Assert.True(url.AbsoluteUri.Length <= BugReportService.MaxFormUrlLength);
        }
    }

    [Fact]
    public void IssueForm_AShortReportIsSentWhole()
    {
        using var service = Make(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var url = service.BuildIssueFormUrl(Report);
        Assert.DoesNotContain(Uri.EscapeDataString("Cut short"), url.AbsoluteUri);
        Assert.EndsWith(Uri.EscapeDataString(service.BuildText(Report)), url.AbsoluteUri);
    }

    [Fact]
    public void AboutThisPc_SaysNothingAboutThePersonOrTheirMachines()
    {
        var text = BugReportService.DescribeThisPc(demo: true, installed: true);
        Assert.Contains("- Installed: with Setup", text);
        Assert.Contains($"Hyper-V Manage {AppInfo.Version}, demo mode", text);
        Assert.Contains("Screen reader running:", text);
        Assert.DoesNotContain(Environment.MachineName, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
    }
}

public class ReportBugViewModelTests
{
    private sealed class FakeReports(bool canSend, BugReportResult result) : IBugReportService
    {
        public int Sends;
        public bool CanSend => canSend;
        public Task<BugReportResult> SendAsync(BugReport report, CancellationToken cancellationToken = default)
        {
            Sends++;
            return Task.FromResult(result);
        }
        public string BuildText(BugReport report) => $"{report.WhatHappened}|about";
        public Uri BuildIssueFormUrl(BugReport report) => new("https://github.com/TheIdeaPlace/HyperVManage/issues/new?title=" + report.Summary);
    }

    private static readonly BugReportResult Issue = new(new Uri("https://github.com/TheIdeaPlace/HyperVManage/issues/7"), null);

    [Fact]
    public async Task Send_WantsASummaryAndWhatHappened_AndSaysWhichIsMissing()
    {
        var reports = new FakeReports(true, Issue);
        using var vm = new ReportBugViewModel(reports);
        var missing = new List<string>();
        var said = new List<string>();
        vm.MissingField += missing.Add;
        vm.Announce += said.Add;

        await vm.SendCommand.ExecuteAsync(null);
        vm.Summary = "Crash";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal([nameof(vm.Summary), nameof(vm.WhatHappened)], missing);
        Assert.Equal(["Type a summary first.", "Say what happened first."], said);
        Assert.Equal(0, reports.Sends);
    }

    [Fact]
    public async Task Send_ThatWorks_GivesTheIssue_AndCantBeSentTwice()
    {
        var reports = new FakeReports(true, Issue);
        using var vm = new ReportBugViewModel(reports) { Summary = "Crash", WhatHappened = "It closed." };
        var sent = 0;
        vm.Sent += () => sent++;
        Assert.Equal("Cancel", vm.CloseText);

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(1, sent);
        Assert.Equal(Issue.IssueUrl, vm.IssueUrl);
        Assert.Equal("Sent. Thank you. It's issue 7.", vm.StatusText);
        Assert.Equal("Close", vm.CloseText);
        Assert.False(vm.SendCommand.CanExecute(null));
        Assert.True(vm.OpenIssueCommand.CanExecute(null));
    }

    [Fact]
    public async Task Send_ThatFails_PointsToSendingWithGitHub()
    {
        var reports = new FakeReports(true, new BugReportResult(null, "The report service answered 502."));
        using var vm = new ReportBugViewModel(reports) { Summary = "Crash", WhatHappened = "It closed." };
        var failed = 0;
        vm.SendFailed += () => failed++;
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal(1, failed);
        Assert.Null(vm.IssueUrl);
        Assert.Equal("Couldn't send the report. The report service answered 502. Send with GitHub sends it from your browser instead.", vm.StatusText);
        Assert.True(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task WithoutARelay_SendGoesThroughTheBrowser_WithTheWholeReportOnTheClipboard()
    {
        var reports = new FakeReports(false, Issue);
        var copied = new List<string>();
        var opened = new List<Uri>();
        using var vm = new ReportBugViewModel(reports) { Summary = "Crash", WhatHappened = "It closed.", CopyText = copied.Add, OpenPage = opened.Add };
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal(0, reports.Sends);
        Assert.Equal(["It closed.|about"], copied);
        Assert.Equal("https://github.com/TheIdeaPlace/HyperVManage/issues/new?title=Crash", Assert.Single(opened).AbsoluteUri);
        Assert.Contains("needs a GitHub account", vm.StatusText);
    }

    [Fact]
    public void AClipboardInUse_StillOpensTheForm()
    {
        var opened = new List<Uri>();
        using var vm = new ReportBugViewModel(new FakeReports(false, Issue))
        {
            Summary = "Crash", WhatHappened = "x", CopyText = _ => throw new System.Runtime.InteropServices.COMException("OpenClipboard failed"), OpenPage = opened.Add,
        };
        vm.SendYourselfCommand.Execute(null);
        Assert.Single(opened);
        Assert.Contains("couldn't be put on the clipboard", vm.StatusText);
    }

    [Fact]
    public async Task Send_StaysAvailableWhileSending_ButSendsOnce()
    {
        var gate = new TaskCompletionSource<BugReportResult>();
        var reports = new SlowReports(gate.Task);
        using var vm = new ReportBugViewModel(reports) { Summary = "Crash", WhatHappened = "x" };
        var first = vm.SendCommand.ExecuteAsync(null);
        Assert.True(vm.SendCommand.CanExecute(null));
        await vm.SendCommand.ExecuteAsync(null);
        gate.SetResult(Issue);
        await first;
        Assert.Equal(1, reports.Sends);
    }

    private sealed class SlowReports(Task<BugReportResult> answer) : IBugReportService
    {
        public int Sends;
        public bool CanSend => true;
        public Task<BugReportResult> SendAsync(BugReport report, CancellationToken cancellationToken = default) { Sends++; return answer; }
        public string BuildText(BugReport report) => "";
        public Uri BuildIssueFormUrl(BugReport report) => new("https://github.com/TheIdeaPlace/HyperVManage/issues/new");
    }

    [Fact]
    public void ABrowserThatWontOpen_IsSaidNotThrown()
    {
        using var vm = new ReportBugViewModel(new FakeReports(false, Issue))
        {
            Summary = "Crash", WhatHappened = "x", CopyText = _ => { }, OpenPage = _ => throw new InvalidOperationException("No browser."),
        };
        vm.SendYourselfCommand.Execute(null);
        Assert.Equal("Couldn't open the browser. No browser.", vm.StatusText);
    }

    [Fact]
    public void Preview_FollowsWhatIsTyped()
    {
        using var vm = new ReportBugViewModel(new FakeReports(true, Issue));
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.WhatHappened = "abc";
        Assert.Contains(nameof(vm.Preview), changed);
        Assert.Equal("abc|about", vm.Preview);
    }
}

[Collection("Wpf")]
public class ReportBugWindowTests
{
    private sealed class NoRelay : IBugReportService
    {
        public bool CanSend => false;
        public Task<BugReportResult> SendAsync(BugReport report, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public string BuildText(BugReport report) => "preview";
        public Uri BuildIssueFormUrl(BugReport report) => new("https://github.com/TheIdeaPlace/HyperVManage/issues/new");
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    [StaFact]
    public void OpensInSummary_EveryBoxIsNamed_AndWithoutARelayThereIsOneWayToSend()
    {
        TestApp.Ensure();
        var window = new ReportBugWindow(new ReportBugViewModel(new NoRelay()))
        {
            ShowActivated = true, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
        };
        try
        {
            window.Show();
            window.Activate();
            Pump();
            Assert.Same(window.FindName("SummaryBox"), Keyboard.FocusedElement);
            foreach (var (box, name) in new[] { ("SummaryBox", "Summary"), ("WhatHappenedBox", "What happened"),
                         ("WhatExpectedBox", "What you expected"), ("StepsBox", "Steps to reproduce"), ("PreviewBox", "What is sent") })
                Assert.Equal(name, AutomationProperties.GetName((TextBox)window.FindName(box)));
            Assert.True(((TextBox)window.FindName("PreviewBox")).IsReadOnly);
            Assert.Equal(Visibility.Collapsed, ((Button)window.FindName("SendButton")).Visibility);
            Assert.Equal(Visibility.Visible, ((Button)window.FindName("SendYourselfButton")).Visibility);
            Assert.Contains("needs a GitHub account", ((TextBlock)window.FindName("IntroText")).Text);
        }
        finally { window.Close(); }
    }
}

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(answer(request));
}
