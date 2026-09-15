#!/usr/bin/env bash
set -euo pipefail

test_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
source "$test_root/lanview-host"
test_tmp=$(mktemp -d)
trap 'rm -r -- "$test_tmp"' EXIT

assert() { "$@" || { printf 'FAIL: %s\n' "$*" >&2; exit 1; }; }
reject() { if "$@" 2>/dev/null; then printf 'Unexpected success: %s\n' "$*" >&2; exit 1; fi; }

assert is_private_ipv4 192.168.1.100
assert is_private_ipv4 10.0.0.2
assert is_private_ipv4 172.31.255.1
reject is_private_ipv4 172.32.0.1
reject is_private_ipv4 127.0.0.1
reject is_private_ipv4 192.168.1.256
reject is_private_ipv4 192.168.001.1
reject is_private_ipv4 '192.168.1.1;echo unexpected'

printf '%s\n' 'bind_address=192.168.1.100' 'capture=kms' 'output_name=eDP-1' > "$test_tmp/profile"
assert read_profile "$test_tmp/profile"
assert test "$lv_capture" = kms
assert test "$lv_output" = eDP-1
assert test "$lv_bind" = 192.168.1.100
printf '%s\n' 'capture=$(touch should-not-exist)' > "$test_tmp/profile"
reject read_profile "$test_tmp/profile"
printf '%s\n' 'global_prep_cmd=unexpected' > "$test_tmp/profile"
reject read_profile "$test_tmp/profile"

mkdir -p "$test_tmp/pci/0000:01:00.0/power" "$test_tmp/pci/0000:01:00.1/power"
printf '0x10de\n' > "$test_tmp/pci/0000:01:00.0/vendor"
printf '0x030200\n' > "$test_tmp/pci/0000:01:00.0/class"
printf 'suspended\n' > "$test_tmp/pci/0000:01:00.0/power/runtime_status"
printf '0x10de\n' > "$test_tmp/pci/0000:01:00.1/vendor"
printf '0x040300\n' > "$test_tmp/pci/0000:01:00.1/class"
assert gpu_state "$test_tmp/pci"
assert test "$lv_gpu_status" = suspended
assert test "$lv_gpu_pci" = 0000:01:00.0

if command -v jq >/dev/null; then
    umask 077
    printf '%s\n' '{"username":"test","salt":"abcdefghijklmnop","password":"0000000000000000000000000000000000000000000000000000000000000000"}' > "$test_tmp/credentials.json"
    assert check_credentials "$test_tmp/credentials.json"
    printf '%s\n' '{"username":"","salt":"abcdefghijklmnop","password":"0000000000000000000000000000000000000000000000000000000000000000"}' > "$test_tmp/credentials.json"
    reject check_credentials "$test_tmp/credentials.json"
    printf '%s\n' '{"username":"test"}' > "$test_tmp/credentials.json"
    reject check_credentials "$test_tmp/credentials.json"
    reject check_credentials "$test_tmp/missing.json"
else
    printf 'SKIP: credential JSON checks require jq.\n'
fi

unit_state() { lv_unit_state=active; }
printf '\n\n' | lease_loop
reject lease_loop <<< 'unexpected command'

test_calls=0
systemctl() { (( test_calls += 1 )); }
owned_unit() { return 1; }
reject stop_host
assert test "$test_calls" = 0
owned_unit() { return 0; }
assert stop_host
assert test "$test_calls" = 1

printf 'Host helper checks passed.\n'
