# Connecting to Linux with Windows' Remote Desktop

Windows' Remote Desktop Connection (mstsc) isn't only for Windows. Remote Desktop
is a protocol, RDP, and Linux has RDP servers of its own. Install or turn one on,
and the Remote Desktop app you already use connects to a Linux desktop the same
way it connects to a Windows PC: picture, keyboard, mouse, clipboard and, with
the right server, sound.

For a screen reader user, the sound is the point. Orca, Linux's screen reader,
speaks inside the Linux machine. Over Remote Desktop, that speech is played on
your Windows PC, so you hear Orca as you would if you were sitting at the Linux
machine. That makes a Linux machine you can't hear directly usable: a virtual
machine with no sound card, a server in a cupboard, or a PC in another room.

This guide is general, and not tied to Hyper-V Manage. The Hyper-V Manage
section near the end says what that app sets up for you.

## The short version

1. On the Linux machine, turn on an RDP server: GNOME Remote Desktop on Ubuntu
   and other GNOME desktops, KRdp on KDE Plasma, or xrdp for other desktops.
2. Give it a user name and password.
3. On Windows, open Remote Desktop Connection, type the Linux machine's name or
   address, and connect with that user name and password.
4. In Remote Desktop's options, under Local Resources, set Remote audio to Play
   on this computer.

## Which server to use

There are three, and the desktop you run decides which.

| Linux desktop | Server | Notes |
|---|---|---|
| GNOME, as on Ubuntu, Fedora and Debian's default | GNOME Remote Desktop | Comes with the desktop. Sends sound. |
| KDE Plasma 6 | KRdp | Comes with recent Plasma. Not tried for this guide. |
| Xfce, MATE, Cinnamon and other X11 desktops | xrdp | A separate package. Sound needs an extra module. |

### GNOME Remote Desktop

GNOME's own server, part of the desktop on Ubuntu 22.04 and later. It has three
ways of working, and the difference matters:

- **Desktop Sharing.** You share the session that's already on the machine's
  screen. Someone has to be signed in at the machine, and the session must not
  be locked, since a locked screen asks for the password over Remote Desktop.
  Turn it on in Settings, System, Remote Desktop, Desktop Sharing.
- **Remote Login.** Remote Desktop connects to the machine's sign-in screen, and
  you sign in there, so you sign in twice: once to Remote Desktop, once to the
  desktop. Nobody needs to be signed in at the machine. Turn it on in Settings,
  System, Remote Desktop, Remote Login (GNOME 46 and later; Ubuntu 24.04 and
  later). For a screen reader user, the second sign-in screen is the hard part:
  Orca has to be on at the sign-in screen as well, and its speech has to reach
  you before you've signed in.
- **Headless.** A desktop session runs on the machine with no screen at all,
  and GNOME Remote Desktop serves it directly. Remote Desktop goes straight to
  that desktop, with one sign-in and no screen at the machine. This is the most
  comfortable for a screen reader, and it's what Hyper-V Manage sets up, but
  it's set up from the command line (see [Headless](#gnome-headless-one-sign-in)
  below).

GNOME Remote Desktop sends sound over Remote Desktop. On Ubuntu 26.04 (GNOME
Remote Desktop 50) it was checked in the headless mode: speech from Orca and from
speech-dispatcher, and an ordinary sound file, all reached the Remote Desktop
client. The other two modes weren't checked for sound.

### xrdp

The long-standing RDP server for Linux. It starts its own X11 desktop session
for each connection, so it works with X11 desktops such as Xfce and MATE.
Ubuntu's GNOME no longer has an X11 session, so xrdp isn't the choice there.

For sound it needs a module for the Linux sound system: `pipewire-module-xrdp`
on current Ubuntu and Debian, or `pulseaudio-module-xrdp` on older systems.

xrdp is also what Hyper-V's "enhanced session" uses for Linux: the same
Remote Desktop protocol, carried over Hyper-V's own connection rather than the
network.

xrdp wasn't tried for this guide.

## Setting up GNOME Remote Desktop

### From Settings (Desktop Sharing or Remote Login)

1. Open Settings, System, Remote Desktop.
2. Turn on Desktop Sharing, or open Remote Login and turn it on.
3. Turn on Remote Control, so your keyboard reaches the Linux machine and not
   just the picture.
4. Under Login Details, note or set the user name and password. These are
   Remote Desktop's own, not necessarily your Linux account's.

### From the command line

`grdctl` sets the same things. For Remote Login, the system-wide server, every
command needs `sudo` and `--system`:

```sh
# A certificate for the server (Remote Desktop's traffic is encrypted).
G=/var/lib/gnome-remote-desktop/.local/share/gnome-remote-desktop
sudo install -d -o gnome-remote-desktop -g gnome-remote-desktop -m 700 "$G"
sudo -u gnome-remote-desktop openssl req -new -newkey rsa:3072 -days 3650 -nodes -x509 \
    -subj "/CN=$(hostname)" -keyout "$G/rdp-tls.key" -out "$G/rdp-tls.crt"

sudo grdctl --system rdp set-tls-key "$G/rdp-tls.key"
sudo grdctl --system rdp set-tls-cert "$G/rdp-tls.crt"
sudo grdctl --system rdp set-credentials 'remote-user' 'remote-password'
sudo grdctl --system rdp enable
sudo systemctl enable --now gnome-remote-desktop.service
sudo grdctl --system status --show-credentials
```

On Ubuntu 26.04, `set-credentials` took the user name and password as
arguments; read from standard input, they weren't saved.

### GNOME headless: one sign-in

This is what goes straight to the desktop with Orca already talking. It's two
pieces:

1. **A desktop session with no screen, started at boot.** GDM 50 (Ubuntu 26.04)
   has `gdm-new-session USER --headless`, which starts one. Run as root from a
   systemd service, so it starts with the machine and starts again if you sign
   out:

   ```ini
   # /etc/systemd/system/remote-session.service
   [Unit]
   Description=Desktop session for Remote Desktop
   Requires=gdm.service
   After=gdm.service

   [Service]
   ExecStart=/usr/libexec/gdm-new-session YOURUSER --headless
   Restart=always
   RestartSec=5

   [Install]
   WantedBy=graphical.target
   ```

   GDM ships a `gnome-headless-session@.service` for the same job, but on Ubuntu
   26.04 its sandboxing stopped it from reaching GDM ("The connection is
   closed"), so the service above is used instead.

2. **GNOME Remote Desktop's headless server inside that session.** It runs as
   the user, on Remote Desktop's usual port, 3389, so turn off the system-wide
   Remote Login server first (`sudo systemctl disable --now
   gnome-remote-desktop.service`). Then, as the user:

   ```sh
   systemctl --user enable gnome-remote-desktop-headless.service
   G=~/.local/share/gnome-remote-desktop
   mkdir -p "$G"
   openssl req -new -newkey rsa:3072 -days 3650 -nodes -x509 -subj "/CN=$(hostname)" \
       -keyout "$G/rdp-tls.key" -out "$G/rdp-tls.crt"
   grdctl --headless rdp set-tls-key "$G/rdp-tls.key"
   grdctl --headless rdp set-tls-cert "$G/rdp-tls.crt"
   grdctl --headless rdp set-credentials 'YOURUSER' 'your-password'
   gsettings set org.gnome.desktop.remote-desktop.rdp view-only false
   gsettings set org.gnome.desktop.remote-desktop.rdp enable true
   ```

   Then restart the session service: `sudo systemctl restart remote-session`.

Also worth doing for this mode:

- **Turn off the screen lock and blanking.** A locked session asks for the
  password over Remote Desktop:
  `gsettings set org.gnome.desktop.screensaver lock-enabled false` and
  `gsettings set org.gnome.desktop.session idle-delay 0`.
- **Turn Orca on for the session:**
  `gsettings set org.gnome.desktop.a11y.applications screen-reader-enabled true`.
- **On a machine with no sound card,** such as a virtual machine, give PipeWire
  a virtual speaker for Remote Desktop to play. Without one, PipeWire's
  stand-in output was seen to leave speech stuck until something else played.
  Put this in `/etc/pipewire/pipewire.conf.d/50-remote-desktop.conf` and
  restart the session:

  ```
  context.objects = [
    { factory = adapter
      args = {
        factory.name     = support.null-audio-sink
        node.name        = "remote-desktop-speaker"
        node.description = "Remote Desktop"
        media.class      = "Audio/Sink"
        audio.position   = [ FL FR ]
        priority.session = 2000
      }
    }
  ]
  ```

## Connecting from Windows

1. Open Remote Desktop Connection: press Windows+R, type `mstsc`, press Enter.
2. In Computer, type the Linux machine's address. Its name followed by `.local`
   usually works on a home network (Ubuntu answers to it through Avahi), for
   example `ubuntu-pc.local`. Otherwise use its IP address, which `ip addr` on
   the Linux machine shows.
3. Choose Show Options, then Local Resources. Under Remote audio, choose
   Settings and set Play on this computer. Here you can also send your
   microphone to Linux.
4. Connect. When asked, sign in with the Remote Desktop user name and password
   you set on Linux. A `DOMAIN\` in front of the user name is ignored, so a
   saved sign-in such as `ubuntu-pc\kelly` works too.
5. Windows warns that it can't verify the certificate, since it's one you made.
   Accept it.

Save the connection (Show Options, General, Save As) to get a `.rdp` file you
can open next time. In that file, `audiomode:i:0` is the setting that plays
sound on your PC.

## With a screen reader

- **You hear Orca, from Linux.** Your Windows screen reader keeps reading
  Windows: the Remote Desktop window's title, and the Remote Desktop prompts.
  Once connected, keys go to Linux and Orca answers.
- **Modifier keys.** Orca's key is Insert in its desktop layout, and Caps Lock in
  its laptop layout. JAWS and NVDA use the same keys, and they may take them
  before Remote Desktop sends them on. If Orca doesn't respond to its own
  commands, set Orca to the other layout, or change your Windows screen
  reader's modifier while you use Remote Desktop. This wasn't tested for this
  guide.
- **Windows key combinations.** Remote Desktop's options, Local Resources,
  Keyboard, decide whether keys like Alt+Tab go to Linux. Choose On the remote
  computer, or Only when using the full screen.
- **Speech delay.** Sound over Remote Desktop is a little behind the screen.
  Expect Orca to feel slightly slower than on the machine itself.

## When it doesn't work

- **It connects to a sign-in screen, not your desktop.** That's Remote Login,
  not Desktop Sharing or headless. Either sign in there, or set up one of the
  others.
- **No sound.** Check that Remote audio is set to Play on this computer. With
  xrdp, check that its sound module is installed. With GNOME, check there's an
  output device (`wpctl status`), and on a machine without one, add the virtual
  speaker above.
- **It refuses the password.** GNOME Remote Desktop's sign-in is its own, set
  with `grdctl` or in Settings, and can differ from the Linux account's.
- **Nothing answers.** Check that something is listening on the Linux machine
  with `ss -ltn | grep 3389`. Check that its firewall allows port 3389. Note
  that only one server can use port 3389 at a time.
- **Testing without Windows.** FreeRDP (`xfreerdp3`) is a Linux Remote Desktop
  client, useful for checking the server from the Linux side.

## In Hyper-V Manage

Hyper-V gives a virtual machine no sound card, so Remote Desktop is the only
way to hear one. Hyper-V Manage's New Ubuntu Virtual Machine sets up the
headless mode above when it builds the VM. Its Connect with Remote Desktop and
Save Connection File then work as they do for Windows VMs. The script's
[README](../hyperv-rdp-vm/README.md#ubuntu) says exactly what it configures.
