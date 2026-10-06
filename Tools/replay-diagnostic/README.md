# Version-pinned replay diagnostic reader

This adapter reads engine **289.0.3**, game build
`94087a918a2fae4571f5a529fe14ef7f5dce29a3`. It exports bounded diagnostic JSONL;
the Go `replay-pack` command turns a completed capture into a browser package.

Install Git, Bash and the **.NET SDK 10.0.100** (the SDK requested by both the
source checkout and pinned game checkout). Public GitHub repositories and NuGet
feeds must be reachable for the initial checkout/restore. Existing NuGet packages
can be reused. No private adapter assemblies, resource paths or credentials are
required.

From this repository checkout:

```sh
bash Tools/replay-diagnostic/run-289.sh --help
```

The helper creates a separate temporary worktree, checks out the pinned game and
recursive engine submodules, applies `robust-289.0.3.patch`, copies this adapter's
source and builds Release with `RobustToolsBuild=false`. It prints the reader
worktree path and starts the adapter from `bin/Content.Replay.Diagnostic`, where
the engine's relative resource mount resolves correctly. Help exits without
opening replay/resources or starting the engine. The worktree remains available
for reuse and inspection.

For the verified public three-second fixture, download both original ZIPs:

```sh
mkdir -p replay-inputs
curl -fL -o replay-inputs/replay.zip \
  'https://replays.spacestation14.com/leviathan/2026/10/05/leviathan-2026_10_05-01_43-round_114689.zip'
curl -fL -o replay-inputs/SS14.Client.zip \
  'https://wizards.cdn.spacestation14.com/fork/wizards/version/94087a918a2fae4571f5a529fe14ef7f5dce29a3/file/SS14.Client.zip'

input_dir=$(cd replay-inputs && pwd)
capture_dir="$PWD/replay-capture"
bash Tools/replay-diagnostic/run-289.sh \
  --input "$input_dir/replay.zip" --resources "$input_dir/SS14.Client.zip" \
  --output "$capture_dir" --profile ten-second --seconds 3 --tiles true
```

Use absolute input/resource/output paths: the helper changes its working
directory before running the adapter. Use a new output directory. Success
produces `scene.jsonl`, `tiles.jsonl` and `summary.json`; a partial directory
without a success summary is a failed capture. The adapter verifies the resource
bundle's SHA-256 against the replay metadata. These fixture SHA-256 values are:

| File | SHA-256 |
| --- | --- |
| Replay ZIP | `88d1d04542f8ae1a72334757187a77d0842ee068fefa1d8b1143348ff58dac25` |
| Client ZIP | `cb7f1c2e9d2397717cdff6401f1f50085c43cac9234408cdc6d101a71e87d857` |

The helper defaults `DOTNET_GCHeapHardLimit` to `0x200000000` (8 GiB), unless
already set. The verified three-second native run used about 3.4 GiB peak RSS;
the heap ceiling is not a total process memory bound. Capture guards remain
unchanged.

The clean bootstrap/build/help check was verified on **Debian 13, Linux x64**,
using .NET 10.0.100 and an existing NuGet package cache. It did not perform another
replay export. macOS and Windows have not been verified. The helper requires Bash;
Windows also needs symbolic-link support for the runtime content module links.

See [animation-phases-07.md](animation-phases-07.md) for owner presentation
semantics, and [minute-preview.md](minute-preview.md) for named clip limits.
