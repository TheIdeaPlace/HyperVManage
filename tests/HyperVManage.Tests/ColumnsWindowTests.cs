using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using HyperVManage.Models;
using HyperVManage.Services;
using HyperVManage.ViewModels;
using HyperVManage.Views;
using Xunit;

namespace HyperVManage.Tests;

/// <summary>The VM list's columns, the Columns dialog, and the Location boxes, as real windows shown
/// off-screen against the demo backend.</summary>
[Collection("Wpf")]
public class ColumnsWindowTests
{
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void ShowOffscreen(Window w)
    {
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -10000;
        w.Top = -10000;
        w.Show();
        Pump();
    }

    private static List<string> RowNames(ListView list)
    {
        var peer = (ListViewAutomationPeer)UIElementAutomationPeer.CreatePeerForElement(list);
        return peer.GetChildren().Where(c => c.GetAutomationControlType() is AutomationControlType.ListItem or AutomationControlType.DataItem)
                   .Select(c => c.GetName()).ToList();
    }

    [StaFact]
    public async Task TheListsColumns_FollowTheLayout_AndTheRowsAreReadInTheSameOrder()
    {
        TestApp.Ensure();
        var vm = new MainViewModel(new DemoHyperVService { Delay = TimeSpan.Zero }, settings: new InMemorySettingsStore());
        await vm.RefreshAsync();
        var window = new MainWindow(vm, demo: true);
        try
        {
            ShowOffscreen(window);
            var list = (ListView)window.FindName("VmList");
            var grid = (GridView)list.View;
            Assert.Equal(["Name", "State", "Address", "Network", "Memory", "Processors", "Starts with PC"], grid.Columns.Select(c => (string)c.Header));

            vm.ApplyColumns(["location", "name", "state"], speakNames: true);
            Pump();
            Assert.Equal(["Location", "Name", "State"], grid.Columns.Select(c => (string)c.Header));
            // Type-ahead goes by what's read first.
            Assert.Equal(nameof(VmInfo.Location), TextSearch.GetTextPath(list));
            var names = RowNames(list);
            Assert.Equal(vm.Vms.Select(v => v.AccessibleName), names);
            Assert.Contains(@"D:\Hyper-V\Build Agent, Name: Build Agent, Saved", names);

            // The cells show what the row's name says, column by column.
            var agent = vm.Vms.Single(v => v.Name == "Build Agent");
            var row = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(agent);
            Assert.Equal(agent.AccessibleName, AutomationProperties.GetName(row));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public async Task ColumnWidths_DraggedThisSession_AreSavedWhenTheWindowCloses()
    {
        TestApp.Ensure();
        var settings = new InMemorySettingsStore();
        var vm = new MainViewModel(new DemoHyperVService { Delay = TimeSpan.Zero }, settings: settings);
        await vm.RefreshAsync();
        var window = new MainWindow(vm, demo: true);
        ShowOffscreen(window);
        var grid = (GridView)((ListView)window.FindName("VmList")).View;
        grid.Columns[0].Width = 233;
        Pump();
        window.Close();
        Assert.Equal(233, settings.Load().VmListColumnWidths!["name"]);

        // And used next time.
        var again = new MainViewModel(new DemoHyperVService { Delay = TimeSpan.Zero }, settings: settings);
        var second = new MainWindow(again, demo: true);
        try
        {
            ShowOffscreen(second);
            Assert.Equal(233, ((GridView)((ListView)second.FindName("VmList")).View).Columns[0].Width);
        }
        finally { second.Close(); }
    }

    private static (ColumnsWindow Window, ColumnsViewModel Vm) OpenColumns(params string[] shown)
    {
        TestApp.Ensure();
        var sample = new VmInfo("1") { Name = "Lab", State = "Off", Location = @"D:\Hyper-V\Lab" };
        var editor = new ColumnsViewModel(VmColumns.Set, shown, speakNames: true, (c, s) => new VmRowFormat(c, s).Compose(sample));
        var window = new ColumnsWindow(editor);
        ShowOffscreen(window);
        return (window, editor);
    }

    [StaFact]
    public void TheColumnsDialog_OpensOnTheFirstShownColumn_AndEachActionSaysWhatItDid()
    {
        var (window, editor) = OpenColumns("name", "state");
        try
        {
            var shown = (ListBox)window.FindName("ShownList");
            var available = (ListBox)window.FindName("AvailableList");
            Assert.Equal(0, shown.SelectedIndex);
            var preview = (TextBox)window.FindName("PreviewBox");
            Assert.Equal("Lab, Off", preview.Text);

            available.SelectedIndex = editor.Available.ToList().FindIndex(c => c.Id == "location");
            window.AddSelected();
            Assert.Equal("Location added at the end, 3 of 3.", window.Spoken[^1]);
            Assert.Equal(@"Lab, Off, Location: D:\Hyper-V\Lab", preview.Text);
            // Focus stays in Available, on the column that took its place.
            Assert.True(available.SelectedIndex >= 0);

            shown.SelectedIndex = 2;
            window.MoveSelected(i => i - 1);
            Assert.Equal("Location moved above State.", window.Spoken[^1]);
            Assert.Equal(1, shown.SelectedIndex);
            Assert.Equal("location", ((ColumnItem)shown.SelectedItem).Id);

            shown.SelectedIndex = 0;
            window.MoveSelected(i => i - 1);
            Assert.Equal("Name is already at the top.", window.Spoken[^1]);

            shown.SelectedIndex = 2;
            window.RemoveSelected();
            Assert.Equal("State removed, 2 columns shown.", window.Spoken[^1]);
            Assert.Equal(1, shown.SelectedIndex);

            // Each list item is read by its heading.
            var peer = UIElementAutomationPeer.CreatePeerForElement(shown);
            Assert.Equal(["Name", "Location"], peer.GetChildren().Select(c => c.GetName()));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void TheColumnsDialog_TheLastColumnCantGo_AndAddingTheLastAvailableMovesToShown()
    {
        var all = VmColumns.Set.Ids.ToArray();
        var (window, editor) = OpenColumns(all[..^1]);
        try
        {
            var shown = (ListBox)window.FindName("ShownList");
            var available = (ListBox)window.FindName("AvailableList");
            available.SelectedIndex = 0;
            window.AddSelected();
            Assert.Empty(editor.Available);
            Assert.Equal(editor.Shown.Count - 1, shown.SelectedIndex);
        }
        finally { window.Close(); }

        var (single, _) = OpenColumns("name");
        try
        {
            ((ListBox)single.FindName("ShownList")).SelectedIndex = 0;
            single.RemoveSelected();
            Assert.Equal("At least one column has to be shown.", single.Spoken[^1]);
        }
        finally { single.Close(); }
    }

    [StaFact]
    public void Clone_RefusesALocationHyperVCantUse_InTheDialog_AndAcceptsAGoodOne()
    {
        TestApp.Ensure();
        var vm = new VmInfo("1") { Name = "Lab", State = "Off" };
        var w = new CloneWindow(vm, @"E:\VMs")
        {
            CheckLocation = l => l.StartsWith("E:", StringComparison.OrdinalIgnoreCase) ? "E:\\ is exFAT." : null,
        };
        ShowOffscreen(w);
        try
        {
            var name = (TextBox)w.FindName("NameBox");
            var location = (TextBox)w.FindName("LocationBox");
            Assert.Equal("Lab-copy", name.Text);
            Assert.Contains(@"E:\VMs\Lab-copy", AutomationProperties.GetHelpText(location));

            w.Ok_Click(w, new RoutedEventArgs());
            Assert.Null(w.Result);
            Assert.Equal("E:\\ is exFAT.", ((TextBlock)w.FindName("ErrorText")).Text);
            Assert.True(w.IsVisible);

            w.PickFolder = _ => @"D:\Hyper-V";
            w.GetType().GetMethod("ChooseFolder_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(w, [w, new RoutedEventArgs()]);
            Assert.Equal(@"D:\Hyper-V", location.Text);
            Assert.Equal(@"The copy goes in D:\Hyper-V\Lab-copy, with its disk in Virtual Hard Disks there.", ((TextBlock)w.FindName("WhereText")).Text);
        }
        finally { w.Close(); }
    }

    [StaFact]
    public void NewVm_ChooseFolder_FillsTheLocation_AndTheSentenceIsTheBoxsDescription()
    {
        TestApp.Ensure();
        var n = new NewVmViewModel([], new InMemoryIsoHistory(), ubuntu: true);
        var w = new NewVmWindow(n) { PickFolder = _ => @"D:\Hyper-V" };
        ShowOffscreen(w);
        try
        {
            var box = (TextBox)w.FindName("LocationBox");
            Assert.Equal("", box.Text);
            Assert.StartsWith("Empty means", AutomationProperties.GetHelpText(box));
            w.GetType().GetMethod("ChooseFolder_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(w, [w, new RoutedEventArgs()]);
            Pump();
            Assert.Equal(@"D:\Hyper-V", n.Location);
            Assert.Equal(@"D:\Hyper-V", box.Text);
            Assert.Equal($@"The VM goes in D:\Hyper-V\{n.VmName}, with its disk in Virtual Hard Disks there.", AutomationProperties.GetHelpText(box));
        }
        finally { w.Close(); }
    }
}
