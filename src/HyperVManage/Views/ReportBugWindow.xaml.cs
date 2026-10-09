using System.Windows;
using System.Windows.Controls;
using HyperVManage.Helpers;
using HyperVManage.ViewModels;

namespace HyperVManage.Views;

/// <summary>Help, Report a Bug. Focus starts in Summary.</summary>
public partial class ReportBugWindow : Window
{
    internal ReportBugViewModel ViewModel { get; }

    internal ReportBugWindow(ReportBugViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm;
        DataContext = vm;

        // Shown in the window, where a screen reader's "read window" command reads it.
        IntroText.Text = vm.CanSendItself
            ? "Say what went wrong. Send makes it a public issue on GitHub, with what's shown under What is sent, " +
              "and no GitHub account is needed. Send with GitHub fills in GitHub's form in your browser instead."
            : "Say what went wrong. Send with GitHub fills in GitHub's new issue form in your browser, with what's shown " +
              "under What is sent; sending it there needs a GitHub account. The report is copied to the clipboard as well.";
        // Without the relay there is only the one way to send.
        if (!vm.CanSendItself) SendButton.Visibility = Visibility.Collapsed;

        vm.Announce += text => Announcer.Announce(this, text);
        vm.MissingField += field => (field == nameof(ReportBugViewModel.Summary) ? SummaryBox : WhatHappenedBox).Focus();
        vm.Sent += () =>
        {
            OpenIssueButton.Visibility = Visibility.Visible;
            SendButton.Visibility = Visibility.Collapsed;
            SendYourselfButton.Visibility = Visibility.Collapsed;
            OpenIssueButton.Focus();
        };
        vm.SendFailed += () => SendYourselfButton.Focus();
        vm.CopyText = text => Clipboard.SetText(text);
        vm.OpenPage = Services.Browser.Open;

        Loaded += (_, _) => SummaryBox.Focus();
        Closed += (_, _) => vm.Dispose();
    }

    internal static void Show(Window owner, ReportBugViewModel vm) => new ReportBugWindow(vm) { Owner = owner }.ShowDialog();
}
