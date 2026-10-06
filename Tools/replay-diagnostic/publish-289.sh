#!/usr/bin/env bash
# Release maintainers compile the pinned game/engine once. End users only unpack the archive.
set -euo pipefail

usage() {
    printf 'Usage: publish-289.sh --rid linux-x64|osx-arm64|osx-x64 --output DIRECTORY [--reader-tree PINNED_CHECKOUT]\n'
}
if [[ ${1:-} == --help || ${1:-} == -h ]]; then usage; exit 0; fi
rid= output= reader_tree=
while [[ $# -gt 0 ]]; do
    if [[ $# -lt 2 ]]; then usage >&2; exit 1; fi
    case "$1" in
        --rid) rid=$2 ;;
        --output) output=$2 ;;
        --reader-tree) reader_tree=$2 ;;
        *) usage >&2; exit 1 ;;
    esac
    shift 2
done
case "$rid" in
    linux-x64) target_os=Linux ;;
    osx-arm64|osx-x64) target_os=MacOS ;;
    *) usage >&2; exit 1 ;;
esac
if [[ -z "$output" || -e "$output" ]]; then
    printf 'Supply a new output directory; existing files are preserved.\n' >&2
    exit 1
fi
output_parent=$(dirname -- "$output")
mkdir -p "$output_parent"
output="$(cd -- "$output_parent" && pwd -P)/$(basename -- "$output")"
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
source "$script_dir/prepare-289.sh"
if [[ -n $(git -C "$source_root" status --porcelain --untracked-files=no) ]]; then
    printf 'Publish requires a clean tracked source checkout so adapterSourceRevision identifies the shipped source.\n' >&2
    exit 1
fi

stage=$(mktemp -d "${TMPDIR:-/tmp}/ss14-release.XXXXXX")
trap 'rm -rf -- "$stage"' EXIT
name="ss14-replay-289.0.3-${game_revision:0:8}-$rid"
package="$stage/$name"
dotnet publish "$reader_tree/Content.Replay.Diagnostic/Content.Replay.Diagnostic.csproj" \
    -c Release -r "$rid" --self-contained true -m:2 \
    -p:TargetOS="$target_os" -p:FullRelease=True -p:RobustToolsBuild=false \
    -p:RobustILLink=false -p:PublishTrimmed=false -p:PublishSingleFile=false \
    -p:ValidateExecutableReferencesMatchSelfContained=false -o "$package"
# The apphost still points to Content.Replay.Diagnostic.dll, preserving native module identity.
mv "$package/Content.Replay.Diagnostic" "$package/ss14-replay"
cp -R "$reader_tree/RobustToolbox/Resources" "$package/Resources"
mkdir "$package/notices"
cp "$source_root/LICENSE.TXT" "$package/notices/Content-LICENSE.TXT"
cp "$reader_tree/RobustToolbox/"LICENSE-*.TXT "$package/notices/"
source_revision=$(git -C "$source_root" rev-parse HEAD)
cat > "$package/reader-manifest.json" <<JSON
{"schema":"ss14-replay-reader/0.1","runtimeIdentifier":"$rid","selfContained":true,"sceneSchema":"ss14-diagnostic/0.8","supportedFork":"wizards","gameBuild":"$game_revision","engineVersion":"289.0.3","engineCommit":"$engine_revision","adapterSourceRevision":"$source_revision"}
JSON
cat > "$package/README.txt" <<'TXT'
SS14 replay reader. No Git, source checkout, SDK, or separately installed .NET runtime is required.
Keep the executable, sibling libraries, and Resources directory together.

  ./ss14-replay --help
  ./ss14-replay inspect --input /path/replay.zip
  ./ss14-replay resources --input /path/replay.zip --cache /path/cache
  ./ss14-replay export --input /path/replay.zip --output /path/capture --cache /path/cache --seconds 10 --tiles true

The reader supports only the game/engine recorded in reader-manifest.json.
Downloading another build cannot make it compatible. Progress uses stderr; resources/inspect return JSON on stdout.
A resource-manifest.json folder can be passed to the Go packer with -resources.
Linux and macOS still require their normal OS runtime libraries. Mac release signing/notarization is a separate maintainer step.
TXT
mkdir -p "$output"
cp -R "$package" "$output/$name"
tar -czf "$output/$name.tar.gz" -C "$stage" "$name"
if command -v sha256sum >/dev/null 2>&1; then
    (cd "$output" && sha256sum "$name.tar.gz" > "$name.tar.gz.sha256")
else
    (cd "$output" && shasum -a 256 "$name.tar.gz" > "$name.tar.gz.sha256")
fi
printf 'Published reader folder: %s\nArchive: %s\n' "$output/$name" "$output/$name.tar.gz"
