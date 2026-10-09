using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HyperVManage.Services;
using HyperVManage.ViewModels;
using HyperVManage.Views;
using Xunit;

namespace HyperVManage.Tests;

public sealed class IsoHistoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hvm-iso-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Iso(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "");
        return path;
    }

    private IsoHistory Make() => new(Path.Combine(_dir, "settings", "recent-isos.json"));

    [Fact]
    public void Remember_PutsTheIsoFirst_WithoutRepeatingIt_AndSurvivesANewInstance()
    {
        var a = Iso("a.iso");
        var b = Iso("b.iso");
        var history = Make();
        history.Remember(a);
        history.Remember(b);
        history.Remember(a.ToUpperInvariant());
        Assert.Equal([a.ToUpperInvariant(), b], Make().Recent());
    }

    [Fact]
    public void Recent_LeavesOutIsosThatAreGone()
    {
        var a = Iso("a.iso");
        var b = Iso("b.iso");
        var history = Make();
        history.Remember(a);
        history.Remember(b);
        File.Delete(b);
        Assert.Equal([a], history.Recent());
    }

    [Fact]
    public void KeepsTheTenMostRecent()
    {
        var history = Make();
        var isos = Enumerable.Range(1, 12).Select(i => Iso($"{i}.iso")).ToList();
        foreach (var iso in isos) history.Remember(iso);
        Assert.Equal(isos.AsEnumerable().Reverse().Take(IsoHistory.MaxRemembered), history.Recent());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"a\":1}")]
    [InlineData("[\"relative.iso\", \"\", null]")]
    public void ABadFile_IsAnEmptyList_AndIsReplacedOnTheNextBuild(string content)
    {
        var file = Path.Combine(_dir, "settings", "recent-isos.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        var history = Make();
        Assert.Empty(history.Recent());
        var a = Iso("a.iso");
        history.Remember(a);
        Assert.Equal([a], history.Recent());
    }

    [Fact]
    public void AFileThatCantBeWritten_NeverStopsABuild()
    {
        // A folder where the file should be: writing fails, and is ignored.
        var file = Path.Combine(_dir, "blocked");
        Directory.CreateDirectory(file);
        var history = new IsoHistory(file);
        history.Remember(Iso("a.iso"));
        Assert.Empty(history.Recent());
    }

    [Fact]
    public void NewVm_OffersTheRememberedIsosFirst_ThenBrowse_AndChoosesTheLatest()
    {
        var a = Iso("a.iso");
        var b = Iso("b.iso");
        var history = new InMemoryIsoHistory();
        history.Remember(a);
        history.Remember(b);
        var vm = new NewVmViewModel([], history);
        Assert.Equal(b, vm.IsoPath);
        Assert.Equal([b, a], vm.IsoChoices.Take(2));
        Assert.Equal(NewVmViewModel.BrowseChoice, vm.IsoChoices[^1]);
    }

    [Fact]
    public void NewVm_ABrowsedIso_JoinsTheListAboveBrowse_AndIsChosen()
    {
        var a = Iso("a.iso");
        var vm = new NewVmViewModel([], new InMemoryIsoHistory());
        vm.ChooseBrowsedIso(a);
        vm.ChooseBrowsedIso(a);
        Assert.Equal(a, vm.IsoPath);
        Assert.Single(vm.IsoChoices, a);
        Assert.Equal(NewVmViewModel.BrowseChoice, vm.IsoChoices[^1]);
    }

    [Fact]
    public void NewVm_BrowseLeftShowing_IsAProblem_NotTheDownloadsIso()
    {
        var vm = new NewVmViewModel([], new InMemoryIsoHistory())
        {
            VmName = "Lab", IsoPath = NewVmViewModel.BrowseChoice, Processors = "1", MemoryGB = "2", DiskGB = "64",
        };
        Assert.Null(vm.Validate());
        Assert.StartsWith("Choose a Windows ISO", vm.Error);
    }

    [Fact]
    public async Task NewVm_RemembersTheIso_WhenTheBuildStarts_NotBefore()
    {
        var a = Iso("a.iso");
        var history = new InMemoryIsoHistory();
        var vm = new NewVmViewModel([], history)
        {
            VmName = "Lab", IsoPath = a, Processors = "1", MemoryGB = "2", DiskGB = "64",
            RunScript = (_, _, _) => Task.FromResult(BuildOutcome.Failed),
        };
        Assert.Empty(history.Recent());
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.Equal([a], history.Recent());

        var refused = new InMemoryIsoHistory();
        var bad = new NewVmViewModel([], refused) { VmName = "", IsoPath = a };
        await bad.CreateCommand.ExecuteAsync(null);
        Assert.Empty(refused.Recent());
    }
}

[Collection("Wpf")]
public class IsoListWindowTests
{
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    [StaFact]
    public void TheIsoList_IsAnEditableComboBoxNamedWindowsIso_ListingBrowseLast()
    {
        TestApp.Ensure();
        var window = new NewVmWindow(new NewVmViewModel([], new InMemoryIsoHistory()))
        {
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
        };
        try
        {
            window.Show();
            Pump();
            var box = (ComboBox)window.FindName("IsoBox");
            Assert.True(box.IsEditable);
            Assert.Equal("Windows ISO", UIElementAutomationPeer.CreatePeerForElement(box).GetName());
            Assert.Equal(NewVmViewModel.BrowseChoice, box.Items[^1]);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void Escape_WithTheIsoListOpen_ClosesOnlyTheList()
    {
        TestApp.Ensure();
        var window = new NewVmWindow(new NewVmViewModel([], new InMemoryIsoHistory()))
        {
            ShowActivated = true, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
        };
        try
        {
            window.Show();
            Pump();
            var box = (ComboBox)window.FindName("IsoBox");
            box.IsDropDownOpen = true;
            Pump();
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            window.RaiseEvent(escape);
            Pump();
            Assert.True(window.IsLoaded && window.IsVisible, "Escape closed the window");
        }
        finally { window.Close(); }
    }
}
