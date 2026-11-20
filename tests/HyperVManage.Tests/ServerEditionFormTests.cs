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

        f.Reader.Release(f.Server);
        await create;
        // The read landed, but after Create: the edition and name are what the build was given.
        Assert.Equal("Windows Server 2025 SERVERDATACENTER", (await built.Task).Edition);
        Assert.Equal("Windows Server 2025 SERVERDATACENTER", f.Vm.Edition);
        Assert.Equal(name, f.Vm.VmName);
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
            f.Vm.IsoPath = f.Server;
            Pump();
            Assert.Equal($"{Environment.MachineName}-Server", name.Text);
            if (name.IsKeyboardFocused) Assert.Equal(name.Text.Length, name.SelectionLength);
        }
        finally { window.Close(); }
    }
}
