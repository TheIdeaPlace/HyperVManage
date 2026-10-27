using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HyperVManage.ViewModels;
using HyperVManage.Views;
using Xunit;

namespace HyperVManage.Tests;

/// <summary>
/// Real keystrokes, through WPF's own input pipeline, at a real window. They need the window to be
/// active, which takes focus from whatever else is on screen, so they only run when
/// HYPERVMANAGE_RUN_INPUT_TESTS=1 is set.
/// </summary>
public static class InputTests
{
    public const string SkipReason = "Keyboard tests take focus; set HYPERVMANAGE_RUN_INPUT_TESTS=1 to run them.";
    public static bool Enabled => Environment.GetEnvironmentVariable("HYPERVMANAGE_RUN_INPUT_TESTS") == "1";
}

[Collection("Wpf")]
public class KeyboardTests
{
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void Press(Window window, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        var source = PresentationSource.FromVisual(window)!;
        // Shift+Tab: WPF reads the modifier state from the keyboard device, so it can't be faked
        // here; Shift+Tab is covered by moving focus backwards from the element after the group.
        // One event: the input manager raises the preview and bubbling halves itself. Sending
        // both would press the key twice, and a two-item radio group would cycle straight back.
        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
        Pump();
        Pump();
    }

    private static (NewVmWindow window, NewVmViewModel vm) Open()
    {
        TestApp.Ensure();
        var vm = new NewVmViewModel([]);
        var window = new NewVmWindow(vm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
        window.Show();
        window.Activate();
        Pump();
        return (window, vm);
    }

    [StaFact(Skip = InputTests.SkipReason, SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public void NetworkChoice_ArrowsMoveAndChoose_TabLandsOnTheChoiceWithoutChangingIt()
    {
        var (window, vm) = Open();
        try
        {
            var yours = (RadioButton)window.FindName("YourNetworkRadio");
            var hostOnly = (RadioButton)window.FindName("HostOnlyRadio");
            yours.Focus();
            Pump();
            Assert.True(yours.IsKeyboardFocused, "couldn't put focus on the first choice");

            Press(window, Key.Down);
            Assert.True(hostOnly.IsKeyboardFocused, "Down didn't reach the second choice");
            Assert.True(vm.HostOnly);

            Press(window, Key.Up);
            Assert.True(yours.IsKeyboardFocused);
            Assert.True(vm.OnYourNetwork);

            // Tab into the group must land on the choice made and never change it. One test with
            // the arrows above: WPF's Application belongs to the thread that made it, and each
            // StaFact runs on a thread of its own that ends with it.
            vm.HostOnly = true; // the second choice is the one made

            // Forwards, from the field before the group.
            ((TextBox)window.FindName("DiskBox")).Focus();
            Pump();
            Press(window, Key.Tab);
            Assert.True(hostOnly.IsKeyboardFocused, "Tab didn't land on the chosen radio");
            Assert.True(vm.HostOnly, "Tabbing in changed the choice");

            // Backwards: focus moving in from the check box after the group.
            var after = (CheckBox)((Panel)((GroupBox)LogicalTreeHelper.GetParent(LogicalTreeHelper.GetParent(hostOnly))).Parent)
                .Children.OfType<CheckBox>().First();
            after.Focus();
            Pump();
            after.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
            Pump();
            Pump();
            Assert.True(hostOnly.IsKeyboardFocused, "Shift+Tab didn't land on the chosen radio");
            Assert.True(vm.HostOnly, "Shift+Tab in changed the choice");
        }
        finally { window.Close(); }
    }

    [StaFact(Skip = InputTests.SkipReason, SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public async Task MainWindow_TabNeverLandsOnTheMenuBar()
    {
        // The menu bar is reached with Alt or F10, as in any Windows app. Tab stopping on
        // File, VM and Help puts three extra stops between the user and the list.
        TestApp.Ensure();
        var vm = new MainViewModel(new HyperVManage.Services.DemoHyperVService { Delay = TimeSpan.Zero });
        await vm.RefreshAsync();
        var window = new MainWindow(vm, demo: true) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
        window.Show();
        window.Activate();
        Pump();
        try
        {
            var list = (ListView)window.FindName("VmList");
            list.SelectedIndex = 0;
            Pump();
            ((ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(0)).Focus();
            Pump();
            Assert.True(list.IsKeyboardFocusWithin, "couldn't put focus on the list");

            for (var i = 0; i < 6; i++)
            {
                Press(window, Key.Tab);
                var focused = Keyboard.FocusedElement as DependencyObject;
                for (var d = focused; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
                {
                    Assert.False(d is Menu, $"Tab {i + 1} landed on the menu bar ({focused})");
                }
            }
        }
        finally { window.Close(); }
    }
    [StaFact(Skip = InputTests.SkipReason, SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public async Task MainWindow_ArrowsFromTheCheckpointListItself_StayInIt()
    {
        // Ctrl+click can leave no checkpoint chosen, and Tab then lands on the list itself.
        TestApp.Ensure();
        var vm = new MainViewModel(new HyperVManage.Services.DemoHyperVService { Delay = TimeSpan.Zero });
        await vm.RefreshAsync();
        var window = new MainWindow(vm, demo: true) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
        window.Show();
        window.Activate();
        Pump();
        try
        {
            vm.Selected = vm.Vms.Single(v => v.Name == "Build Agent");
            Pump();
            var list = (ListView)window.FindName("CheckpointList");
            Assert.True(list.Items.Count > 1);
            foreach (var key in new[] { Key.Up, Key.Down })
            {
                list.SelectedItems.Clear();
                Pump();
                list.Focus();
                Pump();
                Press(window, key);
                Assert.True(list.IsKeyboardFocusWithin, $"{key} from the checkpoint list itself moved focus to {Keyboard.FocusedElement}");
            }
        }
        finally { window.Close(); }
    }

    [StaFact(Skip = InputTests.SkipReason, SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public async Task MainWindow_ArrowsStopAtTheEndsOfTheLists()
    {
        // Up on the first row or Down on the last stays put, as in any Windows list; focus must
        // never wander out to the menu bar or the other list.
        TestApp.Ensure();
        var vm = new MainViewModel(new HyperVManage.Services.DemoHyperVService { Delay = TimeSpan.Zero });
        await vm.RefreshAsync();
        var window = new MainWindow(vm, demo: true) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
        window.Show();
        window.Activate();
        Pump();
        try
        {
            vm.Selected = vm.Vms.Single(v => v.Name == "Build Agent");
            Pump();
            foreach (var name in new[] { "VmList", "CheckpointList" })
            {
                var list = (ListView)window.FindName(name);
                Assert.True(list.Items.Count > 1, $"{name} needs two rows for this test");
                foreach (var (start, key) in new[] { (0, Key.Up), (list.Items.Count - 1, Key.Down) })
                {
                    list.SelectedIndex = start;
                    Pump();
                    ((ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(start)).Focus();
                    Pump();
                    for (var i = 0; i < 2; i++)
                    {
                        Press(window, key);
                        var row = Keyboard.FocusedElement as ListViewItem;
                        Assert.True(row is not null && ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(row), list),
                            $"{key} at the end of {name} moved focus to {Keyboard.FocusedElement}");
                        Assert.Equal(start, list.ItemContainerGenerator.IndexFromContainer(row));
                    }
                }
            }
        }
        finally { window.Close(); }
    }

    [StaFact(Skip = InputTests.SkipReason, SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public async Task MainWindow_ArrowsOnTheOnlyVmStayOnIt()
    {
        TestApp.Ensure();
        var vm = new MainViewModel(new HyperVManage.Services.DemoHyperVService { Delay = TimeSpan.Zero });
        await vm.RefreshAsync();
        var window = new MainWindow(vm, demo: true) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
        window.Show();
        window.Activate();
        Pump();
        try
        {
            while (vm.Vms.Count > 1) vm.Vms.RemoveAt(vm.Vms.Count - 1);
            vm.Selected = vm.Vms[0];
            Pump();
            var list = (ListView)window.FindName("VmList");
            foreach (var key in new[] { Key.Up, Key.Down, Key.Up })
            {
                ((ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(0)).Focus();
                Pump();
                Press(window, key);
                Assert.True(Keyboard.FocusedElement is ListViewItem row && ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(row), list),
                    $"{key} on the only VM moved focus to {Keyboard.FocusedElement}");
            }
        }
        finally { window.Close(); }
    }

    [StaFact(Skip = InputTests.SkipReason, SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public async Task MainWindow_ArrowsInEmptyListsStayInThem()
    {
        // With no VMs, focus is on the list itself: no row to stop on, so WPF would move it to
        // the nearest control that way, the menu bar above or the checkpoint list below.
        TestApp.Ensure();
        var vm = new MainViewModel(new HyperVManage.Services.DemoHyperVService { Delay = TimeSpan.Zero });
        await vm.RefreshAsync();
        var window = new MainWindow(vm, demo: true) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
        window.Show();
        window.Activate();
        Pump();
        try
        {
            vm.Vms.Clear();
            vm.Selected = null;
            Pump();
            foreach (var name in new[] { "VmList", "CheckpointList" })
            {
                var list = (ListView)window.FindName(name);
                Assert.Empty(list.Items);
                foreach (var key in new[] { Key.Up, Key.Down, Key.Left, Key.Right, Key.Home, Key.End, Key.PageUp, Key.PageDown })
                {
                    list.Focus();
                    Pump();
                    Press(window, key);
                    Assert.True(list.IsKeyboardFocused, $"{key} in the empty {name} moved focus to {Keyboard.FocusedElement}");
                }
            }
        }
        finally { window.Close(); }
    }

    [StaFact(Skip = InputTests.SkipReason, SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public async Task MainWindow_TabGoesFromTheVmsToTheirCheckpoints_WhereDeleteDeletesACheckpoint()
    {
        TestApp.Ensure();
        var vm = new MainViewModel(new HyperVManage.Services.DemoHyperVService { Delay = TimeSpan.Zero });
        await vm.RefreshAsync();
        var window = new MainWindow(vm, demo: true) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
        window.Show();
        window.Activate();
        Pump();
        try
        {
            var vms = (ListView)window.FindName("VmList");
            var agent = vm.Vms.Single(v => v.Name == "Build Agent");
            vm.Selected = agent;
            Pump();
            ((ListViewItem)vms.ItemContainerGenerator.ContainerFromItem(agent)).Focus();
            Pump();

            Press(window, Key.Tab);
            var checkpoints = (ListView)window.FindName("CheckpointList");
            // On the selected row itself, so a screen reader reads the checkpoint, not just the list.
            var row = Assert.IsType<ListViewItem>(Keyboard.FocusedElement);
            Assert.Same(checkpoints, ItemsControl.ItemsControlFromItemContainer(row));
            Assert.Same(vm.Checkpoints[0], row.DataContext);

            var asked = false;
            vm.ConfirmDelete = (_, _) => throw new InvalidOperationException("Delete in the checkpoint list asked to delete the VM");
            vm.ConfirmDeleteCheckpoints = (_, going, _) => { asked = true; return true; };
            // Real input raises the preview half first, which is where the list handles its keys.
            var source = PresentationSource.FromVisual(window)!;
            var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Delete) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            InputManager.Current.ProcessInput(preview);
            if (!preview.Handled) Press(window, Key.Delete);
            for (var i = 0; i < 5 && vm.Checkpoints.Count == 3; i++) Pump();

            Assert.True(asked);
            Assert.Equal(2, vm.Checkpoints.Count);
            Assert.Contains(agent, vm.Vms);
            // Focus moved to the row that took its place, so Delete can be pressed again.
            Assert.True(checkpoints.IsKeyboardFocusWithin, $"focus left the checkpoint list for {Keyboard.FocusedElement}");
        }
        finally { window.Close(); }
    }
}
