using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HyperVManage.Models;
using HyperVManage.Services;
using HyperVManage.ViewModels;
using Xunit;

namespace HyperVManage.Tests;

/// <summary>New Ubuntu Virtual Machine: what the app asks the script for, and the script's own
/// Ubuntu pieces, run in Windows PowerShell 5.1 as the build runs them.</summary>
public class UbuntuBuildTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Script() =>
        File.ReadAllText(Path.Combine(ScriptBuildingTests.RepoRoot(), "hyperv-rdp-vm", "New-HyperVRdpVM.ps1")).Replace("\r\n", "\n");

    private static NewVmOptions Ubuntu(bool orca = true) =>
        new("Kelly's Ubuntu", "", "", "kelly", "pa'ss", 4, 4, 64, false, true, true, Ubuntu: true, Orca: orca);

    internal static Task<string> RunPsForTests(string script) => RunPs(script);

    /// <summary>Runs PowerShell text in Windows PowerShell 5.1, as the build does, and returns what it printed.</summary>
    private static async Task<string> RunPs(string script, string? workingDirectory = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hvm-test-{Guid.NewGuid():N}.ps1");
        // With a byte order mark, so Windows PowerShell reads it as UTF-8.
        await File.WriteAllTextAsync(path, script, new UTF8Encoding(true), Ct);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(SystemTools.PowerShell)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync(Ct);
            var errors = p.StandardError.ReadToEndAsync(Ct);
            await p.WaitForExitAsync(Ct);
            Assert.True(p.ExitCode == 0, $"PowerShell failed ({p.ExitCode}):\n{await errors}\n{await output}");
            return await output;
        }
        finally { File.Delete(path); }
    }

    /// <summary>PowerShell that compiles the script's own C# helpers, exactly as the build does.</summary>
    private static string CompileHelpers()
    {
        var script = Script();
        const string start = "        Add-Type -TypeDefinition @'\nusing System;\nusing System.Collections.Generic;";
        var at = script.IndexOf(start, StringComparison.Ordinal);
        Assert.True(at >= 0, "The script's C# helpers weren't found.");
        var end = script.IndexOf("\n'@", at, StringComparison.Ordinal);
        return "$OutputEncoding = [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false\n"
            + script[(at + 8)..(end + 3)] + "\n";
    }

    [Fact]
    public void TheCommand_AsksForUbuntu_WithoutAnEditionOrIso_AndPassesNoOrcaOnlyWhenAsked()
    {
        var cmd = NewVmScript.BuildCommand(Ubuntu(), "HyperVManage-test");
        Assert.Contains("& ([scriptblock]::Create($scriptText)) -Ubuntu -VMName 'Kelly''s Ubuntu'", cmd);
        Assert.DoesNotContain("-IsoPath", cmd);
        Assert.DoesNotContain("-Edition", cmd);
        Assert.DoesNotContain("-NoOrca", cmd);
        Assert.Contains("-NoOrca", NewVmScript.BuildCommand(Ubuntu(orca: false), "HyperVManage-test"));

        // A Windows build is as it was.
        var windows = NewVmScript.BuildCommand(Ubuntu() with { Ubuntu = false, Edition = "Windows 11 Pro", Orca = false }, "HyperVManage-test");
        Assert.DoesNotContain("-Ubuntu", windows);
        Assert.DoesNotContain("-NoOrca", windows);
        Assert.Contains("-Edition 'Windows 11 Pro'", windows);
    }

    [Fact]
    public async Task EveryParameterTheAppPasses_IsOneTheScriptTakes()
    {
        var script = Script();
        var names = await RunPs($$"""
            $e = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseInput({{Ps.Quote(script)}}, [ref]$null, [ref]$e)
            $ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath }
            """);
        var taken = names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var options in new[] { Ubuntu(orca: false), Ubuntu() with { Ubuntu = false, HostOnly = true, AutoStart = false, Connect = false, IsoPath = "x.iso", Edition = "e" } })
        {
            var cmd = NewVmScript.BuildCommand(options, "HyperVManage-test");
            var passed = Regex.Matches(cmd[cmd.IndexOf("Create($scriptText))", StringComparison.Ordinal)..], @"(?<=\s)-([A-Za-z]+)").Select(m => m.Groups[1].Value);
            foreach (var p in passed) Assert.Contains(p, taken);
        }
    }

    [Fact]
    public void ThePrecheck_RefusesALeftoverInstallerDisk_AndTheCleanupRemovesOne()
    {
        Assert.Contains("$installer = Join-Path (Get-VMHost).VirtualHardDiskPath \"$name-installer.vhd\"", NewVmScript.BuildPrecheckScript("A", ""));
        Assert.Contains("InstallerExists = [bool](Test-Path -LiteralPath $installer)", NewVmScript.BuildPrecheckScript("A", ""));
        // The script names its installer disk the same way.
        Assert.Contains("$seedPath = Join-Path $VhdFolder \"$VMName-installer.vhd\"", Script());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task TheCleanupAfterAStop_RemovesBothDisks_OnlyIfNoVmWasMade(bool vmExists, bool removed)
    {
        // Hyper-V's cmdlets stood in for, so this runs on any PC; the files are real.
        var folder = Directory.CreateTempSubdirectory("hvm-cleanup-").FullName;
        try
        {
            var vhd = Path.Combine(folder, "Lab.vhdx");
            var installer = Path.Combine(folder, "Lab-installer.vhd");
            File.WriteAllText(vhd, "disk");
            File.WriteAllText(installer, "installer");
            var stubs = $$"""
                function Get-VM { {{(vmExists ? "[pscustomobject]@{ Name = 'Lab' }" : "")}} }
                function Dismount-VHD { param($Path, $ErrorAction) }
                function Dismount-DiskImage { param($ImagePath, $ErrorAction) }

                """;
            var said = await RunPs(stubs + NewVmScript.BuildCleanupScript("Lab", "", vhd, false, installer));
            Assert.Equal(!removed, File.Exists(vhd));
            Assert.Equal(!removed, File.Exists(installer));
            if (removed) Assert.Contains("Removed the installer disk", said);
            else Assert.Contains("had already been created", said);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void TheForm_ForUbuntu_HasUbuntusDefaults_AndNeedsNoIso()
    {
        var n = new NewVmViewModel([], new InMemoryIsoHistory(), ubuntu: true);
        Assert.True(n.IsUbuntu);
        Assert.False(n.IsWindows);
        Assert.Equal("New Ubuntu Virtual Machine", n.Title);
        Assert.EndsWith("-Ubuntu", n.VmName);
        Assert.Equal("64", n.DiskGB);
        Assert.True(n.Orca);
        n.VmName = "Lab";
        n.IsoPath = @"C:\nowhere\not-there.iso"; // ignored: the script gets Ubuntu itself
        var o = n.Validate();
        Assert.NotNull(o);
        Assert.True(o!.Ubuntu);
        Assert.Equal("", o.IsoPath);
        Assert.Equal("", o.Edition);
        Assert.True(o.Orca);

        n.Orca = false;
        Assert.False(n.Validate()!.Orca);

        var windows = new NewVmViewModel([], new InMemoryIsoHistory());
        Assert.Equal("New Windows Virtual Machine", windows.Title);
        Assert.EndsWith("-Win11", windows.VmName);
        Assert.Equal("128", windows.DiskGB);
    }

    [Theory]
    [InlineData("kelly", null)]
    [InlineData("k", null)]
    [InlineData("dev_ops-2", null)]
    [InlineData("Kelly", "lower-case")]
    [InlineData("2kelly", "lower-case")]
    [InlineData("kel ly", "lower-case")]
    [InlineData("kelly.ford", "lower-case")]
    [InlineData("", "lower-case")]
    [InlineData("root", "keeps the name")]
    [InlineData("gdm", "keeps the name")]
    [InlineData("systemd-network", "keeps the name")]
    public void UbuntuUserNames_AreCheckedAsUbuntuChecksThem(string user, string? problem)
    {
        var said = NewVmScript.UbuntuUserNameProblem(user);
        if (problem is null) Assert.Null(said);
        else Assert.Contains(problem, said);
        Assert.Contains("lower-case", NewVmScript.UbuntuUserNameProblem(new string('a', 33)));
        Assert.Null(NewVmScript.UbuntuUserNameProblem(new string('a', 32)));
    }

    [Fact]
    public void TheForm_ForUbuntu_RefusesWhatUbuntuWouldRefuse()
    {
        NewVmViewModel Form() => new([], new InMemoryIsoHistory(), ubuntu: true) { VmName = "Lab", Processors = "1", MemoryGB = "2" };

        var upper = Form(); upper.UserName = "Kelly";
        Assert.Null(upper.Validate());
        Assert.Contains("lower-case", upper.Error);

        var noPassword = Form(); noPassword.Password = "";
        Assert.Null(noPassword.Validate());
        Assert.Contains("password", noPassword.Error);

        var small = Form(); small.DiskGB = "31";
        Assert.Null(small.Validate());
        Assert.Contains("at least 32", small.Error);
        small.DiskGB = "32";
        Assert.NotNull(small.Validate());

        // Windows' own limits don't apply: its 64 GB disk, its Home edition, its user names.
        var ok = Form(); ok.DiskGB = "40"; ok.Edition = "Windows 11 Home"; ok.UserName = "a_long_ubuntu_user_name_ok";
        Assert.NotNull(ok.Validate());
    }

    [Fact]
    public void AnUbuntuVm_StartsWithThePc_OnlyOnceItsInstallerDiskIsGone()
    {
        // Otherwise a PC restarting mid-install would start the installer again, which wipes the disk.
        var script = Script();
        var create = script.IndexOf("    New-BaseVM -Later", StringComparison.Ordinal);
        var removed = script.IndexOf("Remove-Item -LiteralPath $seedPath -Force -ErrorAction Stop", StringComparison.Ordinal);
        var autoStart = script.IndexOf("if (-not $NoAutoStart) { Set-VM -Name $VMName -AutomaticStartAction Start", StringComparison.Ordinal);
        Assert.True(create > 0 && removed > create && autoStart > removed);
        Assert.Contains("if ($Later) {\n        Set-VM -Name $VMName -AutomaticStartAction Nothing", script);
    }

    [Fact]
    public void TheScriptsListOfSystemAccounts_IsTheApps()
    {
        var script = Script();
        var at = script.IndexOf("$systemAccounts = @(", StringComparison.Ordinal);
        var list = script[at..script.IndexOf(')', at)];
        var names = Regex.Matches(list, "'([^']+)'").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(NewVmScript.UbuntuSystemAccounts, names);
        Assert.Contains("$Ubuntu -and $DiskGB -lt 32", script);
        Assert.Equal(32, NewVmScript.UbuntuMinimumDiskGB);
    }

    [Fact]
    public async Task AnUbuntuBuild_DoesntRememberAnIso()
    {
        var history = new InMemoryIsoHistory();
        var n = new NewVmViewModel([], history, ubuntu: true) { VmName = "Lab", Processors = "1", MemoryGB = "2" };
        NewVmOptions? ran = null;
        n.RunScript = (o, _, _) => { ran = o; return Task.FromResult(BuildOutcome.Succeeded); };
        await n.CreateCommand.ExecuteAsync(null);
        Assert.True(ran!.Ubuntu);
        Assert.Empty(history.Recent());
    }

    [Fact]
    public async Task TheDemo_ShowsUbuntusSteps()
    {
        DemoNewVmScript.Pace = TimeSpan.Zero;
        var lines = new List<string>();
        await DemoNewVmScript.RunAsync(Ubuntu(), lines.Add, Ct);
        Assert.Contains("Step 4 of 5: Installing Ubuntu. The VM turns itself off when the install is done.", lines);
        Assert.DoesNotContain(lines, l => l.Contains("Windows", StringComparison.Ordinal));
    }

    // The ISOs' own boot menus, as Ubuntu 26.04.1 ships them for Arm64 and x64, cut short.
    private const string ArmMenu = """
        set timeout=30

        loadfont unicode

        menuentry "Try or Install Ubuntu" {
            set gfxpayload=keep
            linux  /casper/vmlinuz $cmdline  --- quiet splash console=tty0
            initrd /casper/initrd
        }
        """;
    private const string X64Menu = """
        set timeout=30

        menuentry "Try or Install Ubuntu" {
            set gfxpayload=keep
            linux  /casper/vmlinuz  --- quiet splash
            initrd /casper/initrd
        }
        menuentry "Ubuntu (safe graphics)" {
            set gfxpayload=keep
            linux  /casper/vmlinuz nomodeset  --- quiet splash
            initrd /casper/initrd
        }
        """;

    [Theory]
    [InlineData(ArmMenu, 1)]
    [InlineData(X64Menu, 2)]
    public async Task TheBootMenu_StartsTheInstallerUnattended_FromTheIso(string menu, int entries)
    {
        // The script's own lines, run on the menu.
        // The three that change it, not the one that reads it from the ISO.
        var lines = Script().Split('\n').Where(l => Regex.IsMatch(l, @"^\s*\$grub = (\$grub|"")")).ToList();
        Assert.Equal(3, lines.Count);
        var result = await RunPs($"$grub = {Ps.Quote(menu.Replace("\r\n", "\n"))}\n{string.Join("\n", lines)}\n$grub");
        Assert.StartsWith("search --no-floppy --set=root --file /casper/vmlinuz\nset prefix=($root)/boot/grub\n", result.Replace("\r\n", "\n"));
        Assert.Contains("set timeout=1\n", result.Replace("\r\n", "\n"));
        Assert.Equal(entries, Regex.Matches(result, @"linux\s+/casper/vmlinuz autoinstall ds=nocloud ").Count);
    }

    [Fact]
    public async Task GnomeRemoteDesktopsSignIn_IsWrittenAsItsOwnGrdctlWritesIt()
    {
        // grdctl writes a GVariant inside a GKeyFile; checked against what grdctl wrote on Ubuntu
        // 26.04 for the same password. Backslashes are doubled twice, quotes escaped once.
        var lines = Script().Split('\n').Where(l => Regex.IsMatch(l, @"^\s*\$(gvPassword|credentials) = ")).ToList();
        Assert.Equal(2, lines.Count);
        var result = await RunPs("$UserName = 'kelly'\n$Password = 'it''s a \\back slash'\n" + string.Join("\n", lines) + "\n$credentials");
        Assert.Equal("[RDP]\ncredentials={'username': <'kelly'>, 'password': <'it\\\\'s a \\\\\\\\back slash'>}", result.Replace("\r\n", "\n").Trim());
    }

    [Fact]
    public async Task ThePasswordHash_MatchesTheSha512CryptSpecification()
    {
        // The specification's own test vectors (Drepper, "Unix crypt using SHA-256 and SHA-512").
        var result = await RunPs(CompileHelpers() + """
            [NewVmSha512Crypt]::Hash('Hello world!', 'saltstring')
            [NewVmSha512Crypt]::Hash('Hello world!', 'saltstringsaltstring', 10000, $true)
            [NewVmSha512Crypt]::Hash('This is just a test', 'toolongsaltstring', 5000, $true)
            [NewVmSha512Crypt]::Hash('a very much longer text to encrypt.  This one even stretches over morethan one line.', 'anotherlongsaltstring', 1400, $true)
            [NewVmSha512Crypt]::Hash('we have a short salt string but not a short password', 'short', 77777, $true)
            $s = [NewVmSha512Crypt]::NewSalt(); "$($s.Length) $($s -match '^[./0-9A-Za-z]+$')"
            """);
        Assert.Equal(
        [
            "$6$saltstring$svn8UoSVapNtMuq1ukKS4tPQd8iKwSMHWjl/O817G3uBnIFNjnQJuesI68u4OTLiBFdcbYEdFCoEOfaS35inz1",
            "$6$rounds=10000$saltstringsaltst$OW1/O6BYHV6BcXZu8QVeXbDWra3Oeqh0sbHbbMCVNSnCM/UrjmM0Dp8vOuZeHBy/YTBmSK6H9qs/y3RnOaw5v.",
            "$6$rounds=5000$toolongsaltstrin$lQ8jolhgVRVhY4b5pZKaysCLi0QBxGoNeKQzQ3glMhwllF7oGDZxUhx1yxdYcz/e1JSbq3y6JMxxl8audkUEm0",
            "$6$rounds=1400$anotherlongsalts$POfYwTEok97VWcjxIiSOjiykti.o/pQs.wPvMxQ6Fm7I6IoYN3CmLs66x9t0oSwbtEW7o7UmJEiDwGqd8p4ur1",
            "$6$rounds=77777$short$WuQyW2YR.hBNpjjRhpYD/ifIw05xdfeEyQoMxIXbkvr0gge1a1x3yRULJ5CCaUeOxFmtlcGZelFl5CxtgfiAc0",
            "16 True",
        ], result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    [Fact]
    public async Task TheHelpers_Compile_EvenWhereTheCurrentFolderHoldsDotNetsOwnAssemblies()
    {
        // The installed app's folder holds .NET's System.Core.dll, in which HashSet is only a
        // forward; Add-Type's compiler looks in the current folder first, and failed there.
        // .NET's own runtime folder holds the same files.
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        Assert.True(File.Exists(Path.Combine(runtime, "System.Core.dll")));
        var said = await RunPs(CompileHelpers() + "'compiled'", runtime);
        Assert.Contains("compiled", said);
    }

    [Fact]
    public async Task EveryPowerShellTheAppStarts_StartsInSystem32_NotTheAppsFolder()
    {
        var runner = (await PowerShellRunner.RunAsync("(Get-Location).Path", Ct)).Trim();
        Assert.Equal(Environment.SystemDirectory, runner, ignoreCase: true);

        var lines = new List<string>();
        using var p = await NewVmScript.StartWithScriptAsync("Write-Host \"at=$((Get-Location).Path)\"",
            pipe => NewVmScript.ReadScriptFromPipe(pipe) + "& ([scriptblock]::Create($scriptText))",
            l => { lock (lines) lines.Add(l); }, allowCurrentUser: true, ct: Ct);
        await p.WaitForExitAsync(Ct);
        p.WaitForExit();
        Assert.Contains(lines, l => l.Equals("at=" + Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheTimeZone_IsTheLinuxName_OrNothing()
    {
        var result = await RunPs(CompileHelpers() + """
            [NewVmTimeZone]::ToIana('Central Standard Time')
            [NewVmTimeZone]::ToIana('GMT Standard Time')
            "[$([NewVmTimeZone]::ToIana('Not A Zone'))]"
            """);
        Assert.Equal(["America/Chicago", "Europe/London", "[]"], result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    [Fact]
    public async Task TheInstallerDisk_IsAFixedVhd_WithOneFat16EfiPartition_HoldingEveryFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hvm-seed-{Guid.NewGuid():N}.vhd");
        var big = new byte[3_000_000];
        new Random(7).NextBytes(big);
        var bigFile = Path.GetTempFileName();
        await File.WriteAllBytesAsync(bigFile, big, Ct);
        try
        {
            await RunPs(CompileHelpers() + $$"""
                $files = New-Object 'System.Collections.Generic.Dictionary[string,byte[]]'
                $files['EFI/BOOT/BOOTAA64.EFI'] = [IO.File]::ReadAllBytes({{Ps.Quote(bigFile)}})
                $files['EFI/BOOT/grubaa64.efi'] = [Text.Encoding]::ASCII.GetBytes('grub')
                $files['boot/grub/grub.cfg'] = [Text.Encoding]::ASCII.GetBytes("set timeout=1`n")
                $files['user-data'] = [Text.Encoding]::UTF8.GetBytes("#cloud-config`n")
                $files['meta-data'] = [byte[]]@()
                $files['credentials.ini'] = [Text.Encoding]::UTF8.GetBytes('[RDP]')
                [NewVmSeedDisk]::Write({{Ps.Quote(path)}}, 'CIDATA', $files, 24)
                """);
            var disk = await File.ReadAllBytesAsync(path, Ct);
            var fat = Fat16.Open(disk);
            Assert.Equal("CIDATA", fat.Label);
            Assert.Equal(big, fat.Read("EFI/BOOT/BOOTAA64.EFI"));
            Assert.Equal("grub"u8.ToArray(), fat.Read("EFI/BOOT/grubaa64.efi"));
            Assert.Equal("set timeout=1\n"u8.ToArray(), fat.Read("boot/grub/grub.cfg"));
            Assert.Equal("#cloud-config\n"u8.ToArray(), fat.Read("user-data"));
            Assert.Empty(fat.Read("meta-data"));
            Assert.Equal("[RDP]"u8.ToArray(), fat.Read("credentials.ini"));
            // Long names keep their case, for Linux and for GRUB.
            Assert.Contains("grubaa64.efi", fat.List("EFI/BOOT"));
            Assert.Contains("credentials.ini", fat.List(""));
        }
        finally
        {
            File.Delete(path);
            File.Delete(bigFile);
        }
    }

    [Fact]
    public async Task TheInstallerDisk_KeepsItsLastSectorForTheFinishedMark()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hvm-seed-{Guid.NewGuid():N}.vhd");
        try
        {
            var said = await RunPs(CompileHelpers() + $$"""
                $files = New-Object 'System.Collections.Generic.Dictionary[string,byte[]]'
                $files['user-data'] = [byte[]]@(1)
                [NewVmSeedDisk]::Write({{Ps.Quote(path)}}, 'CIDATA', $files, 24)
                "fresh $([NewVmSeedDisk]::Finished({{Ps.Quote(path)}}))"
                # What the install's last step does, from Windows' side: the mark in the last sector.
                $f = [IO.File]::Open({{Ps.Quote(path)}}, 'Open', 'ReadWrite')
                $f.Seek(-1024, 'End') | Out-Null
                $mark = [Text.Encoding]::ASCII.GetBytes([NewVmSeedDisk]::FinishedMark)
                $f.Write($mark, 0, $mark.Length); $f.Dispose()
                "marked $([NewVmSeedDisk]::Finished({{Ps.Quote(path)}}))"
                """);
            Assert.Equal(["fresh False", "marked True"], said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            // The mark is outside the partition, so it can't touch a file.
            var disk = await File.ReadAllBytesAsync(path, Ct);
            var start = BitConverter.ToUInt32(disk, 446 + 8);
            var sectors = BitConverter.ToUInt32(disk, 446 + 12);
            Assert.Equal((long)(disk.Length - 512) / 512 - 1, (long)(start + sectors));
            Assert.Equal(new byte[] { 1 }, Fat16.Open(disk).Read("user-data"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task TheInstallerDisk_WontOverwriteAFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            var said = await RunPs(CompileHelpers() + $$"""
                $files = New-Object 'System.Collections.Generic.Dictionary[string,byte[]]'
                $files['a'] = [byte[]]@(1)
                try { [NewVmSeedDisk]::Write({{Ps.Quote(path)}}, 'CIDATA', $files, 24); 'written' } catch { 'refused' }
                """);
            Assert.Equal("refused", said.Trim());
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Just enough of a FAT16 reader, written from the specification and independent of the
    /// script's writer, to check what a firmware or Linux would find: the VHD footer, the MBR,
    /// the boot sector, long names and cluster chains.
    /// </summary>
    private sealed class Fat16
    {
        private readonly byte[] _d;
        private readonly int _part, _bytesPerCluster, _fatAt, _rootAt, _rootEntries, _dataAt;
        public string Label { get; }

        private Fat16(byte[] d)
        {
            _d = d;
            var footer = d.AsSpan(d.Length - 512);
            Assert.Equal("conectix", Encoding.ASCII.GetString(footer[..8]));
            Assert.Equal(2u, BigEndian(footer[60..64])); // fixed disk
            Assert.Equal((ulong)(d.Length - 512), BigEndian64(footer[48..56]));
            var sum = 0u;
            for (var i = 0; i < 512; i++) if (i is < 64 or >= 68) sum += footer[i];
            Assert.Equal(~sum, BigEndian(footer[64..68]));

            Assert.Equal(0xAA55, BitConverter.ToUInt16(d, 510));
            Assert.Equal(0xEF, d[446 + 4]); // EFI system partition
            _part = (int)BitConverter.ToUInt32(d, 446 + 8) * 512;
            var bs = _part;
            Assert.Equal(0xAA55, BitConverter.ToUInt16(d, bs + 510));
            Assert.Equal("FAT16   ", Encoding.ASCII.GetString(d, bs + 54, 8));
            var bytesPerSector = BitConverter.ToUInt16(d, bs + 11);
            _bytesPerCluster = bytesPerSector * d[bs + 13];
            var reserved = BitConverter.ToUInt16(d, bs + 14);
            var fats = d[bs + 16];
            _rootEntries = BitConverter.ToUInt16(d, bs + 17);
            var fatSectors = BitConverter.ToUInt16(d, bs + 22);
            _fatAt = bs + reserved * bytesPerSector;
            _rootAt = _fatAt + fats * fatSectors * bytesPerSector;
            _dataAt = _rootAt + _rootEntries * 32;
            // Both copies of the FAT agree.
            Assert.Equal(d.AsSpan(_fatAt, fatSectors * bytesPerSector).ToArray(), d.AsSpan(_fatAt + fatSectors * bytesPerSector, fatSectors * bytesPerSector).ToArray());
            Label = Encoding.ASCII.GetString(d, bs + 43, 11).Trim();
        }

        public static Fat16 Open(byte[] disk) => new(disk);

        private static uint BigEndian(ReadOnlySpan<byte> b) => (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        private static ulong BigEndian64(ReadOnlySpan<byte> b) => (ulong)BigEndian(b[..4]) << 32 | BigEndian(b[4..8]);

        private IEnumerable<int> Chain(int cluster)
        {
            for (var c = cluster; c is >= 2 and < 0xFFF8; c = BitConverter.ToUInt16(_d, _fatAt + c * 2)) yield return c;
        }

        private byte[] Clusters(int first) =>
            Chain(first).SelectMany(c => _d.AsSpan(_dataAt + (c - 2) * _bytesPerCluster, _bytesPerCluster).ToArray()).ToArray();

        private record Entry(string Name, bool Folder, int Cluster, int Size);

        private List<Entry> Entries(byte[] area)
        {
            var result = new List<Entry>();
            var longName = new StringBuilder();
            for (var i = 0; i + 32 <= area.Length && area[i] != 0; i += 32)
            {
                var attributes = area[i + 11];
                if (attributes == 0x0F)
                {
                    var part = new StringBuilder();
                    foreach (var slot in new[] { 1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30 })
                    {
                        var ch = BitConverter.ToUInt16(area, i + slot);
                        if (ch is 0 or 0xFFFF) break;
                        part.Append((char)ch);
                    }
                    longName.Insert(0, part);
                    continue;
                }
                if ((attributes & 0x08) == 0)
                {
                    var name = longName.Length > 0 ? longName.ToString() : Encoding.ASCII.GetString(area, i, 11).Trim();
                    result.Add(new Entry(name, (attributes & 0x10) != 0, BitConverter.ToUInt16(area, i + 26), BitConverter.ToInt32(area, i + 28)));
                }
                longName.Clear();
            }
            return result;
        }

        private List<Entry> Folder(string path)
        {
            var entries = Entries(_d.AsSpan(_rootAt, _rootEntries * 32).ToArray());
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var folder = entries.Single(e => e.Folder && e.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                entries = Entries(Clusters(folder.Cluster)).Where(e => e.Name is not "." and not "..").ToList();
            }
            return entries;
        }

        public List<string> List(string path) => Folder(path).Select(e => e.Name).ToList();

        public byte[] Read(string path)
        {
            var slash = path.LastIndexOf('/');
            var file = Folder(slash < 0 ? "" : path[..slash]).Single(e => !e.Folder && e.Name.Equals(path[(slash + 1)..], StringComparison.OrdinalIgnoreCase));
            return file.Size == 0 ? [] : Clusters(file.Cluster)[..file.Size];
        }
    }
}

/// <summary>Screenshot of a VM that doesn't run Windows: straight to its own screen, no sign-in.</summary>
public class LinuxScreenshotTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(ScreenshotTaker taker, VmInfo vm, List<string?> asked)> Setup(GuestOs hyperV, InMemoryCredentialStore? store = null)
    {
        var vm = (await hyperV.GetVmsAsync(Ct)).First(v => v.State == "Running");
        var asked = new List<string?>();
        var taker = new ScreenshotTaker(hyperV, store ?? new InMemoryCredentialStore())
        {
            AskSignIn = (_, why, _) => { asked.Add(why); return null; },
        };
        return (taker, vm, asked);
    }

    [Fact]
    public async Task Ubuntu_GoesStraightToTheVmsOwnScreen_WithoutAskingForASignIn()
    {
        var (taker, vm, asked) = await Setup(new GuestOs("Ubuntu"));
        var picture = await taker.TakeAsync(vm, Ct);
        Assert.Empty(asked);
        Assert.Null(picture.Info);
        Assert.Equal("Ubuntu runs in this VM. Only Windows can show the session you work in over Remote Desktop, so this is the VM's own screen.", picture.Note);
    }

    [Theory]
    [InlineData("Windows 11 Pro")]
    [InlineData(null)]
    public async Task Windows_OrNothingSaid_AsksForTheSignIn_AsBefore(string? os)
    {
        var (taker, vm, asked) = await Setup(new GuestOs(os));
        await taker.TakeAsync(vm, Ct);
        Assert.Equal([null], asked);
    }

    [Fact]
    public async Task IfTheGuestCantBeAsked_ItIsTreatedAsWindows()
    {
        var (taker, vm, asked) = await Setup(new GuestOs(null, fails: true));
        await taker.TakeAsync(vm, Ct);
        Assert.Equal([null], asked);
    }

    [Fact]
    public async Task AKeptSignIn_IsStillUsed_WithoutAskingTheGuest()
    {
        var store = new InMemoryCredentialStore();
        var hyperV = new GuestOs("Ubuntu");
        var (taker, vm, _) = await Setup(hyperV, store);
        store.Save(vm.Id, new GuestCredential("vmuser", "vmadmin"));
        var picture = await taker.TakeAsync(vm, Ct);
        Assert.Equal(0, hyperV.Asked);
        Assert.NotNull(picture.Info);
    }

    [Fact]
    public async Task WhatTheGuestRuns_IsAskedOnce_AndNotAtAllOnceTheSignInWasDeclined()
    {
        var ubuntu = new GuestOs("Ubuntu");
        var (taker, vm, _) = await Setup(ubuntu);
        await taker.TakeAsync(vm, Ct);
        await taker.TakeAsync(vm, Ct, askEvenIfDeclined: true);
        Assert.Equal(1, ubuntu.Asked);

        var windows = new GuestOs("Windows 11 Pro");
        var (declining, wvm, asked) = await Setup(windows);
        await declining.TakeAsync(wvm, Ct); // asks, and is declined
        await declining.TakeAsync(wvm, Ct); // from the list again: not asked, and the guest isn't either
        Assert.Equal([null], asked);
        Assert.Equal(1, windows.Asked);
    }

    [Theory]
    [InlineData("Ubuntu 26.04 desktop, made by Hyper-V Manage. Connect with Remote Desktop: the console has no sound.", "Ubuntu")]
    [InlineData("My Ubuntu notes", "")]
    [InlineData("", "")]
    public async Task WhileUbuntuInstalls_TheScriptsNote_SaysWhatItRuns(string notes, string expected)
    {
        // The installer runs no guest services, so Hyper-V has nothing to say; WMI stood in for.
        var said = await UbuntuBuildTests.RunPsForTests($$"""
            function Get-CimInstance { param($Namespace, $ClassName, $Filter) $null }
            function Get-CimAssociatedInstance { param($InputObject, $ResultClassName, $ErrorAction) $null }
            $vm = [pscustomobject]@{ Id = 'x'; Notes = {{Ps.Quote(notes)}} }

            """ + PowerShellHyperVService.GuestOsScript);
        Assert.Equal(expected, said.Trim());
        // The note is the one the script gives the VM.
        Assert.Contains("-Notes \"Ubuntu $ubuntuSeries desktop, made by Hyper-V Manage.", File.ReadAllText(Path.Combine(ScriptBuildingTests.RepoRoot(), "hyperv-rdp-vm", "New-HyperVRdpVM.ps1")));
    }

    /// <summary>The demo, with the guest saying it runs the given system.</summary>
    private sealed class GuestOs(string? os, bool fails = false) : IHyperVService
    {
        private readonly DemoHyperVService _demo = new() { Delay = TimeSpan.Zero };
        public int Asked { get; private set; }
        public Task<string?> GetGuestOsAsync(string vmId, CancellationToken ct = default)
        {
            Asked++;
            return fails ? Task.FromException<string?>(new HyperVException("WMI isn't answering.")) : Task.FromResult(os);
        }
        public Task<SessionScreenshot> TakeSessionScreenshotAsync(string vmId, GuestCredential credential, CancellationToken ct = default) => _demo.TakeSessionScreenshotAsync(vmId, credential, ct);
        public Task<IReadOnlyList<VmInfo>> GetVmsAsync(CancellationToken ct = default) => _demo.GetVmsAsync(ct);
        public Task<IReadOnlyList<SwitchInfo>> GetSwitchesAsync(CancellationToken ct = default) => _demo.GetSwitchesAsync(ct);
        public Task RunActionAsync(VmAction action, string vmId, CancellationToken ct = default) => _demo.RunActionAsync(action, vmId, ct);
        public Task ApplySettingsAsync(string vmId, VmSettings current, VmSettings wanted, CancellationToken ct = default) => _demo.ApplySettingsAsync(vmId, current, wanted, ct);
        public Task CreateCheckpointAsync(string vmId, string checkpointName, CancellationToken ct = default) => _demo.CreateCheckpointAsync(vmId, checkpointName, ct);
        public Task<IReadOnlyList<CheckpointInfo>> GetCheckpointsAsync(string vmId, CancellationToken ct = default) => _demo.GetCheckpointsAsync(vmId, ct);
        public Task ApplyCheckpointAsync(string vmId, string checkpointId, string? saveCurrentAs, CancellationToken ct = default) => _demo.ApplyCheckpointAsync(vmId, checkpointId, saveCurrentAs, ct);
        public Task CloneAsync(string vmId, string newName, CancellationToken ct = default) => _demo.CloneAsync(vmId, newName, ct);
        public Task<IReadOnlyList<string>> GetDiskPathsAsync(string vmId, CancellationToken ct = default) => _demo.GetDiskPathsAsync(vmId, ct);
        public Task<DeleteResult> DeleteAsync(string vmId, CancellationToken ct = default) => _demo.DeleteAsync(vmId, ct);
        public Task<string> CreateExternalSwitchAsync(CancellationToken ct = default) => _demo.CreateExternalSwitchAsync(ct);
        public Task ConnectAsync(VmInfo vm, CancellationToken ct = default) => _demo.ConnectAsync(vm, ct);
        public void OpenConsole(VmInfo vm) => _demo.OpenConsole(vm);
        public Task<SavedConnection> SaveConnectionFileAsync(VmInfo vm, CancellationToken ct = default) => _demo.SaveConnectionFileAsync(vm, ct);
        public Task<ScreenPicture> TakeScreenshotAsync(string vmId, CancellationToken ct = default) => _demo.TakeScreenshotAsync(vmId, ct);
    }
}
