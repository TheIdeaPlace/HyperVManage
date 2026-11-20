using System.IO;
using System.Xml;
using HyperVManage.Services;
using HyperVManage.ViewModels;
using Xunit;

namespace HyperVManage.Tests;

/// <summary>Windows Server ISOs: the Edition list read from the ISO, and the extra answers the
/// build gives Server so it sets itself up without stopping.</summary>
public sealed class ServerEditionTests : IDisposable
{
    private static readonly string[] Win11 =
        ["Windows 11 Home", "Windows 11 Home N", "Windows 11 Education", "Windows 11 Pro", "Windows 11 Pro N"];

    private static readonly string[] Server2025 =
    [
        "Windows Server 2025 Standard Evaluation",
        "Windows Server 2025 Standard Evaluation (Desktop Experience)",
        "Windows Server 2025 Datacenter Evaluation",
        "Windows Server 2025 Datacenter Evaluation (Desktop Experience)",
    ];

    private readonly string _dir = Directory.CreateTempSubdirectory("hvm-editions-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Iso(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "");
        return path;
    }

    // --- Choosing an edition --------------------------------------------------------------

    [Fact]
    public void Choose_KeepsTheCurrentEdition_InTheIsosOwnSpelling()
    {
        Assert.Equal("Windows 11 Education", WindowsEditions.Choose(Win11, "windows 11 education "));
    }

    [Fact]
    public void Choose_PrefersPro_OnAWindows11Iso()
    {
        Assert.Equal("Windows 11 Pro", WindowsEditions.Choose(Win11, "Something else"));
    }

    [Fact]
    public void Choose_PrefersDatacenterWithTheDesktop_OnAServerIso()
    {
        // Datacenter can run any number of VMs under Hyper-V; Core has no desktop or Narrator.
        Assert.Equal("Windows Server 2025 Datacenter Evaluation (Desktop Experience)",
            WindowsEditions.Choose(Server2025, "Windows 11 Pro"));
        Assert.Equal("Windows Server 2025 Standard Evaluation (Desktop Experience)",
            WindowsEditions.Choose([Server2025[0], Server2025[1]], "Windows 11 Pro"));
    }

    [Fact]
    public void Choose_AvoidsHome_UnlessThatsAllThereIs()
    {
        Assert.Equal("Windows 11 Education", WindowsEditions.Choose(["Windows 11 Home", "Windows 11 Education"], ""));
        Assert.Equal("Windows 11 Home", WindowsEditions.Choose(["Windows 11 Home"], ""));
        Assert.Null(WindowsEditions.Choose([], "Windows 11 Pro"));
    }

    [Theory]
    [InlineData("Windows 11 Pro", false, false)]
    [InlineData("Windows Server 2025 Datacenter Evaluation", true, true)]
    [InlineData("Windows Server 2025 Datacenter Evaluation (Desktop Experience)", true, false)]
    public void ServerAndCore_AreToldApartByName(string edition, bool server, bool core)
    {
        Assert.Equal(server, WindowsEditions.IsServer(edition));
        Assert.Equal(core, WindowsEditions.IsServerCore(edition));
    }

    [Fact]
    public void Parse_TakesOneNamePerLine_SkippingBlanksAndRepeats()
    {
        Assert.Equal(["Windows 11 Home", "Windows 11 Pro"],
            WindowsEditions.Parse("Windows 11 Home\r\n\r\nWindows 11 Pro\r\nwindows 11 pro\r\n"));
    }

    // --- Reading an image ------------------------------------------------------------------

    /// <summary>A .wim with no files in it, only the header and the description of its images,
    /// laid out as a real one is.</summary>
    private string FakeWim(string imagesXml, string magic = "MSWIM\0\0\0")
    {
        var xml = System.Text.Encoding.Unicode.GetPreamble()
            .Concat(System.Text.Encoding.Unicode.GetBytes($"<WIM><TOTALBYTES>0</TOTALBYTES>{imagesXml}</WIM>")).ToArray();
        const int offset = 208;
        var bytes = new byte[offset + xml.Length];
        System.Text.Encoding.ASCII.GetBytes(magic).CopyTo(bytes, 0);
        // The size's top byte is the resource's flags; they must not be read as part of the size.
        BitConverter.GetBytes((long)xml.Length | (0x02L << 56)).CopyTo(bytes, 72);
        BitConverter.GetBytes((long)offset).CopyTo(bytes, 80);
        xml.CopyTo(bytes, offset);
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.wim");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static Task<string> RunGetWimImages(string wim) => PowerShellRunner.RunAsync(
        WindowsEditions.ReadNamesFunction + $"\nGet-WimImages {Ps.Quote(wim)} | ForEach-Object {{ \"$($_.Index)|$($_.Name)|$($_.DisplayName)|$($_.Type)\" }}",
        TestContext.Current.CancellationToken);

    [Fact]
    public async Task GetWimImages_GivesEachImagesNames_InIndexOrder_FallingBackToTheNameForDisplay()
    {
        var wim = FakeWim("""
            <IMAGE INDEX="2"><NAME>Windows Server 2025 SERVERDATACENTER</NAME><DISPLAYNAME>Windows Server 2025 Datacenter Evaluation (Desktop Experience)</DISPLAYNAME><WINDOWS><INSTALLATIONTYPE>Server</INSTALLATIONTYPE></WINDOWS></IMAGE>
            <IMAGE INDEX="1"><NAME>Windows Server 2025 SERVERDATACENTERCORE</NAME><DISPLAYNAME>Windows Server 2025 Datacenter Evaluation</DISPLAYNAME><WINDOWS><INSTALLATIONTYPE>Server Core</INSTALLATIONTYPE></WINDOWS></IMAGE>
            <IMAGE INDEX="10"><NAME>Windows 11 Pro</NAME><WINDOWS><INSTALLATIONTYPE>Client</INSTALLATIONTYPE></WINDOWS></IMAGE>
            """);
        var lines = (await RunGetWimImages(wim)).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
        [
            "1|Windows Server 2025 SERVERDATACENTERCORE|Windows Server 2025 Datacenter Evaluation|Server Core",
            "2|Windows Server 2025 SERVERDATACENTER|Windows Server 2025 Datacenter Evaluation (Desktop Experience)|Server",
            "10|Windows 11 Pro|Windows 11 Pro|Client",
        ], lines);
    }

    [Fact]
    public async Task GetWimImages_RefusesAFileThatIsntAWindowsImage()
    {
        var notWim = FakeWim("<IMAGE INDEX=\"1\"><NAME>x</NAME></IMAGE>", magic: "NOTAWIM!");
        var ex = await Assert.ThrowsAsync<HyperVException>(() => RunGetWimImages(notWim));
        Assert.Contains("isn't a Windows image", ex.Message);
    }

    /// <summary>Set HVM_TEST_ISO to a Windows ISO to read its real editions the way the app does:
    /// mounted, read and dismounted again.</summary>
    [Fact]
    public async Task ARealIso_GivesItsEditions()
    {
        var iso = Environment.GetEnvironmentVariable("HVM_TEST_ISO");
        if (string.IsNullOrEmpty(iso) || !File.Exists(iso)) Assert.Skip("Set HVM_TEST_ISO to a Windows ISO to run this.");
        var editions = await WindowsEditions.ReadAsync(iso, TestContext.Current.CancellationToken);
        Assert.NotEmpty(editions);
        Assert.NotNull(WindowsEditions.Choose(editions, ""));
        Assert.False((await PowerShellRunner.RunAsync($"(Get-DiskImage -ImagePath {Ps.Quote(iso)}).Attached",
            TestContext.Current.CancellationToken)).Trim().Equals("True", StringComparison.OrdinalIgnoreCase), "The ISO was left mounted.");
        TestContext.Current.SendDiagnosticMessage(string.Join("; ", editions));
    }

    [Fact]
    public void TheFunction_IsTakenFromTheScriptTheAppCarries()
    {
        var function = WindowsEditions.ReadNamesFunction;
        Assert.StartsWith("# --- Get-WimImages (begin)", function);
        Assert.Contains("function Get-WimImages([string]$Path)", function);
        Assert.Throws<HyperVException>(() => WindowsEditions.FunctionFrom("no function here"));
    }

    [Fact]
    public void TheBuild_AcceptsEitherOfAServerImagesNames()
    {
        var script = File.ReadAllText(Path.Combine(ScriptBuildingTests.RepoRoot(), "hyperv-rdp-vm", "New-HyperVRdpVM.ps1"));
        Assert.Contains("$_.Name -eq $Edition -or $_.DisplayName -eq $Edition", script);
        // Defined before the Windows build uses it.
        Assert.True(script.IndexOf("function Get-WimImages", StringComparison.Ordinal)
                    < script.IndexOf("Get-WimImages $wim", StringComparison.Ordinal));
    }

    // --- The New VM form -------------------------------------------------------------------

    private sealed class FakeReader(Func<string, IReadOnlyList<string>> answer)
    {
        public List<string> Read { get; } = [];

        public Task<IReadOnlyList<string>> ReadAsync(string path, CancellationToken ct)
        {
            Read.Add(path);
            return Task.FromResult(answer(path));
        }
    }

    private (NewVmViewModel vm, string win, string server, FakeReader reader, List<string> said) Form(string[]? taken = null)
    {
        var win = Iso("win11.iso");
        var server = Iso("server.iso");
        var history = new InMemoryIsoHistory();
        history.Remember(server);
        history.Remember(win); // most recent, so chosen when the form opens
        var vm = new NewVmViewModel(taken ?? [], history) { EditionReadDelay = TimeSpan.Zero };
        var said = new List<string>();
        vm.Announce += said.Add;
        var reader = new FakeReader(p => p == server ? Server2025 : Win11);
        vm.ReadEditions = reader.ReadAsync;
        return (vm, win, server, reader, said);
    }

    [Fact]
    public async Task TheIsoChosenWhenTheFormOpens_IsRead_Quietly()
    {
        var (vm, win, _, reader, said) = Form();
        await vm.EditionReadTask;
        Assert.Equal([win], reader.Read);
        Assert.Equal(Win11, vm.EditionChoices);
        Assert.Equal("Windows 11 Pro", vm.Edition);
        Assert.Empty(said);
    }

    [Fact]
    public async Task ChoosingAServerIso_ChoosesItsDesktopEdition_SaysSo_AndRenamesTheSuggestedVm()
    {
        var (vm, _, server, _, said) = Form();
        await vm.EditionReadTask;
        vm.IsoPath = server;
        await vm.EditionReadTask;
        Assert.Equal(Server2025, vm.EditionChoices);
        Assert.Equal("Windows Server 2025 Datacenter Evaluation (Desktop Experience)", vm.Edition);
        Assert.Equal(["Edition: Windows Server 2025 Datacenter Evaluation (Desktop Experience)"], said);
        Assert.Equal($"{Environment.MachineName}-Server", vm.VmName);
        Assert.NotNull(vm.Validate());
    }

    [Fact]
    public async Task GoingBackToTheWindows11Iso_GoesBackToWin11_FromWhatWasAlreadyRead()
    {
        var (vm, win, server, reader, _) = Form();
        await vm.EditionReadTask;
        vm.IsoPath = server;
        await vm.EditionReadTask;
        vm.IsoPath = win;
        await vm.EditionReadTask;
        Assert.Equal("Windows 11 Pro", vm.Edition);
        Assert.Equal($"{Environment.MachineName}-Win11", vm.VmName);
        Assert.Equal(2, reader.Read.Count); // each ISO mounted once
    }

    [Fact]
    public async Task ANameTheUserTyped_IsLeftAlone()
    {
        var (vm, _, server, _, _) = Form();
        await vm.EditionReadTask;
        vm.VmName = "Stadium-Test";
        vm.IsoPath = server;
        await vm.EditionReadTask;
        Assert.Equal("Stadium-Test", vm.VmName);
    }

    [Fact]
    public async Task TheServerName_SkipsOneInUse()
    {
        var (vm, _, server, _, _) = Form([$"{Environment.MachineName}-Server"]);
        await vm.EditionReadTask;
        vm.IsoPath = server;
        await vm.EditionReadTask;
        Assert.Equal($"{Environment.MachineName}-Server-2", vm.VmName);
    }

    [Fact]
    public async Task AnEditionTheIsoDoesntHave_IsRefusedBeforeTheBuild()
    {
        var (vm, _, _, _, _) = Form();
        await vm.EditionReadTask;
        vm.Edition = "Windows 11 Pro for Workstations";
        Assert.Null(vm.Validate());
        Assert.Contains("no edition called Windows 11 Pro for Workstations", vm.Error);
        vm.Edition = "";
        Assert.Null(vm.Validate());
        Assert.Contains("Choose an edition", vm.Error);
    }

    [Fact]
    public async Task AnIsoThatCantBeRead_LeavesTheEditionToType()
    {
        var bad = Iso("not-windows.iso");
        var history = new InMemoryIsoHistory();
        history.Remember(bad);
        var vm = new NewVmViewModel([], history) { EditionReadDelay = TimeSpan.Zero, Edition = "Windows 11 Enterprise" };
        vm.ReadEditions = (_, _) => throw new HyperVException("not a Windows install ISO");
        await vm.EditionReadTask;
        Assert.Empty(vm.EditionChoices);
        Assert.Equal("Windows 11 Enterprise", vm.Edition);
        Assert.NotNull(vm.Validate()); // the build is left to say what's wrong
    }

    [Fact]
    public async Task ArrowingPastIsos_ReadsOnlyTheOneLeftChosen()
    {
        var (vm, win, server, reader, _) = Form();
        vm.EditionReadDelay = TimeSpan.FromMilliseconds(200);
        vm.IsoPath = server;
        vm.IsoPath = win;
        vm.IsoPath = server;
        await vm.EditionReadTask;
        Assert.Equal([win, server], reader.Read); // the first is the one read as the form opened
    }

    [Fact]
    public void Ubuntu_ReadsNoEditions()
    {
        var vm = new NewVmViewModel([], ubuntu: true);
        var reads = 0;
        vm.ReadEditions = (_, _) => { reads++; return Task.FromResult<IReadOnlyList<string>>(Server2025); };
        Assert.Equal(0, reads);
        Assert.StartsWith($"{Environment.MachineName}-Ubuntu", vm.VmName);
    }

    // --- The answer file -------------------------------------------------------------------

    /// <summary>Runs the script's own answer-file code, as the build does once Windows is copied
    /// onto the disk, and returns the answer file and the folder it wrote setup scripts to.</summary>
    private async Task<(XmlDocument xml, string scripts)> AnswerFile(string installationType)
    {
        var script = File.ReadAllText(Path.Combine(ScriptBuildingTests.RepoRoot(), "hyperv-rdp-vm", "New-HyperVRdpVM.ps1")).Replace("\r\n", "\n");
        var start = script.IndexOf("    $serverSpecialize = ''", StringComparison.Ordinal);
        var end = script.IndexOf("    [xml]$unattend | Out-Null", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "The answer file code has moved.");
        var scripts = Path.Combine(_dir, installationType.Replace(' ', '-'), "Scripts");
        var block = script[start..end].Replace("\"${winLetter}:\\Windows\\Setup\\Scripts\"", Ps.Quote(scripts));
        Assert.Contains(Ps.Quote(scripts), block);

        var run = $$"""
            $isServer = {{Ps.Bool(installationType.StartsWith("Server"))}}
            $isServerCore = {{Ps.Bool(installationType == "Server Core")}}
            $component = 'processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS"'
            $ComputerName = 'STADIUM-Server'; $xTz = 'Central Standard Time'; $xLocale = 'en-US'; $xLanguage = 'en-US'
            $xUser = 'vmuser'; $xPass = 'vm&amp;admin'
            {{block}}
            $unattend
            """;
        var xml = new XmlDocument();
        xml.LoadXml(await PowerShellRunner.RunAsync(run, TestContext.Current.CancellationToken));
        return (xml, scripts);
    }

    private static XmlNamespaceManager Ns(XmlDocument xml)
    {
        var ns = new XmlNamespaceManager(xml.NameTable);
        ns.AddNamespace("u", "urn:schemas-microsoft-com:unattend");
        return ns;
    }

    [Fact]
    public async Task Windows11_GetsNoServerAnswers()
    {
        var (xml, scripts) = await AnswerFile("Client");
        var ns = Ns(xml);
        Assert.Null(xml.SelectSingleNode("//u:AdministratorPassword", ns));
        Assert.Null(xml.SelectSingleNode("//u:component[@name='Microsoft-Windows-ServerManager-SvrMgrNc']", ns));
        Assert.Equal(5, xml.SelectNodes("//u:RunSynchronousCommand", ns)!.Count);
        Assert.False(Directory.Exists(scripts));
        Assert.NotNull(xml.SelectSingleNode("//u:LocalAccount/u:Name[.='vmuser']", ns));
    }

    [Fact]
    public async Task Server_GetsTheAdministratorPassword_SimplePasswordsAndNoServerManager()
    {
        var (xml, scripts) = await AnswerFile("Server");
        var ns = Ns(xml);
        // Before LocalAccounts, as the answer file schema orders them, and the same password.
        var admin = xml.SelectSingleNode("//u:UserAccounts/u:AdministratorPassword", ns)!;
        Assert.Equal("vm&admin", admin.SelectSingleNode("u:Value", ns)!.InnerText);
        Assert.Equal("LocalAccounts", admin.NextSibling!.LocalName);
        Assert.Equal("true", xml.SelectSingleNode(
            "//u:settings[@pass='specialize']/u:component[@name='Microsoft-Windows-ServerManager-SvrMgrNc']/u:DoNotOpenServerManagerAtLogon", ns)!.InnerText);

        var run = xml.SelectSingleNode("//u:RunSynchronousCommand[u:Order='6']/u:Path", ns)!.InnerText;
        Assert.Contains(@"C:\Windows\Setup\Scripts\HyperVManage-PasswordRules.ps1", run);
        Assert.True(run.Length < 260, "Windows Setup ignores a RunSynchronous path longer than 259 characters.");
        var rules = File.ReadAllText(Path.Combine(scripts, "HyperVManage-PasswordRules.ps1"));
        Assert.Contains("PasswordComplexity = 0", rules);
        Assert.Contains("/minpwlen:0", rules);
        await AssertParses(rules);
    }

    [Fact]
    public async Task ServerCore_GetsTheServerAnswers_ButNotServerManager_WhichItDoesntHave()
    {
        var (xml, _) = await AnswerFile("Server Core");
        var ns = Ns(xml);
        Assert.NotNull(xml.SelectSingleNode("//u:AdministratorPassword", ns));
        Assert.NotNull(xml.SelectSingleNode("//u:RunSynchronousCommand[u:Order='6']", ns));
        Assert.Null(xml.SelectSingleNode("//u:component[@name='Microsoft-Windows-ServerManager-SvrMgrNc']", ns));
    }

    [Fact]
    public async Task ThePasswordRule_IsTurnedOff_InWhatSeceditExports()
    {
        // The same replacement the setup script makes, on the lines secedit writes.
        var rules = File.ReadAllText(Path.Combine(ScriptBuildingTests.RepoRoot(), "hyperv-rdp-vm", "New-HyperVRdpVM.ps1"));
        Assert.Contains(@"-replace '^\s*PasswordComplexity\s*=.*$', 'PasswordComplexity = 0'", rules);
        var result = await PowerShellRunner.RunAsync("""
            @('[System Access]', 'MinimumPasswordAge = 0', 'PasswordComplexity = 1', 'ClearTextPassword = 0') -replace '^\s*PasswordComplexity\s*=.*$', 'PasswordComplexity = 0'
            """, TestContext.Current.CancellationToken);
        Assert.Contains("PasswordComplexity = 0", result);
        Assert.DoesNotContain("PasswordComplexity = 1", result);
        Assert.Contains("ClearTextPassword = 0", result);
    }

    [Fact]
    public void TheAudioServices_AreTurnedOnOnlyWhereTheyExist()
    {
        // Server has both off; Server Core has neither, and an error in the guest step would be
        // taken as "not ready yet" until the build timed out.
        var script = File.ReadAllText(Path.Combine(ScriptBuildingTests.RepoRoot(), "hyperv-rdp-vm", "New-HyperVRdpVM.ps1"));
        Assert.Contains("foreach ($audio in 'AudioEndpointBuilder', 'Audiosrv')", script);
        Assert.Contains("if (Get-Service -Name $audio -ErrorAction SilentlyContinue)", script);
        Assert.DoesNotContain("Set-Service -Name Audiosrv -StartupType Automatic\n", script.Replace("\r\n", "\n"));
    }

    private static async Task AssertParses(string script)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hvm-parse-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(path, script);
        try
        {
            var errors = await PowerShellRunner.RunAsync(
                $"$e = $null; [void][Management.Automation.Language.Parser]::ParseFile({Ps.Quote(path)}, [ref]$null, [ref]$e); $e.Count",
                TestContext.Current.CancellationToken);
            Assert.Equal("0", errors.Trim());
        }
        finally { File.Delete(path); }
    }
}
