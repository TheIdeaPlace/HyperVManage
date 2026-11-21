using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HyperVManage.Helpers;
using HyperVManage.ViewModels;

namespace HyperVManage.Views;

/// <summary>
/// View, Columns…, as the baseline's fields editor: Shown and Available lists, the same actions as
/// keys and buttons, Speak column names, and a Preview of the selected VM as it will be read. Each
/// action says what happened. OK applies; Cancel and Escape leave everything as it was.
/// </summary>
public partial class ColumnsWindow : Window
{
    private readonly ColumnsViewModel _vm;

    public ColumnsWindow(ColumnsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        // The first column chosen straight away, so it's chosen even before the dialog is shown;
        // focus on it once the list has made its rows, which it hasn't yet when Loaded is raised.
        ShownList.SelectedIndex = vm.Shown.Count > 0 ? 0 : -1;
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => FocusShown(0), DispatcherPriority.Input);
    }

    /// <summary>Opens the dialog; true if OK was chosen, with the choices left in the view model.</summary>
    public static bool Ask(Window owner, ColumnsViewModel vm) => new ColumnsWindow(vm) { Owner = owner }.ShowDialog() == true;

    /// <summary>Every sentence said, for tests.</summary>
    internal List<string> Spoken { get; } = [];

    private void Say(string text)
    {
        Spoken.Add(text);
        Announcer.Announce(this, text);
    }

    internal void FocusShown(int index) => FocusIn(ShownList, index);

    private void FocusIn(ListBox list, int index)
    {
        if (list.Items.Count == 0) { list.Focus(); return; }
        index = Math.Clamp(index, 0, list.Items.Count - 1);
        list.SelectedIndex = index;
        list.ScrollIntoView(list.Items[index]);
        list.UpdateLayout();
        // On the item itself, so arrowing starts there and a screen reader reads it.
        if (list.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item) item.Focus();
        else Dispatcher.BeginInvoke(() =>
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem late) late.Focus();
            else list.Focus();
        }, DispatcherPriority.Input);
    }

    // ── The actions, shared by keys and buttons ───────────────────────────

    internal void AddSelected()
    {
        var index = AvailableList.SelectedIndex;
        var ok = _vm.Add(index);
        Say(_vm.Said);
        if (!ok) return;
        // Stays in Available, on the next column, or moves to Shown once Available is empty.
        if (_vm.Available.Count > 0) FocusIn(AvailableList, index);
        else FocusShown(_vm.Shown.Count - 1);
    }

    internal void RemoveSelected()
    {
        var index = ShownList.SelectedIndex;
        var ok = _vm.Remove(index);
        Say(_vm.Said);
        // The one that took its place, or the one above if it was the last.
        if (ok) FocusShown(Math.Min(index, _vm.Shown.Count - 1));
    }

    internal void MoveSelected(Func<int, int> target)
    {
        var index = ShownList.SelectedIndex;
        var to = index < 0 ? -1 : target(index);
        var ok = _vm.Move(index, to);
        Say(_vm.Said);
        // Focus stays on the column that moved.
        if (ok) FocusShown(to);
    }

    private int Last => _vm.Shown.Count - 1;

    private void Add_Click(object sender, RoutedEventArgs e) => AddSelected();
    private void Remove_Click(object sender, RoutedEventArgs e) => RemoveSelected();
    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(i => i - 1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(i => i + 1);
    private void MoveTop_Click(object sender, RoutedEventArgs e) => MoveSelected(_ => 0);
    private void MoveBottom_Click(object sender, RoutedEventArgs e) => MoveSelected(_ => Last);

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _vm.Reset();
        Say(_vm.Said);
    }

    private void AvailableList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // The list takes Enter itself, so it never reaches OK.
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            AddSelected();
        }
    }

    private void ShownList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Alt+arrows arrive as Key.System, with the key itself in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        Func<int, int>? target = (mods, key) switch
        {
            (ModifierKeys.Alt, Key.Up) => i => i - 1,
            (ModifierKeys.Alt, Key.Down) => i => i + 1,
            (ModifierKeys.Alt, Key.Home) => _ => 0,
            (ModifierKeys.Alt, Key.End) => _ => Last,
            _ => null,
        };
        if (target is not null)
        {
            e.Handled = true;
            MoveSelected(target);
        }
        else if (key == Key.Delete && mods == ModifierKeys.None)
        {
            e.Handled = true;
            RemoveSelected();
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
