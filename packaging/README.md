# LanView for Windows

Run `LanView-0.1.0-Setup-win-x64.exe` to install LanView for the current user. It needs no administrator access and includes .NET and Moonlight 6.1.0; there is no separate Moonlight selection or download. The default folder is `%USERPROFILE%\.local\share\LanView\App`. Start LanView from its Start menu or desktop shortcut. Close the app and its video window before upgrading.

For the portable ZIP, extract the whole archive into a writable folder and open `LanView.exe`. Keep the `tools` folder beside it. Both packages use the included Moonlight client automatically and download no components when the app starts.

Windows OpenSSH Client must already be installed. Set up SSH key access to the Linux computer and verify its host key before connecting. LanView uses that trusted connection; it does not configure SSH keys, accept passwords or trust unknown host keys.

The Linux computer needs a working Wayland session, Sunshine, NVIDIA NVENC and the host helpers in `host`. Clipboard sharing also needs Python 3.11 or later and a compositor with `wlr-data-control`, such as Hyprland. Follow [the Linux setup guide](host/README.md) and [clipboard setup](host/clipboard/README.md) for the required packages and the CMake build/install script. The Windows bundle does not replace those Linux requirements.

Open LanView, enter the Linux computer's private IPv4 address and SSH username, then save the profile. Use **Parear** (Pair) for the first connection and confirm Moonlight's PIN in Sunshine. **Conectar** (Connect) opens the desktop in Moonlight. **Desconectar** (Disconnect) releases the session. The current Windows interface uses Portuguese labels.

While the video window has focus, keyboard shortcuts go to Linux. Click outside it to return control to Windows; no release shortcut is needed. Windows still handles `Ctrl+Alt+Del`.

Enable **Sincronizar texto e arquivos copiados durante a sessão** for two-way copy and paste of plain text, files and folders. Use normal Copy and Paste commands, for example between Windows Explorer and Thunar. Files become available to paste after their transfer finishes. Content copied before connecting or enabling sharing is not sent.

Turning sharing off stops it during the session and cancels pending transfers without closing the video. It does not erase content already received. Clipboard status messages do not contain copied text or file contents.

Limits per selection are 2 GiB across 4,096 files and folders, or 1 MiB of UTF-8 text. File transfers are capped at 8 MiB/s. Clipboard images, rich-text formats and Linux primary selection are not supported. Direct drag-and-drop between desktops is not implemented.

Completed file transfers stay in `%USERPROFILE%\.cache\LanView\Clipboard` on Windows and `~/.cache/lanview/clipboard` on Linux. There is no automatic cleanup of completed transfers. Remove their cache directories only after all paste operations finish. Canceled, incomplete transfers are removed.

The profile stays in `%USERPROFILE%\.config\LanView\profile.json`, outside the app folder. Pairing state also stays outside the app folder. Uninstalling removes installed program files and shortcuts but keeps the profile, pairing state and received-file caches. Updating the app does not require pairing again.

This is a clean package. It contains no saved connection profile, SSH key, password, certificate or pre-paired identity.

The client requests 1080p at 60 FPS, hardware decoding and HEVC 4:4:4. This preserves more color detail than 4:2:0, but does not make compression mathematically lossless. Actual negotiation and performance depend on both computers and the LAN. Moonlight shows its own startup messages and opens a separate video window.

See [THIRD_PARTY.md](THIRD_PARTY.md) for component licenses and [SOURCE.md](SOURCE.md) for the accompanying source archive. LanView source and build scripts are in `source/LanView`.
