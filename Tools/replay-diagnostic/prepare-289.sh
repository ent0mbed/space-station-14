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
reader_patches=("$script_dir/robust-289.0.3.patch" "$script_dir/robust-289.0.3-animation-eligibility.patch" "$script_dir/robust-289.0.3-animation-boundaries.patch" "$script_dir/robust-289.0.3-light-mask-observation.patch")
# Later patches overlap earlier context. Validate a fully prepared tree by
# reversing the stack in a temporary index, never in the real source/index.
reader_patch_paths=()
while IFS= read -r reader_path; do
    reader_patch_paths+=("$reader_path")
done < <(awk '$1 == "+++" && substr($2, 1, 2) == "b/" { print substr($2, 3) }' "${reader_patches[@]}" | sort -u)
reader_patch_index=$(mktemp "${TMPDIR:-/tmp}/ss14-reader-patches.XXXXXX")
rm -- "$reader_patch_index"
reader_patches_applied=false
if (
    export GIT_INDEX_FILE="$reader_patch_index"
    git -C "$reader_tree/RobustToolbox" read-tree HEAD || exit 1
    git -C "$reader_tree/RobustToolbox" add -A -- "${reader_patch_paths[@]}" || exit 1
    for ((reader_patch_number=${#reader_patches[@]}-1; reader_patch_number>=0; reader_patch_number--)); do
        git -C "$reader_tree/RobustToolbox" apply --cached --reverse "${reader_patches[reader_patch_number]}" || exit 1
    done
) >/dev/null 2>&1; then
    reader_patches_applied=true
fi
rm -f -- "$reader_patch_index"
if ! "$reader_patches_applied"; then
    for reader_patch in "${reader_patches[@]}"; do
        if ! git -C "$reader_tree/RobustToolbox" apply --reverse --check "$reader_patch" >/dev/null 2>&1; then
            git -C "$reader_tree/RobustToolbox" apply --check "$reader_patch"
            git -C "$reader_tree/RobustToolbox" apply "$reader_patch"
        fi
    done
fi
mkdir -p "$reader_tree/Content.Replay.Diagnostic"
cp "$source_root/Content.Replay.Diagnostic/"*.cs \
    "$source_root/Content.Replay.Diagnostic/"*.csproj \
    "$source_root/Content.Replay.Diagnostic/runtimeconfig.template.json" "$reader_tree/Content.Replay.Diagnostic/"
