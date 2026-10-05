# Optional native tile diagnostic

Pass `--tiles true` to the version-pinned diagnostic reader to write `tiles.jsonl`
beside `scene.jsonl`. The scene remains `ss14-diagnostic/0.2`; no tile records are
inserted into it. `summary.json.tiles.enabled`, `file`, `schema`, and `sceneSchema`
explicitly identify the companion. When disabled, `enabled` is false and `file`
is null. Consumers must use that capability declaration rather than discover
possibly older files in a reused output directory.

A successful process exit and complete streams are required in addition to the
capability declaration. Require every scene/tile frame and all of its chunk
indices through the declared frame count, matching identity/clock, and the
scene's final `resources` inventory. Capability metadata alone is not proof of
completion. Before truncating either diagnostic stream, the reader removes any
previous `summary.json`; a new summary is published only after scene output and
tile Finish succeed. Failed runs can leave partial diagnostic streams, which
must be rejected.

This is a bounded diagnostic for the existing ten-second clip, not a finalized
transport. It uses existing public APIs in RobustToolbox
`36905986f6809420dbc78168fc494f91723d356b` / engine 289.0.3. No additional engine
patch is required. The game and resource bundle remain pinned by the reader.

## Identity and records

The first record has `kind: "tile-header"` and
`schema: "ss14-diagnostic-tiles/0.1"`. It declares `sceneSchema`,
`capability: "grid-tiles"`, `gameBuild`, `engineVersion`, `frameCount`, `timeUnit`,
`sourceClockOrigin100ns`, and `scope`. Scope contains the same `gameBuild`,
`forkId`, `bundleSha256`, `roundId`, and `sourceStartTick` as the scene header.
Reject mismatched companions. Integer times are in 100 ns units.

`coordinateSpace` is `grid-local-tile-indices`. `exportChunkSize` is 16 and
`pixelsPerMeter` is 32. These export chunks organize tile data independently
of the engine's internal chunk storage. Chunk `(cx,cy)` begins at tile index
`(cx*16,cy*16)`. Floor division defines chunk indices for negative coordinates.

`tile-definition` records precede their first use. Fields are:

- `typeId`: native numeric tile type; meaningful only within the pinned scope.
- `prototype`: the resolved native prototype ID, not a guessed mapping.
- `variants`, `allowRotationMirror`, and `edgeSpritePriority`: native definition
  values.
- `image`: null or `{path,width,height}` for the original bundle PNG.
- `variantPixelRects`: `[x,y,width,height]` rectangles in top-left PNG coordinates.
  Variant `v` uses `[v*32,0,32,32]`. The PNG must have width `32*variants` and
  height 32, as required by the native tile atlas builder.
- `edgeSprites`: `{direction,image,atlasRotationDegrees}` entries. The rotation
  is the signed degree value applied to the source PNG by the native atlas
  builder; every edge PNG is 32x32. Higher `edgeSpritePriority` wins when both
  adjacent tiles provide an edge, following native rendering rules.

Type 0 is the resolved empty/Space definition. Empty tiles are implicit and do
not draw. The header's `errorTileImage` describes the native engine's built-in
`/Textures/noTile.png`, whose body is outside the verified game bundle. It is
unused in the bounded fixture. Nonempty tiles without a sprite are rejected
rather than introduce that additional engine-resource dependency. Definition
image references resolve against the verified original game bundle, not a
runtime-generated tile atlas. PNG signatures, IHDR dimensions, type mappings,
variants, and rotation/mirroring ranges are checked during export.

## Frame application

Each replay frame has one or more `tile-frame` records, including frames with
no changes. Fields are `sequence`, `chunkIndex`, `chunkCount`, `snapshot`,
`sourceTick`, `sourceTime100ns`, `sourceServerTime100ns`, `gridUpserts`,
`gridDeletes`, and `chunks`. Chunk indices start at 0. Apply the complete frame
atomically after all its records arrive. Frame identity and clock must match
the corresponding scene frame:

`sourceServerTime100ns = sourceClockOrigin100ns + sourceTime100ns`.

Only record 0 contains grid upserts/deletes. A grid upsert is `{id,tileSize}`;
grid IDs refer to actual scene entities with `isGrid: true`. Use that entity's
existing parent/local transform/rotation to place its tiles. Tile data does not
flatten grids or substitute floor sprites. A grid delete removes all its cached
chunks. It may represent native entity deletion or removal of the grid component.

Each chunk is `{gridId,x,y,action,tiles}`:

- `replace`: discard previous chunk contents and insert the listed nonempty
  tiles; absent coordinates become empty. Initial chunks and new grid chunks
  use this action, as do changed chunks in later frames.
- `remove`: discard the whole chunk; `tiles` is empty.

Each tile tuple is `[localX,localY,typeId,flags,variant,rotationMirroring]`.
Local indices are 0..15. Preserve all four native tile fields. Raw
rotation/mirroring is 0..7; the native renderer applies it only when the tile
definition permits rotation/mirroring. For exact UV transformations, see
`Clyde.GridRendering.cs:WriteTileToBuffers`; do not infer another rotation
convention. Chunks and their tiles are sorted by grid ID, chunk X/Y, then local
X/Y. Definitions are emitted deterministically at first encounter.

## Bounds and focused verification

The header declares limits: 4,096 grids, 4,096 definitions, 32,768 retained or
dirty export chunks, 500,000 nonempty tiles, and 256 MiB companion output. Each
frame record carries at most 64 chunks. Dirty chunk keys are coalesced per native
step and cleared after projection; there is no retained event history. Native
tile events are observed by a diagnostic entity system subscribed before the
native event bus freezes, then connected only after initial replay loading.
`NativeTileCapture` owns one run's projection cache, resource catalog, output
stream, and subscription. `CaptureRunner` only coordinates Start/Capture/Finish;
its using scope disposes the listener and stream on success or failure. No tile
state is stored in the runner's sprite or hierarchy caches.
The runner's native texture/move/deletion hooks are released in a finally block,
then StopReplay unloads the replay. These caches are capture-scoped; this change
does not retain a mutable native world or prototype registry across rounds.

After every frame the projected nonempty count must equal the native count on
each grid. At the end, one full enumeration compares every surviving native
tile's coordinates and all four values against the projected cache. The summary
records counts, observed creation/deletion and chunk changes, validated resource
count, native final tiles checked, stage time, and additional output bytes.

The scene's `missing: ["tile-chunks", ...]` describes the scene stream itself.
The companion capability supplies tiles separately; it does not imply that
other missing renderer inputs, including runtime shader parameters, are present.
