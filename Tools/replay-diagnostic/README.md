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

`--lighting-observations true` selects diagnostic **0.10** with exact sprite
presentation. Default exact **0.8** and opt-in visual **0.9** keep their existing
fields/capabilities; visual plus lighting is rejected before replay/resource access.
Every 0.10 snapshot chunk/delta includes a complete point/map observation group,
including explicit empty arrays. Native component removals have owner-ID deletes;
ambient component removal replaces the map baseline with explicit absence.
The helper also applies `robust-289.0.3-light-mask-observation.patch`, whose
read-only accessor exposes the client's actual resolved mask without touching
renderer state. A declared mask prototype does not substitute for that observation.

Lighting samples run after native frame update, independently of sprites/network
dirty candidates. All initialized point/map owners and their transform parents are
retained, including paused, disabled, container-occluded and client-created owners. IDs use
the scene's existing native `NetEntity.Id` space, including negative client IDs.
Point RGB is straight sRGB; ambient RGB is native linear. Offsets/radii are meters;
native mask angles are narrowed to finite float32 radians for this contract.
Known whole-image masks reference preceding image definitions; body availability
is separate. Unmapped/generated textures and atlas regions have explicit unavailable
reasons. The 250000 combined point/map membership limit and 64 MiB live/staged
lighting accounting are fixed; canonical Go additionally applies its combined
512 MiB guard. This capability preserves inputs and does not claim lighting
rendering, roofs, emission, shadows, FOV, sun or AO completeness.

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

Ordinary animation eligibility reuses cached selection for stable owners in this pinned adapter.
Published sprite and RSI mutations and inspection must stay on the engine thread.
Owners that share native layers are classified on every pass.

The clean bootstrap/build/help check was verified on **Debian 13, Linux x64**,
using .NET 10.0.100 and an existing NuGet package cache. It did not perform another
replay export. macOS and Windows have not been verified. The helper requires Bash;
Windows also needs symbolic-link support for the runtime content module links.

See [animation-phases-07.md](animation-phases-07.md) for owner presentation
semantics, [visual-presentation-09.md](visual-presentation-09.md) for the opt-in
visual policy, and [minute-preview.md](minute-preview.md) for named clip limits.
