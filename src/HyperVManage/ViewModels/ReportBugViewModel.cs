using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HyperVManage.Services;

namespace HyperVManage.ViewModels;

/// <summary>
/// Report a Bug: what happened, in the person's words, and what will be sent, shown in full
/// before it goes. It sends through the relay when this build can, and otherwise, or if that
/// fails, opens GitHub's issue form filled in, with the report on the clipboard too.
/// </summary>
public sealed partial class ReportBugViewModel : ObservableObject, IDisposable
{
    private readonly IBugReportService _service;
    private readonly CancellationTokenSource _lifetime = new();

    public ReportBugViewModel(IBugReportService service) => _service = service;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Preview))] private string _summary = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Preview))] private string _whatHappened = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Preview))] private string _whatExpected = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Preview))] private string _steps = "";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(SendYourselfCommand))]
    private bool _isSending;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CloseText)), NotifyCanExecuteChangedFor(nameof(OpenIssueCommand), nameof(SendCommand))]
    private Uri? _issueUrl;

    [ObservableProperty] private string _statusText = "";

    /// <summary>Exactly what is sent.</summary>
    public string Preview => _service.BuildText(Current);

    /// <summary>Send through the relay, or, in a build that can't, through the browser.</summary>
    public bool CanSendItself => _service.CanSend;

    public string CloseText => IssueUrl is null ? "Cancel" : "Close";

    /// <summary>Text a screen reader should speak.</summary>
    public event Action<string>? Announce;

    /// <summary>A required box is empty; the window puts focus in it.</summary>
    public event Action<string>? MissingField;

    /// <summary>Sent: the window moves focus to Open Issue.</summary>
    public event Action? Sent;

    /// <summary>Couldn't send: the window moves focus to Send with GitHub.</summary>
    public event Action? SendFailed;

    /// <summary>Puts text on the clipboard.</summary>
    public Action<string>? CopyText { get; set; }

    /// <summary>Opens an address in the browser.</summary>
    public Action<Uri>? OpenPage { get; set; }

    private BugReport Current => new(Summary, WhatHappened, WhatExpected, Steps);

    private bool Ready()
    {
        var missing = string.IsNullOrWhiteSpace(Summary) ? nameof(Summary)
            : string.IsNullOrWhiteSpace(WhatHappened) ? nameof(WhatHappened) : null;
        if (missing is null) return true;
        Say(missing == nameof(Summary) ? "Type a summary first." : "Say what happened first.");
        MissingField?.Invoke(missing);
        return false;
    }

    private bool CanSendNow() => !IsSending && IssueUrl is null;

    [RelayCommand(CanExecute = nameof(CanSendNow))]
    private async Task Send()
    {
        if (!Ready()) return;
        if (!CanSendItself) { SendYourself(); return; }

        IsSending = true;
        Say("Sending the report.");
        var result = await _service.SendAsync(Current, _lifetime.Token);
        IsSending = false;
        if (_lifetime.IsCancellationRequested) return;
        if (result.Sent)
        {
            IssueUrl = result.IssueUrl;
            Say($"Sent. Thank you. It's issue {result.IssueUrl!.Segments[^1]}.");
            Sent?.Invoke();
        }
        else
        {
            Say($"Couldn't send the report. {result.Problem} Send with GitHub sends it from your browser instead.");
            SendFailed?.Invoke();
        }
    }

    private bool CanSendYourself() => !IsSending;

    /// <summary>GitHub's issue form, filled in, in the browser; the whole report on the clipboard
    /// as well, since a long one is cut short in the address.</summary>
    [RelayCommand(CanExecute = nameof(CanSendYourself))]
    private void SendYourself()
    {
        if (!Ready()) return;
        var report = Current;
        try
        {
            CopyText?.Invoke(_service.BuildText(report));
            OpenPage?.Invoke(_service.BuildIssueFormUrl(report));
            Say("The report is on the clipboard, and GitHub's new issue form is opening in your browser with it filled in. " +
                "Sending it there needs a GitHub account.");
        }
        catch (Exception ex)
        {
            Say($"Couldn't open the browser. {ex.Message}");
        }
    }

    private bool HasIssue() => IssueUrl is not null;

    [RelayCommand(CanExecute = nameof(HasIssue))]
    private void OpenIssue()
    {
        if (IssueUrl is null) return;
        try { OpenPage?.Invoke(IssueUrl); }
        catch (Exception ex) { Say($"Couldn't open the browser. {ex.Message}"); }
    }

    private void Say(string text)
    {
        StatusText = text;
        Announce?.Invoke(text);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
