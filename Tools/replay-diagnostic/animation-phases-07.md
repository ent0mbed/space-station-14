# Native owner-local sprite presentation (diagnostic 0.7, provisional)

Scene and completion-summary identities are `ss14-diagnostic/0.7` and
`ss14-diagnostic-summary/0.7`; required capability is
`native-sprite-presentation-samples/1`. This replaces the earlier provisional
phase-only capability; that capture is not reinterpreted as this contract.
The pinned game/engine and 0.6 frozen material/source definitions are unchanged.
This is converter presentation on replay ticks, not the original player's
render-driven phase. No recorded-player-eye or SyncSprite clock parity is claimed.

## Ownership and compatibility

0.7 shared sprite layers omit `animationFrame`, `animationTimeLeft`, `autoAnimated`
and `reversed` from JSON and equality/hash. Absence is not a zero/default entity
sample. RSI identity/state, visibility, layer loop/cycle and appearance remain;
the sprite head also exports native `loop`, but omits sprite-wide `offset` from
serialization/hash/equality and NativeAppearanceMatches. Layer offsets stay in
shared appearance. Older 0.6 retains its old fields and
meaning. Consumers explicitly dispatch by source identity/capability; existing
captures are not reinterpreted or rewritten.

Each logical snapshot/delta frame has `spritePresentationReplacements`, a list of:

```text
{ entityId, spriteId, sampleReplayTime100ns,
  offset: { x, y }, phaseUnavailableReason: null | "native-realtime-sync",
  layers: [{ index, animationFrame, animationTimeLeft, autoAnimated, reversed }] }
```

All record fields are required. Offset is the exact finite native float32
sprite-wide offset, available for every sprite owner, including static,
single-frame/direct-PNG and SyncSprite owners. A zero offset is a real sample,
not missing data. Identity follows existing entity/sprite rules. `index` is the native layer index;
frame is a folded RSI frame index, time-left is finite native float32. Include
all effective valid multi-frame RSI layers, including invisible, paused, stopped
and manually controlled layers. Single-frame RSI/direct PNG needs no phase.
SyncSprite owners retain their available offset and get an empty phase list with
the explicit phaseUnavailableReason because the pinned native system reads
RealTime, not ReplayTime. Never assign or extrapolate an ordinary phase for them.

## Baselines, reset and exact samples

Initial state and each new/replaced appearance require a complete owner baseline
for every sprite owner, including owners with no multi-frame RSI layers.
New hidden layers receive actual native values, never another layer's sample.
Commit a whole logical frame atomically: entity changes first; changed sprite ID,
sprite removal or deletion clears presentation state; matching replacements second.
Unchanged entity upserts with the same sprite ID retain presentation. An empty
layer list clears ordinary phase membership but still retains the sampled offset;
it does not remove owner presentation. Reject duplicate owners across frame chunks, duplicate or
invalid layer indices, stale sprite IDs, missing required layers or invalid frames.

New sample time equals the source frame's exact replay-relative 100 ns time.
Compare offset, phase scalars and phaseUnavailableReason, excluding timestamp alone. Repeated
frame indices with changed time-left/flags remain distinct replacements. Identical
values retain the previous sample/time. Browser holds sampled frames without
extrapolation. Checkpoints reset/install the complete retained presentation map with
original timestamps, which can precede checkpoint time.

Canonical frames retain every captured replacement at its original sequence/time.
Only render publication may coalesce. Seek applies all replacements through the
requested frame. Ordered audio remains separate and lossless. Consumers construct
an effective owner-local presentation without mutating shared definitions, then
use the same offset before culling, sorting, image demand and drawing. Do not
double-apply offset or silently default a missing required baseline to zero.

## Native membership and bounds

Inspect all retained exported entities, including phase-free/static owners, using
reusable scalar/membership storage. Detect sprite removal/addition, RSI/state/layer
membership, visibility, layer loop/cycle and sprite loop changes independently of
network/move candidates. Recapture appearance before the same-frame phase baseline.
Do not run full material/bounds/JSON capture per owner per tick. Offset-only
changes update presentation without recapturing shared appearance.

Native GetLocalBounds(sprite) includes contributing layer offsets and sprite
scale, but excludes the sprite's own offset/rotation. CalculateBounds applies
the latter afterward. Keep shared nativeLocalBounds unchanged for offset-only
updates; preserve the existing world/eye rotation and NoRotation rules when
applying the sampled sprite-wide offset. Other layer/bounds dependencies retain
their existing appearance-capture behavior.

Queue eligible ordinary owners through public SpriteSystem.ForceUpdate before
native FrameUpdate with adjacent replay-time deltas. Never use cached IsInert for
eligibility: queued inert recomputation is processed inside FrameUpdate. Native
pause and auto-animation rules remain authoritative. No engine patch is needed.

Header `presentationLimits` declares owners=250000, layersPerOwner=256 and
retainedBytes=67108864. The presentation ceiling is a sublimit within combined retained
world/checkpoint/residency guards, not extra capacity; Go/web must account live and
staged typed presentation payloads and wrappers, including all empty-phase owners.
This source change does not implement
Go/web 0.7 consumers.

Existing source JSONL admission is 64 MiB per record and 1 GiB total. Initial
source snapshots remain chunked. Package initial/checkpoint state may use existing
paging, but a complete typed delta is indivisible: Go appendDelta uses
messageSize(1, frame) <= 4193792 bytes (4 MiB minus 512 envelope allowance), with
entities, audio, tiles and presentation combined. Enclosing object stays <= 4 MiB.
Explicitly fail oversized delta. Source chunks do not relax that limit; no new
cross-object atomic delta protocol belongs to this slice.

## Focused native proof

Optional `--assert-ordinary-phase entityId:layerIndex` asserts runtime SyncSprite
absence and a valid multi-frame RSI on that owner, without changing native state.
Completion summary holds bounded scalar samples for this diagnostic assertion.
The earlier phase-only three-second real capture emitted 66 complete frames through
2.1666645 seconds, then failed the unchanged 128 MiB sprite-definition byte guard.
The asserted stationary computer retained one sprite ID and advanced its native
RSI phase, including across two seconds; no completion summary was produced.
This is partial native sampling evidence only. It is not a completed capture or
Go/web checkpoint/seek proof.

## Appearance residency boundary

Independent scalar inspection correctly discovers continuously animated sprite
offsets. The existing appearance dictionary then copies each entire layer array
for every distinct offset. The partial capture contains 10,890 definitions, of
which 4,875 differ from an earlier definition only in sprite offset; those copies
consume approximately 118.5 MB of serialized definition JSON. Observed affected
sprites have 73–103 layers. The unchanged guard rejects the next definition.

Owner-local presentation now holds sprite-wide offset and phases in one complete
replacement. This preserves the independent scan and existing guards while
separating sampled owner presentation from bulk layer layout. Rotation, color,
scale and other appearance values still use the existing dictionary: this is not
a general solution to all animated-head residency. If another head scalar causes
the same guard failure, factor the small appearance head from bulk layers and
post-shaders once, rather than adding repeated per-field exceptions. The 0.7
contract remains provisional until focused checks, the authorized three-second
capture and review pass.
