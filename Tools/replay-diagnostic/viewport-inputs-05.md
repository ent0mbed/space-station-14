# Native CPU viewport and tile-edge inputs

The scene and success summary use `ss14-diagnostic/0.5` and
`ss14-diagnostic-summary/0.5`. They require
`["native-shader-copy-bindings/1", "native-audio-timing-metadata/1", "native-sprite-local-bounds/1"]`.
Shader-copy and audio values keep their 0.4 semantics. The optional tile companion
uses `ss14-diagnostic-tiles/0.2`, `sceneSchema: "ss14-diagnostic/0.5"`, and
`requiredCapabilities: ["native-tile-edge-inputs/1"]`. Consumers must reject
unsupported versions/capabilities; these are diagnostic inputs, not a finalized
wire protocol. The game, resource bundle and RobustToolbox
`36905986f6809420dbc78168fc494f91723d356b` / engine 289.0.3 remain pinned.

## Sprite local bounds

Every interned sprite definition adds
`nativeLocalBounds: [left,bottom,right,top]`. These four finite, ordered coordinates
are in local meters with Y up. Zero-size bounds are valid. Capture calls public
`SpriteSystem.GetLocalBounds((uid, component))` after native presentation. The
immutable value participates in definition equality and hashing, so bounds-only
changes select a new definition rather than reuse stale bounds.

Native bounds include sprite scale and the bounds of contributing layers,
including their scale/offset/rotation allowances and native RSI direction rules.
They exclude the sprite's own offset and rotation. Invisible, blank, and
shader-copy source layers do not contribute. Use the captured sprite offset,
rotation, NoRotation flag, entity world pose and chosen eye rotation to place
the box; follow `SpriteSystem.CalculateBounds` / `Clyde.Sprite.cs` rather than
scaling it again. This permits native CPU viewport admission before checking
material support or demanding image bodies, without inferring dimensions from
unavailable shader parameters.

These are the native CPU bounds used for visibility and sorting. They do not
guarantee coverage of arbitrary shader expansion, encode exact sprite-tree proxy
padding, supply an original recorded eye, or make intersecting unsupported
materials renderable. Bounds capture uses cached native resources and no GPU
readback or extra resource load.

## Edge setting and native chunk presence

The tile header adds:

```json
{"renderTileEdges":true,"renderTileEdgesSource":"converter-client-cvar:render.tile_edges"}
```

The boolean is the converter replay runtime's effective `CVars.RenderTileEdges`.
The reader asserts it remains unchanged on every captured frame and at Finish.
The pinned CVar is client-only and defaults true; recording saves replicated
CVars, so this value is not the original player's edge setting. Do not replace
it with an assumed original-client value. False disables the edge pass.

Tile 0.2 requires native chunks to be exactly 16x16, matching `exportChunkSize`.
The reader proves that size with public `GridTileToChunkIndices` conversions and
rejects another native layout. Chunk records now preserve native presence:

- `replace` with nonempty tuples creates/replaces a present native chunk; omitted
  cells are empty tile type 0.
- `replace` with `tiles: []` preserves a present native chunk containing only
  empty cells. It must not be normalized into a removal.
- `remove` means the native chunk is absent.

Initial capture enumerates native chunks including their empty cells. Later
frames reconcile native presence even when an empty chunk changes without a
changed-tile event. Native/projected presence and nonempty counts must agree
after every frame; Finish checks every cell, including empty cells, in every
surviving native chunk. Summary counts distinguish retained chunks and empty
chunks and report native final chunks checked. No synthetic chunks are added.

The existing definitions and one-cell neighbor lookup supply the edge inputs.
For each cell in a present chunk, including empty cells, visit `nx=-1..1` and
then `ny=-1..1`, skipping (0,0). A neighbor must belong to a present chunk, have
a different type, provide edges, and have strictly higher edge priority. Draw
that neighbor type's edge for the opposite direction onto the current cell.
Skip an absent direction. Use the recorded atlas rotation, edge variant 0 and
no tile rotation/mirroring. Draw each chunk's base tiles followed by its edges,
in chunk X/Y and cell X/Y order. A changed chunk invalidates its own and adjacent
chunks' cached edge geometry.

Grid-ID sorting in this diagnostic stream is transport order, not native painter
order. Native ordered overlapping-grid capture is deferred; consumers must keep
the overlapping-grid rejection. This capability does not supply lighting,
overlays, pixel snapping, shader parameters or native fixture-based visibility.
