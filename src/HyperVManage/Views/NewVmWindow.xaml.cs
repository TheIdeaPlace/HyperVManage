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
        _isoToRestore = vm.IsoPath;
        PickIso = ShowIsoDialog;
        DataContext = vm;
        Loaded += (_, _) => SyncIsoSelection();
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
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            // Enter on Browse for an ISO, with the list open or closed, opens the file dialog. Taken
            // here, at the window, because the list itself acts on Enter first when it's open.
            if (e.Key == Key.Enter && IsoBox.IsKeyboardFocusWithin && _vm.IsoPath == NewVmViewModel.BrowseChoice)
            {
                e.Handled = true;
                ChooseBrowse();
            }
            // Escape in the open list closes it and puts back the ISO there was when it opened, as
            // any Windows drop-down does; the list itself then closes it.
            else if (e.Key == Key.Escape && IsoBox.IsDropDownOpen)
            {
                _escapingList = true;
            }
            // Otherwise Escape closes the window, as Cancel does; modeless windows don't get that
            // from IsCancel.
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
        IsoBox.DropDownOpened += (_, _) => _isoToRestore = _vm.IsoPath == NewVmViewModel.BrowseChoice ? _isoToRestore : _vm.IsoPath;
        IsoBox.IsKeyboardFocusWithinChanged += (_, e) =>
        {
            // Arrowed onto Browse in the closed box and then left: it wasn't chosen, so the ISO comes back.
            if (e.NewValue is false && !_browsing && _vm.IsoPath == NewVmViewModel.BrowseChoice) _vm.IsoPath = _isoToRestore;
        };
    }

    private bool _escapingList;

    private void FocusName()
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }

    /// <summary>True while the script runs. The main window won't close until this window has.</summary>
    public bool IsBuilding => _vm.IsRunning;

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NewVmViewModel.IsoPath))
        {
            // While the list is open, arrowing only tries ISOs out; the one to go back to is the one
            // there was when it opened.
            if (!IsoBox.IsDropDownOpen && _vm.IsoPath != NewVmViewModel.BrowseChoice) _isoToRestore = _vm.IsoPath;
            SyncIsoSelection();
        }
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

    /// <summary>The ISO to go back to when Browse for an ISO is left without choosing a file: the
    /// one showing before the list opened, or before arrowing onto Browse in the closed box.</summary>
    private string _isoToRestore = "";

    /// <summary>Asks for an ISO file; null if cancelled. The file dialog, or a stand-in in tests.</summary>
    internal Func<string?> PickIso { get; set; }

    private string? ShowIsoDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a Windows ISO",
            Filter = "Disc images (*.iso)|*.iso",
            InitialDirectory = IsoFinder.DownloadsFolder,
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    // Browse for an ISO opens the dialog only when chosen: Enter on it (see the window's
    // PreviewKeyDown) or a click. Arrowing onto it only shows it; leaving it any other way puts
    // back the ISO there was.
    private void BrowseItem_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ComboBoxItem { Content: NewVmViewModel.BrowseChoice }) return;
        e.Handled = true;
        ChooseBrowse();
    }

    private void ChooseBrowse()
    {
        _browsing = true;
        IsoBox.IsDropDownOpen = false;
        Dispatcher.BeginInvoke(BrowseForIso);
    }

    private void IsoBox_DropDownClosed(object? sender, EventArgs e)
    {
        // Escape, or closing on Browse without choosing it: the ISO from before comes back.
        var restore = !_browsing && (_escapingList || _vm.IsoPath == NewVmViewModel.BrowseChoice);
        _escapingList = false;
        if (restore) _vm.IsoPath = _isoToRestore;
        SyncIsoSelection();
    }

    /// <summary>From choosing Browse until its dialog closes.</summary>
    private bool _browsing;

    private void BrowseForIso()
    {
        _browsing = true;
        try
        {
            if (PickIso() is { } file) _vm.ChooseBrowsedIso(file);
            else _vm.IsoPath = _isoToRestore;
        }
        finally { _browsing = false; }
        SyncIsoSelection();
        IsoBox.Focus();
    }

    /// <summary>
    /// Keeps the list's selected entry on the ISO showing. With text search off WPF doesn't, and
    /// the arrows would move from an entry that isn't the one shown. A typed path that isn't in
    /// the list is left alone: clearing the selection would clear what was typed, so Down then
    /// goes on from the entry selected before.
    /// </summary>
    private void SyncIsoSelection()
    {
        var index = -1;
        for (var i = 0; i < _vm.IsoChoices.Count && index < 0; i++)
            if (string.Equals(_vm.IsoChoices[i], _vm.IsoPath, StringComparison.OrdinalIgnoreCase)) index = i;
        if (index >= 0 && IsoBox.SelectedIndex != index) IsoBox.SelectedIndex = index;
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
