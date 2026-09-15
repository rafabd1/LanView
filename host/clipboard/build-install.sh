#!/usr/bin/env bash
set -euo pipefail
[[ $EUID -ne 0 ]] || { printf 'Build and install as the desktop user.\n' >&2; exit 2; }
source_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
build_dir=${1:-$source_dir/build}
prefix=${2:-$HOME/.local}
[[ $prefix == /* && $prefix != / && $build_dir != / ]] || { printf 'Use an absolute user installation prefix.\n' >&2; exit 2; }
cmake -S "$source_dir" -B "$build_dir" -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$prefix"
cmake --build "$build_dir" --parallel 2
"$build_dir/lanview-clipboard-wayland" --self-test
python3 -m unittest discover -s "$source_dir/tests" -v
cmake --install "$build_dir"
