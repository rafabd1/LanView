# Optional NVIDIA session clocks

This helper keeps a chosen graphics clock while a managed LanView session is active. It can reduce latency when the NVIDIA driver's automatic clock policy responds too slowly to short video workloads. Measure the result on your hardware before enabling it.

It requires Linux, systemd, Python 3.11 or later, sudo, and an NVIDIA driver with graphics clock locking support. It does not change the display GPU, memory clocks, power limits or persistence mode. The clock setting affects the whole selected GPU, including other applications using it.

## Install

Choose a supported clock that you have tested. An administrator installs the helper once for a specific desktop account and PCI device. For example, replace the account and device below with your own:

```sh
sudo bash host/gpu-clock/install.sh --user desktopuser --gpu-pci 0000:01:00.0 --clock-mhz 1200
```

The installer queries supported clocks, which can wake the GPU. It does not apply a clock setting or enable a boot service. It creates a root-owned helper at `/usr/local/libexec/lanview-gpu-clock`, a private configuration at `/etc/lanview/gpu-clock.json`, and a sudo rule allowing only the helper's fixed `hold` action for the chosen account. It refuses to overwrite unrelated files.

Set `gpu_clock_control=enabled` in the desktop user's private `~/.config/lanview/host.conf`. New managed sessions will request the clock lease automatically. Leave this setting disabled if the privileged helper is not installed. To disable the feature, disconnect, then change the setting to `disabled`.

## Lifetime and recovery

The helper accepts only empty heartbeat lines over standard input. It applies clocks only when the configured user's LanView Sunshine unit is active. EOF, a 35-second heartbeat timeout, or a change to that Sunshine session ends the lease. Detached `lanview-host start` sessions do not request it.

The system service resets clocks through `ExecStopPost`, including when its worker crashes. It retains a private ownership marker if restoration fails, and retries that restoration before applying a new lease. A second clock lease cannot replace an active one. Inspect failures with `sudo journalctl -u lanview-gpu-clock.service`.

Restoration returns graphics clocks to the driver's defaults; it does not recover a custom clock lock set by another program. Do not combine this feature with another GPU clock manager. Driver failures can prevent restoration and require administrator attention.

Status checks read PCI power state without querying NVIDIA tools. When the session releases its GPU resources, normal driver power management can suspend the GPU.

References: [NVIDIA clock controls](https://docs.nvidia.com/deploy/nvidia-smi/index.html#lgc-lock-gpu-clocks-min-gpu-clock-max-gpu-clock) and [systemd service cleanup](https://www.freedesktop.org/software/systemd/man/latest/systemd.service.html#ExecStopPost=).
