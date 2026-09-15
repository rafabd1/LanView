#!/usr/bin/python3 -I
"""Install the opt-in, fixed LanView GPU lease and its hold-only sudo rule."""

import argparse
import json
import os
import pwd
import re
import stat
import subprocess
import sys
import tempfile
from pathlib import Path

HELPER = Path("/usr/local/libexec/lanview-gpu-clock")
CONFIG = Path("/etc/lanview/gpu-clock.json")
POLICY = Path("/etc/sudoers.d/lanview-gpu-clock")
RUNTIME = Path("/run/lanview-gpu-clock")
UNIT = "lanview-gpu-clock.service"
ENV = {"PATH": "/usr/bin:/bin", "LANG": "C", "LC_ALL": "C"}


def fail(message):
    raise ValueError(message)


def directory(path):
    if not path.exists() and not path.is_symlink():
        directory(path.parent)
        path.mkdir(mode=0o755)
    for item in [path, *path.parents]:
        info = item.lstat()
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
            fail(f"Unsafe privileged directory: {item}")


def existing_file(path, marker):
    directory(path.parent)
    try:
        info = path.lstat()
    except FileNotFoundError:
        return
    if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
        fail(f"Refusing to replace an unsafe privileged file: {path}")
    if info.st_size > 65536 or marker not in path.read_text(encoding="utf-8"):
        fail(f"Refusing to replace a file not managed by LanView: {path}")


def atomic_write(path, data, mode):
    fd, temporary = tempfile.mkstemp(prefix=".lanview-install-", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--user", required=True, help="Desktop account that runs LanView")
    parser.add_argument("--gpu-pci", required=True, help="Full GPU PCI address, for example 0000:01:00.0")
    parser.add_argument("--clock-mhz", required=True, type=int, help="An explicitly tested supported core clock")
    args = parser.parse_args()
    if os.geteuid() != 0:
        fail("Run this one-time installer through sudo.")
    if not re.fullmatch(r"[a-z_][a-z0-9_-]{0,31}", args.user):
        fail("Invalid desktop account name.")
    account = pwd.getpwnam(args.user)
    if not 1 <= account.pw_uid <= 2147483647:
        fail("Choose a non-root desktop account.")
    if not re.fullmatch(r"[0-9a-fA-F]{4}:[0-9a-fA-F]{2}:[0-9a-fA-F]{2}\.[0-7]", args.gpu_pci):
        fail("Use the GPU's full PCI address.")
    if not 300 <= args.clock_mhz <= 3000:
        fail("The core clock must be between 300 and 3000 MHz.")
    pci = args.gpu_pci.lower()
    device = Path("/sys/bus/pci/devices") / pci
    if device.joinpath("vendor").read_text().strip() != "0x10de" or not device.joinpath("class").read_text().strip().startswith("0x03"):
        fail("The PCI address does not identify an NVIDIA display-class GPU.")
    for binary in ("/usr/bin/systemd-run", "/usr/bin/systemctl", "/usr/bin/nvidia-smi", "/usr/bin/sudo", "/usr/bin/visudo"):
        if not os.path.isfile(binary) or not os.access(binary, os.X_OK):
            fail(f"Missing required installed tool: {binary}")
    supported = subprocess.run(["/usr/bin/nvidia-smi", "-i", pci,
                                "--query-supported-clocks=graphics", "--format=csv,noheader,nounits"],
                               env=ENV, capture_output=True, text=True, timeout=7, check=True).stdout
    supported_mhz = {int(line.strip()) for line in supported.splitlines() if line.strip().isdigit()}
    if args.clock_mhz not in supported_mhz:
        fail("The selected core clock is not in this GPU driver's supported clock list.")
    state = subprocess.run(["/usr/bin/systemctl", "show", UNIT, "--property=ActiveState", "--value"],
                           env=ENV, capture_output=True, text=True, timeout=5, check=True).stdout.strip()
    if state not in {"inactive", "failed"}:
        fail("Stop the existing LanView GPU clock lease before installing.")
    if RUNTIME.exists() or RUNTIME.is_symlink():
        directory(RUNTIME)
        if (RUNTIME / "applied.json").exists() or (RUNTIME / "applied.json").is_symlink():
            fail("A GPU clock ownership marker remains. Restore its clocks before installing.")
    source = Path(__file__).with_name("lanview-gpu-clock")
    if not stat.S_ISREG(source.lstat().st_mode):
        fail("The helper source must be a regular file, not a symlink.")
    helper = source.read_text(encoding="utf-8")
    if not helper.startswith("#!/usr/bin/python3 -I\n# LanView GPU clock helper v1\n"):
        fail("The helper source is not recognized.")
    compile(helper, str(source), "exec")
    existing_file(HELPER, "# LanView GPU clock helper v1\n")
    existing_file(CONFIG, '"managedBy": "LanViewGpuClock/1"')
    existing_file(POLICY, "# LanView GPU clock policy v1\n")
    config = json.dumps({"managedBy": "LanViewGpuClock/1", "uid": account.pw_uid,
                         "gpuPci": pci, "clockMHz": args.clock_mhz}, indent=2) + "\n"
    policy = ("# LanView GPU clock policy v1\n"
              f"{args.user} ALL=(root) NOPASSWD: {HELPER} hold\n")
    fd, temporary = tempfile.mkstemp(prefix=".lanview-policy-", dir=POLICY.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            stream.write(policy)
        subprocess.run(["/usr/bin/visudo", "-c", "-f", temporary], env=ENV,
                       stdout=subprocess.DEVNULL, check=True, timeout=5)
    finally:
        os.unlink(temporary)
    atomic_write(HELPER, helper, 0o755)
    atomic_write(CONFIG, config, 0o600)
    atomic_write(POLICY, policy, 0o440)
    print("Installed the hold-only GPU clock lease. Queried supported clocks; no GPU settings were changed.")
    print("Enable gpu_clock_control=enabled in the desktop user's LanView host.conf to opt in.")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        print(f"LanView GPU installer: {error}", file=sys.stderr)
        sys.exit(1)
