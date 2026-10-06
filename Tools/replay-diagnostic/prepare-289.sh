#!/usr/bin/env bash
# Shared release/development preparation; source this file from either helper.
game_revision=94087a918a2fae4571f5a529fe14ef7f5dce29a3
engine_revision=36905986f6809420dbc78168fc494f91723d356b
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
source_root=$(git -C "$script_dir" rev-parse --show-toplevel)
reader_tree=${reader_tree:-$(mktemp -d "${TMPDIR:-/tmp}/ss14-replay-289.XXXXXX")}
# Keep MSBuild's project root and working directory consistent across macOS /var aliases.
reader_tree=$(cd -- "$reader_tree" && pwd -P)

if ! git -C "$source_root" cat-file -e "$game_revision^{commit}"; then
    git -C "$source_root" fetch https://github.com/space-wizards/space-station-14.git "$game_revision"
fi
if ! git -C "$reader_tree" rev-parse --show-toplevel >/dev/null 2>&1; then
    git -C "$source_root" worktree add --detach "$reader_tree" "$game_revision"
fi
if [[ $(git -C "$reader_tree" rev-parse HEAD) != "$game_revision" ]]; then
    printf 'Unexpected game revision; refusing to use this reader checkout.\n' >&2
    exit 1
fi
git -C "$reader_tree" submodule update --init --recursive
if [[ $(git -C "$reader_tree/RobustToolbox" rev-parse HEAD) != "$engine_revision" ]]; then
    printf 'Unexpected engine revision; refusing to run this adapter.\n' >&2
    exit 1
fi
for reader_patch in robust-289.0.3.patch robust-289.0.3-animation-eligibility.patch; do
    if ! git -C "$reader_tree/RobustToolbox" apply --reverse --check "$script_dir/$reader_patch" >/dev/null 2>&1; then
        git -C "$reader_tree/RobustToolbox" apply --check "$script_dir/$reader_patch"
        git -C "$reader_tree/RobustToolbox" apply "$script_dir/$reader_patch"
    fi
done
mkdir -p "$reader_tree/Content.Replay.Diagnostic"
cp "$source_root/Content.Replay.Diagnostic/"*.cs \
    "$source_root/Content.Replay.Diagnostic/"*.csproj \
    "$source_root/Content.Replay.Diagnostic/runtimeconfig.template.json" "$reader_tree/Content.Replay.Diagnostic/"
