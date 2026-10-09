using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HyperVManage.Helpers;
using HyperVManage.Services;
using HyperVManage.ViewModels;
using Microsoft.Win32;

namespace HyperVManage.Views;

/// <summary>
/// Modeless and unowned, so the VM list stays usable, and reachable with Alt+Tab, while a build
/// runs. It has no editable text over a live browser control, so the modal-dialog hazards of a
/// WebView2 host don't apply; modeless is simply friendlier for something that runs half an hour.
/// </summary>
public partial class NewVmWindow : Window
{
    private readonly NewVmViewModel _vm;
    private bool _closeWhenStopped;

    public NewVmWindow(NewVmViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        _isoBeforeBrowse = vm.IsoPath;
        DataContext = vm;
        vm.Announce += text => Announcer.Announce(this, text);
        vm.LineAppended += AppendLine;
        vm.PropertyChanged += OnVmPropertyChanged;
        vm.Finished += _ =>
        {
            // The user asked to close during the build; now that it has stopped and cleaned up, do.
            if (_closeWhenStopped) Close();
        };
        // Focusing a control in a window that isn't active would bring it to the front, so a
        // window opened in the background waits until the user goes to it.
        var focusNameWhenActive = false;
        Loaded += (_, _) =>
        {
            if (IsActive) FocusName();
            else focusNameWhenActive = true;
        };
        Activated += (_, _) =>
        {
            if (!focusNameWhenActive) return;
            focusNameWhenActive = false;
            Dispatcher.BeginInvoke(FocusName, System.Windows.Threading.DispatcherPriority.Input);
        };

        var downloads = IsoDownloads.ForThisPcFirst(IsoFinder.HostIsArm64);
        IsoLinkThisPc.NavigateUri = downloads[0].Page;
        IsoLinkThisPcText.Text = downloads[0].Text;
        IsoLinkOther.NavigateUri = downloads[1].Page;
        IsoLinkOtherText.Text = downloads[1].Text;

        PreviewKeyDown += (_, e) =>
        {
            // Escape closes, as Cancel does; modeless windows don't get that from IsCancel. With the
            // ISO list open it only closes the list, as in any drop-down, keeping the form.
            if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && !IsoBox.IsDropDownOpen)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    private void FocusName()
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }

    /// <summary>True while the script runs. The main window won't close until this window has.</summary>
    public bool IsBuilding => _vm.IsRunning;

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NewVmViewModel.IsoPath) && _vm.IsoPath != NewVmViewModel.BrowseChoice)
            _isoBeforeBrowse = _vm.IsoPath;
        // Once the script starts the form disappears; put focus on its output so the user is
        // somewhere they can read, rather than on a control that just vanished.
        if (e.PropertyName == nameof(NewVmViewModel.HasStarted) && _vm.HasStarted)
            Dispatcher.BeginInvoke(() => LogBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// Adds a line without disturbing the reader: AppendText leaves the caret where it is, and the
    /// view only follows the new text if the caret was already at the end.
    /// </summary>
    private void AppendLine(string line)
    {
        var atEnd = LogBox.CaretIndex >= LogBox.Text.Length;
        LogBox.AppendText(LogBox.Text.Length == 0 ? line : Environment.NewLine + line);
        if (atEnd)
        {
            LogBox.CaretIndex = LogBox.Text.Length;
            LogBox.ScrollToEnd();
        }
    }

    // Selection follows focus in the radio group, as in any Windows radio group: arrowing to a
    // choice chooses it.
    private void Radio_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is RadioButton rb && rb.IsChecked != true) rb.IsChecked = true;
    }

    // Tab or Shift+Tab into the group must land on the choice already made. WPF lands on the
    // first or last radio, and with selection following focus that would quietly change the
    // choice, so focus arriving from outside on an unchosen radio is sent to the chosen one.
    // Arrowing within the group is untouched.
    private void NetworkGroup_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var fromOutside = e.OldFocus is not DependencyObject old || !NetworkGroup.IsAncestorOf(old);
        if (!fromOutside || e.NewFocus is not RadioButton { IsChecked: not true }) return;
        var chosen = NetworkGroup.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
        if (chosen is null) return;
        e.Handled = true; // cancels this focus change
        Dispatcher.BeginInvoke(() => chosen.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Browse_Click(object sender, RoutedEventArgs e) => BrowseForIso();

    /// <summary>The ISO chosen before Browse for an ISO was, to go back to if the dialog is cancelled.</summary>
    private string _isoBeforeBrowse = "";

    private void IsoBox_DropDownClosed(object? sender, EventArgs e)
    {
        // Chosen from the open list with Enter or a click.
        if (Equals(IsoBox.SelectedItem, NewVmViewModel.BrowseChoice)) Dispatcher.BeginInvoke(BrowseForIso);
    }

    private void IsoBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Arrowed onto in the closed box, it only shows; Enter then opens the dialog, rather than
        // Create, the window's default button.
        if (e.Key == Key.Enter && !IsoBox.IsDropDownOpen && _vm.IsoPath == NewVmViewModel.BrowseChoice)
        {
            e.Handled = true;
            BrowseForIso();
        }
    }

    private void BrowseForIso()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a Windows ISO",
            Filter = "Disc images (*.iso)|*.iso",
            InitialDirectory = IsoFinder.DownloadsFolder,
        };
        if (dialog.ShowDialog(this) == true) _vm.ChooseBrowsedIso(dialog.FileName);
        else if (_vm.IsoPath == NewVmViewModel.BrowseChoice) _vm.IsoPath = _isoBeforeBrowse;
        IsoBox.Focus();
    }

    private void IsoLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Documents.Hyperlink { NavigateUri: { } page }) return;
        try
        {
            IsoDownloads.Open(page);
            Announcer.Announce(this, "Opening the download page in your browser.");
        }
        catch (Exception ex) { Announcer.Announce(this, $"Couldn't open the download page: {ex.Message}"); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.IsRunning)
        {
            e.Cancel = true;
            if (_closeWhenStopped) return; // already stopping; it closes when the cleanup is done
            var answer = MessageBox.Show(this,
                "The virtual machine is still being built. Stop the build?\n\n" +
                "Whatever it has made so far is cleaned up. If it has already got as far as creating the VM, " +
                "that VM is left in the list for you to delete.",
                "Stop building the VM?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            if (!_vm.IsRunning)
            {
                // It finished while the question was up: nothing to stop, so just close.
                e.Cancel = false;
                base.OnClosing(e);
                return;
            }
            _closeWhenStopped = true;
            _vm.Stop();
            return;
        }
        base.OnClosing(e);
    }
}
