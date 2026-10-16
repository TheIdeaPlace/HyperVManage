# Security Policy

## Reporting a vulnerability

If you find a security problem in Hyper-V Manage or New-HyperVRdpVM.ps1, please email
**kelly@theideaplace.net** rather than opening a public issue.

Please include what the problem is, how to reproduce it, and what it would let someone do.
I'll reply within 48 hours and work with you on a fix and on when to make it public.

Hyper-V Manage runs as administrator, so anything that lets another program change what it runs
or loads is in scope.

## What an Ubuntu VM build downloads and keeps

New Ubuntu Virtual Machine, and `New-HyperVRdpVM.ps1 -Ubuntu`, download Ubuntu's desktop ISO from
ubuntu.com over HTTPS, and check it against the SHA-256 checksums Ubuntu publishes beside it,
also over HTTPS. Ubuntu's GPG signature on those checksums isn't checked. An ISO that doesn't
match is deleted. The ISO is kept in your own `%LocalAppData%`.

The password is given to Ubuntu's installer only as a SHA-512 crypt hash. GNOME Remote Desktop,
inside the VM, needs the password itself to check Remote Desktop's sign-in; it is kept in the
user's home folder, readable only by that user, which is where GNOME Remote Desktop keeps it on a
PC without a TPM. The VM is built without a virtual TPM for that reason. Until the install
finishes, the password is also on the installer disk beside the VM's own disk, which the build
deletes once Ubuntu is installed. The VM runs an SSH server that accepts the password, as Remote
Desktop does.
