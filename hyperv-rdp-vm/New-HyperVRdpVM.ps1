<#
.SYNOPSIS
    Creates a Hyper-V Windows or Ubuntu virtual machine with a fully unattended
    install, ready to sign in to with Remote Desktop.

.DESCRIPTION
    Remote Desktop is the only way to get proper audio out of a Hyper-V virtual
    machine for a screen reader, so this script builds a VM that is ready for it
    the moment the script finishes. For Windows:

      1. Applies Windows straight from an ISO onto a new virtual disk. Windows
         Setup never runs, so there is no "press any key to boot from CD"
         prompt and nothing to click.
      2. Adds an answer file that names the computer, creates the local
         administrator account, skips every first-run screen, and turns on
         Remote Desktop.
      3. Creates and starts a Generation 2 VM, then waits until Windows has
         finished setting itself up and Remote Desktop is answering.
      4. Allows sound in both directions over Remote Desktop, saves a
         ready-to-use .rdp file on your desktop, stores the sign-in in
         Credential Manager, and opens the connection.

    With -Ubuntu it builds an Ubuntu desktop instead, with the Orca screen
    reader on:

      1. Downloads Ubuntu's desktop installer the first time, checked against
         Ubuntu's published checksums, and keeps it for next time.
      2. Starts the installer from a small disk of its own that adds
         "autoinstall" to the installer's start-up line and carries the
         answers, so the installer asks nothing. The ISO itself isn't changed.
      3. Waits for the install to finish and turn the VM off, then removes the
         installer and starts Ubuntu.
      4. Inside Ubuntu, a desktop session starts by itself with no screen of
         its own, and GNOME Remote Desktop serves it: Remote Desktop goes
         straight to that desktop, with Orca talking and its sound playing on
         this PC. The Hyper-V console shows only Ubuntu's sign-in screen, and
         has no sound.

    Every message is plain text, one line at a time, with no progress bars,
    so it reads cleanly with JAWS, NVDA or Narrator.

.PARAMETER Ubuntu
    Build an Ubuntu desktop VM instead of Windows.

.PARAMETER NoOrca
    With -Ubuntu: don't turn the Orca screen reader on. It can be turned on
    later with Super+Alt+S.

.PARAMETER IsoPath
    The Windows ISO. If you leave it out, the newest ISO in your Downloads
    folder with "win" in its name is used. The ISO must match this PC's
    processor: an x64 ISO on an Intel or AMD PC, an Arm64 ISO on an Arm PC.
    With -Ubuntu, an Ubuntu desktop ISO; left out, the newest Ubuntu release
    for this PC's processor is downloaded, or the copy downloaded before is
    used.

.PARAMETER VMName
    Name of the virtual machine. The default is this PC's name followed by
    -Win11, for example SURFACEPRO7-Win11, so VMs made on different PCs on
    one network don't share a name; -Ubuntu for an Ubuntu VM. It is also the
    computer name, Windows' or Ubuntu's, which Ubuntu writes in lower case. It
    is shortened if needed to Windows' limit of 15 characters: the ending is
    kept, and the start is cut short and followed by four characters worked
    out from the whole name, so SURFACEPRO7-Win11 becomes something like
    SURFAk3x9-Win11. The script prints the computer name it uses, and stops if
    another computer on the network already has it.

.PARAMETER Edition
    Which edition in the ISO to install. It must be one that can accept
    Remote Desktop connections, so Home editions are refused.

.PARAMETER SwitchName
    The Hyper-V virtual switch to connect the VM to. Leave it out and the VM
    joins this PC's own network through an external switch: an existing one
    if there is one, otherwise one the script creates on the adapter this PC
    uses for the internet. Other computers on your network can then reach it.

.PARAMETER HostOnly
    Use the Default Switch instead, as earlier versions did. The VM can then be
    reached only from this PC, and nothing about this PC's networking changes.

.PARAMETER NoAutoStart
    Don't start the VM when this PC starts. By default it starts every time,
    so turning the PC on is enough to be able to connect.

.PARAMETER Remove
    Deletes the virtual machine named by VMName, its virtual disk, its .rdp
    file and its saved sign-in, so you can start over.

.EXAMPLE
    .\New-HyperVRdpVM.ps1

.EXAMPLE
    .\New-HyperVRdpVM.ps1 -IsoPath D:\ISOs\Win11_24H2_English_x64.iso -VMName Test2 -MemoryGB 8

.EXAMPLE
    .\New-HyperVRdpVM.ps1 -Ubuntu

.EXAMPLE
    .\New-HyperVRdpVM.ps1 -VMName Test2 -Remove

.NOTES
    Windows blocks running scripts by default. Start this one with
    Create VM.cmd, or with: powershell -ExecutionPolicy Bypass -File .\New-HyperVRdpVM.ps1
#>
[CmdletBinding()]
param(
    [switch]$Ubuntu,
    [string]$IsoPath,
    # The defaults of these three depend on -Ubuntu, so they are filled in below.
    [string]$VMName,
    [int]$DiskGB,
    [int]$TimeoutMinutes,
    [string]$Edition = 'Windows 11 Pro',
    [string]$UserName = 'vmuser',
    [string]$Password = 'vmadmin',
    [ValidateRange(1, 64)]
    [int]$ProcessorCount = 4,
    # Dynamic memory never goes below 2 GB, so the starting memory can't either.
    [ValidateRange(2, 512)]
    [int]$MemoryGB = 4,
    [string]$SwitchName,
    [string]$VhdFolder,
    [string]$Locale,
    [string]$TimeZone = (Get-TimeZone).Id,
    [switch]$NoOrca,
    [switch]$NoConnect,
    [switch]$HostOnly,
    [switch]$NoAutoStart,
    [switch]$Remove
)

if (-not $VMName) { $VMName = if ($Ubuntu) { "$env:COMPUTERNAME-Ubuntu" } else { "$env:COMPUTERNAME-Win11" } }
# Windows 11 needs 64 GB; the Ubuntu desktop fits in far less, and the disk only takes up
# what is used either way.
if (-not $DiskGB) { $DiskGB = if ($Ubuntu) { 64 } else { 128 } }
# Ubuntu's install downloads its updates as it goes, which can take a while.
if (-not $TimeoutMinutes) { $TimeoutMinutes = if ($Ubuntu) { 90 } else { 45 } }

$ErrorActionPreference = 'Stop'
# Progress bars are noisy with a screen reader and slow down image apply.
$ProgressPreference = 'SilentlyContinue'

function Say([string]$Message) { Write-Host $Message }
function Step([string]$Message) { Write-Host ''; Write-Host $Message }

# --- Run as administrator ----------------------------------------------------
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Say 'This script needs administrator rights. Windows will ask for permission,'
    Say 'then the script carries on in a new PowerShell window.'
    # Quote for the Windows command line: a backslash before a quote, or at the
    # end, has to be doubled, or "D:\VMs\" would swallow the next argument.
    function ConvertTo-QuotedArgument([string]$Value) {
        $escaped = $Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1'
        "`"$escaped`""
    }
    $argList = @('-NoProfile', '-NoExit', '-ExecutionPolicy', 'Bypass', '-File', (ConvertTo-QuotedArgument $PSCommandPath))
    foreach ($p in $PSBoundParameters.GetEnumerator()) {
        if ($p.Value -is [switch]) {
            if ($p.Value) { $argList += "-$($p.Key)" }
        } else {
            $argList += "-$($p.Key)"
            $argList += ConvertTo-QuotedArgument "$($p.Value)"
        }
    }
    Start-Process -FilePath powershell.exe -Verb RunAs -ArgumentList $argList
    return
}

# --- Hyper-V must be available ------------------------------------------------
if (-not (Get-Command New-VM -ErrorAction SilentlyContinue)) {
    $os = (Get-CimInstance Win32_OperatingSystem).Caption
    Say "Hyper-V isn't available on this PC ($os)."
    if ($os -match 'Home') {
        Say 'Windows Home editions do not include Hyper-V. You need Windows Pro, Enterprise or Education.'
    } else {
        Say 'Turn it on by running this in an administrator PowerShell window, then restart the PC:'
        Say '    Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All'
    }
    return
}

# The name becomes file names, and Get-VM -Name reads * ? [ ] as wildcards, which
# would let -Remove match more than one VM.
if ($VMName -match '[\\/:*?"<>|\[\]]') {
    throw "The VM name '$VMName' can't contain any of these: \ / : * ? `" < > | [ ]"
}

# Windows computer names: letters, digits and hyphens, 15 characters at most.
# A longer name keeps its ending, from the earliest hyphen that leaves room,
# since the ending tells a PC's VMs apart (-Win11, -Win11-2). The start,
# usually this PC's name, is cut short and followed by four characters worked
# out from the whole name, so PCs whose names begin alike (DESKTOP-ABC1234 and
# DESKTOP-ABC9876) still give their VMs different names:
# SURFACEPRO7-Win11 becomes SURFA, four characters, then -Win11.
# Hyper-V Manage's RemoteDesktop.ComputerName does the same, and a test checks
# that the two agree.
# -creplace, not -replace: ignoring case lets the Kelvin sign and the long s
# match A-Z, and they aren't letters Windows allows in a computer name.
$ComputerName = ($VMName -creplace '[^A-Za-z0-9-]', '').Trim('-')
# The name earlier versions gave: the first 15 of those characters. A VM made
# by one is still recognised by -Remove.
$LegacyComputerName = ($VMName -creplace '[^A-Za-z0-9-]', '')
if ($LegacyComputerName.Length -gt 15) { $LegacyComputerName = $LegacyComputerName.Substring(0, 15) }
if ($ComputerName.Length -gt 15) {
    $sha1 = [Security.Cryptography.SHA1]::Create()
    $hashBytes = $sha1.ComputeHash([Text.Encoding]::UTF8.GetBytes($ComputerName.ToUpperInvariant()))
    $n = ((([uint64]$hashBytes[0] * 256 + $hashBytes[1]) * 256 + $hashBytes[2]) * 256 + $hashBytes[3]) % 1679616
    $tag = ''
    for ($i = 0; $i -lt 4; $i++) {
        $tag = [string]'0123456789abcdefghijklmnopqrstuvwxyz'[[int]($n % 36)] + $tag
        $n = [uint64][math]::Floor($n / 36)
    }
    $cut = -1
    for ($i = 1; $i -lt $ComputerName.Length; $i++) {
        if ($ComputerName[$i] -eq '-' -and ($ComputerName.Length - $i) -le 10) { $cut = $i; break }
    }
    $ComputerName = if ($cut -gt 0) {
        $ComputerName.Substring(0, 11 - ($ComputerName.Length - $cut)) + $tag + $ComputerName.Substring($cut)
    } else {
        $ComputerName.Substring(0, 11) + $tag
    }
}
if (-not $ComputerName) { throw "Can't make a Windows computer name from the VM name '$VMName'. Use letters and digits." }
if ($ComputerName -match '^[0-9]+$') { throw "Windows can't use a computer name made only of digits ($ComputerName). Put a letter in the VM name." }

$desktop = [Environment]::GetFolderPath('Desktop')
$rdpFile = Join-Path $desktop "$VMName.rdp"

# --- Remove mode --------------------------------------------------------------
if ($Remove) {
    $vm = @(Get-VM | Where-Object Name -eq $VMName)
    if ($vm.Count -gt 1) { throw "There are $($vm.Count) virtual machines named $VMName. Delete the one you mean in Hyper-V Manager." }
    $disks = @()
    $ips = @()
    $vmId = $null
    if ($vm.Count -eq 1) {
        $vm = $vm[0]
        $vmId = $vm.Id
        $ips = @(Get-VMNetworkAdapter -VM $vm | ForEach-Object { $_.IPAddresses })
        if ($vm.State -ne 'Off') { Say "Turning off $VMName."; Stop-VM -VM $vm -TurnOff -Force }
        Get-VMSnapshot -VM $vm -ErrorAction SilentlyContinue | Remove-VMSnapshot -IncludeAllChildSnapshots -ErrorAction SilentlyContinue
        $mergeDeadline = (Get-Date).AddMinutes(5)
        while ((Get-VM -Id $vm.Id).Status -match 'Merging' -and (Get-Date) -lt $mergeDeadline) { Start-Sleep -Seconds 2 }
        $disks = @(Get-VMHardDiskDrive -VM $vm | Select-Object -ExpandProperty Path)
    } else {
        $vm = $null
        Say "There is no virtual machine named $VMName. Looking for anything a failed run left behind."
    }

    # A run that failed after the disk was built but before the VM existed
    # leaves the disk behind, and the next run refuses to overwrite it.
    # An Ubuntu build stopped partway can leave its installer disk too.
    $folder = if ($VhdFolder) { $VhdFolder } else { (Get-VMHost).VirtualHardDiskPath }
    foreach ($leftover in @((Join-Path $folder "$VMName.vhdx"), (Join-Path $folder "$VMName-installer.vhd"))) {
        if ((Test-Path -LiteralPath $leftover) -and -not ($disks | Where-Object { $_ -and $_.ToLowerInvariant() -eq $leftover.ToLowerInvariant() })) {
            $disks += $leftover
        }
    }

    # A disk another VM relies on stays: one it has attached, or the parent of one of its
    # differencing disks. Deleting either would leave that VM unable to start.
    $inUse = @{}
    foreach ($other in @(Get-VM | Where-Object { $_.Id -ne $vmId })) {
        foreach ($path in @(Get-VMHardDiskDrive -VM $other | Select-Object -ExpandProperty Path)) {
            $current = $path
            while ($current) {
                $inUse[$current.ToLowerInvariant()] = $other.Name
                $current = (Get-VHD -Path $current -ErrorAction SilentlyContinue).ParentPath
            }
        }
    }

    if ($vm) {
        Say "Deleting the virtual machine $VMName."
        Remove-VM -VM $vm -Force
    }
    foreach ($d in $disks) {
        if (-not $d) { continue }
        if ($inUse.ContainsKey($d.ToLowerInvariant())) { Say "Keeping $d, which $($inUse[$d.ToLowerInvariant()]) uses."; continue }
        Say "Deleting the virtual disk $d."
        try { Remove-Item -LiteralPath $d -Force -ErrorAction Stop }
        catch { Say "Couldn't delete $d. $($_.Exception.Message)" }
    }
    # Only the connection file this script made: one that connects to this VM.
    if (Test-Path -LiteralPath $rdpFile) {
        $address = Get-Content -LiteralPath $rdpFile | Where-Object { $_ -like 'full address:s:*' } |
            Select-Object -First 1 | ForEach-Object { $_.Substring(15) }
        $names = @($ComputerName, $LegacyComputerName) | Select-Object -Unique
        $ours = @($names | ForEach-Object { "$_.local"; $_; "$_.mshome.net" }) + $ips
        # This script writes "username:s:<computer>\<user>", which marks the file as this VM's
        # even when the VM is off or gone and has no address to compare.
        $madeForIt = [bool](Get-Content -LiteralPath $rdpFile | Where-Object { $line = $_; $names | Where-Object { $line -like "username:s:$_\*" } })
        if ($address -and (($ours -contains $address) -or $madeForIt)) {
            cmdkey /delete:"TERMSRV/$address" | Out-Null
            Say "Deleting $rdpFile and its saved sign-in."
            try { Remove-Item -LiteralPath $rdpFile -Force -ErrorAction Stop }
            catch { Say "Couldn't delete $rdpFile. $($_.Exception.Message)" }
        } else {
            Say "Keeping $rdpFile, which connects to $address rather than this VM."
        }
    }
    Say 'Done.'
    return
}

# --- Check everything before touching anything --------------------------------
# Two computers with one name confuse Remote Desktop (it can connect to the
# wrong one, and with the same sign-in nothing warns you) and Windows
# networking. Only <name>.local is asked: just computers on this network answer
# it, where some internet providers answer a bare name with an address of
# their own. A name nothing answers to takes a few seconds to time out.
$answering = @()
try {
    $answering = @([Net.Dns]::GetHostAddresses("$ComputerName.local") |
        Where-Object AddressFamily -eq 'InterNetwork' | ForEach-Object IPAddressToString)
} catch { }
if ($answering.Count -gt 0) {
    throw "Another computer on your network is already called $ComputerName (at $($answering -join ', ')). Pick another name with -VMName."
}
# Windows and Ubuntu would only refuse these inside the VM, after the disk is
# built, where the failure looks like setup never finishing.
if ($Ubuntu) {
    # -cnotmatch: Linux user names are lower case, and Ubuntu's installer refuses
    # the names its own system accounts already use.
    $systemAccounts = @('root', 'daemon', 'bin', 'sys', 'sync', 'games', 'man', 'lp', 'mail', 'news', 'uucp',
        'proxy', 'www-data', 'backup', 'list', 'irc', 'gnats', 'nobody', 'messagebus', 'syslog', 'avahi',
        'gdm', 'gdm-greeter', 'sshd', 'polkitd', 'rtkit', 'colord', 'pulse', 'saned', 'cups', 'speech-dispatcher',
        'gnome-initial-setup', 'gnome-remote-desktop', 'geoclue', 'tss', 'usbmux', 'kernoops', 'whoopsie',
        'dnsmasq', 'nm-openvpn', 'fwupd-refresh', 'tcpdump', 'uuidd', 'landscape', 'admin', 'sudo', 'adm',
        'ubuntu', 'user', 'users', 'guest')
    if ($UserName -cnotmatch '^[a-z][a-z0-9_-]{0,31}$') {
        throw "Ubuntu can't use '$UserName' as a user name. Start with a lower-case letter, then use up to 31 lower-case letters, digits, hyphens or underscores."
    }
    if ($systemAccounts -contains $UserName -or $UserName -like 'systemd-*') {
        throw "Ubuntu keeps the name '$UserName' for itself. Pick another user name."
    }
} elseif ($UserName.Length -lt 1 -or $UserName.Length -gt 20 -or $UserName -match '["/\\\[\]:;|=,+*?<>@]' -or $UserName.Trim('. ') -eq '') {
    throw "Windows can't use '$UserName' as a user name. Use 1 to 20 letters, digits, spaces, dots, hyphens or underscores."
}
# Remote Desktop won't sign in to an account with no password, and a double
# quote can't be passed safely to cmdkey, which saves the sign-in.
if (-not $Password -or $Password.Contains('"')) {
    throw 'The password must not be empty, and must not contain a double quote (").'
}
if ($Locale) {
    try { $null = [Globalization.CultureInfo]::GetCultureInfo($Locale) }
    catch { throw "'$Locale' isn't a language Windows knows. Use a tag such as en-US or en-GB." }
}
if ($Ubuntu -and $DiskGB -lt 32) {
    throw "The Ubuntu desktop needs a disk of at least 32 GB. Pass a larger -DiskGB."
}
if (-not $Ubuntu -and $Edition -match 'Home') {
    throw "$Edition can't accept Remote Desktop connections. Use a Pro, Enterprise or Education edition."
}
if (Get-VM -Name $VMName -ErrorAction SilentlyContinue) {
    throw "A virtual machine named $VMName already exists. Pick another name with -VMName, or delete it with -Remove."
}

# --- Network -------------------------------------------------------------------
# By default the VM joins this PC's own network through an external switch, so
# other computers can reach it with Remote Desktop. The Default Switch hides the
# VM behind this PC, where only this PC can reach it; -HostOnly asks for that.

# The adapter this PC reaches the internet through: the default route Windows
# itself prefers, judged as Windows does by route metric plus interface metric
# (DHCP routes nearly all have route metric 0, so that alone can't tell Wi-Fi
# from Ethernet), on a physical adapter that is up.
function Get-InternetAdapter {
    $routes = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | ForEach-Object {
        $interface = Get-NetIPInterface -InterfaceIndex $_.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
        [pscustomobject]@{ Index = $_.ifIndex; Metric = [int]$_.RouteMetric + [int]$interface.InterfaceMetric }
    } | Sort-Object Metric
    foreach ($route in $routes) {
        $adapter = Get-NetAdapter -InterfaceIndex $route.Index -ErrorAction SilentlyContinue
        if ($adapter -and $adapter.Status -eq 'Up' -and -not $adapter.Virtual) { return $adapter }
    }
    return $null
}

$createSwitchOn = $null
if ($SwitchName) {
    if (-not (Get-VMSwitch -Name $SwitchName -ErrorAction SilentlyContinue)) {
        $names = (Get-VMSwitch | Select-Object -ExpandProperty Name) -join ', '
        throw "There is no Hyper-V virtual switch named '$SwitchName'. Switches on this PC: $names. Pass one with -SwitchName."
    }
} elseif ($HostOnly) {
    $SwitchName = 'Default Switch'
    if (-not (Get-VMSwitch -Name $SwitchName -ErrorAction SilentlyContinue)) {
        throw "There is no Default Switch on this PC. Pass a switch with -SwitchName."
    }
} else {
    # Only a switch whose adapter is connected: one left on an unplugged dock's
    # Ethernet would leave the VM waiting for an address that never comes.
    $external = Get-VMSwitch -SwitchType External -ErrorAction SilentlyContinue | Where-Object {
        # Several adapters can share one description (a Wi-Fi card that has been
        # reinstalled leaves 'Not Present' copies), so ask whether any of them is up.
        @(Get-NetAdapter -InterfaceDescription $_.NetAdapterInterfaceDescription -ErrorAction SilentlyContinue |
            Where-Object Status -eq 'Up').Count -gt 0
    } | Select-Object -First 1
    if ($external) {
        $SwitchName = $external.Name
    } else {
        $createSwitchOn = Get-InternetAdapter
        if (-not $createSwitchOn) {
            throw "Can't find the network adapter this PC uses for the internet, so the VM can't join your network. Connect to a network and run the script again, or run it with -HostOnly to make a VM only this PC can reach."
        }
        $SwitchName = 'External Network'
        $taken = Get-VMSwitch -Name $SwitchName -ErrorAction SilentlyContinue
        if ($taken -and $taken.SwitchType -eq 'External') {
            throw "The switch '$SwitchName' is on a network adapter that isn't connected ($($taken.NetAdapterInterfaceDescription)). Connect it, for example by docking the PC, or pass another switch with -SwitchName, or use -HostOnly."
        } elseif ($taken) {
            throw "A Hyper-V switch named '$SwitchName' already exists but isn't an external switch. Rename it, or pass a switch with -SwitchName."
        }
    }
}

# Win32_Processor.Architecture uses the same numbers as a Windows image: 9 is x64, 12 is Arm64.
$archNames = @{ 0 = 'x86'; 9 = 'x64'; 12 = 'Arm64' }
$hostArch = [int](Get-CimInstance Win32_Processor | Select-Object -First 1).Architecture

if (-not $IsoPath -and -not $Ubuntu) {
    # An ISO kept next to the script wins, so a folder carried to another PC
    # (on OneDrive or a USB stick) brings its ISO with it. Downloads is next.
    $downloads = Join-Path $env:USERPROFILE 'Downloads'
    $isoFolders = @($PSScriptRoot, $downloads) | Where-Object { $_ } | Select-Object -Unique
    $downloadPage = if ($hostArch -eq 12) { 'https://www.microsoft.com/software-download/windows11arm64' } else { 'https://www.microsoft.com/software-download/windows11' }
    # Microsoft names its ISOs with the processor type, for example
    # Win11_25H2_English_x64.iso and Win11_25H2_English_Arm64.iso. Skip any whose
    # name says it's for the other kind of PC, so one Downloads folder can hold both.
    $otherArch = if ($hostArch -eq 12) { 'x64|amd64' } else { 'arm64|aarch64' }
    $iso = $null
    foreach ($folder in $isoFolders) {
        $iso = Get-ChildItem -LiteralPath $folder -Filter '*.iso' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match 'win' -and $_.Name -notmatch $otherArch } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($iso) { break }
    }
    if (-not $iso) {
        throw "No $($archNames[$hostArch]) Windows ISO found in $($isoFolders -join ' or '). Download one from $downloadPage and run the script again, or pass its location with -IsoPath."
    }
    $IsoPath = $iso.FullName
}
if ($IsoPath) {
    if (-not (Test-Path -LiteralPath $IsoPath)) { throw "Can't find the ISO $IsoPath." }
    $IsoPath = (Resolve-Path -LiteralPath $IsoPath).Path
}

if (-not $VhdFolder) { $VhdFolder = (Get-VMHost).VirtualHardDiskPath }
New-Item -ItemType Directory -Path $VhdFolder -Force | Out-Null
$vhdPath = Join-Path $VhdFolder "$VMName.vhdx"
if (Test-Path -LiteralPath $vhdPath) {
    throw "The virtual disk $vhdPath already exists. Delete it or pick another VM name."
}

$osName = if ($Ubuntu) { 'Ubuntu' } else { 'Windows' }
Say "Creating the virtual machine $VMName."
if ($Ubuntu) {
    Say "Its computer name is $($ComputerName.ToLowerInvariant())."
    Say 'This usually takes 20 to 40 minutes, and longer the first time, when Ubuntu is downloaded. You do not need to do anything until it finishes.'
} else {
    if ($ComputerName -ne $VMName) { Say "Its Windows computer name is $ComputerName, which Windows limits to 15 characters." }
    Say "Windows image: $IsoPath"
    Say 'This usually takes 15 to 30 minutes. You do not need to do anything until it finishes.'
}

if ($createSwitchOn) {
    Say "Network: a new Hyper-V switch named $SwitchName on $($createSwitchOn.Name), so other computers can reach the VM. It is made once the virtual disk is ready."
} elseif ($SwitchName -eq 'Default Switch') {
    Say 'Network: the Default Switch. Only this PC can reach the VM.'
} else {
    Say "Network: the $SwitchName switch."
}

# --- Network switch -----------------------------------------------------------
# Made only once every check and the disks have been built, so a wrong ISO or a
# full disk never changes this PC's networking. If it fails, the disks the build
# made are removed, so they don't block a second run.
function Add-NetworkSwitch([string[]]$RemoveOnFailure) {
    if (-not $createSwitchOn) { return }
    Step "Setting up network access: creating the Hyper-V switch $SwitchName on $($createSwitchOn.Name)."
    Say "This PC's network connection drops for a few seconds while Windows sets it up."
    try {
        New-VMSwitch -Name $SwitchName -NetAdapterName $createSwitchOn.Name -AllowManagementOS $true -ErrorAction Stop | Out-Null
    } catch {
        # Leave nothing that would block a second run: the disks go too.
        foreach ($disk in $RemoveOnFailure) { Remove-Item -LiteralPath $disk -Force -ErrorAction SilentlyContinue }
        throw "Couldn't create the switch $SwitchName on $($createSwitchOn.Name): $($_.Exception.Message) If this PC has lost its network connection, remove what was made with: Remove-VMSwitch -Name '$SwitchName' -Force. Then run the script again, or add -HostOnly to keep the VM on this PC only."
    }
    # Wait for this PC's own connection to come back, now through the switch.
    $netDeadline = (Get-Date).AddSeconds(90)
    while (-not (Get-NetRoute -DestinationPrefix '0.0.0.0/0' -InterfaceAlias "vEthernet ($SwitchName)" -ErrorAction SilentlyContinue) -and (Get-Date) -lt $netDeadline) {
        Start-Sleep -Seconds 3
    }
    if (Get-NetRoute -DestinationPrefix '0.0.0.0/0' -InterfaceAlias "vEthernet ($SwitchName)" -ErrorAction SilentlyContinue) {
        Say 'The switch is ready and this PC is back on the network.'
    } else {
        Say "This PC hasn't got its network connection back through the switch yet. The VM is being set up anyway."
        Say "If the connection doesn't return, remove the switch with: Remove-VMSwitch -Name '$SwitchName' -Force"
    }
}

# --- The VM itself ------------------------------------------------------------
# What every VM the script makes has, whatever runs in it. With -Later, starting with this PC
# is left to the caller to set once the VM is ready: an Ubuntu VM still holding its installer
# disk would install itself over again if the PC restarted first.
function New-BaseVM([switch]$Later) {
    New-VM -Name $VMName -Generation 2 -MemoryStartupBytes ([int64]$MemoryGB * 1GB) -VHDPath $vhdPath -SwitchName $SwitchName | Out-Null
    Set-VMProcessor -VMName $VMName -Count $ProcessorCount
    Set-VMMemory -VMName $VMName -DynamicMemoryEnabled $true -MinimumBytes 2GB -StartupBytes ([int64]$MemoryGB * 1GB) -MaximumBytes ([int64][Math]::Max($MemoryGB * 2, 8) * 1GB)
    Set-VM -Name $VMName -AutomaticCheckpointsEnabled $false
    if ($Later) {
        Set-VM -Name $VMName -AutomaticStartAction Nothing
    } elseif (-not $NoAutoStart) {
        # Start with this PC every time, not only when it was running at shutdown,
        # so turning the PC on is all it takes to be able to connect.
        Set-VM -Name $VMName -AutomaticStartAction Start -AutomaticStartDelay 0
    }
}

# --- Connection file and saved sign-in -----------------------------------------
# The end of every build: wait for Remote Desktop to answer at the VM's address,
# save the connection file and the sign-in, and open it.
function Test-RdpPort([string]$HostName) {
    $client = New-Object Net.Sockets.TcpClient
    try { return $client.ConnectAsync($HostName, 3389).Wait(3000) -and $client.Connected }
    catch { return $false }
    finally { $client.Dispose() }
}

function Complete-Connection([string]$Address) {
    # Connect by name where one points at the VM, since its address can change: the
    # Default Switch hands out a new one when this PC restarts, and a router can too.
    # The Default Switch publishes <computer>.mshome.net, visible only to this PC.
    # On your own network the VM answers to <computer>.local, which other Windows
    # PCs and Macs can look up too; it can take a little while to start answering.
    $names = if ($SwitchName -eq 'Default Switch') { @("$ComputerName.mshome.net") } else { @("$ComputerName.local", $ComputerName) }
    $target = $Address
    $nameDeadline = (Get-Date).AddSeconds(60)
    do {
        foreach ($name in $names) {
            try {
                # Only a name that means this VM alone. Another computer with the same
                # name on the network answers too, and Remote Desktop could pick it.
                $resolved = @([Net.Dns]::GetHostAddresses($name) |
                    Where-Object AddressFamily -eq 'InterNetwork' | ForEach-Object IPAddressToString)
                if ($resolved.Count -gt 0 -and -not ($resolved | Where-Object { $_ -ne $Address })) { $target = $name; break }
            } catch { }
        }
        if ($target -ne $Address) { break }
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $nameDeadline)

    $rdpDeadline = (Get-Date).AddMinutes(5)
    while (-not (Test-RdpPort $target)) {
        if ((Get-Date) -gt $rdpDeadline) {
            throw "$osName is set up, but Remote Desktop isn't answering at $target. Try restarting the VM in Hyper-V Manager, then connect to $target."
        }
        Start-Sleep -Seconds 5
    }
    Say 'Remote Desktop is answering.'

    # audiomode 0 plays the VM's sound on this PC; audiocapturemode 1 sends this
    # PC's microphone to the VM. Authentication level 0 skips the certificate
    # warning that a brand-new VM's self-signed certificate would otherwise raise.
    $rdp = @(
        "full address:s:$target"
        "username:s:$ComputerName\$UserName"
        'prompt for credentials:i:0'
        'authentication level:i:0'
        'audiomode:i:0'
        'audiocapturemode:i:1'
        'redirectclipboard:i:1'
        'autoreconnection enabled:i:1'
        'screen mode id:i:2'
    )
    Set-Content -LiteralPath $rdpFile -Value $rdp -Encoding ASCII
    cmdkey /generic:"TERMSRV/$target" /user:"$ComputerName\$UserName" /pass:"$Password" | Out-Null

    Write-Host ''
    Say 'All done.'
    Say "Virtual machine: $VMName"
    Say "Connect to: $target"
    if ((Get-VMSwitch -Name $SwitchName).SwitchType -eq 'External') {
        Say 'Other computers on your network can connect to that address too.'
    }
    if (-not $NoAutoStart) {
        Say 'The virtual machine starts whenever this PC starts.'
    }
    Say "User name: $UserName"
    Say "Password: $Password"
    Say "Connection file: $rdpFile"
    Say 'The sign-in is saved, so opening the connection file signs you in without typing the password.'
    Say 'Remote Desktop still asks each time whether to allow sound, the microphone and the clipboard. Choose Connect.'
    if ($Ubuntu) {
        Say "Remote Desktop goes straight to the Ubuntu desktop, already signed in$(if (-not $NoOrca) { ', with Orca talking' })."
        Say "The Hyper-V console shows only Ubuntu's sign-in screen, and has no sound."
    }

    if (-not $NoConnect) {
        Say 'Opening Remote Desktop now.'
        Start-Process -FilePath mstsc.exe -ArgumentList "`"$rdpFile`""
    }
}

# --- Ubuntu --------------------------------------------------------------------
# Ubuntu's own installer does the install, with "autoinstall" on its start-up line
# so it asks nothing. Hyper-V can't edit that line, and the ISO isn't changed, so
# the VM first starts from a small disk the script makes: on it are the ISO's own
# signed boot loader (so Secure Boot stays on), a boot menu that adds
# "autoinstall" and finds everything else on the ISO, and the installer's answers.
# The boot loader reads its menu from the disk it started from, which is what
# makes this work.
#
# Hyper-V gives a VM no sound card, so sound has to come over Remote Desktop.
# Inside Ubuntu, a desktop session starts at boot with no screen of its own, and
# GNOME Remote Desktop, running inside that session, serves it: Remote Desktop
# goes straight to the desktop, with no second sign-in, and Orca's speech plays
# on this PC. The VM has no virtual TPM: GNOME Remote Desktop would keep its
# sign-in there instead of in the file the installer writes.
if ($Ubuntu) {
    $ubuntuSeries = '26.04'
    $ubuntuArch = if ($hostArch -eq 12) { 'arm64' } else { 'amd64' }
    # Ubuntu keeps Arm64 releases on cdimage and x64 ones on releases.
    $ubuntuBase = if ($ubuntuArch -eq 'arm64') { "https://cdimage.ubuntu.com/ubuntu/releases/$ubuntuSeries/release" } else { "https://releases.ubuntu.com/$ubuntuSeries" }
    $ubuntuIsoPattern = "^ubuntu-$([regex]::Escape($ubuntuSeries))(\.\d+)?-desktop-$ubuntuArch\.iso$"
    $ubuntuCache = Join-Path $env:LOCALAPPDATA 'HyperVManage\UbuntuInstall'
    $seedPath = Join-Path $VhdFolder "$VMName-installer.vhd"
    if (Test-Path -LiteralPath $seedPath) {
        throw "The installer disk $seedPath is left from an earlier run. Delete it, or run the script with -Remove."
    }

    if (-not ('NewVmSeedDisk' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

// The installer's disk: a fixed VHD holding one FAT16 partition, written byte by byte so
// nothing has to be mounted. Every name gets a long-name entry, so case and length are kept.
// The disk's last sector is left outside the partition: the install writes FinishedMark there
// as its very last step, which is how the script knows it finished rather than just stopped.
public static class NewVmSeedDisk {
    public const string FinishedMark = "HYPERVMANAGE-UBUNTU-INSTALLED";
    const int Sector = 512;
    const int SectorsPerCluster = 4;
    const int ClusterBytes = Sector * SectorsPerCluster;
    const int PartitionStart = 2048;
    const int ReservedSectors = 4;
    const int RootEntries = 512;

    class Node {
        public string Name;
        public byte[] Data;                       // null for a folder
        public List<Node> Children = new List<Node>();
        public int FirstCluster;
        public byte[] ShortName;
    }

    // files: a path on the disk, such as "EFI/BOOT/BOOTAA64.EFI", to its bytes.
    public static void Write(string vhdPath, string label, IDictionary<string, byte[]> files, int sizeMB) {
        long totalSectors = (long)sizeMB * 1024 * 1024 / Sector;
        long partSectors = totalSectors - PartitionStart - 1;
        if (partSectors > 65535) throw new ArgumentException("The installer disk must be under 32 MB.");

        Node root = new Node { Name = "" };
        foreach (KeyValuePair<string, byte[]> f in files) {
            string[] parts = f.Key.Split('/');
            Node at = root;
            for (int i = 0; i < parts.Length; i++) {
                Node next = null;
                foreach (Node c in at.Children) if (string.Equals(c.Name, parts[i], StringComparison.OrdinalIgnoreCase)) next = c;
                if (next == null) {
                    next = new Node { Name = parts[i] };
                    at.Children.Add(next);
                }
                if (i == parts.Length - 1) next.Data = f.Value;
                at = next;
            }
        }

        int rootSectors = RootEntries * 32 / Sector;
        // The FAT's size depends on the number of clusters, which depends on the FAT's size;
        // a few rounds settle it.
        int fatSectors = 1;
        for (int i = 0; i < 4; i++) {
            long clustersGuess = (partSectors - ReservedSectors - 2 * fatSectors - rootSectors) / SectorsPerCluster;
            fatSectors = (int)(((clustersGuess + 2) * 2 + Sector - 1) / Sector);
        }
        long dataStart = ReservedSectors + 2 * fatSectors + rootSectors;
        long clusterCount = (partSectors - dataStart) / SectorsPerCluster;
        if (clusterCount < 4085 || clusterCount > 65524) throw new ArgumentException("That size doesn't make a FAT16 volume.");

        byte[] disk = new byte[totalSectors * Sector];
        ushort[] fat = new ushort[clusterCount + 2];
        fat[0] = 0xFFF8; fat[1] = 0xFFFF;
        int nextCluster = 2;
        long partOffset = (long)PartitionStart * Sector;
        Func<int, int> allocate = delegate (int bytes) {
            int n = Math.Max(1, (bytes + ClusterBytes - 1) / ClusterBytes);
            if (nextCluster + n > clusterCount + 2) throw new InvalidOperationException("The files don't fit on the installer disk.");
            int first = nextCluster;
            for (int k = 0; k < n; k++) fat[first + k] = (ushort)(k == n - 1 ? 0xFFFF : first + k + 1);
            nextCluster += n;
            return first;
        };
        Func<int, long> clusterOffset = delegate (int cluster) {
            return partOffset + (dataStart + (long)(cluster - 2) * SectorsPerCluster) * Sector;
        };

        // Every folder and file gets its clusters, depth first.
        Action<Node> place = null;
        place = delegate (Node folder) {
            AssignShortNames(folder.Children);
            foreach (Node c in folder.Children) {
                if (c.Data != null) {
                    c.FirstCluster = c.Data.Length == 0 ? 0 : allocate(c.Data.Length);
                    if (c.FirstCluster != 0) Buffer.BlockCopy(c.Data, 0, disk, (int)clusterOffset(c.FirstCluster), c.Data.Length);
                } else {
                    c.FirstCluster = allocate(EntriesBytes(c));
                    place(c);
                }
            }
        };
        place(root);

        // Folder contents: the top folder's in its fixed area, the rest in their clusters.
        Action<Node, Node> writeFolder = null;
        writeFolder = delegate (Node folder, Node parent) {
            MemoryStream entries = new MemoryStream();
            if (folder == root) {
                entries.Write(Entry(Pad(label.ToUpperInvariant(), 11), 0x08, 0, 0), 0, 32);
            } else {
                entries.Write(Entry(Pad(".", 11), 0x10, folder.FirstCluster, 0), 0, 32);
                entries.Write(Entry(Pad("..", 11), 0x10, parent == root ? 0 : parent.FirstCluster, 0), 0, 32);
            }
            foreach (Node c in folder.Children) {
                foreach (byte[] lfn in LongNameEntries(c.Name, c.ShortName)) entries.Write(lfn, 0, 32);
                entries.Write(Entry(c.ShortName, (byte)(c.Data == null ? 0x10 : 0x20), c.FirstCluster, c.Data == null ? 0 : c.Data.Length), 0, 32);
            }
            byte[] bytes = entries.ToArray();
            if (folder == root) {
                if (bytes.Length > RootEntries * 32) throw new InvalidOperationException("Too many files in the installer disk's top folder.");
                Buffer.BlockCopy(bytes, 0, disk, (int)(partOffset + (ReservedSectors + 2L * fatSectors) * Sector), bytes.Length);
            } else {
                Buffer.BlockCopy(bytes, 0, disk, (int)clusterOffset(folder.FirstCluster), bytes.Length);
            }
            foreach (Node c in folder.Children) if (c.Data == null) writeFolder(c, folder);
        };
        writeFolder(root, null);

        // The partition's boot sector.
        int b = (int)partOffset;
        disk[b] = 0xEB; disk[b + 1] = 0x3C; disk[b + 2] = 0x90;
        Encoding.ASCII.GetBytes("MSWIN4.1").CopyTo(disk, b + 3);
        Le16(disk, b + 11, Sector);
        disk[b + 13] = SectorsPerCluster;
        Le16(disk, b + 14, ReservedSectors);
        disk[b + 16] = 2;
        Le16(disk, b + 17, RootEntries);
        Le16(disk, b + 19, (int)partSectors);
        disk[b + 21] = 0xF8;
        Le16(disk, b + 22, fatSectors);
        Le16(disk, b + 24, 63);
        Le16(disk, b + 26, 255);
        Le32(disk, b + 28, PartitionStart);
        disk[b + 36] = 0x80;
        disk[b + 38] = 0x29;
        Le32(disk, b + 39, (uint)Environment.TickCount);
        Pad(label.ToUpperInvariant(), 11).CopyTo(disk, b + 43);
        Encoding.ASCII.GetBytes("FAT16   ").CopyTo(disk, b + 54);
        disk[b + 510] = 0x55; disk[b + 511] = 0xAA;

        // Both copies of the FAT.
        for (int copy = 0; copy < 2; copy++) {
            int f = b + (ReservedSectors + copy * fatSectors) * Sector;
            for (int i = 0; i < fat.Length; i++) Le16(disk, f + i * 2, fat[i]);
        }

        // The partition table: one partition of type EF, the EFI system partition, which is
        // where the firmware looks for a boot loader.
        disk[446 + 1] = 0xFE; disk[446 + 2] = 0xFF; disk[446 + 3] = 0xFF;
        disk[446 + 4] = 0xEF;
        disk[446 + 5] = 0xFE; disk[446 + 6] = 0xFF; disk[446 + 7] = 0xFF;
        Le32(disk, 446 + 8, PartitionStart);
        Le32(disk, 446 + 12, (uint)partSectors);
        Le32(disk, 440, (uint)Environment.TickCount ^ 0x5EED);
        disk[510] = 0x55; disk[511] = 0xAA;

        using (FileStream fs = new FileStream(vhdPath, FileMode.CreateNew, FileAccess.Write)) {
            fs.Write(disk, 0, disk.Length);
            byte[] footer = VhdFooter(disk.LongLength);
            fs.Write(footer, 0, footer.Length);
        }
    }

    // Whether the install wrote FinishedMark into the disk's last sector.
    public static bool Finished(string vhdPath) {
        using (FileStream fs = new FileStream(vhdPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
            if (fs.Length < 1024) return false;
            byte[] sector = new byte[Sector];
            fs.Seek(fs.Length - 512 - Sector, SeekOrigin.Begin);
            int read = 0;
            while (read < Sector) { int n = fs.Read(sector, read, Sector - read); if (n <= 0) return false; read += n; }
            return Encoding.ASCII.GetString(sector, 0, FinishedMark.Length) == FinishedMark;
        }
    }

    static int EntriesBytes(Node folder) {
        int n = 2;
        foreach (Node c in folder.Children) n += 2 + (c.Name.Length + 12) / 13;
        return n * 32;
    }

    // The short name only has to be unique in its folder, since every name has a long one too.
    static void AssignShortNames(List<Node> children) {
        HashSet<string> used = new HashSet<string>();
        foreach (Node c in children) {
            string upper = c.Name.ToUpperInvariant();
            int dot = upper.LastIndexOf('.');
            string stem = Clean(dot > 0 ? upper.Substring(0, dot) : upper);
            string ext = Clean(dot > 0 ? upper.Substring(dot + 1) : "");
            if (ext.Length > 3) ext = ext.Substring(0, 3);
            string candidate = stem.Length > 8 ? null : stem;
            if (candidate == null || candidate.Length == 0 || used.Contains(candidate + "." + ext)) {
                for (int i = 1; ; i++) {
                    string tail = "~" + i;
                    string s = stem.Length + tail.Length > 8 ? stem.Substring(0, Math.Max(0, 8 - tail.Length)) : stem;
                    candidate = s + tail;
                    if (!used.Contains(candidate + "." + ext)) break;
                }
            }
            used.Add(candidate + "." + ext);
            byte[] sn = new byte[11];
            for (int i = 0; i < 11; i++) sn[i] = 0x20;
            Encoding.ASCII.GetBytes(candidate).CopyTo(sn, 0);
            Encoding.ASCII.GetBytes(ext).CopyTo(sn, 8);
            c.ShortName = sn;
        }
    }

    static string Clean(string s) {
        StringBuilder sb = new StringBuilder();
        foreach (char ch in s) if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_') sb.Append(ch);
        return sb.ToString();
    }

    static List<byte[]> LongNameEntries(string name, byte[] shortName) {
        byte sum = 0;
        foreach (byte c in shortName) sum = (byte)(((sum & 1) << 7) + (sum >> 1) + c);
        int count = (name.Length + 12) / 13;
        int[] slots = { 1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30 };
        List<byte[]> result = new List<byte[]>();
        // Stored last part first.
        for (int n = count; n >= 1; n--) {
            byte[] e = new byte[32];
            e[0] = (byte)(n | (n == count ? 0x40 : 0));
            e[11] = 0x0F;
            e[13] = sum;
            for (int k = 0; k < 13; k++) {
                int i = (n - 1) * 13 + k;
                int ch = i < name.Length ? name[i] : (i == name.Length ? 0 : 0xFFFF);
                Le16(e, slots[k], ch);
            }
            result.Add(e);
        }
        return result;
    }

    static byte[] Entry(byte[] shortName, byte attributes, int cluster, int size) {
        byte[] e = new byte[32];
        shortName.CopyTo(e, 0);
        e[11] = attributes;
        // 1 January 2026, midday; FAT dates count from 1980.
        int date = ((2026 - 1980) << 9) | (1 << 5) | 1;
        int time = 12 << 11;
        Le16(e, 14, time); Le16(e, 16, date); Le16(e, 18, date);
        Le16(e, 22, time); Le16(e, 24, date);
        Le16(e, 26, cluster);
        Le32(e, 28, (uint)size);
        return e;
    }

    static byte[] Pad(string s, int length) {
        byte[] r = new byte[length];
        for (int i = 0; i < length; i++) r[i] = i < s.Length ? (byte)s[i] : (byte)0x20;
        return r;
    }

    static void Le16(byte[] a, int at, int v) { a[at] = (byte)v; a[at + 1] = (byte)(v >> 8); }
    static void Le32(byte[] a, int at, uint v) { for (int i = 0; i < 4; i++) a[at + i] = (byte)(v >> (8 * i)); }
    static void Be32(byte[] a, int at, uint v) { for (int i = 0; i < 4; i++) a[at + i] = (byte)(v >> (24 - 8 * i)); }
    static void Be64(byte[] a, int at, ulong v) { for (int i = 0; i < 8; i++) a[at + i] = (byte)(v >> (56 - 8 * i)); }

    // The 512 bytes after the data that make a raw disk image a fixed VHD.
    static byte[] VhdFooter(long size) {
        byte[] f = new byte[512];
        Encoding.ASCII.GetBytes("conectix").CopyTo(f, 0);
        Be32(f, 8, 2);
        Be32(f, 12, 0x00010000);
        Be64(f, 16, ulong.MaxValue);
        Be32(f, 24, (uint)(DateTime.UtcNow - new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
        Encoding.ASCII.GetBytes("win ").CopyTo(f, 28);
        Be32(f, 32, 0x000A0000);
        Encoding.ASCII.GetBytes("Wi2k").CopyTo(f, 36);
        Be64(f, 40, (ulong)size);
        Be64(f, 48, (ulong)size);
        // Cylinders, heads and sectors, by the VHD specification's own rule.
        long sectors = Math.Min(size / 512, 65535L * 16 * 255);
        int spt, heads; long cth;
        if (sectors >= 65535L * 16 * 63) { spt = 255; heads = 16; cth = sectors / spt; }
        else {
            spt = 17; cth = sectors / spt;
            heads = (int)((cth + 1023) / 1024);
            if (heads < 4) heads = 4;
            if (cth >= heads * 1024 || heads > 16) { spt = 31; heads = 16; cth = sectors / spt; }
            if (cth >= heads * 1024) { spt = 63; heads = 16; cth = sectors / spt; }
        }
        long cylinders = cth / heads;
        f[56] = (byte)(cylinders >> 8); f[57] = (byte)cylinders; f[58] = (byte)heads; f[59] = (byte)spt;
        Be32(f, 60, 2);
        Guid.NewGuid().ToByteArray().CopyTo(f, 68);
        uint checksum = 0;
        foreach (byte x in f) checksum += x;
        Be32(f, 64, ~checksum);
        return f;
    }
}

// The $6$ password hash Linux keeps in /etc/shadow (SHA-512 crypt), which Ubuntu's installer
// takes instead of the password itself.
public static class NewVmSha512Crypt {
    const string Alphabet = "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public static string NewSalt() {
        byte[] r = new byte[16];
        using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(r);
        StringBuilder sb = new StringBuilder();
        foreach (byte x in r) sb.Append(Alphabet[x % 64]);
        return sb.ToString();
    }

    public static string Hash(string password, string salt) { return Hash(password, salt, 5000, false); }

    // rounds and explicitRounds are for the specification's own test vectors.
    public static string Hash(string password, string salt, int rounds, bool explicitRounds) {
        byte[] p = Encoding.UTF8.GetBytes(password);
        if (salt.Length > 16) salt = salt.Substring(0, 16);
        byte[] s = Encoding.UTF8.GetBytes(salt);

        using (SHA512 sha = SHA512.Create()) {
            byte[] b = sha.ComputeHash(Concat(Concat(p, s), p));

            byte[] aInput = Concat(p, s);
            int len = p.Length;
            for (; len > 64; len -= 64) aInput = Concat(aInput, b);
            aInput = Concat(aInput, Slice(b, len));
            for (int n = p.Length; n > 0; n >>= 1) aInput = Concat(aInput, (n & 1) != 0 ? b : p);
            byte[] a = sha.ComputeHash(aInput);

            byte[] dpInput = new byte[0];
            for (int i = 0; i < p.Length; i++) dpInput = Concat(dpInput, p);
            byte[] pSeq = Repeat(sha.ComputeHash(dpInput), p.Length);

            byte[] dsInput = new byte[0];
            for (int i = 0; i < 16 + a[0]; i++) dsInput = Concat(dsInput, s);
            byte[] sSeq = Repeat(sha.ComputeHash(dsInput), s.Length);

            byte[] c = a;
            for (int r = 0; r < rounds; r++) {
                byte[] input = (r & 1) != 0 ? pSeq : c;
                if (r % 3 != 0) input = Concat(input, sSeq);
                if (r % 7 != 0) input = Concat(input, pSeq);
                input = Concat(input, (r & 1) != 0 ? c : pSeq);
                c = sha.ComputeHash(input);
            }

            StringBuilder o = new StringBuilder("$6$");
            if (explicitRounds) o.Append("rounds=").Append(rounds).Append('$');
            o.Append(salt).Append('$');
            int[,] order = {
                {0,21,42},{22,43,1},{44,2,23},{3,24,45},{25,46,4},{47,5,26},{6,27,48},{28,49,7},{50,8,29},{9,30,51},
                {31,52,10},{53,11,32},{12,33,54},{34,55,13},{56,14,35},{15,36,57},{37,58,16},{59,17,38},{18,39,60},
                {40,61,19},{62,20,41} };
            for (int i = 0; i < 21; i++) Encode(o, c[order[i, 0]], c[order[i, 1]], c[order[i, 2]], 4);
            Encode(o, 0, 0, c[63], 2);
            return o.ToString();
        }
    }

    static void Encode(StringBuilder o, byte b2, byte b1, byte b0, int n) {
        int w = (b2 << 16) | (b1 << 8) | b0;
        for (int i = 0; i < n; i++) { o.Append(Alphabet[w & 0x3f]); w >>= 6; }
    }

    static byte[] Concat(byte[] x, byte[] y) {
        byte[] r = new byte[x.Length + y.Length];
        Buffer.BlockCopy(x, 0, r, 0, x.Length);
        Buffer.BlockCopy(y, 0, r, x.Length, y.Length);
        return r;
    }

    static byte[] Slice(byte[] x, int n) { byte[] r = new byte[n]; Buffer.BlockCopy(x, 0, r, 0, n); return r; }

    static byte[] Repeat(byte[] d, int n) {
        byte[] r = new byte[n];
        for (int i = 0; i < n; i++) r[i] = d[i % 64];
        return r;
    }
}

// Windows' time zone names to the ones Linux uses (America/Chicago), from Windows' own copy
// of ICU. Null if Windows doesn't know the name.
public static class NewVmTimeZone {
    [DllImport("icu.dll", CharSet = CharSet.Unicode)]
    static extern int ucal_getTimeZoneIDForWindowsID(string winid, int len, string region, StringBuilder id, int capacity, ref int status);

    public static string ToIana(string windowsId) {
        try {
            StringBuilder sb = new StringBuilder(128);
            int status = 0;
            int n = ucal_getTimeZoneIDForWindowsID(windowsId, -1, null, sb, sb.Capacity, ref status);
            return status <= 0 && n > 0 ? sb.ToString(0, n) : null;
        } catch (DllNotFoundException) { return null; } catch (EntryPointNotFoundException) { return null; }
    }
}
'@
    }

    # --- Step 1: the installer ISO ---
    Step 'Step 1 of 5: Getting Ubuntu.'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Add-Type -AssemblyName System.Net.Http
    $http = New-Object Net.Http.HttpClient
    $http.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    $http.DefaultRequestHeaders.UserAgent.ParseAdd('HyperVManage')

    # Ubuntu's list of every ISO in this release and its SHA-256, newest point release last.
    $sums = @{}
    try {
        $list = $http.GetStringAsync("$ubuntuBase/SHA256SUMS").GetAwaiter().GetResult()
        foreach ($line in $list -split "`n") {
            if ($line -notmatch '^([0-9a-f]{64}) \*?(\S+)\s*$') { continue }
            # Kept before the next -match replaces $Matches.
            $listedHash, $listedName = $Matches[1], $Matches[2]
            if ($listedName -match $ubuntuIsoPattern) { $sums[$listedName] = $listedHash }
        }
    } catch {
        $e = $_.Exception
        while ($e.InnerException) { $e = $e.InnerException }
        Say "Couldn't reach Ubuntu's download site: $($e.Message)"
    }
    # 26.04.1 sorts after 26.04, which [version] reads as 26.4 with no third part.
    $newest = $sums.Keys | Sort-Object { [version]($_ -replace '^ubuntu-' -replace '-desktop.*$') } | Select-Object -Last 1

    function Test-UbuntuIso([string]$Path) {
        Say "Checking $Path against Ubuntu's checksum."
        $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        return [bool]($sums.Values | Where-Object { $_ -eq $hash })
    }

    if ($IsoPath) {
        if ((Split-Path -Leaf $IsoPath) -notmatch $ubuntuIsoPattern) {
            throw "$IsoPath isn't an Ubuntu $ubuntuSeries desktop ISO for this PC's processor. It should be named like ubuntu-$ubuntuSeries.1-desktop-$ubuntuArch.iso. Leave out -IsoPath and the script downloads the right one."
        }
        if ($sums.Count -eq 0) {
            Say "Using $IsoPath without checking it, since Ubuntu's checksums couldn't be downloaded."
        } elseif (-not (Test-UbuntuIso $IsoPath)) {
            throw "$IsoPath doesn't match the checksum of any Ubuntu $ubuntuSeries release Ubuntu publishes now. It may be an older release, or damaged. Leave out -IsoPath and the script downloads the current one."
        }
    } else {
        $downloads = Join-Path $env:USERPROFILE 'Downloads'
        if ($newest) {
            # One already downloaded, here or by hand, is used if it is intact.
            foreach ($candidate in @((Join-Path $ubuntuCache $newest), (Join-Path $downloads $newest))) {
                if ((Test-Path -LiteralPath $candidate) -and (Test-UbuntuIso $candidate)) { $IsoPath = $candidate; break }
            }
            if ($IsoPath) {
                Say "Using $IsoPath, downloaded before."
            } else {
                New-Item -ItemType Directory -Path $ubuntuCache -Force | Out-Null
                $cacheFree = (Get-Item -LiteralPath $ubuntuCache).PSDrive.Free
                if ($ubuntuArch -eq 'arm64') { $isoGB = 5 } else { $isoGB = 7 }
                if ($cacheFree -lt [int64]$isoGB * 1GB) {
                    throw "Ubuntu's installer needs about $isoGB GB free to download into $ubuntuCache, and there's $('{0:0.0}' -f ($cacheFree / 1GB)) GB. Make room and run the script again."
                }
                $IsoPath = Join-Path $ubuntuCache $newest
                Remove-Item -LiteralPath $IsoPath -Force -ErrorAction SilentlyContinue
                $part = "$IsoPath.part"
                # A download stopped partway carries on from where it got to.
                $have = if (Test-Path -LiteralPath $part) { (Get-Item -LiteralPath $part).Length } else { 0 }
                $request = New-Object Net.Http.HttpRequestMessage ([Net.Http.HttpMethod]::Get), "$ubuntuBase/$newest"
                if ($have -gt 0) { $request.Headers.Range = New-Object Net.Http.Headers.RangeHeaderValue ([Nullable[long]]$have), ([Nullable[long]]$null) }
                $response = $http.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
                if ([int]$response.StatusCode -eq 416) {
                    # Asked for more than there is: the earlier download had finished.
                    $response.Dispose()
                } else {
                    if (-not $response.IsSuccessStatusCode) { throw "Couldn't download $newest from Ubuntu: $([int]$response.StatusCode) $($response.ReasonPhrase)." }
                    if ([int]$response.StatusCode -ne 206) { $have = 0 }
                    if (-not $response.Content.Headers.ContentLength) { throw "Ubuntu's download site didn't say how big $newest is. Try again later." }
                    $total = $have + [long]$response.Content.Headers.ContentLength
                    $gb = { param($bytes) '{0:0.0}' -f ($bytes / 1GB) }
                    if ($have -gt 0) {
                        Say "Carrying on with the download of $newest from $(& $gb $have) GB of $(& $gb $total) GB."
                    } else {
                        Say "Downloading $newest from ubuntu.com, $(& $gb $total) GB. It's kept for the next Ubuntu VM."
                    }
                    $source = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                    $target = New-Object IO.FileStream $part, ($(if ($have -gt 0) { [IO.FileMode]::Append } else { [IO.FileMode]::Create })), ([IO.FileAccess]::Write)
                    try {
                        $buffer = New-Object byte[] (1MB)
                        $done = $have
                        # A line every tenth of the way, so a screen reader isn't flooded.
                        $nextTenth = [Math]::Floor($done * 10 / $total) + 1
                        while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                            $target.Write($buffer, 0, $read)
                            $done += $read
                            if ($done * 10 / $total -ge $nextTenth -and $done -lt $total) {
                                Say "Downloaded $(& $gb $done) GB of $(& $gb $total) GB."
                                $nextTenth = [Math]::Floor($done * 10 / $total) + 1
                            }
                        }
                    } finally {
                        $target.Dispose()
                        $source.Dispose()
                        $response.Dispose()
                    }
                    if ($done -lt $total) { throw "The download of $newest stopped partway. Run the script again to carry on from where it got to." }
                }
                Move-Item -LiteralPath $part -Destination $IsoPath -Force
                if (-not (Test-UbuntuIso $IsoPath)) {
                    Remove-Item -LiteralPath $IsoPath -Force
                    throw "The download of $newest doesn't match Ubuntu's checksum, so it was deleted. Run the script again to download it again."
                }
                Say 'Downloaded, and it matches.'
                # Each is 4 to 6.5 GB: the releases this one replaces aren't kept.
                Get-ChildItem -LiteralPath $ubuntuCache -File |
                    Where-Object { $_.Name -ne $newest -and ($_.Name -match $ubuntuIsoPattern -or $_.Name -match ($ubuntuIsoPattern -replace '\$$', '\.part$')) } |
                    ForEach-Object {
                        Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
                        Say "Deleted $($_.Name), which this release replaces."
                    }
            }
        } else {
            # Offline: an ISO downloaded before still does, unchecked.
            $IsoPath = Get-ChildItem -LiteralPath $ubuntuCache -Filter '*.iso' -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match $ubuntuIsoPattern } | Sort-Object { [version]($_.Name -replace '^ubuntu-' -replace '-desktop.*$') } |
                Select-Object -Last 1 -ExpandProperty FullName
            if (-not $IsoPath) {
                throw "Can't download Ubuntu, and there's no copy from before. Connect to the internet and run the script again, or pass an Ubuntu $ubuntuSeries desktop ISO with -IsoPath."
            }
            Say "Using $IsoPath, downloaded before. It couldn't be checked against Ubuntu's checksums this time."
        }
    }
    $http.Dispose()

    # --- Step 2: the installer's disk and the VM's own ---
    Step 'Step 2 of 5: Preparing the installer.'
    # Windows' own tar reads ISO files, so nothing is mounted.
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (-not (Test-Path -LiteralPath $tar)) { throw "This version of Windows has no $tar, which the script uses to read the ISO. Update Windows and run the script again." }
    $isoFiles = Join-Path $env:TEMP "NewVmUbuntu-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $isoFiles | Out-Null
    $files = New-Object 'System.Collections.Generic.Dictionary[string,byte[]]'
    try {
        # Through cmd, as with bcdboot: Windows PowerShell stops on the first line a
        # program writes to its error output when that is redirected.
        $tarOutput = cmd /c "`"$tar`" -xf `"$IsoPath`" -C `"$isoFiles`" EFI/boot boot/grub/grub.cfg 2>&1" | Out-String
        $grubCfg = Join-Path $isoFiles 'boot\grub\grub.cfg'
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $grubCfg)) {
            throw "Couldn't read the boot files from $IsoPath. $($tarOutput.Trim())"
        }
        foreach ($f in Get-ChildItem -LiteralPath (Join-Path $isoFiles 'EFI\boot') -File) {
            $files["EFI/BOOT/$($f.Name.ToUpperInvariant())"] = [IO.File]::ReadAllBytes($f.FullName)
        }
        $grub = [IO.File]::ReadAllText($grubCfg)
    } finally {
        Remove-Item -LiteralPath $isoFiles -Recurse -Force -ErrorAction SilentlyContinue
    }
    # The ISO's own menu, with "autoinstall" added and a 1-second wait, read from
    # the ISO's drive: the search finds the ISO, which alone has the kernel.
    $menuEntries = [regex]::Matches($grub, '(?m)^\s*linux\s+/casper/vmlinuz(?=\s)').Count
    if ($menuEntries -eq 0) { throw "$IsoPath doesn't look like an Ubuntu desktop ISO: its boot menu has no installer to start." }
    $grub = $grub -replace '(?m)^set timeout=\d+', 'set timeout=1'
    $grub = $grub -replace '(?m)^(\s*linux\s+/casper/vmlinuz)(?=\s)', '$1 autoinstall ds=nocloud'
    $grub = "search --no-floppy --set=root --file /casper/vmlinuz`nset prefix=(`$root)/boot/grub`n" + $grub

    $hostName = $ComputerName.ToLowerInvariant()
    # Formats and language from this PC where Ubuntu has a match, otherwise US
    # English. A locale Ubuntu doesn't have would stop the install.
    $culture = if ($Locale) { $Locale } else { (Get-Culture).Name }
    $knownLocales = @('en-US', 'en-GB', 'en-AU', 'en-CA', 'en-IE', 'en-NZ', 'en-ZA', 'en-IN', 'de-DE', 'de-AT', 'de-CH',
        'fr-FR', 'fr-CA', 'fr-BE', 'fr-CH', 'es-ES', 'es-MX', 'it-IT', 'pt-PT', 'pt-BR', 'nl-NL', 'nl-BE', 'sv-SE',
        'nb-NO', 'da-DK', 'fi-FI', 'pl-PL', 'ja-JP')
    $ubuntuLocale = if ($knownLocales -contains $culture) { ($culture -replace '-', '_') + '.UTF-8' } else { 'en_US.UTF-8' }
    # The keyboard layout the region uses, where it isn't the US one.
    $layouts = @{ 'en-GB' = 'gb'; 'en-IE' = 'ie'; 'de-DE' = 'de'; 'de-AT' = 'at'; 'de-CH' = 'ch'; 'fr-FR' = 'fr'; 'fr-CA' = 'ca';
        'fr-BE' = 'be'; 'fr-CH' = 'ch'; 'es-ES' = 'es'; 'es-MX' = 'latam'; 'it-IT' = 'it'; 'pt-PT' = 'pt'; 'pt-BR' = 'br';
        'nl-BE' = 'be'; 'sv-SE' = 'se'; 'nb-NO' = 'no'; 'da-DK' = 'dk'; 'fi-FI' = 'fi'; 'pl-PL' = 'pl'; 'ja-JP' = 'jp' }
    $keyboard = if ($layouts.ContainsKey($culture)) { $layouts[$culture] } else { 'us' }
    $ianaZone = [NewVmTimeZone]::ToIana($TimeZone)
    if (-not $ianaZone) { $ianaZone = 'Etc/UTC'; Say "Ubuntu has no time zone matching '$TimeZone', so it uses UTC." }

    # YAML single-quoted strings: a quote is written twice.
    function ConvertTo-Yaml([string]$Value) { "'" + $Value.Replace("'", "''") + "'" }
    $passwordHash = [NewVmSha512Crypt]::Hash($Password, [NewVmSha512Crypt]::NewSalt())
    $userData = @"
#cloud-config
autoinstall:
  version: 1
  interactive-sections: []
  locale: $(ConvertTo-Yaml $ubuntuLocale)
  keyboard: {layout: $(ConvertTo-Yaml $keyboard)}
  timezone: $(ConvertTo-Yaml $ianaZone)
  source: {id: ubuntu-desktop}
  identity:
    hostname: $(ConvertTo-Yaml $hostName)
    username: $(ConvertTo-Yaml $UserName)
    realname: $(ConvertTo-Yaml $UserName)
    password: $(ConvertTo-Yaml $passwordHash)
  ssh:
    install-server: true
    allow-pw: true
  storage:
    layout: {name: direct}
  codecs: {install: true}
  updates: security
  # Hyper-V's guest services, which tell Hyper-V the VM's address. Ubuntu builds
  # them for Arm64 only in its Azure tools; they run on any kernel.
  packages:
    - linux-cloud-tools-azure
  late-commands:
    - mkdir -p /run/hypervmanage && mount -o ro LABEL=CIDATA /run/hypervmanage && bash /run/hypervmanage/setup.sh; status=`$?; umount /run/hypervmanage; exit `$status
  shutdown: poweroff
"@

    # GNOME Remote Desktop's sign-in, as its own grdctl writes it: a GVariant in a
    # GKeyFile. The password is quoted for GVariant, then backslashes are doubled
    # again for the key file. User names can't hold anything that needs quoting.
    $gvPassword = $Password.Replace('\', '\\').Replace("'", "\'")
    $credentials = "[RDP]`ncredentials={'username': <'$UserName'>, 'password': <'$($gvPassword.Replace('\', '\\'))'>}`n"
    $screenReader = if ($NoOrca) { 'false' } else { 'true' }

    # Runs at the end of the install, in the installer, with Ubuntu at /target.
    $setup = @"
#!/bin/bash
set -eu
U='$UserName'
T=/target
H=`$T/home/`$U
G=`$H/.local/share/gnome-remote-desktop
uid=`$(chroot `$T id -u `$U)
gid=`$(chroot `$T id -g `$U)

# A desktop session for Remote Desktop, started at boot with no screen of its own,
# and started again if it ends, as when signing out.
cat > `$T/etc/systemd/system/hypervmanage-remote-session.service <<EOF
[Unit]
Description=Desktop session for Remote Desktop (made by Hyper-V Manage)
Requires=gdm.service
After=gdm.service

[Service]
ExecStart=/usr/libexec/gdm-new-session `$U --headless
Restart=always
RestartSec=5

[Install]
WantedBy=graphical.target
EOF
mkdir -p `$T/etc/systemd/system/graphical.target.wants
ln -sf /etc/systemd/system/hypervmanage-remote-session.service `$T/etc/systemd/system/graphical.target.wants/hypervmanage-remote-session.service

# GNOME Remote Desktop inside that session, on Remote Desktop's own port, with
# a certificate of its own and the user's sign-in.
mkdir -p `$H/.config/systemd/user/gnome-session.target.wants `$G
ln -sf /usr/lib/systemd/user/gnome-remote-desktop-headless.service `$H/.config/systemd/user/gnome-session.target.wants/gnome-remote-desktop-headless.service
chroot `$T openssl req -new -newkey rsa:3072 -days 3650 -nodes -x509 -subj '/CN=$hostName' -keyout /home/`$U/.local/share/gnome-remote-desktop/rdp-tls.key -out /home/`$U/.local/share/gnome-remote-desktop/rdp-tls.crt 2>/dev/null
install -m 600 /run/hypervmanage/credentials.ini `$G/credentials.ini
chmod 700 `$H/.local/share/gnome-remote-desktop
chmod 600 `$G/rdp-tls.key

# No first-run wizard, which would also ask for the password to change privacy settings, nor
# the one for what's new in this release, which has a marker of its own named after it.
release=`$(. `$T/etc/os-release; echo `$VERSION_ID)
mkdir -p `$H/.config/gnome-initial-setup
echo yes > `$H/.config/gnome-initial-setup-done
touch `$H/.config/gnome-initial-setup/upgrade-`$release-done
chown -R `$uid:`$gid `$H/.config `$H/.local

# Settings for every session: Remote Desktop on, Orca on, and no locking or
# sleeping, since a locked session asks for the password over Remote Desktop.
mkdir -p `$T/etc/dconf/profile `$T/etc/dconf/db/local.d
printf 'user-db:user\nsystem-db:local\n' > `$T/etc/dconf/profile/user
cat > `$T/etc/dconf/db/local.d/00-hypervmanage <<EOF
[org/gnome/desktop/remote-desktop/rdp]
enable=true
view-only=false
tls-cert='/home/`$U/.local/share/gnome-remote-desktop/rdp-tls.crt'
tls-key='/home/`$U/.local/share/gnome-remote-desktop/rdp-tls.key'

[org/gnome/desktop/remote-desktop/rdp/headless]
enable=true

[org/gnome/desktop/a11y/applications]
screen-reader-enabled=$screenReader

[org/gnome/desktop/a11y]
always-show-universal-access-status=true

[org/gnome/desktop/session]
idle-delay=uint32 0

[org/gnome/desktop/screensaver]
lock-enabled=false

[org/gnome/settings-daemon/plugins/power]
sleep-inactive-ac-type='nothing'
EOF
chroot `$T dconf update

# A speaker for Remote Desktop to play. Without a sound card PipeWire's stand-in
# can stall speech until something plays from it.
mkdir -p `$T/etc/pipewire/pipewire.conf.d
cat > `$T/etc/pipewire/pipewire.conf.d/50-hypervmanage-remote-desktop.conf <<EOF
context.objects = [
  { factory = adapter
    args = {
      factory.name     = support.null-audio-sink
      node.name        = "remote-desktop-speaker"
      node.description = "Remote Desktop"
      media.class      = "Audio/Sink"
      audio.position   = [ FL FR ]
      priority.session = 2000
      monitor.channel-volumes = true
    }
  }
]
EOF

# Hyper-V's guest services from the Azure tools, whichever version is newest:
# Ubuntu's own launcher looks only for tools matching the running kernel.
mkdir -p `$T/etc/systemd/system/hv-kvp-daemon.service.d
cat > `$T/etc/systemd/system/hv-kvp-daemon.service.d/hypervmanage.conf <<'EOF'
[Service]
ExecStart=
ExecStart=/bin/sh -c 'exec "`$(ls -d /usr/lib/linux-*tools-*/hv_kvp_daemon | sort -V | tail -n 1)" -n'
EOF

# No crash reports to answer.
if [ -f `$T/etc/default/apport ]; then sed -i 's/^enabled=.*/enabled=0/' `$T/etc/default/apport; fi

# Last: tell the script the install finished, in the installer disk's last sector, which is
# outside its partition. Turning off alone doesn't say that: Hyper-V turns a VM off too when
# it can't recover from an error, such as this PC's drive filling up.
disk=/dev/`$(lsblk -no PKNAME "`$(readlink -f /dev/disk/by-label/CIDATA)")
printf '$([NewVmSeedDisk]::FinishedMark)' | dd of="`$disk" bs=512 seek=`$((`$(blockdev --getsz "`$disk") - 1)) conv=sync,notrunc status=none
sync
"@

    $vhdFree = (Get-Item -LiteralPath $VhdFolder).PSDrive.Free
    if ($vhdFree -lt 20GB) {
        throw "Ubuntu needs about 20 GB free for the VM's disk in $VhdFolder, and there's $('{0:0.0}' -f ($vhdFree / 1GB)) GB. When the drive fills during the install, Hyper-V pauses the VM. Make room and run the script again."
    }
    New-VHD -Path $vhdPath -SizeBytes ([uint64]$DiskGB * 1GB) -Dynamic | Out-Null
    try {
        $files['boot/grub/grub.cfg'] = [Text.Encoding]::UTF8.GetBytes($grub.Replace("`r`n", "`n"))
        $files['user-data'] = [Text.Encoding]::UTF8.GetBytes($userData.Replace("`r`n", "`n"))
        $files['meta-data'] = [Text.Encoding]::UTF8.GetBytes("instance-id: $([guid]::NewGuid())`nlocal-hostname: $hostName`n")
        $files['setup.sh'] = [Text.Encoding]::UTF8.GetBytes($setup.Replace("`r`n", "`n"))
        $files['credentials.ini'] = [Text.Encoding]::UTF8.GetBytes($credentials)
        [NewVmSeedDisk]::Write($seedPath, 'CIDATA', $files, 24)
    } catch {
        Remove-Item -LiteralPath $vhdPath, $seedPath -Force -ErrorAction SilentlyContinue
        throw
    }

    Add-NetworkSwitch @($vhdPath, $seedPath)

    # --- Step 3: the VM ---
    Step 'Step 3 of 5: Creating the virtual machine and starting the installer.'
    New-BaseVM -Later
    # Ubuntu's boot loader is signed by Microsoft's third-party certificate, not
    # the Windows one Hyper-V uses by default.
    Set-VMFirmware -VMName $VMName -SecureBootTemplate MicrosoftUEFICertificateAuthority
    Add-VMHardDiskDrive -VMName $VMName -Path $seedPath
    Add-VMDvdDrive -VMName $VMName -Path $IsoPath
    Set-VMFirmware -VMName $VMName -FirstBootDevice (Get-VMHardDiskDrive -VMName $VMName | Where-Object Path -eq $seedPath)
    Set-VM -Name $VMName -Notes "Ubuntu $ubuntuSeries desktop, made by Hyper-V Manage. Connect with Remote Desktop: the console has no sound."
    Start-VM -Name $VMName

    # --- Step 4: the install ---
    Step 'Step 4 of 5: Installing Ubuntu. The VM turns itself off when the install is done.'
    $started = Get-Date
    $deadline = $started.AddMinutes($TimeoutMinutes)
    $nextNote = $started.AddMinutes(3)
    while (($vmNow = Get-VM -Name $VMName).State -ne 'Off') {
        if ($vmNow.State -eq 'Paused') {
            throw "Hyper-V paused the virtual machine during the install ($($vmNow.Status)). That most often means the drive holding its disk, $VhdFolder, is full. Make room, then delete this VM with: .\New-HyperVRdpVM.ps1 -VMName $VMName -Remove, and run the script again."
        }
        if ((Get-Date) -gt $deadline) {
            throw "Ubuntu's installer didn't finish within $TimeoutMinutes minutes. The VM is left running: Screenshot in Hyper-V Manage shows its screen, and Run Windows OCR reads it. Delete it with: .\New-HyperVRdpVM.ps1 -VMName $VMName -Remove"
        }
        if ((Get-Date) -gt $nextNote) {
            Say "Still installing. $([int]((Get-Date) - $started).TotalMinutes) minutes so far."
            $nextNote = (Get-Date).AddMinutes(3)
        }
        Start-Sleep -Seconds 15
    }
    if (-not [NewVmSeedDisk]::Finished($seedPath)) {
        throw "The virtual machine turned off before Ubuntu's installer had finished. Screenshot can't show why once it's off. Delete it with: .\New-HyperVRdpVM.ps1 -VMName $VMName -Remove, and run the script again; if it happens again, check there's room on the drive holding $VhdFolder."
    }
    Say "Ubuntu is installed, after $([int]((Get-Date) - $started).TotalMinutes) minutes."

    # --- Step 5: first start ---
    Step 'Step 5 of 5: Starting Ubuntu.'
    Get-VMHardDiskDrive -VMName $VMName | Where-Object Path -eq $seedPath | Remove-VMHardDiskDrive
    # The installer ejects the ISO as it finishes, and Hyper-V then often can't find the empty
    # drive to remove it. An empty drive does no harm, so it stays; only the ISO must go.
    Get-VMDvdDrive -VMName $VMName | Where-Object Path | Set-VMDvdDrive -Path $null
    try { Remove-Item -LiteralPath $seedPath -Force -ErrorAction Stop }
    catch { Say "Couldn't delete the installer disk $seedPath, which holds the password. Delete it by hand. $($_.Exception.Message)" }
    if (-not $NoAutoStart) { Set-VM -Name $VMName -AutomaticStartAction Start -AutomaticStartDelay 0 }
    Start-VM -Name $VMName

    # Hyper-V learns the address from the guest services. Should they not report
    # it, the name the VM gives itself on the network does, but only after a few
    # minutes, and only where Remote Desktop answers: a name can still lead to an
    # earlier VM's address, such as one deleted and rebuilt with the same name.
    $names = if ($SwitchName -eq 'Default Switch') { @("$ComputerName.mshome.net") } else { @("$ComputerName.local") }
    $address = $null
    $startedUbuntu = Get-Date
    $addressDeadline = $startedUbuntu.AddMinutes(10)
    while (-not $address) {
        if ((Get-VM -Name $VMName).State -eq 'Off') {
            throw "The virtual machine turned itself off as Ubuntu started. Open it in Hyper-V Manager to see why, or delete it with: .\New-HyperVRdpVM.ps1 -VMName $VMName -Remove"
        }
        $address = Get-VMNetworkAdapter -VMName $VMName | ForEach-Object { $_.IPAddresses } |
            Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' -and $_ -notlike '169.254.*' } | Select-Object -First 1
        if (-not $address -and (Get-Date) -gt $startedUbuntu.AddMinutes(3)) {
            foreach ($name in $names) {
                try {
                    $found = [Net.Dns]::GetHostAddresses($name) | Where-Object AddressFamily -eq 'InterNetwork' |
                        Select-Object -First 1 | ForEach-Object IPAddressToString
                    if ($found -and (Test-RdpPort $found)) { $address = $found; break }
                } catch { }
            }
        }
        if ($address) { break }
        if ((Get-Date) -gt $addressDeadline) {
            throw "Ubuntu is installed, but the virtual machine hasn't been given a network address in 10 minutes on the $SwitchName switch. Some routers refuse a second address over Wi-Fi. Plug this PC into Ethernet, or start again with -HostOnly. Delete this VM first with: .\New-HyperVRdpVM.ps1 -VMName $VMName -Remove"
        }
        Start-Sleep -Seconds 10
    }
    Say "Ubuntu is running. The virtual machine's address is $address."
    Complete-Connection $address
    return
}

# --- Build the virtual disk ---------------------------------------------------
$isoMounted = $false
$vhdMounted = $false
$vhdDone = $false
$bcdWork = $null
try {
    Step 'Step 1 of 5: Reading the ISO.'
    # If the ISO is already open in File Explorer, use that copy and leave it
    # mounted afterwards; only dismount what this script mounted.
    $isoImage = Get-DiskImage -ImagePath $IsoPath
    if (-not $isoImage.Attached) {
        $isoImage = Mount-DiskImage -ImagePath $IsoPath -PassThru
        $isoMounted = $true
    }
    $isoLetter = ($isoImage | Get-Volume).DriveLetter
    if (-not $isoLetter) {
        throw "$IsoPath is mounted but has no drive letter. Eject it in File Explorer and run the script again."
    }
    $wim = @("${isoLetter}:\sources\install.wim", "${isoLetter}:\sources\install.esd") |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $wim) { throw "$IsoPath doesn't look like a Windows install ISO. There is no sources\install.wim or install.esd on it." }

    $images = Get-WindowsImage -ImagePath $wim
    $match = $images | Where-Object ImageName -eq $Edition | Select-Object -First 1
    if (-not $match) {
        $available = ($images | Select-Object -ExpandProperty ImageName) -join '; '
        throw "The ISO has no edition called '$Edition'. It has: $available. Pass one with -Edition."
    }
    $info = Get-WindowsImage -ImagePath $wim -Index $match.ImageIndex
    if ([int]$info.Architecture -ne $hostArch) {
        throw "This ISO is for $($archNames[[int]$info.Architecture]) PCs, but this PC is $($archNames[$hostArch]). Hyper-V can only run Windows built for the same kind of processor. Download the $($archNames[$hostArch]) ISO."
    }
    $unattendArch = if ($hostArch -eq 12) { 'arm64' } else { 'amd64' }
    $imageLanguage = @($info.Languages)[[int]$info.DefaultLanguageIndex]
    if (-not $Locale) { $Locale = $imageLanguage }
    Say "Found $($info.ImageName), version $($info.Version), language $imageLanguage."

    Step "Step 2 of 5: Creating a $DiskGB GB virtual disk and copying Windows onto it. This is the longest step."
    New-VHD -Path $vhdPath -SizeBytes ([uint64]$DiskGB * 1GB) -Dynamic | Out-Null
    $disk = Mount-VHD -Path $vhdPath -Passthru | Get-Disk
    $vhdMounted = $true
    Initialize-Disk -Number $disk.Number -PartitionStyle GPT
    # Newer Windows adds a reserved partition on initialise; start from an empty disk.
    Get-Partition -DiskNumber $disk.Number -ErrorAction SilentlyContinue | Remove-Partition -Confirm:$false

    $basicData = '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}'
    $msr       = '{e3c9e316-0b5c-4db8-817d-f92df00215ae}'
    $esp       = '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'

    # The boot partition is created as ordinary data so it can be formatted and
    # given a drive letter, and is switched to the EFI type once it is written.
    $sysPart = New-Partition -DiskNumber $disk.Number -Size 260MB -GptType $basicData
    Format-Volume -Partition $sysPart -FileSystem FAT32 -NewFileSystemLabel System -Force -Confirm:$false | Out-Null
    New-Partition -DiskNumber $disk.Number -Size 16MB -GptType $msr | Out-Null
    $winPart = New-Partition -DiskNumber $disk.Number -UseMaximumSize -GptType $basicData
    Format-Volume -Partition $winPart -FileSystem NTFS -NewFileSystemLabel Windows -Force -Confirm:$false | Out-Null

    $sysPart | Add-PartitionAccessPath -AssignDriveLetter
    $winPart | Add-PartitionAccessPath -AssignDriveLetter
    $sysLetter = (Get-Partition -DiskNumber $disk.Number -PartitionNumber $sysPart.PartitionNumber).DriveLetter
    $winLetter = (Get-Partition -DiskNumber $disk.Number -PartitionNumber $winPart.PartitionNumber).DriveLetter

    Expand-WindowsImage -ImagePath $wim -Index $match.ImageIndex -ApplyPath "${winLetter}:\" | Out-Null
    Say 'Windows is copied.'

    Step 'Step 3 of 5: Making the disk bootable and adding the answer file.'
    $efiBoot = "${sysLetter}:\EFI\Microsoft\Boot"
    function Invoke-Bcdedit([string]$Arguments) {
        $out = cmd /c "bcdedit $Arguments 2>&1" | Out-String
        if ($LASTEXITCODE -ne 0) { throw "bcdedit $Arguments failed: $($out.Trim()) (bcdboot said: $($bootOutput.Trim()))" }
        return $out
    }
    # "partition=X:" (or a reference through the VHDX file) records this PC's
    # view of the disk, which the VM can't find: it stops at Windows Boot
    # Manager with 0xc000000e. "boot" and "locate" are resolved at boot time.
    $portableDevices = @('device locate', 'osdevice locate')

    # bcdboot loads the new boot store under the same registry key as this PC's
    # own, and on some builds (seen on 26340 Insider) that collides and it fails
    # with 0xc0000035. Only then, build the same boot files and store by hand:
    # bcdedit /createstore loads the store under a key of its own. Any other
    # bcdboot failure is a real problem, so it stops the script.
    $bootOutput = cmd /c "`"${winLetter}:\Windows\System32\bcdboot.exe`" ${winLetter}:\Windows /s ${sysLetter}: /f UEFI 2>&1" | Out-String
    $bootExit = $LASTEXITCODE
    if ($bootExit -eq 0) {
        Invoke-Bcdedit "/store `"$efiBoot\BCD`" /set {bootmgr} device boot" | Out-Null
        foreach ($setting in $portableDevices) {
            Invoke-Bcdedit "/store `"$efiBoot\BCD`" /set {default} $setting" | Out-Null
        }
    } elseif ($bootOutput -notmatch 'c0000035') {
        throw "bcdboot couldn't make the disk bootable (exit code $bootExit): $($bootOutput.Trim())"
    } else {
        Say "bcdboot couldn't do it (exit code $bootExit), so the boot files are being set up directly instead."
        # Start clean: the failed bcdboot can leave a partial BCD store behind.
        Remove-Item -LiteralPath "${sysLetter}:\EFI" -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Path $efiBoot, "${sysLetter}:\EFI\Boot" -Force | Out-Null
        Copy-Item -Path "${winLetter}:\Windows\Boot\EFI\*" -Destination $efiBoot -Recurse -Force
        if (Test-Path "${winLetter}:\Windows\Boot\Fonts") {
            Copy-Item -Path "${winLetter}:\Windows\Boot\Fonts" -Destination $efiBoot -Recurse -Force
        }
        # The firmware starts \EFI\Boot\boot<arch>.efi when it has no boot entry of its own.
        $fallbackName = if ($hostArch -eq 12) { 'bootaa64.efi' } else { 'bootx64.efi' }
        Copy-Item -Path "$efiBoot\bootmgfw.efi" -Destination "${sysLetter}:\EFI\Boot\$fallbackName" -Force

        $bcdWork = Join-Path $env:TEMP "NewVmBcd-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $bcdWork | Out-Null
        $store = "$bcdWork\BCD"
        Invoke-Bcdedit "/createstore `"$store`"" | Out-Null

        # bcdedit /createstore makes an ordinary store. The specialize pass
        # refuses one that isn't flagged as the system store ("File is not
        # system store", then "could not configure Windows to run on this
        # computer's hardware"), so set the flags bcdboot would have set.
        # reg load refuses BCD files ("The filename or extension is too long"),
        # but RegLoadAppKey opens a byte-for-byte copy with another name. It has
        # to happen now: once bcdedit adds entries, opening it is access denied.
        if (-not ('NewVmAppHive' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
public static class NewVmAppHive {
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegLoadAppKey(string file, out IntPtr key, int sam, int options, int reserved);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegCreateKeyEx(SafeRegistryHandle key, string subKey, int reserved, string cls, int options, int sam, IntPtr security, out IntPtr result, out int disposition);
    [DllImport("advapi32.dll")]
    static extern int RegFlushKey(SafeRegistryHandle key);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LookupPrivilegeValue(string system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivilege state, int length, IntPtr previous, IntPtr returnLength);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TokenPrivilege { public int Count; public long Luid; public int Attributes; }
    static void Enable(string name) {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(), 0x28, out token)) throw new System.ComponentModel.Win32Exception();
        try {
            TokenPrivilege tp = new TokenPrivilege { Count = 1, Attributes = 2 };
            if (!LookupPrivilegeValue(null, name, out tp.Luid)) throw new System.ComponentModel.Win32Exception();
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception();
            // AdjustTokenPrivileges also succeeds when the account doesn't hold
            // the privilege at all; that only shows up as ERROR_NOT_ALL_ASSIGNED.
            if (Marshal.GetLastWin32Error() == 1300) {
                throw new InvalidOperationException("This account doesn't have " + name + ", which is needed to write the boot store. Run the script from a full administrator account.");
            }
        } finally { CloseHandle(token); }
    }
    public static RegistryKey Open(string file) {
        IntPtr h;
        int rc = RegLoadAppKey(file, out h, 0xF003F, 0, 0);
        if (rc != 0) throw new System.ComponentModel.Win32Exception(rc);
        return RegistryKey.FromHandle(new SafeRegistryHandle(h, true));
    }
    // The store's keys deny Administrators write access. Opening with
    // REG_OPTION_BACKUP_RESTORE and the backup and restore privileges
    // writes regardless of the key's own permissions.
    public static RegistryKey OpenForWrite(RegistryKey parent, string name) {
        Enable("SeBackupPrivilege");
        Enable("SeRestorePrivilege");
        IntPtr h; int disposition;
        int rc = RegCreateKeyEx(parent.Handle, name, 0, null, 4, 0x20006 | 0x0001, IntPtr.Zero, out h, out disposition);
        if (rc != 0) throw new System.ComponentModel.Win32Exception(rc);
        return RegistryKey.FromHandle(new SafeRegistryHandle(h, true));
    }
    public static void Flush(RegistryKey key) { RegFlushKey(key.Handle); }
}
'@
        }
        $hiveFile = "$bcdWork\store.hiv"
        [IO.File]::WriteAllBytes($hiveFile, [IO.File]::ReadAllBytes($store))
        # Every key is closed in a finally: the hive stays loaded, and the file
        # locked, until the last handle to it is gone.
        $root = [NewVmAppHive]::Open($hiveFile)
        $desc = $null
        try {
            $desc = [NewVmAppHive]::OpenForWrite($root, 'Description')
            $desc.SetValue('KeyName', 'BCD00000000', [Microsoft.Win32.RegistryValueKind]::String)
            $desc.SetValue('System', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
            $desc.SetValue('TreatAsSystem', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
            [NewVmAppHive]::Flush($desc)
            [NewVmAppHive]::Flush($root)
        } finally {
            if ($desc) { $desc.Close() }
            $root.Close()
        }
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
        $flagged = [IO.File]::ReadAllBytes($hiveFile)

        # Read the flags back so a silent failure shows up here, not at first boot.
        $verifyFile = "$bcdWork\verify.hiv"
        [IO.File]::WriteAllBytes($verifyFile, $flagged)
        $root = [NewVmAppHive]::Open($verifyFile)
        $desc = $null
        try {
            $desc = $root.OpenSubKey('Description')
            if (-not $desc -or [int]$desc.GetValue('System', 0) -ne 1 -or [int]$desc.GetValue('TreatAsSystem', 0) -ne 1) {
                throw "The new boot store didn't keep its system-store flags."
            }
        } finally {
            if ($desc) { $desc.Close() }
            $root.Close()
        }
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()

        # Put the flagged store on the boot partition and add the entries there.
        $store = "$efiBoot\BCD"
        [IO.File]::WriteAllBytes($store, $flagged)

        Invoke-Bcdedit "/store `"$store`" /create {bootmgr} /d `"Windows Boot Manager`"" | Out-Null
        Invoke-Bcdedit "/store `"$store`" /set {bootmgr} device boot" | Out-Null
        Invoke-Bcdedit "/store `"$store`" /set {bootmgr} locale $imageLanguage" | Out-Null
        Invoke-Bcdedit "/store `"$store`" /set {bootmgr} timeout 0" | Out-Null
        $created = Invoke-Bcdedit "/store `"$store`" /create /d `"Windows`" /application osloader"
        $loader = [regex]::Match($created, '\{[0-9a-fA-F-]{36}\}').Value
        if (-not $loader) { throw "bcdedit didn't report the new boot entry: $created" }
        foreach ($setting in $portableDevices + @(
            'path \Windows\system32\winload.efi'
            'systemroot \Windows'
            "locale $imageLanguage"
            'nx OptIn'
        )) {
            Invoke-Bcdedit "/store `"$store`" /set $loader $setting" | Out-Null
        }
        Invoke-Bcdedit "/store `"$store`" /set {bootmgr} default $loader" | Out-Null
        Invoke-Bcdedit "/store `"$store`" /set {bootmgr} displayorder $loader" | Out-Null
    }

    $xUser = [Security.SecurityElement]::Escape($UserName)
    $xPass = [Security.SecurityElement]::Escape($Password)
    $xTz   = [Security.SecurityElement]::Escape($TimeZone)
    $xLocale = [Security.SecurityElement]::Escape($Locale)
    $xLanguage = [Security.SecurityElement]::Escape($imageLanguage)
    $component = "processorArchitecture=`"$unattendArch`" publicKeyToken=`"31bf3856ad364e35`" language=`"neutral`" versionScope=`"nonSxS`""

    # Windows Setup is skipped entirely, so only the specialize and oobeSystem
    # passes are needed. Windows reads this from \Windows\Panther on first boot.
    $unattend = @"
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
  <settings pass="specialize">
    <component name="Microsoft-Windows-Shell-Setup" $component>
      <ComputerName>$ComputerName</ComputerName>
      <TimeZone>$xTz</TimeZone>
    </component>
    <component name="Microsoft-Windows-TerminalServices-LocalSessionManager" $component>
      <fDenyTSConnections>false</fDenyTSConnections>
    </component>
    <component name="Microsoft-Windows-Deployment" $component>
      <RunSynchronous>
        <RunSynchronousCommand wcm:action="add">
          <Order>1</Order>
          <Description>Open the firewall for Remote Desktop (group name works in every language)</Description>
          <Path>powershell.exe -NoProfile -Command "Get-NetFirewallRule -Group '@FirewallAPI.dll,-28752' | Enable-NetFirewallRule"</Path>
        </RunSynchronousCommand>
        <RunSynchronousCommand wcm:action="add">
          <Order>2</Order>
          <Description>Stop the password expiring, which would lock Remote Desktop out</Description>
          <Path>cmd.exe /c net accounts /maxpwage:unlimited</Path>
        </RunSynchronousCommand>
        <RunSynchronousCommand wcm:action="add">
          <Order>3</Order>
          <Description>Never sleep</Description>
          <Path>powercfg.exe /change standby-timeout-ac 0</Path>
        </RunSynchronousCommand>
        <RunSynchronousCommand wcm:action="add">
          <Order>4</Order>
          <Description>No hibernation</Description>
          <Path>powercfg.exe /hibernate off</Path>
        </RunSynchronousCommand>
        <RunSynchronousCommand wcm:action="add">
          <Order>5</Order>
          <Description>Skip the network requirement in first-run setup</Description>
          <Path>reg.exe add HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE /v BypassNRO /t REG_DWORD /d 1 /f</Path>
        </RunSynchronousCommand>
      </RunSynchronous>
    </component>
  </settings>
  <settings pass="oobeSystem">
    <component name="Microsoft-Windows-International-Core" $component>
      <InputLocale>$xLocale</InputLocale>
      <SystemLocale>$xLocale</SystemLocale>
      <UILanguage>$xLanguage</UILanguage>
      <UserLocale>$xLocale</UserLocale>
    </component>
    <component name="Microsoft-Windows-Shell-Setup" $component>
      <OOBE>
        <HideEULAPage>true</HideEULAPage>
        <HideLocalAccountScreen>true</HideLocalAccountScreen>
        <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
        <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
        <ProtectYourPC>3</ProtectYourPC>
        <SkipMachineOOBE>true</SkipMachineOOBE>
        <SkipUserOOBE>true</SkipUserOOBE>
      </OOBE>
      <UserAccounts>
        <LocalAccounts>
          <LocalAccount wcm:action="add">
            <Name>$xUser</Name>
            <DisplayName>$xUser</DisplayName>
            <Group>Administrators</Group>
            <Password>
              <Value>$xPass</Value>
              <PlainText>true</PlainText>
            </Password>
          </LocalAccount>
        </LocalAccounts>
      </UserAccounts>
      <TimeZone>$xTz</TimeZone>
    </component>
  </settings>
</unattend>
"@
    [xml]$unattend | Out-Null   # fail here, not on first boot, if the XML is malformed
    $panther = "${winLetter}:\Windows\Panther"
    New-Item -ItemType Directory -Path $panther -Force | Out-Null
    [IO.File]::WriteAllText("$panther\unattend.xml", $unattend, (New-Object Text.UTF8Encoding $false))

    Set-Partition -DiskNumber $disk.Number -PartitionNumber $sysPart.PartitionNumber -GptType $esp
    $vhdDone = $true
}
finally {
    if ($bcdWork) {
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
        Remove-Item -LiteralPath $bcdWork -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($vhdMounted) { Dismount-VHD -Path $vhdPath -ErrorAction SilentlyContinue }
    if ($isoMounted) { Dismount-DiskImage -ImagePath $IsoPath -ErrorAction SilentlyContinue | Out-Null }
    if (-not $vhdDone -and (Test-Path -LiteralPath $vhdPath)) {
        Remove-Item -LiteralPath $vhdPath -Force -ErrorAction SilentlyContinue
    }
}

Add-NetworkSwitch @($vhdPath)

# --- Create and start the VM --------------------------------------------------
Step 'Step 4 of 5: Creating the virtual machine and starting it.'
New-BaseVM
Set-VMFirmware -VMName $VMName -FirstBootDevice (Get-VMHardDiskDrive -VMName $VMName)
try {
    # A virtual TPM keeps Windows 11 happy for future feature updates.
    Set-VMKeyProtector -VMName $VMName -NewLocalKeyProtector
    Enable-VMTPM -VMName $VMName
} catch {
    Say "Note: couldn't add a virtual TPM ($($_.Exception.Message)). Windows still runs without one."
}
Start-VM -Name $VMName
Say 'The virtual machine is running. Windows is now setting itself up and will restart once or twice.'

# --- Wait for Windows to finish, then finish configuring it -------------------
Step 'Step 5 of 5: Waiting for Windows to finish setting up.'

# PowerShell Direct talks to the VM over Hyper-V itself, so it needs no network
# and only succeeds once the account from the answer file exists.
$credential = New-Object PSCredential("$ComputerName\$UserName", (ConvertTo-SecureString $Password -AsPlainText -Force))
$configureGuest = {
    $setup = Get-ItemProperty 'HKLM:\SYSTEM\Setup'
    if ([int]$setup.SystemSetupInProgress -or [int]$setup.OOBEInProgress) {
        return [pscustomobject]@{ Ready = $false }
    }
    Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0
    Get-NetFirewallRule -Group '@FirewallAPI.dll,-28752' | Enable-NetFirewallRule
    # Allow sound to play through, and the microphone to come back, over Remote Desktop.
    $policy = 'HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
    reg.exe add $policy /v fDisableCam /t REG_DWORD /d 0 /f | Out-Null
    reg.exe add $policy /v fDisableAudioCapture /t REG_DWORD /d 0 /f | Out-Null
    Set-Service -Name Audiosrv -StartupType Automatic
    Start-Service -Name Audiosrv -ErrorAction SilentlyContinue
    Get-NetConnectionProfile | Set-NetConnectionProfile -NetworkCategory Private -ErrorAction SilentlyContinue
    $ip = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '169.254.*' -and $_.IPAddress -ne '127.0.0.1' } |
        Select-Object -First 1 -ExpandProperty IPAddress
    [pscustomobject]@{ Ready = $true; IPAddress = $ip }
}

$started = Get-Date
$deadline = $started.AddMinutes($TimeoutMinutes)
$nextNote = $started.AddMinutes(3)
$guest = $null
$readyWithoutAddress = $null
while (-not ($guest -and $guest.Ready -and $guest.IPAddress)) {
    # Set up but never given an address is a network problem, not a slow setup:
    # say so, rather than waiting out the whole timeout.
    if ($guest -and $guest.Ready -and -not $guest.IPAddress) {
        if (-not $readyWithoutAddress) {
            $readyWithoutAddress = Get-Date
            Say 'Windows is set up, but the virtual machine has no network address yet. Waiting for one.'
        } elseif ((Get-Date) -gt $readyWithoutAddress.AddMinutes(5)) {
            throw "Windows is set up, but the virtual machine hasn't been given a network address in 5 minutes on the $SwitchName switch. Some routers refuse a second address over Wi-Fi. Plug this PC into Ethernet, or start again with -HostOnly. Delete this VM first with: .\New-HyperVRdpVM.ps1 -VMName $VMName -Remove"
        }
    }
    if ((Get-Date) -gt $deadline) {
        throw "Windows didn't finish setting up within $TimeoutMinutes minutes. The VM is left running so you can look at it in Hyper-V Manager. Delete it with: .\New-HyperVRdpVM.ps1 -VMName $VMName -Remove"
    }
    if ((Get-VM -Name $VMName).State -eq 'Off') {
        throw "The virtual machine turned itself off during setup. Open it in Hyper-V Manager to see why, or delete it with: .\New-HyperVRdpVM.ps1 -VMName $VMName -Remove"
    }
    if ((Get-Date) -gt $nextNote) {
        Say "Still setting up. $([int]((Get-Date) - $started).TotalMinutes) minutes so far."
        $nextNote = (Get-Date).AddMinutes(3)
    }
    try {
        $guest = Invoke-Command -VMName $VMName -Credential $credential -ScriptBlock $configureGuest -ErrorAction Stop
    } catch {
        $guest = $null
    }
    if (-not ($guest -and $guest.Ready -and $guest.IPAddress)) { Start-Sleep -Seconds 15 }
}
Say "Windows is set up. The virtual machine's address is $($guest.IPAddress)."

Complete-Connection $guest.IPAddress
