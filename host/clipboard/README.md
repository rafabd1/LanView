# Clipboard bridge

This optional helper shares new clipboard selections during a LanView session. It uses the existing SSH connection and the desktop's `wlr-data-control` interface. It opens no network listener and starts no persistent service. Connecting does not send the current clipboard.

The native Wayland helper reads new text or copied-file offers and publishes text or file offers requested by the authenticated SSH client. File offers include `text/uri-list` and `x-special/gnome-copied-files` for file managers such as Thunar. The Python supervisor transfers only files named by a new local clipboard selection. Paths supplied by a remote peer can address only its private incoming transfer directory.

## Build and install

Dependencies: CMake 3.20 or later, a C++20 compiler, pkg-config, Wayland client development files, `wayland-scanner`, Bash, and Python 3.11 or later. The Wayland protocol XML comes from [wlr-protocols](https://github.com/swaywm/wlr-protocols/blob/master/unstable/wlr-data-control-unstable-v1.xml); its license is included in the file.

Run `bash host/clipboard/build-install.sh` as the desktop user. This runs tests without opening the real clipboard and installs `~/.local/bin/lanview-bridge` plus private helpers under `~/.local/libexec/lanview`. Optional arguments select a build directory and installation prefix.

Start the bridge through an authenticated SSH session. Its stdin and stdout carry UTF-8 JSON lines. The peer sends `ping` every five seconds. EOF or 35 seconds without an incoming message closes the Wayland connection. Clipboard ownership ends with the session; file content already received remains in `~/.cache/lanview/clipboard` so a file-manager operation can finish. These completed cache directories can be removed when no paste operation uses them. Incomplete transfers are removed on cancellation or disconnect.

Limits are 1 MiB of UTF-8 text, 4,096 manifest entries, 2 GiB per selection, and 64 KiB per decoded file chunk. Downloads are limited to 8 MiB/s. Transfers reject symlinks, special files, traversal, file names that conflict on Windows, and files changed since selection. Incoming files appear in the clipboard only after their declared bytes arrive. Copying a file never deletes its source.

The bridge reports errors without clipboard contents or local source paths. It handles the standard clipboard, not primary selection. Desktop support for `wlr-data-control` is required. The streaming session remains responsible for mouse, keyboard, audio, and video.
