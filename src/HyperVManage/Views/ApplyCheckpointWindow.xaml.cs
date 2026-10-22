using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using HyperVManage.Models;
using HyperVManage.Services;
using HyperVManage.ViewModels;

namespace HyperVManage.Views;

/// <summary>Asks which checkpoint to put a VM back to. The note under the list says what is lost.</summary>
public partial class ApplyCheckpointWindow : Window
{
    private ApplyCheckpointWindow() => InitializeComponent();

    /// <param name="chosen">The one to start on, as chosen in the checkpoint list; otherwise the newest.</param>
    public static CheckpointChoice? Ask(Window owner, VmInfo vm, IReadOnlyList<CheckpointInfo> checkpoints, CheckpointInfo? chosen = null)
    {
        var w = Create(vm, checkpoints, chosen);
        w.Owner = owner;
        return w.ShowDialog() == true && w.CheckpointList.SelectedItem is CheckpointInfo applied
            ? new CheckpointChoice(applied, w.SaveFirstBox.IsChecked == true)
            : null;
    }

    internal static ApplyCheckpointWindow Create(VmInfo vm, IReadOnlyList<CheckpointInfo> checkpoints, CheckpointInfo? chosen = null)
    {
        var w = new ApplyCheckpointWindow { Title = $"Apply a checkpoint to {vm.Name}" };
        w.NoteText.Text = Note(vm);
        AutomationProperties.SetHelpText(w.CheckpointList, w.NoteText.Text);
        // Focus is on an item, not the list, and a screen reader speaks the description of what
        // has focus; so each item carries the note too, or the warning would never be heard.
        var item = new Style(typeof(ListBoxItem));
        item.Setters.Add(new Setter(AutomationProperties.HelpTextProperty, w.NoteText.Text));
        // On the items, not the list, so double-clicking the scroll bar applies nothing.
        item.Setters.Add(new EventSetter(MouseDoubleClickEvent, new MouseButtonEventHandler(w.Item_MouseDoubleClick)));
        w.CheckpointList.ItemContainerStyle = item;
        w.CheckpointList.ItemsSource = checkpoints;
        var start = chosen is null ? 0 : Math.Max(0, checkpoints.ToList().FindIndex(c => c.Id == chosen.Id));
        w.CheckpointList.SelectedIndex = start;
        w.Loaded += (_, _) =>
        {
            w.CheckpointList.ScrollIntoView(w.CheckpointList.SelectedItem);
            w.CheckpointList.UpdateLayout();
            if (w.CheckpointList.ItemContainerGenerator.ContainerFromIndex(start) is ListBoxItem row) row.Focus();
            else w.CheckpointList.Focus();
        };
        return w;
    }

    internal static string Note(VmInfo vm) =>
        (VmStates.CanTurnOff(vm.State)
            ? $"{vm.Name} is {vm.StateText.ToLowerInvariant()}, so it is turned off first, and anything unsaved in it is lost. "
            : "")
        + $"{vm.Name} goes back to exactly how it was when the checkpoint was taken. " +
        "Everything that changed since is lost, unless you keep it as a checkpoint first.";

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (CheckpointList.SelectedItem is null) { CheckpointList.Focus(); return; }
        DialogResult = true;
    }

    private void Item_MouseDoubleClick(object sender, MouseButtonEventArgs e) => DialogResult = true;
}
