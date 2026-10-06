#!/usr/bin/env bash
# Build the explicit 289.0.3 reader in a separate checkout; the current game tree stays untouched.
set -euo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
source "$script_dir/prepare-289.sh"

dotnet build "$reader_tree/Content.Replay.Diagnostic/Content.Replay.Diagnostic.csproj" \
    -c Release -m:2 -p:RobustToolsBuild=false
printf 'Version-pinned reader checkout: %s\n' "$reader_tree"
# Native prototype/entity initialization needs more than 2 GiB for the verified station fixture.
export DOTNET_GCHeapHardLimit=${DOTNET_GCHeapHardLimit:-0x200000000}
cd "$reader_tree/bin/Content.Replay.Diagnostic"
exec dotnet Content.Replay.Diagnostic.dll "$@"
