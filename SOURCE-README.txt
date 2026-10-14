OwnDesk / 巫妖远控 v0.2.1

Windows x64, .NET Framework 4.8+. C# WinForms; no NuGet dependencies.
build.ps1 produces OwnDesk.exe and OwnDesk.Update.exe using the system csc.
build-setup.ps1 -PackageDirectory <staged app> -Destination <output> creates
a single EXE containing the staged application files. Installer source is in
installer/InstallerMain.cs. Generated payload-hash source is a build temporary.

The unmodified official RustDesk executable supplies screen/input/clipboard/
file-transfer sessions. Its corresponding source archives are in upstream/.
Expand the hbb_common archive into rustdesk-1.5.0/libs/hbb_common, matching
commit 229b904508364c8997aad0fb5af57effac859f60. Upstream build instructions:
https://rustdesk.com/docs/en/dev/build/windows/

Installer machine setup requests a visible Windows administrator permission.
The official Tailscale MSI is downloaded only when required, over HTTPS with
a pinned SHA256. New hosts get a random permanent RustDesk credential;
existing hosts retain their original credential. Changed host options are
backed up for removal. OwnDesk user data remains outside the install directory.

Updates: tools/Publish-Update.ps1 prepares a manager ZIP and signed latest.json.
The publisher's DPAPI private-key file is NOT included. UpdateTrust.cs contains
only the public key and chosen GitHub URL. Releases use the public project feed;
online verification is recorded separately in the release assets.
Authentication: RSA PKCS1 SHA256 signature, signed ZIP digest, per-file digests,
safe ZIP paths, product/architecture checks and numeric version comparison.
The update helper backs up files and requires a startup acknowledgement;
failed transactions attempt rollback. Manager updates do not replace engine
files or write device credentials. RustDesk maintains its own official updates.

Runtime on-disk display keys in RustDesk_default.toml [options] use underscores:
image_quality, custom_image_quality, view_style; custom-fps and codec-preference
retain hyphens. File clipboard is enable-file-copy-paste. Existing peer display
settings can override defaults. Host options use the installed engine CLI.

Pairing protocol version 1 carries a device name, private IPv4 and host secret.
Pair codes are credentials, not encrypted envelopes. Stored secrets use Windows
DPAPI CurrentUser; device exports omit them. The official CLI receives the
secret transiently when starting a paired session. It is not logged or exported.

QQ screenshot directly dragged into a remote browser is NOT implemented here.
ScreenshotShelf only captures an image on explicit request, saves local PNG and
starts a local OS file drag. Image clipboard/file-copy defaults are enabled;
actual remote website/QQ compatibility remains unverified.

Checks: OwnDesk.exe --self-test <absolute report path>. Fixtures are isolated.
Previews: --render <absolute PNG path> --devices / --host / --help / --updates.
Feed verification: --verify-update <signed feed> <absolute report path>.
Installer checks: --verify-payload <report> and --render-setup <PNG>.
These verification modes do not install system components or open remote sessions.

Local data: %LOCALAPPDATA%\OwnDesk
Default program root: %LOCALAPPDATA%\Programs\OwnDesk
No independent security audit or commercial Authenticode certificate is claimed.
Actual system setup, VPN coexistence, two-person remote sessions and reboot
access require validation on the intended computers.
