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

Current exports use diagnostic and summary **0.13** and always require
`native-viewer-metadata-chat/1`. Entity/grid upserts include the exact native
`name`, including empty names. Snapshot chunk 0 carries the full station/player
baseline; subsequent frames carry upserts/removals. Stations use native network
IDs, names and grid membership. Players use their recorded GUID key, account
name, status and nullable attached network entity ID. A nonempty native player
list replaces the roster; an empty native list means no update. Detach changes
the attachment to null rather than removing the player. Valid references remain
recorded even when their targets are absent from the current graph.

Chat uses independent `chat-events` records, including messages in frame 0.
Events preserve native frame order and ordinals in the playback-filtered message
list returned by `GetMessages(index)`. Native resource/prototype uploads are already
removed; ordinals count every remaining message type rather than chat alone.
Replay-scoped `sequence:messageIndex` IDs are deterministic for the same native
replay and reader. Events store native post-accent `Message`
text, channel name/value, nullable sender ID, available speaker name and `hideChat`.
No markup is evaluated, messages are not coalesced, and wrapped formatting is
outside this narrow contract. Chat has no native emission timestamp: its clock
is the containing native replay frame. The clip preserves recorded messages;
native recording settings can omit messages before export. Header limits and
the summary describe bounded metadata membership, text, events and accounting.

Exact and visual presentation remain separate policies; visual holds countdown-only
sprite changes. `--lighting-observations true` adds point/map, occluder and resolved roof observations with
exact presentation; visual plus lighting is rejected before replay/resource access.
Every lighting-enabled snapshot chunk/delta includes complete lighting and roof observation groups,
including explicit empty arrays. Native component removals have owner-ID deletes;
ambient component removal replaces the map baseline with explicit absence.
The helper also applies `robust-289.0.3-light-mask-observation.patch`, whose
read-only accessor exposes the client's actual resolved mask without touching
renderer state. A declared mask prototype does not substitute for that observation.

Lighting samples run after native frame update, independently of sprites/network
dirty candidates. All initialized point/map owners and their transform parents are
retained, including paused, disabled, container-occluded and client-created owners. All
world/light/parent references use the scene's native network `NetEntity.Id` space,
valid positive int32 IDs, rather than the client's local `EntityUid.Id`. Native
client IDs set bit 30 (`1 << 30`) and remain positive. Server network IDs preserve
recorded identity across replay entity recreation; client IDs identify entities in
the current native replay execution and have no cross-run stability promise.
No identity cast, owner omission or new remapping is introduced by this profile.
Point RGB is straight sRGB; ambient RGB is native linear. Offsets/radii are meters;
native `Angle.Theta` is double and is explicitly narrowed to finite float32 radians
for this contract; the observation does not preserve double angular precision.
Known whole-image masks reference preceding image definitions; body availability
is separate. Unmapped/generated textures and atlas regions have explicit unavailable
reasons. The 250000 combined point/map/occluder membership limit and 64 MiB live/staged
lighting accounting are fixed. This accounting covers owned lighting values,
membership projections and removals; it is not an all-allocation/process cap.
Snapshot chunks use views over the owned replacement lists, and lighting frames
serialize once through the unchanged 64 MiB record/1 GiB scene output guards.
Canonical Go additionally applies its combined 512 MiB guard.
These capabilities preserve inputs and do not establish native rendering parity.

`native-resolved-roof-observations/1` stores roof-bearing grid membership and
nonempty 8×8 coverage chunks independently of the optional floor-image export.
Grid records include `ownerId`, positive uint16 `tileSize` and required `implicit`
pass classification. Ordered color layers contain native float32 sRGB/alpha and
signed int32 grid-local tile origins, with one occurrence per grid/tile. Implicit
roofs take precedence; explicit bits and contributor colors are resolved by the
pinned native `SharedRoofSystem.GetColor`. `TurfSystem.IsSpace` excludes tiles
whose definition uses `MapAtmosphere`; floor presence does not imply a roof.
The snapshot is complete; later transactions replace/delete only affected chunks.
Grid replacement preserves chunks; grid deletion removes them all. An observed
roof grid may have empty coverage. Snapshot arrays use 1000-item slices; deltas
remain one transaction subject to the existing record/output guards.

Roof membership is enumerated once. Native tile, replay component-state, lifecycle,
contributor/ancestor movement, lookup and prototype invalidations select affected
grids; retained scalar stamps detect local changes. Stable grid movement uses the
scene pose and retains immutable local geometry. Roof limits are separate:
4096 grids, 32768 chunks, 500000 unique grid/tile pairs and 64 MiB live/pending/
resolution/invalidation accounting. Within a grid, chunks cover disjoint cells.
Identity queues, contributor records, ancestry links and refresh scratch reserve
their byte accounting before growth, including the baseline queue's transient peak.
Native implicit-before-explicit passes are identified, but viewport-dependent
grid order within a pass remains an unproven overlap/color/alpha parity constraint.
Differently colored overlapping `IsRoof` contributors have a separate unproven
selection constraint: unrelated native lookup-tree changes may alter the first
native contributor without a covered invalidation. Resolved samples use the native
winner when a grid is inspected; this exporter does not claim exact selection parity
for that ambiguous case.
Focused BCL checks:

```sh
dotnet run -c Release --project Tools/replay-diagnostic/roof-observation-tests/RoofObservationTests.csproj
```

The `resources` command also retrieves the original pinned shader bodies needed by
the first TypeScript viewer's point/mask and wall passes: `light-soft.swsl`, its
`light_shared.swsl` and `shadow_cast_shared.swsl` includes,
`wall-bleed-blur.swsl`/`wall-merge.swsl`, and the engine's
`base-raw.frag`/`base-raw.vert` wrappers. Together
with the existing sprite source and three sprite wrappers this is an exact
eleven-file engine allowlist. Every body retains its pinned size, SHA-256, origin,
source URL and original MIT notice. Downloads remain bounded and publish only
after the whole cache validates. FOV and unused native passes are outside
this closure; retrieving source bodies does not enable rendering support.

Regenerate a cache produced before this allowlist change by using a fresh `--cache`
directory. Intermediate cache manifests are internal artifacts; this reader does
not migrate them or retain a separate four-file compatibility path.

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
