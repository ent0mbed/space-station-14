#!/usr/bin/env bash
# Build the explicit 289.0.3 reader in a separate checkout; the current game tree stays untouched.
set -euo pipefail

game_revision=94087a918a2fae4571f5a529fe14ef7f5dce29a3
engine_revision=36905986f6809420dbc78168fc494f91723d356b
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
source_root=$(git -C "$script_dir" rev-parse --show-toplevel)
reader_tree=$(mktemp -d "${TMPDIR:-/tmp}/ss14-replay-289.XXXXXX")
# Keep MSBuild's project root and working directory consistent across macOS /var aliases.
reader_tree=$(cd -- "$reader_tree" && pwd -P)

if ! git -C "$source_root" cat-file -e "$game_revision^{commit}"; then
    git -C "$source_root" fetch https://github.com/space-wizards/space-station-14.git "$game_revision"
fi
git -C "$source_root" worktree add --detach "$reader_tree" "$game_revision"
git -C "$reader_tree" submodule update --init --recursive
if [[ $(git -C "$reader_tree/RobustToolbox" rev-parse HEAD) != "$engine_revision" ]]; then
    printf 'Unexpected engine revision; refusing to run this adapter.\n' >&2
    exit 1
fi
git -C "$reader_tree/RobustToolbox" apply --check "$script_dir/robust-289.0.3.patch"
git -C "$reader_tree/RobustToolbox" apply "$script_dir/robust-289.0.3.patch"
mkdir "$reader_tree/Content.Replay.Diagnostic"
cp "$source_root/Content.Replay.Diagnostic/"*.cs \
    "$source_root/Content.Replay.Diagnostic/"*.csproj "$reader_tree/Content.Replay.Diagnostic/"

dotnet build "$reader_tree/Content.Replay.Diagnostic/Content.Replay.Diagnostic.csproj" \
    -c Release -m:2 -p:RobustToolsBuild=false
printf 'Version-pinned reader checkout: %s\n' "$reader_tree"
# Native prototype/entity initialization needs more than 2 GiB for the verified station fixture.
export DOTNET_GCHeapHardLimit=${DOTNET_GCHeapHardLimit:-0x200000000}
cd "$reader_tree/bin/Content.Replay.Diagnostic"
exec dotnet Content.Replay.Diagnostic.dll "$@"
