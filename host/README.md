# Linux host

`lanview-host` manages an installed [Sunshine](https://github.com/LizardByte/Sunshine) process in the desktop user's systemd session. Sunshine supplies video, audio, input, encryption, and pairing. This helper adds no listening port.

Install Sunshine, its input-device rules, and `jq` through the distribution. The current helper selects NVIDIA NVENC and requires a compatible NVIDIA GPU and driver. Copy `host/lanview-host` to `~/.local/bin/lanview-host`, make it executable, and copy `host/host.conf.example` to `~/.config/lanview/host.conf`. Set the host's private LAN IPv4 address. Keep the profile, certificates, pairings, and logs outside the repository.

For clipboard sharing on Hyprland or another compositor with `wlr-data-control`, install Python 3.11 or later, CMake, a C++20 compiler, pkg-config, Wayland client development files and `wayland-scanner` through the distribution. From the source root, run `bash host/clipboard/build-install.sh` as the desktop user, not root. The script builds the Wayland helper with CMake, runs its tests and installs `~/.local/bin/lanview-bridge` and its private helpers. The [clipboard guide](clipboard/README.md) describes its limits and cache handling. These Linux dependencies are separate from the self-contained Windows app.

Run `lanview-host configure` to create the configuration without opening the host. Create a Web UI account through Sunshine's official `--creds` command. Set that command's `XDG_CONFIG_HOME` to LanView's upstream directory (`~/.config/lanview/upstream` by default), and pass the generated `sunshine.conf` path before `--creds`. Use a long random password and restrict `credentials.json` to mode `600`. The helper checks that this private credentials file exists before starting; it does not accept a username or password and does not implement authentication.

`capture=portal` uses the desktop's screen-sharing prompt. It requires a Sunshine build with portal support. `capture=kms` uses the permissions supplied by the native Sunshine package and a display selected through `output_name`. KMS may require `cap_sys_admin`; the helper does not grant capabilities or run Sunshine as root. The helper offers only portal and KMS. The display can stay on an integrated GPU while Sunshine opens NVENC on the NVIDIA GPU, subject to the installed capture backend's cross-GPU support.

## Actions

- `status`: one JSON line describing the process and NVIDIA PCI runtime power state. `running` does not prove that a stream or encoder is ready.
- `configure`: render the fixed host configuration while stopped. It opens no service, capture session, or GPU device.
- `run`: start Sunshine and retain ownership of its lifetime. Send an empty line on stdin at least every 35 seconds. EOF, a missed heartbeat, or termination stops the owned service. Use a 5-second heartbeat in a client.
- `start`: start a detached host for manual setup. It remains running until `stop`.
- `stop`: stop only the named LanView service and its child processes.

`run` holds an exclusive session lock. A second client cannot take over an existing session. Streaming and authentication use the normal Moonlight/Sunshine flow. Host lifecycle commands can use an existing authenticated SSH connection.

Status has this shape:

```json
{"running":false,"state":"inactive","gpuRuntimeStatus":"suspended","gpuPci":"0000:01:00.0","capture":"portal"}
```

Status reads `runtime_status` from sysfs without polling NVIDIA management tools. NVENC wakes the GPU when Sunshine opens its encoder. Stopping Sunshine releases its contexts; the driver decides when the GPU suspends, and other applications may keep it active.

For optional session-only graphics clock control, install the [privileged clock helper](gpu-clock/README.md), then set `gpu_clock_control=enabled` in the private host profile. Only managed `run` sessions use it; detached `start` sessions do not. The helper resets clocks when its lease ends, including EOF or a missed heartbeat. It does not enable persistence, change memory clocks or power limits, or select a different display GPU. Installation requires administrator access once; the fixed runtime command uses a narrow permission. Leave the setting disabled when the helper is not installed or another tool manages GPU clocks.

The generated configuration requires stream encryption, disables UPnP, binds to the selected LAN address, and permits Web UI access from LAN addresses. The Web UI uses Sunshine's HTTPS authentication with credentials provisioned before launch. Pair clients through the normal authenticated Sunshine UI. No authentication proxy or alternate pairing path is added.

Sunshine configuration lives under `~/.config/lanview/upstream/sunshine`. The helper rewrites `sunshine.conf` from its fixed settings and `host.conf` on each start; it retains credentials, pairings, and portal tokens. The profile accepts only `bind_address`, `capture`, `output_name`, and `gpu_clock_control`. The package remains responsible for upgrades and device permissions.

Logs are available through `journalctl --user -u app-dev.lizardbyte.app.Sunshine-LanView.service`. Shell checks: `bash -n host/lanview-host` and `bash host/tests/test-host.sh`.

References: [Sunshine configuration](https://docs.lizardbyte.dev/projects/sunshine/latest/md_docs_2configuration.html), [Linux setup](https://docs.lizardbyte.dev/projects/sunshine/latest/md_docs_2getting__started.html), [capture backend selection](https://github.com/LizardByte/Sunshine/blob/master/src/platform/linux/misc.cpp).
