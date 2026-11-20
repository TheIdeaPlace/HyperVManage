using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HyperVManage.Services;
using HyperVManage.ViewModels;
using HyperVManage.Views;
using Xunit;

namespace HyperVManage.Tests;

public sealed class IsoHistoryRulesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hvm-isorules-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Iso(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void ANetworkIso_IsOfferedWithoutAskingTheServer_AndOtherFilesAreIgnored()
    {
        var file = Path.Combine(_dir, "recent-isos.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new[]
        {
            @"\\nas-that-is-not-there\isos\win.iso",
            @"C:\Windows\notepad.exe",
        }));
        Assert.Equal([@"\\nas-that-is-not-there\isos\win.iso"], new IsoHistory(file).Recent());
    }

    [Fact]
    public void Remember_LeavesNoTemporaryFileBehind()
    {
        var history = new IsoHistory(Path.Combine(_dir, "settings", "recent-isos.json"));
        history.Remember(Iso("a.iso"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "settings"), "*.tmp"));
    }

    [Fact]
    public void DemoMode_KeepsTheSameRulesAsTheRealList()
    {
        var history = new InMemoryIsoHistory();
        var isos = Enumerable.Range(1, 12).Select(i => Iso($"{i}.iso")).ToList();
        foreach (var iso in isos) history.Remember(iso);
        File.Delete(isos[^1]);
        Assert.Equal(isos.AsEnumerable().Reverse().Skip(1).Take(IsoHistory.MaxRemembered - 1), history.Recent());
    }

    [Theory]
    [InlineData(@"C:\Users\kelly\Downloads\Win11_Arm64.iso", @"Win11_Arm64.iso, in C:\Users\kelly\Downloads")]
    [InlineData(@"\\nas\isos\win.iso", @"win.iso, in \\nas\isos")]
    [InlineData(NewVmViewModel.BrowseChoice, NewVmViewModel.BrowseChoice)]
    [InlineData("typed", "typed")]
    public void ListItems_AreSpokenFileNameFirst(string item, string spoken) =>
        Assert.Equal(spoken, IsoSpokenNameConverter.Speak(item));
}

/// <summary>The Windows ISO list in a real New Virtual Machine window, with the file dialog
/// replaced so each test can count and answer it.</summary>
[Collection("Wpf")]
public class IsoListBehaviorTests
{
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private sealed class Form : IDisposable
    {
        public readonly string Dir = Directory.CreateTempSubdirectory("hvm-isowin-").FullName;
        public readonly string A, B;
        public readonly NewVmViewModel Vm;
        public readonly NewVmWindow Window;
        public readonly ComboBox Box;
        public int Picks;
        public string? Answer;

        public Form()
        {
            TestApp.Ensure();
            A = Path.Combine(Dir, "a.iso");
            B = Path.Combine(Dir, "b.iso");
            File.WriteAllText(A, "");
            File.WriteAllText(B, "");
            var history = new InMemoryIsoHistory();
            history.Remember(A);
            history.Remember(B);
            Vm = new NewVmViewModel([], history);
            Window = new NewVmWindow(Vm)
            {
                ShowActivated = true, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            };
            Window.PickIso = () => { Picks++; return Answer; };
            Window.Show();
            Pump();
            Box = (ComboBox)Window.FindName("IsoBox");
        }

        public int BrowseIndex => Box.Items.Count - 1;

        /// <summary>The box's own text field, where a real key press starts: from there the key
        /// tunnels through the window and the list before anything else sees it.</summary>
        public TextBox Edit => (TextBox)Box.Template.FindName("PART_EditableTextBox", Box);

        public void Press(Key key)
        {
            Window.Activate();
            Edit.Focus();
            Pump();
            var preview = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Edit)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Edit.RaiseEvent(preview);
            if (!preview.Handled)
                Edit.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Edit)!, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
            Pump();
        }

        public void Enter() => Press(Key.Enter);

        public void Dispose()
        {
            Window.Close();
            Directory.Delete(Dir, recursive: true);
        }
    }

    [StaFact]
    public void TheShownIso_IsTheSelectedEntry_SoArrowsMoveFromIt()
    {
        using var f = new Form();
        Assert.Equal(f.B, f.Vm.IsoPath);
        Assert.Equal(0, f.Box.SelectedIndex);
        f.Vm.IsoPath = f.A;
        Pump();
        Assert.Equal(1, f.Box.SelectedIndex);
    }

    [StaFact]
    public void ATypedPath_IsKept()
    {
        using var f = new Form();
        f.Box.Text = @"D:\isos\other.iso";
        Pump();
        Assert.Equal(@"D:\isos\other.iso", f.Box.Text);
        Assert.Equal(@"D:\isos\other.iso", f.Vm.IsoPath);
    }

    [StaFact]
    public void EnterOnBrowse_OpensTheDialogOnce_AndCancelPutsBackTheIso()
    {
        using var f = new Form();
        f.Box.SelectedIndex = f.BrowseIndex; // arrowed onto in the closed box
        Pump();
        Assert.Equal(NewVmViewModel.BrowseChoice, f.Vm.IsoPath);
        Assert.Equal(0, f.Picks);
        f.Enter();
        Assert.Equal(1, f.Picks);
        Assert.Equal(f.B, f.Vm.IsoPath);
        Assert.Equal(0, f.Box.SelectedIndex);

        // Opening and closing the list afterwards never brings the dialog back.
        f.Box.IsDropDownOpen = true;
        Pump();
        f.Box.IsDropDownOpen = false;
        Pump();
        Assert.Equal(1, f.Picks);
    }

    [StaFact]
    public void ClosingTheListOnBrowse_WithoutChoosingIt_PutsBackTheIso()
    {
        using var f = new Form();
        f.Box.IsDropDownOpen = true;
        Pump();
        f.Box.SelectedIndex = f.BrowseIndex;
        Pump();
        f.Box.IsDropDownOpen = false; // Escape, Tab or Alt+Up
        Pump();
        Assert.Equal(0, f.Picks);
        Assert.Equal(f.B, f.Vm.IsoPath);
        Assert.Equal(0, f.Box.SelectedIndex);
    }

    [StaFact]
    public void EnterOnBrowseInTheOpenList_ChoosesAFile_WhichJoinsTheListAndIsSelected()
    {
        using var f = new Form();
        var c = Path.Combine(f.Dir, "c.iso");
        File.WriteAllText(c, "");
        f.Answer = c;
        f.Box.IsDropDownOpen = true;
        Pump();
        f.Box.SelectedIndex = f.BrowseIndex;
        Pump();
        f.Enter();
        Assert.Equal(1, f.Picks);
        Assert.False(f.Box.IsDropDownOpen);
        Assert.Equal(c, f.Vm.IsoPath);
        Assert.Equal(f.Vm.IsoChoices.IndexOf(c), f.Box.SelectedIndex);
        Assert.Equal(NewVmViewModel.BrowseChoice, f.Vm.IsoChoices[^1]);
    }

    [StaFact]
    public void ArrowingThroughTheOpenList_ThenClosingOnBrowse_PutsBackTheIsoFromBeforeItOpened()
    {
        using var f = new Form();
        f.Box.IsDropDownOpen = true;
        Pump();
        f.Box.SelectedIndex = 1; // A
        Pump();
        f.Box.SelectedIndex = f.BrowseIndex;
        Pump();
        f.Box.IsDropDownOpen = false;
        Pump();
        Assert.Equal(f.B, f.Vm.IsoPath);
    }

    [StaFact]
    public void EscapeInTheOpenList_PutsBackTheIsoFromBeforeItOpened()
    {
        using var f = new Form();
        f.Box.IsDropDownOpen = true;
        Pump();
        f.Box.SelectedIndex = 1; // A
        Pump();
        f.Press(Key.Escape);
        Assert.False(f.Box.IsDropDownOpen);
        Assert.True(f.Window.IsVisible);
        Assert.Equal(f.B, f.Vm.IsoPath);
        Assert.Equal(0, f.Picks);
    }

    [StaFact]
    public void EnterOnBrowseInTheOpenList_CancelPutsBackTheIsoFromBeforeItOpened()
    {
        using var f = new Form();
        f.Box.IsDropDownOpen = true;
        Pump();
        f.Box.SelectedIndex = 1;
        Pump();
        f.Box.SelectedIndex = f.BrowseIndex;
        Pump();
        f.Enter();
        Assert.Equal(1, f.Picks);
        Assert.Equal(f.B, f.Vm.IsoPath);
    }

    [StaFact]
    public void LeavingBrowseInTheClosedBox_PutsBackTheIso()
    {
        using var f = new Form();
        f.Window.Activate();
        f.Edit.Focus();
        Pump();
        f.Box.SelectedIndex = f.BrowseIndex; // arrowed onto
        Pump();
        ((TextBox)f.Window.FindName("EditionBox")).Focus(); // Tab away
        Pump();
        Assert.Equal(0, f.Picks);
        Assert.Equal(f.B, f.Vm.IsoPath);
    }

    [StaFact]
    public void ATypedPathInAnotherCase_SelectsItsEntry()
    {
        using var f = new Form();
        f.Box.Text = f.A.ToUpperInvariant();
        Pump();
        Assert.Equal(1, f.Box.SelectedIndex);
    }

    [StaFact]
    public void EnterOnAnIso_IsLeftToTheForm()
    {
        using var f = new Form();
        f.Enter();
        Assert.Equal(0, f.Picks);
    }

    [StaFact]
    public void TheIsoBox_HasNoHelpText()
    {
        using var f = new Form();
        Assert.Equal("", System.Windows.Automation.AutomationProperties.GetHelpText(f.Box));
    }
}
