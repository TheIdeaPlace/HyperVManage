using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HyperVManage.Services;
using HyperVManage.ViewModels;
using HyperVManage.Views;
using Xunit;
using static HyperVManage.Tests.ServerEditionTests;

namespace HyperVManage.Tests;

/// <summary>The Edition list in New Windows Virtual Machine: read from the chosen ISO, without
/// talking over the user, changing things after Create, or leaving ISOs mounted.</summary>
public sealed class ServerEditionFormTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hvm-editionform-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Iso(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "");
        return path;
    }

    /// <summary>Answers at once, or, for an ISO named in <see cref="Hold"/>, when the test says.</summary>
    private sealed class FakeReader(Func<string, IReadOnlyList<WindowsEdition>> answer)
    {
        public List<string> Read { get; } = [];
        public Dictionary<string, TaskCompletionSource<IReadOnlyList<WindowsEdition>>> Held { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Hold(string path) => Held[path] = new TaskCompletionSource<IReadOnlyList<WindowsEdition>>();

        public void Release(string path) => Held[path].SetResult(answer(path));

        public Task<IReadOnlyList<WindowsEdition>> ReadAsync(string path)
        {
            Read.Add(path);
            return Held.TryGetValue(path, out var held) ? held.Task : Task.FromResult(answer(path));
        }
    }

    private sealed record Setup(NewVmViewModel Vm, string Win, string Server, string Win2, FakeReader Reader, List<string> Said);

    private Setup Form(string[]? taken = null, Action<FakeReader>? beforeReading = null)
    {
        var win = Iso("win11.iso");
        var win2 = Iso("win11-other.iso");
        var server = Iso("server.iso");
        var history = new InMemoryIsoHistory();
        history.Remember(win2);
        history.Remember(server);
        history.Remember(win); // most recent, so chosen when the form opens
        var vm = new NewVmViewModel(taken ?? [], history) { EditionReadDelay = TimeSpan.Zero };
        var said = new List<string>();
        vm.Announce += said.Add;
        var reader = new FakeReader(p => p == server ? Server2025 : Win11);
        beforeReading?.Invoke(reader);
        vm.ReadEditions = reader.ReadAsync;
        return new Setup(vm, win, server, win2, reader, said);
    }

    [Fact]
    public async Task TheIsoChosenWhenTheFormOpens_IsRead_Quietly()
    {
        var f = Form();
        await f.Vm.EditionReadTask;
        Assert.Equal([f.Win], f.Reader.Read);
        Assert.Equal(Shown(Win11), f.Vm.EditionChoices);
        Assert.Equal("Windows 11 Pro", f.Vm.Edition);
        Assert.Empty(f.Said);
    }

    [Fact]
    public async Task ChoosingAServerIso_ChoosesItsDesktopEdition_SaysSo_AndRenamesTheSuggestedVm()
    {
        var f = Form();
        var suggested = new List<string>();
        f.Vm.NameSuggested += suggested.Add;
        await f.Vm.EditionReadTask;
        f.Vm.IsoPath = f.Server;
        await f.Vm.EditionReadTask;
        Assert.Equal(Shown(Server2025), f.Vm.EditionChoices);
        Assert.Equal("Windows Server 2025 Datacenter Evaluation (Desktop Experience)", f.Vm.Edition);
        Assert.Equal(["Edition: Windows Server 2025 Datacenter Evaluation (Desktop Experience)"], f.Said);
        Assert.Equal($"{Environment.MachineName}-Server", f.Vm.VmName);
        Assert.Equal([f.Vm.VmName], suggested);
        Assert.NotNull(f.Vm.Validate());
    }

    [Fact]
    public async Task AnotherIsoWithTheSameEditions_SaysNothing()
    {
        var f = Form();
        await f.Vm.EditionReadTask;
        f.Vm.IsoPath = f.Win2;
        await f.Vm.EditionReadTask;
        Assert.Equal("Windows 11 Pro", f.Vm.Edition);
        Assert.Empty(f.Said);
    }

    [Fact]
    public async Task GoingBackToTheWindows11Iso_GoesBackToWin11_FromWhatWasAlreadyRead()
    {
        var f = Form();
        await f.Vm.EditionReadTask;
        f.Vm.IsoPath = f.Server;
        await f.Vm.EditionReadTask;
        f.Vm.IsoPath = f.Win;
        await f.Vm.EditionReadTask;
        Assert.Equal("Windows 11 Pro", f.Vm.Edition);
        Assert.Equal($"{Environment.MachineName}-Win11", f.Vm.VmName);
        Assert.Equal(2, f.Reader.Read.Count); // each ISO mounted once
    }

    [Fact]
    public async Task ANameTheUserTyped_IsLeftAlone()
    {
        var f = Form();
        await f.Vm.EditionReadTask;
        f.Vm.VmName = "Stadium-Test";
        f.Vm.IsoPath = f.Server;
        await f.Vm.EditionReadTask;
        Assert.Equal("Stadium-Test", f.Vm.VmName);
    }

    [Fact]
    public async Task TheServerName_SkipsOneInUse()
    {
        var f = Form([$"{Environment.MachineName}-Server"]);
        await f.Vm.EditionReadTask;
        f.Vm.IsoPath = f.Server;
        await f.Vm.EditionReadTask;
        Assert.Equal($"{Environment.MachineName}-Server-2", f.Vm.VmName);
    }

    [Fact]
    public async Task AnEditionTheIsoDoesntHave_IsRefused_ButEitherNameOfOneItHasIsnt()
    {
        var f = Form();
        await f.Vm.EditionReadTask;
        f.Vm.Edition = "Windows 11 Pro for Workstations";
        Assert.Null(f.Vm.Validate());
        Assert.Contains("no edition called Windows 11 Pro for Workstations", f.Vm.Error);
        f.Vm.Edition = "";
        Assert.Null(f.Vm.Validate());
        Assert.Contains("Choose an edition", f.Vm.Error);

        f.Vm.IsoPath = f.Server;
        await f.Vm.EditionReadTask;
        f.Vm.Edition = "Windows Server 2025 SERVERSTANDARD"; // the image's own name, which the script accepts
        Assert.NotNull(f.Vm.Validate());
    }

    [Fact]
    public async Task AnIsoThatCantBeRead_LeavesTheEditionToType_AndOffersNoOtherIsosEditions()
    {
        var f = Form();
        await f.Vm.EditionReadTask;
        var bad = Iso("not-windows.iso");
        f.Vm.ReadEditions = _ => throw new HyperVException("not a Windows install ISO");
        f.Vm.Edition = "Windows 11 Enterprise";
        f.Vm.ChooseBrowsedIso(bad);
        await f.Vm.EditionReadTask;
        Assert.Empty(f.Vm.EditionChoices);
        Assert.Equal("Windows 11 Enterprise", f.Vm.Edition);
        Assert.NotNull(f.Vm.Validate()); // the build is left to say what's wrong
    }

    [Fact]
    public async Task AnIsoWithNoEditions_LeavesTheEditionAsItWas()
    {
        var f = Form();
        await f.Vm.EditionReadTask;
        f.Vm.ReadEditions = _ => Task.FromResult<IReadOnlyList<WindowsEdition>>([]);
        f.Vm.IsoPath = f.Win2;
        await f.Vm.EditionReadTask;
        Assert.Equal("Windows 11 Pro", f.Vm.Edition);
        Assert.Empty(f.Vm.EditionChoices);
    }

    [Fact]
    public async Task ArrowingPastIsos_ReadsOnlyTheOneLeftChosen()
    {
        var f = Form();
        await f.Vm.EditionReadTask;
        f.Vm.EditionReadDelay = TimeSpan.FromMilliseconds(200);
        f.Vm.IsoPath = f.Server;
        f.Vm.IsoPath = f.Win2;
        f.Vm.IsoPath = f.Server;
        await f.Vm.EditionReadTask;
        Assert.Equal([f.Win, f.Server], f.Reader.Read); // the first is the one read as the form opened
    }

    [Fact]
    public async Task WhileTheIsoListIsOpen_NothingIsReadOrSaid_ThenTheIsoLeftChosenIs()
    {
        var f = Form();
        await f.Vm.EditionReadTask;
        f.Vm.HoldEditionReads = true;
        f.Vm.IsoPath = f.Server;
        f.Vm.IsoPath = f.Win2;
        f.Vm.IsoPath = f.Server;
        await f.Vm.EditionReadTask;
        Assert.Equal([f.Win], f.Reader.Read);
        Assert.Empty(f.Said);
        f.Vm.HoldEditionReads = false;
        await f.Vm.EditionReadTask;
        Assert.Equal([f.Win, f.Server], f.Reader.Read);
        Assert.Single(f.Said);
    }

    [Fact]
    public async Task AReadThatLandsAfterAnotherIsoIsChosen_IsKeptForLater_ButNotShown()
    {
        var f = Form();
        await f.Vm.EditionReadTask;
        f.Reader.Hold(f.Server);
        f.Vm.IsoPath = f.Server;
        f.Vm.IsoPath = f.Win;
        f.Reader.Release(f.Server);
        await Task.Yield();
        Assert.Equal("Windows 11 Pro", f.Vm.Edition);
        Assert.Equal(Shown(Win11), f.Vm.EditionChoices);
        Assert.Empty(f.Said);

        f.Vm.IsoPath = f.Server; // from what was read, with no second mount
        Assert.Equal("Windows Server 2025 Datacenter Evaluation (Desktop Experience)", f.Vm.Edition);
        Assert.Equal(2, f.Reader.Read.Count);
    }

    [Fact]
    public async Task Create_WaitsForAReadThatHasTheIsoMounted_AndNothingChangesAfterwards()
    {
        var f = Form();
        await f.Vm.EditionReadTask;
        f.Reader.Hold(f.Server);
        f.Vm.IsoPath = f.Server;
        f.Vm.Edition = "Windows Server 2025 SERVERDATACENTER"; // typed before the list arrived
        var name = f.Vm.VmName;
        var built = new TaskCompletionSource<NewVmOptions>();
        f.Vm.RunScript = (o, _, _) => { built.TrySetResult(o); return Task.FromResult(BuildOutcome.Succeeded); };
        var create = f.Vm.CreateCommand.ExecuteAsync(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(built.Task.IsCompleted, "The build started while the ISO was still mounted for reading.");

        Assert.Contains("Finishing reading the ISO.", f.Said);

        f.Reader.Release(f.Server);
        await create;
        // The same edition, now in the list's own words; the name is what it was.
        const string datacenter = "Windows Server 2025 Datacenter Evaluation (Desktop Experience)";
        Assert.Equal(datacenter, (await built.Task).Edition);
        Assert.Equal(datacenter, f.Vm.Edition);
        Assert.Equal(name, f.Vm.VmName);
        Assert.Equal(Shown(Server2025), f.Vm.EditionChoices);
    }

    [Fact]
    public async Task AReadStillGoingAfterTheWaitLimit_StopsCreate_WithTheIsoUntouched()
    {
        var f = Form();
        await f.Vm.EditionReadTask;
        f.Reader.Hold(f.Server);
        f.Vm.IsoPath = f.Server;
        f.Vm.Edition = Server2025[3].DisplayName;
        f.Vm.EditionReadWaitLimit = TimeSpan.FromMilliseconds(50);
        var built = false;
        f.Vm.RunScript = (_, _, _) => { built = true; return Task.FromResult(BuildOutcome.Succeeded); };
        await f.Vm.CreateCommand.ExecuteAsync(null);
        Assert.False(built);
        Assert.Equal("The ISO is still being read. Press Create again in a moment.", f.Vm.Error);
        Assert.Contains(f.Vm.Error, f.Said);

        // Once it lands, Create goes ahead, with no second read.
        f.Reader.Release(f.Server);
        await f.Vm.CreateCommand.ExecuteAsync(null);
        Assert.True(built);
        Assert.Equal([f.Win, f.Server], f.Reader.Read);
    }

    [Fact]
    public async Task ClosingTheWindowWhileCreateWaits_StartsNoBuild()
    {
        var f = Form();
        await f.Vm.EditionReadTask;
        f.Reader.Hold(f.Server);
        f.Vm.IsoPath = f.Server;
        f.Vm.Edition = Server2025[3].DisplayName;
        var built = false;
        f.Vm.RunScript = (_, _, _) => { built = true; return Task.FromResult(BuildOutcome.Succeeded); };
        var create = f.Vm.CreateCommand.ExecuteAsync(null);
        f.Vm.WindowClosed(); // Escape while it says "Finishing reading the ISO."
        f.Reader.Release(f.Server);
        await create;
        Assert.False(built);
        Assert.False(f.Vm.HasStarted);
    }

    [Fact]
    public async Task AnIsoChosenWhileCreateWaits_IsntShownTheLastOnesEditions()
    {
        var f = Form();
        await f.Vm.EditionReadTask;
        f.Reader.Hold(f.Server);
        f.Vm.IsoPath = f.Server;
        f.Vm.VmName = ""; // so the check stops it
        var create = f.Vm.CreateCommand.ExecuteAsync(null);
        f.Vm.IsoPath = f.Win2; // not read yet
        f.Reader.Release(f.Server);
        await create;
        Assert.DoesNotContain(Server2025[3].DisplayName, f.Vm.EditionChoices);
        await f.Vm.EditionReadTask; // reads go on afterwards, and read it
        Assert.Equal(Shown(Win11), f.Vm.EditionChoices);
    }

    [Fact]
    public async Task CreateDuringARead_ThatFailsTheCheck_LeavesTheListFilled()
    {
        // Enter pressed as the window opens on a Server ISO, before its editions have arrived.
        var f = Form();
        await f.Vm.EditionReadTask;
        f.Reader.Hold(f.Server);
        f.Vm.IsoPath = f.Server;
        var built = false;
        f.Vm.RunScript = (_, _, _) => { built = true; return Task.FromResult(BuildOutcome.Succeeded); };
        var create = f.Vm.CreateCommand.ExecuteAsync(null);
        f.Reader.Release(f.Server);
        await create;
        Assert.False(built);
        Assert.Contains("no edition called Windows 11 Pro. Choose one from the Edition list", f.Vm.Error);
        Assert.Equal(Shown(Server2025), f.Vm.EditionChoices); // there to choose from
        Assert.Equal("Windows 11 Pro", f.Vm.Edition); // not changed at the last moment
    }

    [Fact]
    public async Task CreateBeforeAReadHasStarted_ThatFailsTheCheck_ReadsTheIsoAfterwards()
    {
        var f = Form();
        await f.Vm.EditionReadTask;
        f.Vm.EditionReadDelay = TimeSpan.FromMilliseconds(200);
        f.Vm.IsoPath = f.Server;
        f.Vm.VmName = "";
        await f.Vm.CreateCommand.ExecuteAsync(null);
        Assert.True(f.Vm.HasError);
        await f.Vm.EditionReadTask;
        Assert.Equal([f.Win, f.Server], f.Reader.Read);
        Assert.Equal(Shown(Server2025), f.Vm.EditionChoices);
    }

    [Fact]
    public async Task AnIsoStillBeingReadFromBefore_IsntReadTwice_AndCreateWaitsForEveryRead()
    {
        var f = Form();
        await f.Vm.EditionReadTask;
        f.Reader.Hold(f.Server);
        f.Reader.Hold(f.Win2);
        f.Vm.IsoPath = f.Server; // read starts, and can't be stopped
        f.Vm.IsoPath = f.Win2;   // so does this one
        f.Vm.IsoPath = f.Server; // still being read: not mounted a second time
        Assert.Equal([f.Win, f.Server, f.Win2], f.Reader.Read);
        f.Vm.Edition = Server2025[3].DisplayName;

        var built = new TaskCompletionSource();
        f.Vm.RunScript = (_, _, _) => { built.TrySetResult(); return Task.FromResult(BuildOutcome.Succeeded); };
        var create = f.Vm.CreateCommand.ExecuteAsync(null);
        f.Reader.Release(f.Server);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(built.Task.IsCompleted, "The build started while another ISO was still mounted for reading.");
        f.Reader.Release(f.Win2);
        await create;
        Assert.True(built.Task.IsCompleted);
    }

    [Fact]
    public async Task AReaderThatFailsAtOnce_DoesntStopThatIsoBeingReadLater()
    {
        var f = Form();
        await f.Vm.EditionReadTask;
        var reader = f.Vm.ReadEditions!;
        var fail = true;
        f.Vm.ReadEditions = p => fail ? throw new HyperVException("not yet") : reader(p);
        f.Vm.IsoPath = f.Server;
        await f.Vm.EditionReadTask;
        fail = false;
        f.Vm.IsoPath = f.Win;
        f.Vm.IsoPath = f.Server;
        await f.Vm.EditionReadTask;
        Assert.Equal(Shown(Server2025), f.Vm.EditionChoices);
    }

    [Fact]
    public async Task TheEditionIsSaid_BeforeTheNameThatFollowsIt()
    {
        var f = Form();
        var order = new List<string>();
        f.Vm.Announce += s => order.Add(s);
        f.Vm.NameSuggested += n => order.Add($"name {n}");
        await f.Vm.EditionReadTask;
        f.Vm.IsoPath = f.Server;
        await f.Vm.EditionReadTask;
        Assert.Equal(["Edition: Windows Server 2025 Datacenter Evaluation (Desktop Experience)", $"name {Environment.MachineName}-Server"], order);
    }

    [Fact]
    public async Task HomeIsRefused_ByTheImagesOwnName_InAnyLanguage()
    {
        var french = Iso("fr.iso");
        var history = new InMemoryIsoHistory();
        history.Remember(french);
        var vm = new NewVmViewModel([], history) { EditionReadDelay = TimeSpan.Zero };
        vm.ReadEditions = _ => Task.FromResult<IReadOnlyList<WindowsEdition>>(
            [new("Windows 11 Famille", "Windows 11 Home", "Client"), new("Windows 11 Professionnel", "Windows 11 Pro", "Client")]);
        await vm.EditionReadTask;
        Assert.Equal("Windows 11 Professionnel", vm.Edition);
        vm.Edition = "Windows 11 Famille";
        Assert.Null(vm.Validate());
        Assert.Contains("Home editions can't accept Remote Desktop", vm.Error);
    }

    [Fact]
    public void Ubuntu_ReadsNoEditions()
    {
        var vm = new NewVmViewModel([], ubuntu: true);
        var reads = 0;
        vm.ReadEditions = _ => { reads++; return Task.FromResult<IReadOnlyList<WindowsEdition>>(Server2025); };
        Assert.Equal(0, reads);
        Assert.StartsWith($"{Environment.MachineName}-Ubuntu", vm.VmName);
    }

    // --- In the window ----------------------------------------------------------------------

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private NewVmWindow Show(Setup f)
    {
        TestApp.Ensure();
        var window = new NewVmWindow(f.Vm)
        {
            ShowActivated = true, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
        };
        window.Show();
        Pump();
        return window;
    }

    [StaFact]
    public void RefillingTheList_KeepsTheEditionInTheBox_AndSaysNothingWhenItsTheSame()
    {
        var f = Form();
        var window = Show(f);
        try
        {
            var box = (ComboBox)window.FindName("EditionBox");
            Assert.True(box.IsEditable);
            Assert.Equal("Windows 11 Pro", box.Text);
            f.Vm.IsoPath = f.Win2;
            Pump();
            Assert.Equal("Windows 11 Pro", box.Text);
            Assert.Equal("Windows 11 Pro", f.Vm.Edition);
            Assert.Equal($"{Environment.MachineName}-Win11", f.Vm.VmName);
            Assert.Empty(f.Said);

            f.Vm.IsoPath = f.Server;
            Pump();
            Assert.Equal("Windows Server 2025 Datacenter Evaluation (Desktop Experience)", box.Text);
            Assert.Equal("Windows Server 2025 Datacenter Evaluation (Desktop Experience)", box.SelectedItem);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void WithTheIsoListOpen_TryingIsosReadsNothing_AndClosingItReadsTheOneChosen()
    {
        var f = Form();
        var window = Show(f);
        try
        {
            var isoBox = (ComboBox)window.FindName("IsoBox");
            isoBox.IsDropDownOpen = true;
            Pump();
            f.Vm.IsoPath = f.Server; // arrowed onto in the open list
            Pump();
            Assert.Equal([f.Win], f.Reader.Read);
            Assert.Empty(f.Said);
            isoBox.IsDropDownOpen = false; // Enter: the ISO is chosen
            Pump();
            Assert.Equal([f.Win, f.Server], f.Reader.Read);
            Assert.Equal("Windows Server 2025 Datacenter Evaluation (Desktop Experience)", f.Vm.Edition);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ANameChangedUnderFocus_StaysSelected_SoTypingStillReplacesIt()
    {
        var f = Form();
        var window = Show(f);
        try
        {
            window.Activate();
            var name = (TextBox)window.FindName("NameBox");
            name.Focus();
            name.SelectAll();
            Pump();
            if (!name.IsKeyboardFocused) Assert.Skip("The test window couldn't take keyboard focus here.");
            f.Vm.IsoPath = f.Server;
            Pump();
            Assert.Equal($"{Environment.MachineName}-Server", name.Text);
            Assert.True(name.IsKeyboardFocused);
            Assert.Equal(name.Text.Length, name.SelectionLength);
        }
        finally { window.Close(); }
    }
}
