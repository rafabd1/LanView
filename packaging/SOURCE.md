# Source code and notices

The Windows bundle includes the unmodified Moonlight Portable x64 6.1.0 binary from the [official release](https://github.com/moonlight-stream/moonlight-qt/releases/tag/v6.1.0). Moonlight is licensed under GNU GPL version 3. Its license and the source archive's component notices are in `licenses/moonlight`.

The package build also provides **MoonlightSrc-6.1.0.tar.gz** beside the Windows ZIP and installer. This is the source archive published with the same upstream release. It is separate from the runnable packages.

- [Official binary download](https://github.com/moonlight-stream/moonlight-qt/releases/download/v6.1.0/MoonlightPortable-x64-6.1.0.zip): SHA256 `95F4D0853A31C7FCED4B6D233DDF55EE41720963F2E2620A9CB49A21D112AED1`.
- [Official source download](https://github.com/moonlight-stream/moonlight-qt/releases/download/v6.1.0/MoonlightSrc-6.1.0.tar.gz): SHA256 `696CC470A62E2F2E9B77739D400B389E7578C9510383C08614007C92BE49D5B0`.
- [Moonlight 6.1.0 source and build instructions](https://github.com/moonlight-stream/moonlight-qt/tree/v6.1.0).

Moonlight's source archive includes submodules and some prebuilt libraries. Additional library source and build locations appear in [THIRD_PARTY.md](THIRD_PARTY.md). Keep the companion source archive, license texts and source directions available with any redistribution. The archive alone does not contain the source of every bundled library.

The LanView source snapshot is in `source/LanView`. The bundled .NET runtime notices are in `licenses/microsoft.netcore.app.runtime.win-x64` and `licenses/microsoft.windowsdesktop.app.runtime.win-x64`. These notices retain the terms of their respective upstream components.
