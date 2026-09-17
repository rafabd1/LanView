# LanView

A small Windows app for using a Linux desktop over the LAN.

LanView starts the Linux host through an existing, trusted SSH connection, then opens Moonlight in a normal window. Closing the viewer releases the host session. NVIDIA runtime power management can suspend the GPU after its encoder resources are released.

## Current version

- Native Windows interface and a local connection profile.
- The video window remembers its last normal size, including display scaling.
- Upstream Sunshine and Moonlight handle pairing, video, audio and input.
- Desktop preset: 1080p, 60 FPS, HEVC, hardware decoding, requested 4:4:4 and absolute mouse input.
- Optional two-way copy and paste for plain text, files and folders through SSH.
- Linux host runs as the desktop user, in its own systemd unit.
- An SSH heartbeat owns the session. EOF or a 35-second heartbeat timeout stops that unit.
- GPU status reads PCI sysfs attributes without waking the GPU.
- Optional session-only NVIDIA clock control, with automatic cleanup on lease loss.

Moonlight opens as a separate, normal window. Direct drag-and-drop between desktops, automatic desktop resizing and a built-in video surface are not implemented. The client requests 4:4:4 to preserve color detail, but this does not make HEVC mathematically lossless or prove that 4:4:4 was negotiated. Check the stream diagnostics for the active format.

## Requirements

The Windows installer includes .NET and the official Moonlight 6.1.0 client. It installs for the current user, without administrator access, under `%USERPROFILE%\.local\share\LanView\App` by default and adds Start menu and desktop shortcuts. Moonlight is selected automatically; no separate client or runtime installation is needed. Close LanView and its video window before upgrading. Uninstall removes the installed app files and keeps profiles, pairing state and received-file caches.

For the portable ZIP, extract it completely, keep its `tools` folder beside `LanView.exe`, and open the app. Both forms work without downloading components at startup. Windows OpenSSH Client must already be installed. Establish SSH key access and verify the Linux host key before using the launcher. LanView does not configure keys, accept passwords or automatically trust new SSH host keys.

Linux requires Sunshine, NVIDIA NVENC, a running Wayland session, systemd user services and jq. The current host preset does not select other encoders. Clipboard sharing also needs Python 3.11 or later and a compositor with `wlr-data-control`, such as Hyprland. Build its native helper with CMake and the distribution's Wayland development tools. See [host setup](host/README.md) and [clipboard setup](host/clipboard/README.md). Configure Sunshine's device access using the upstream package's documented setup.

Set up Sunshine on Linux before the first stream. The **Parear** (Pair) button starts the dedicated host and opens Moonlight's pairing flow. Confirm its PIN in the authenticated Sunshine interface. **Conectar** (Connect) starts the saved desktop session; **Desconectar** (Disconnect) releases it. The current Windows interface uses Portuguese labels.

While the video window has focus, keyboard shortcuts go to Linux. In fullscreen, `Alt+Tab` minimizes the video window so you can use Windows; restoring it returns to fullscreen. Other captured shortcuts still go to Linux. In windowed mode, click outside the video to return control to Windows without a release shortcut. Windows still handles `Ctrl+Alt+Del`.

Profiles and upstream credentials remain outside the repository. The Windows profile is `%USERPROFILE%\.config\LanView\profile.json`; the Linux profile lives under `~/.config/lanview`. When the Windows profile does not yet exist, LanView can read the older `%LOCALAPPDATA%\LanView\profile.json` file. Saving writes to the new path.

## Copy and paste

Enable **Sincronizar texto e arquivos copiados durante a sessão** to share new copies in either direction. Use the applications' normal Copy and Paste commands, including in Windows Explorer and Linux file managers that accept copied-file offers, such as Thunar. Files become available to paste only after their transfer finishes. Copying does not remove the source files.

Only new copies made while sharing is active are sent. Connecting or turning sharing on does not send the existing clipboard. Turning the checkbox off takes effect during the session: it stops sharing and cancels pending transfers without closing the video. It does not erase content already received. Clipboard status messages do not contain copied text or file contents.

Each selection can contain up to 2 GiB across 4,096 files and folders. Plain text is limited to 1 MiB in UTF-8. File transfers are capped at 8 MiB/s; the actual rate may be lower. Clipboard images, rich-text formats and Linux primary selection are not supported.

Received files use `%USERPROFILE%\.cache\LanView\Clipboard` on Windows and `~/.cache/lanview/clipboard` on Linux. Incomplete transfers are removed when canceled. Completed transfers stay in the cache after disconnect so an ongoing paste can finish; there is no automatic cache cleanup. Remove completed cache directories only when no paste operation needs them.

## Build and test

Building the Windows package requires the .NET 10 SDK, PowerShell 7 and `tar.exe`. Run from the repository root:

```powershell
pwsh -File scripts/build.ps1
dotnet run --project tests/LanView.Tests -c Debug
dotnet run --project tests/LanView.Clipboard.Tests -c Debug
```

The icon artwork and multi-size Windows icon are in `src/LanView.Windows/Assets`. To regenerate the ICO from the PNG, run `pwsh -File scripts/build-icon.ps1` on Windows.

The build writes `artifacts/LanView-0.1.2-win-x64.zip` and the accompanying `MoonlightSrc-6.1.0.tar.gz` source archive. The ZIP contains a self-contained Windows executable, clean upstream Moonlight files, Linux helper sources, license notices and a LanView source snapshot. It does not include profiles or pairing state.

The script accepts `-MoonlightArchive` and `-MoonlightSourceArchive` paths for cached official downloads. Both archives must match the pinned SHA256 values. `-Offline` disables build downloads and uses locally cached .NET 10.0.11 runtime packs. Without that switch, the build may fetch missing archives and build-time packages. Linux setup still needs the packages and build tools listed in the host guide.

To build the installer, obtain [Inno Setup 6.7.3 from its official release](https://github.com/jrsoftware/issrc/releases/tag/is-6_7_3) and follow the [download verification instructions](https://jrsoftware.org/isdl-verify.php). The official `innosetup-6.7.3.exe` has SHA256 `9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732` and a valid Authenticode signature from Pyrsys B.V. Its [documented portable mode](https://jrsoftware.org/ishelp/topic_technotes.htm) avoids a global compiler installation. For example, run it with `/PORTABLE=1 /CURRENTUSER /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOICONS /TASKS="" /DIR="C:\build-tools\InnoSetup-6.7.3"`.

After building the ZIP, run `pwsh -File scripts/build-installer.ps1 -CompilerPath "C:\build-tools\InnoSetup-6.7.3\ISCC.exe" -MoonlightArchive "C:\downloads\MoonlightPortable-x64-6.1.0.zip"`. This script uses the verified ZIP and its matching `packaging/LanView.iss` source snapshot; it performs no downloads or installation. It writes `artifacts/LanView-0.1.2-Setup-win-x64.exe` and its SHA256 file. Keep the companion Moonlight source archive beside the installer when distributing it. The generated installer is not code-signed.

Use `scripts/test-package.ps1 -MoonlightArchive <official-portable-zip>` to check the ZIP without launching it. The check compares every bundled Moonlight file with the official archive, verifies the package checksums and checks that host protocol sources and license texts are present.

On Linux:

```sh
bash host/tests/test-host.sh
```

## Power and capture

The desktop may run on an integrated GPU while NVENC uses a separate NVIDIA GPU. Capture compatibility and the transfer between GPUs must be tested on the actual machine. LanView does not change the display GPU, driver persistence mode, memory clocks or power limits.

On supported NVIDIA hardware, an administrator can install the optional [session clock helper](host/gpu-clock/README.md). It applies a configured graphics clock while a managed session is active and restores automatic clocks when the session ends or its heartbeat expires. It is disabled by default and must not be combined with another clock manager. The Windows app does not need administrator access.

Stopping the host releases its resources; the kernel and driver decide when to suspend the GPU. Other programs can keep it active. Checking a running process alone does not establish that capture or streaming succeeded.

The host starts with a private LAN bind address, authenticated upstream management, mandatory stream encryption and UPnP disabled. LanView adds no network listener; its host control and clipboard bridge use the existing SSH connection.
