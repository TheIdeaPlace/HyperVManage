# Hyper-V Manage

> **Status: pre-release.** On 2 October 2026 it built Windows VMs
> end to end on an Arm64 PC and an x64 PC, connected to them with Remote Desktop, opened the
> console, and paused one. Settings, Checkpoint, Apply Checkpoint, Clone and Delete have so far been exercised only
> against the pretend VMs of demo mode. See [Testing it on a real machine](#testing-it-on-a-real-machine).

A Windows app for managing Hyper-V virtual machines, built screen reader first. It is the
Windows counterpart of [Parallels Manager](https://github.com/kellylford/TheWorkBench/tree/main/parallels-manager), and it builds new Windows
VMs with [New-HyperVRdpVM.ps1](hyperv-rdp-vm/), so a VM made from the app is exactly the
VM the script makes: ready to sign in to with Remote Desktop, with sound.

It isn't called Hyper-V Manager because Windows already has a Hyper-V Manager.

## What it does

- Lists every VM with its state, address, network, memory, processors and whether it starts
  with the PC. The list refreshes itself every ten seconds without moving your place in it.
- **Connect with Remote Desktop**, the way to hear a VM with a screen reader. It opens the
  connection file the script left on your desktop if there is one, so the saved sign-in is
  used; otherwise it makes a connection to the VM's name, which keeps working when its address
  changes. Either way, it connects by name only when the name leads to this VM and nothing
  else, and otherwise by address: a VM with the same name on another PC answers to the same
  name, and Remote Desktop could reach that one instead.
- **Save Connection File** puts `<VM name>.rdp` on the desktop, for a VM whose file was lost or
  that you want to connect to from another computer. It never overwrites a desktop file of that
  name that connects somewhere else.
- **Open Console**: the Hyper-V window, for when Windows inside the VM isn't up yet.
- **Screenshot** (Ctrl+Shift+S) shows what's going on in a running or paused VM: a picture your
  screen reader can describe (JAWS Picture Smart, an NVDA image description add-on, or paste it
  into Be My AI, ChatGPT or Claude), and the same in words.
  - **The session you're working in, Remote Desktop included.** Over Remote Desktop you work in a
    session of your own, which the VM's own screen doesn't show: it sits at the lock screen. So
    Hyper-V Manage signs in to Windows inside the VM over PowerShell Direct (no network needed),
    finds the signed-in session, and takes the picture there, at full resolution. It also reads
    the title of the window in front, and, from the session's accessibility tree (UI Automation),
    what has focus and which windows are open. The picture's name says what's in front:
    "Screen of vm2, taken 8:57:10 AM, 1920 by 1080, notes - Notepad in front". The box under the
    picture, What's on screen (Alt+W), has the rest, a line at a time.
  - **The sign-in** is an account that is an administrator in the VM. It's asked for the first
    time, with the user name filled in, and kept in Windows Credential Manager for your account
    (as "HyperVManage:VM:" and the VM's id, where it can be removed by hand). A VM made with New
    Virtual Machine already knows its own. If Windows refuses it, you're asked again, saying so.
  - **Otherwise, the VM's own screen**, from Hyper-V, from outside the VM. That works when
    nothing inside can talk to you: Windows setup, a BitLocker prompt, a stuck sign-in, a blue
    screen, or another operating system. It's what you get when you choose Use the VM's Own
    Screen instead of signing in, when nobody is signed in, and when Windows isn't drawing the
    session, as with a minimized Remote Desktop window. What's on screen says which and why, and
    the picture's name ends "the VM's own screen". A picture that is all one color, most often a
    VM whose display has gone to sleep, says "blank" as well.
  - **Run Windows OCR** (Alt+O) reads the text in the picture with Windows' own text
    recognition, on this PC, and adds it to What's on screen, where focus goes to it. It works
    on either kind of picture, so it can read a setup screen or an error the VM's own screen shows.
  - Take Again (F5), Copy (Ctrl+Shift+C), Save As (Ctrl+S) and Close (Escape) are below the
    picture. Ctrl+Shift+C copies the picture from anywhere in the window; in What's on screen,
    Ctrl+A and Ctrl+C select and copy its text, as in any text.
    Taking another picture of the same VM, from the viewer or the main window, replaces the one
    in its open viewer and puts focus back on it.
- **Start, Shut Down, Turn Off, Save, Pause, Resume, Restart.** Only the ones that make sense for
  the VM's state are available. Shut Down and Restart ask Windows inside the VM, so nothing
  unsaved is lost; Turn Off is the power switch.
- **Settings**: processors, memory, network, whether it starts with the PC, and automatic
  checkpoints. Processors and memory can only change while the VM is off; the window says so
  and keeps the current values readable. If no switch reaches your own network, it offers to
  create one.
- **Checkpoint, Apply Checkpoint, Clone, Delete.**
  - Apply Checkpoint lists the VM's checkpoints, newest first, each with when it was taken, and
    puts the VM back to the one you choose. A running or paused VM is turned off first. Unless
    you uncheck it, how the VM is now is kept as a checkpoint first, so nothing is lost for good.
  - Clone works on a VM that is off or saved, since a copy of a running one would join the
    network as a second machine with the same name. Windows inside the copy keeps the same
    computer name; rename it there before running both.
  - Delete asks first, naming the disk files it will remove. It keeps any disk another VM uses
    or depends on, and only removes a desktop connection file that connects to this VM. It says
    afterwards what it kept and anything it couldn't delete.
- **ISOs used before**: New Virtual Machine's Windows ISO is a list of the ISOs you've built VMs
  from, the most recent first and already chosen, then the newest Windows ISO in your Downloads
  folder, then Browse for an ISO. Arrow through it, or open it with Alt+Down Arrow; in the open
  list each ISO is read file name first, then its folder. Browse for an ISO opens the file dialog
  when you press Enter on it or click it. Arrowing onto it does nothing else, and closing the list
  while on it puts back the ISO you had. You can still type or paste a path. An ISO is remembered
  when a build starts with it, up to ten, in `%AppData%\HyperVManage\recent-isos.json`; one that
  has been moved or deleted isn't offered. Demo mode remembers them only until it closes.
- **Windows ISO downloads**: New Virtual Machine has links, under the ISO field, to Microsoft's
  Windows 11 download pages, with the page for this PC's kind of processor first (Hyper-V only
  runs Windows built for it) and the other kind's second. The Help menu has the same two. They
  open in your browser as you, not as administrator.
- **New Virtual Machine**: the script's options in a form, with a name that starts with this
  PC's name (for example `SURFACEPRO7-Win11`) so VMs made on different PCs never share one. Then
  the script's own progress as it
  runs, one line at a time, each one also spoken (PowerShell's own error detail lines stay in the
  log but aren't read out). Closing the window during a build stops it and cleans up what it had
  made: the ISO and disk are unmounted and the half-built disk deleted. If it had already got as
  far as creating the VM, that VM is left in the list to delete. The main window won't close
  while a build runs.
  New Virtual Machine and the screenshot viewers are windows of their own, so Alt+Tab moves
  between them and the list, and the list stays usable while a build runs.

Every action goes through Hyper-V's own PowerShell commands, the way Parallels Manager goes
through `prlctl`, so anything the app does can be repeated by hand.

## Requirements

| Requirement | Notes |
|---|---|
| Windows 10 or 11 Pro, Enterprise or Education | Windows Home doesn't include Hyper-V |
| Hyper-V turned on | The app tells you the command if it isn't |
| Administrator rights | The app asks when it starts, as the script does |
| .NET 10 SDK | Only for building it |

## Installing

From the [latest release](https://github.com/TheIdeaPlace/HyperVManage/releases):

- **HyperVManage-Setup-x64.exe** (Intel and AMD) or **HyperVManage-Setup-arm64.exe** (Arm)
  installs it for your account, in `%LocalAppData%\HyperVManage`, with a Start menu entry. Remove
  it from Settings, Apps, like any other app.
- **HyperVManage-x64.exe** or **HyperVManage-arm64.exe** is the same app as one program to run from
  anywhere, with nothing to install.
- **The zip** holds New-HyperVRdpVM.ps1, the script the app runs to build a VM, for the command
  line.

### Updates

An installed copy checks for a new version each time it starts. If there is one, it says so,
downloads it while you work, and installs it when you close Hyper-V Manage, so the next start is
the new version. Help, Check for Updates checks straight away and offers to install now, which
closes Hyper-V Manage and opens the new version; it won't while a VM is being built, cloned,
checkpointed or deleted, since closing would cut that short. An update closed before it finished
downloading is found again at the next start. The single exe can't update itself: it says when there's a new version, and Check for
Updates offers its download page.

### Reporting a bug

Help, Report a Bug asks what happened, what you expected and how to make it happen, and shows
exactly what will be sent: that, the app's version, Windows' version, the processor, whether it
was installed, and which screen reader is running. Nothing about your VMs, network or account.

- **Send** files it as a public issue on GitHub, with no GitHub account needed, in a release
  built with the shared bug-report relay, which every TheIdeaPlace app uses (see
  [TheIdeaPlace/app-kit](https://github.com/TheIdeaPlace/app-kit)). Release builds get its address
  and this app's key from the repository's `APPKIT_RELAY_URL` variable and `APPKIT_RELAY_KEY` secret.
- **Send with GitHub** opens GitHub's new issue form in your browser with the report filled in,
  and copies the whole report to the clipboard, since a long one is cut short there. Sending it
  needs a GitHub account. It's the only way in a build without the relay.

## Using it

Run Hyper-V Manage from the Start menu, or `HyperVManage.exe`, and choose Yes when Windows asks
for permission.

Focus starts on the first VM in the list. Each one reads as its name, state, address and
network, for example "Win11-RDP, Running, 10.0.0.41, External Wi-Fi".

### Keyboard

In the list of virtual machines:

| Key | Action |
|---|---|
| Enter | Connect with Remote Desktop |
| Delete | Delete the VM (asks first) |
| Shift+F10 or the Applications key | Every action for the VM |

Anywhere in the window:

| Key | Action |
|---|---|
| Ctrl+N | New virtual machine |
| F5 | Refresh the list |
| Ctrl+Enter | Start |
| Ctrl+Period | Shut down |
| Ctrl+Shift+Period | Turn off |
| Ctrl+U | Save |
| Ctrl+P | Pause |
| Ctrl+Shift+P | Resume |
| Ctrl+R | Restart |
| Alt+Enter | Settings |
| Ctrl+K | Checkpoint |
| Ctrl+Shift+K | Apply a checkpoint |
| Ctrl+D | Clone |
| Ctrl+Shift+S | Screenshot of the VM's screen |

Every action is also on the VM menu (Alt+V) and in the list's context menu. The menu bar is
reached with Alt or F10, never with Tab. Help, then Keyboard Shortcuts opens these as a list, one
shortcut per line and each section's heading a line of its own: arrow through it, and press
Escape to close it.

In the screenshot window:

| Key | Action |
|---|---|
| F5 | Take the picture again |
| Ctrl+Shift+C | Copy the picture |
| Ctrl+S | Save the picture as a PNG |
| Escape | Close |

In the Hyper-V console window that Open Console opens:

| Key | Action |
|---|---|
| Ctrl+Alt+Left Arrow | Take the keyboard back from the VM |
| Ctrl+Alt+End | Send Ctrl+Alt+Delete to the VM |
| Ctrl+Alt+Pause | Switch between full screen and a window |

Its View menu has Enhanced Session, which runs the console over Remote Desktop and can carry
sound once Windows in the VM is up. Connect with Remote Desktop is still the dependable way to
hear a VM.

### Remote Desktop asks each time

Windows asks about sharing every time a connection file opens: it lists the sound, microphone
and clipboard the connection uses, and you choose Connect. Recent versions of Windows do this for
every connection file that isn't digitally signed, which a file made on your own PC is not, and
it can't be turned off. The saved sign-in still means you don't type the password.

### What it says

When an action starts and when it finishes or fails, the app reports it through the screen
reader with a UI Automation notification, and shows the same text in the status bar. A failure
carries Hyper-V's own message. Nothing is announced that the screen reader already reports,
such as the selection moving or a check box changing.

### Demo mode

```bat
HyperVManage.exe --demo
```

Three pretend VMs, no Hyper-V, and no administrator rights. Every action and dialog works
against them, and New Virtual Machine prints the script's steps without running anything.
Connect, Open Console and Save Connection File say there is no real VM, rather than reaching a
real one that happens to share a demo VM's name. Screenshot asks for a sign-in (any password
works) and shows a made-up screen: a blue desktop with a window and a taskbar, with Notepad in
front. Use it to
try the app, or to check the interface on a PC without Hyper-V.

## Building

Open `Build App.cmd`, or run it from a command prompt. It builds one self-contained
`HyperVManage.exe`, with the creation script inside it, in `build\x64\` or `build\arm64\` to
match the PC. Pass `x64` or `arm64` to choose:

```bat
"Build App.cmd" x64
```

### Signing

Builds are signed with Kelly Ford's Azure Artifact Signing certificate (publisher "kelly ford"),
like the other apps; see `The-Idea-Place-Projects/signing/windows.md`.

- **On this PC:** add `sign` to sign the exe after building, or sign any file with
  `Sign Files.ps1`. It needs `winget install Microsoft.Azure.TrustedSigningClientTools` and
  `az login` once, and fails unless every file comes out validly signed and timestamped.

  ```bat
  "Build App.cmd" x64 sign
  powershell -ExecutionPolicy Bypass -File "Sign Files.ps1" build\arm64\HyperVManage.exe
  ```

- **Releases:** pushing a tag `v<major>.<minor>.<patch>` runs `.github/workflows/release.yml`.
  It tests, builds both apps with the tag as their version, signs them and the VM script, packs a
  Velopack installer and update feed for each processor (x64 on Velopack's `win` channel, Arm64
  on `win-arm64`), checks every signature, including inside the packages, and publishes a GitHub
  release. Its notes start with `docs/release-notes/v<version>.md` when there is one. Before 1.0
  a release is a GitHub pre-release, and installed copies still see it as an update.

  ```bat
  git tag v0.9.3
  git push origin v0.9.3
  ```

  Set `<Version>` in `src/HyperVManage/HyperVManage.csproj` to the next release's number. A pull
  request, or a run by hand, builds the same files at that version with `-test.<run>.<attempt>`
  added and keeps them as a workflow artifact; a hand run signs them if asked. A test install
  sorts below the release of its number, so it updates to that release.

- **Trying an update without a release:** pack two versions with `vpk pack` (as the workflow
  does) into a folder, install the older one, and start it with `--update-feed <folder>`. Help,
  Check for Updates then finds the newer one there.

The repository's `New-HyperVRdpVM.ps1` stays unsigned: the app embeds it and a test checks the
embedded copy byte for byte. Only the copies handed out are signed.

Tests:

```bat
dotnet test tests\HyperVManage.Tests
```

Two tests press real keys at a real window: one checks that the arrow keys move between the
network choices in New Virtual Machine and that Tab into them never changes the choice, the
other that Tab from the VM list never stops on the menu bar. They take focus from whatever else
is on screen, so they only run with `HYPERVMANAGE_RUN_INPUT_TESTS=1` set.

## How it is put together

```
Build App.cmd                   Builds build\<arch>\HyperVManage.exe
hyperv-rdp-vm/                  New-HyperVRdpVM.ps1, the script that builds a VM
src/HyperVManage/
    App.xaml.cs                 Velopack's hooks, elevation, Hyper-V check, --demo
    Models/VmInfo.cs            A VM, and which actions each state allows
    Services/
      PowerShellRunner.cs       Runs powershell.exe; Ps.Quote makes every value a literal
      PowerShellHyperVService   The Hyper-V commands: list, actions, settings, clone, delete
      DemoHyperVService         The pretend VMs
      NewVmScript.cs            Runs the embedded New-HyperVRdpVM.ps1
      RemoteDesktop.cs          Connection files, and choosing a name over an address
      ScreenPicture.cs          Hyper-V's screen pixels made into a PNG
      UpdateService.cs          Velopack updates, or GitHub's releases for the single exe
      BugReportService.cs       Report a Bug: the relay, or GitHub's form in the browser
      Browser.cs                Opens pages as the user, not as administrator
    ViewModels/                 Main list, Settings, New VM, the screenshot viewer, updates, bug reports
    Views/                      The windows
tests/HyperVManage.Tests/
```

- **The script is embedded, not copied.** The project embeds
  `hyperv-rdp-vm/New-HyperVRdpVM.ps1` itself, and a test checks the embedded copy is
  byte-for-byte the one in the repository, so the app and the command line can't drift.
- **No value is ever pasted into PowerShell as code.** Names, paths and passwords go through
  `Ps.Quote`, and a test checks PowerShell's own parser reads each one back unchanged. Another
  test parses every script the app can send.
- **The computer name rule is shared.** The app finds a VM on the network by the Windows
  computer name the script gives it. A test runs the script's own lines for that name in
  PowerShell and checks the app computes the same for each case.
- **Nothing it runs as administrator can be swapped by another program.** The app runs elevated,
  so the creation script is never written to a file, where another program could rewrite it in
  the moment before PowerShell reads it. The app opens a named pipe with a random name that only
  Administrators and SYSTEM can open, starts PowerShell with a short command that reads the
  script from that pipe, and runs it from memory. (Standard input was tried and rejected:
  Windows PowerShell then wraps its messages in XML.) PowerShell, Remote Desktop, the console and
  `cmdkey` are started by their full paths in System32, never by bare name, which would find a
  copy in the app's own folder first.
- **What PowerShell prints is read out as plain text.** With its output captured, Windows
  PowerShell writes some messages as XML ("#< CLIXML"), including a "Preparing modules for first
  use" progress record. The app drops progress records and turns errors back into plain lines
  before showing or speaking them.
- **The installed copy isn't one bundled exe.** The single exe unpacks some of .NET's own
  libraries to the user's TEMP when it starts, where another program could replace them, and
  that first start took 23 seconds in the test VM. The installer packs the app's files as they
  are, so nothing is unpacked. They're in `%LocalAppData%\HyperVManage`, which programs running as
  you can write to, as with any per-user install; a per-machine install in Program Files would
  close that too.
- **Pages open as you, not as administrator.** Help's links and Report a Bug ask the desktop's own
  shell to open the page, so the browser never runs elevated. Explorer's command line, used
  before, can't open an address with a query in it: it opens the Documents folder instead.
- **VMs are addressed by id.** Hyper-V allows two VMs with the same name, and `Get-VM -Name`
  reads `* ? [ ]` as wildcards, so names are never used to find a VM to act on.
- **The list is updated in place.** Replacing it would move a screen reader back to the top
  every ten seconds.
- **Session pictures are taken inside the VM**, by a one-off scheduled task that runs as the
  signed-in user in their session (vmtest captures the same way). The app reaches Windows in the
  VM with `New-PSSession -VMId`, finds the active session with `quser`, and writes the capture
  script to `C:\ProgramData\HyperVManage` there; the task is removed and the picture deleted from
  the VM as soon as it has been copied out. The sign-in goes to PowerShell in an environment
  variable, never on a command line, which Windows can log, and the variable is cleared before
  anything else runs.
- **Screenshots come from Hyper-V's WMI classes**, since no cmdlet takes one:
  `Msvm_VirtualSystemManagementService.GetVirtualSystemThumbnailImage`. Hyper-V refuses a
  picture larger than the VM's screen is now, so the app reads that size from the VM's
  `Msvm_VideoHead` and asks for exactly it. Failing that it asks for the same shape within
  1024 by 768 (plain 1024 by 768 when the size can't be read), then 640 by 480. The pixels
  come as 16-bit RGB565 after a 4-byte header that holds the data's length; the app drops the
  header only when it says exactly that, and refuses data of any other size rather than show a
  shifted picture.

## Testing it on a real machine

Done so far, on 2 October 2026: building a VM with New Virtual Machine on an Arm64 PC and on an
x64 PC, each on "Your network"; Connect with Remote Desktop; Open Console; Pause.

Still to do, on a PC with Hyper-V:

1. Confirm the list matches `Get-VM`.
2. Start, Shut Down, Save and Resume a test VM, and Turn Off one that is running.
3. Connect with Remote Desktop and confirm sound plays, and that `ipconfig` in the session shows
   the address the list shows.
4. Save Connection File, then open the file from the desktop.
5. In Settings, change the network and the start setting on a running VM; then shut it down
   and change processors and memory.
6. Checkpoint it, change something, Apply Checkpoint and confirm the change is gone. Do it again
   with the VM running, paused and saved, and with the keep-it-first box checked and unchecked.
   Clone it, then Delete the clone and confirm its folder and disk are gone.
7. Connect to a VM from another computer on the network.
8. Screenshot a running VM at its sign-in screen and at the desktop, a paused one, and one
   whose display has gone to sleep (it should say blank). Have JAWS Picture Smart and NVDA
   describe the picture, paste it into a web page, and save it. Then the same over Remote
   Desktop: with the window open, minimized, and closed.
