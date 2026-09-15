# Third-party software

The Windows package contains these separate components:

- [Moonlight PC 6.1.0](https://github.com/moonlight-stream/moonlight-qt/releases/tag/v6.1.0): unmodified official portable client, GNU GPL version 3. The package preserves its DLLs and data files and adds the upstream license texts under `licenses/moonlight`.
- [.NET 10.0.11](https://github.com/dotnet/runtime/tree/v10.0.11) and [Windows Forms](https://github.com/dotnet/winforms/tree/v10.0.11): included in the self-contained Windows executable. Their MIT licenses and available third-party notices are copied from the exact runtime packages into `licenses`.
- The Windows installer uses [Inno Setup 6.7.3](https://github.com/jrsoftware/issrc/tree/is-6_7_3). Its license is installed under `licenses/inno-setup`. The portable ZIP does not contain Inno Setup.

The package build provides Moonlight's `MoonlightSrc-6.1.0.tar.gz` source archive as a separate artifact alongside the Windows ZIP and installer. The package's `SOURCE.md` records its origin, checksum and source-access instructions. The upstream source release includes [these prebuilt library revisions](https://github.com/cgutman/moonlight-qt-prebuilts/tree/a27d6a7995ef504963fa9058c69e6ba1b449cc0f); their build project is [moonlight-deps](https://github.com/cgutman/moonlight-deps).

Moonlight's bundled libraries keep their own licenses. Their source locations include [Qt 6.7.2](https://download.qt.io/archive/qt/6.7/6.7.2/single/), [FFmpeg 7.0.2](https://ffmpeg.org/releases/ffmpeg-7.0.2.tar.xz), [SDL 2 at revision 10b4a79](https://github.com/libsdl-org/SDL/tree/10b4a79), [SDL_ttf 2.22.0](https://github.com/libsdl-org/SDL_ttf/releases/tag/release-2.22.0), [OpenSSL 3.3.2](https://github.com/openssl/openssl/releases/tag/openssl-3.3.2), and [dav1d 1.4.3](https://code.videolan.org/videolan/dav1d/-/tree/1.4.3). Qt uses GPL/LGPL and component-specific terms; FFmpeg's build configuration controls its LGPL/GPL terms; SDL uses the zlib license, OpenSSL uses Apache-2.0 and dav1d uses BSD-2-Clause. Refer to those sources and the included notices for the full terms.

The following components are installed separately and are not included as binaries in the Windows ZIP:

- [Sunshine](https://github.com/LizardByte/Sunshine): Linux streaming host, GNU GPL version 3.
- [OpenSSH](https://www.openssh.com/): the existing trusted SSH client and server.
- Linux packages needed by the host helper, including Bash, systemd, util-linux and jq, plus dependencies listed in [the host guide](host/README.md).
