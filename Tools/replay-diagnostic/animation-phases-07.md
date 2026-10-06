# Native ordinary RSI layer phases (diagnostic 0.7)

Scene and completion-summary identities are `ss14-diagnostic/0.7` and
`ss14-diagnostic-summary/0.7`; required capability is `native-rsi-layer-phases/1`.
The pinned game/engine and 0.6 frozen material/source definitions are unchanged.
This is converter presentation on replay ticks, not the original player's
render-driven phase. No recorded-player-eye or SyncSprite clock parity is claimed.

## Ownership and compatibility

0.7 shared sprite layers omit `animationFrame`, `animationTimeLeft`, `autoAnimated`
and `reversed` from JSON and equality/hash. Absence is not a zero/default entity
sample. RSI identity/state, visibility, layer loop/cycle and appearance remain;
the sprite head also exports native `loop`. Older 0.6 retains its old fields and
meaning. Consumers explicitly dispatch by source identity/capability; existing
captures are not reinterpreted or rewritten.

Each logical snapshot/delta frame has `spritePhaseReplacements`, a list of:

```text
{ entityId, spriteId, sampleReplayTime100ns,
  unavailableReason: null | "native-realtime-sync",
  layers: [{ index, animationFrame, animationTimeLeft, autoAnimated, reversed }] }
```

Identity follows existing entity/sprite rules. `index` is the native layer index;
frame is a folded RSI frame index, time-left is finite native float32. Include
all effective valid multi-frame RSI layers, including invisible, paused, stopped
and manually controlled layers. Single-frame RSI/direct PNG needs no phase.
SyncSprite owners get an empty list with the explicit unavailable reason because
the pinned native system reads RealTime, not ReplayTime.

## Baselines, reset and exact samples

Initial state and each new/replaced appearance require a complete owner baseline.
New hidden layers receive actual native values, never another layer's sample.
Commit a whole logical frame atomically: entity changes first; changed sprite ID,
sprite removal or deletion clears phase state; matching replacements second.
Unchanged entity upserts with the same sprite ID retain phases. Empty replacement
means clear, not retain. Reject duplicate owners across frame chunks, duplicate or
invalid layer indices, stale sprite IDs, missing required layers or invalid frames.

New sample time equals the source frame's exact replay-relative 100 ns time.
Compare phase scalars and unavailable reason, excluding timestamp alone. Repeated
frame indices with changed time-left/flags remain distinct replacements. Identical
values retain the previous sample/time. Browser holds sampled frames without
extrapolation. Checkpoints reset/install the complete retained phase map with
original timestamps, which can precede checkpoint time.

Canonical frames retain every captured replacement at its original sequence/time.
Only render publication may coalesce. Seek applies all replacements through the
requested frame. Ordered audio remains separate and lossless.

## Native membership and bounds

Inspect all retained exported entities, including phase-free/static owners, using
reusable scalar/membership storage. Detect sprite removal/addition, RSI/state/layer
membership, visibility, layer loop/cycle and sprite loop changes independently of
network/move candidates. Recapture appearance before the same-frame phase baseline.
Do not run full material/bounds/JSON capture per owner per tick.

Queue eligible ordinary owners through public SpriteSystem.ForceUpdate before
native FrameUpdate with adjacent replay-time deltas. Never use cached IsInert for
eligibility: queued inert recomputation is processed inside FrameUpdate. Native
pause and auto-animation rules remain authoritative. No engine patch is needed.

Header `phaseLimits` declares owners=250000, layersPerOwner=256 and
retainedBytes=67108864. The phase ceiling is a sublimit within combined retained
world/checkpoint/residency guards, not extra capacity; Go/web must account live and
staged typed phase payloads and wrappers. This source change does not implement
Go/web 0.7 consumers.

Existing source JSONL admission is 64 MiB per record and 1 GiB total. Initial
source snapshots remain chunked. Package initial/checkpoint state may use existing
paging, but a complete typed delta is indivisible: Go appendDelta uses
messageSize(1, frame) <= 4193792 bytes (4 MiB minus 512 envelope allowance), with
entities, audio, tiles and phases combined. Enclosing object stays <= 4 MiB.
Explicitly fail oversized delta. Source chunks do not relax that limit; no new
cross-object atomic delta protocol belongs to this slice.

## Focused native proof

Optional `--assert-ordinary-phase entityId:layerIndex` asserts runtime SyncSprite
absence and a valid multi-frame RSI on that owner, without changing native state.
Completion summary holds bounded scalar samples for this diagnostic assertion.
The single approved three-second real capture emitted 66 complete frames through
2.1666645 seconds, then failed the unchanged 128 MiB sprite-definition byte guard.
The asserted stationary computer retained one sprite ID and advanced its native
RSI phase, including across two seconds; no completion summary was produced.
This is partial native sampling evidence only. It is not a completed capture or
Go/web checkpoint/seek proof.

## Blocking appearance residency finding

Independent scalar inspection correctly discovers continuously animated sprite
offsets. The existing appearance dictionary then copies each entire layer array
for every distinct offset. The partial capture contains 10,890 definitions, of
which 4,875 differ from an earlier definition only in sprite offset; those copies
consume approximately 118.5 MB of serialized definition JSON. Observed affected
sprites have 73–103 layers. The unchanged guard rejects the next definition.

Do not omit these native offsets, silently narrow appearance inspection, or raise
the guard to call this proof complete. Before freezing 0.7 for consumers, resolve
this residency issue in the source contract. The smallest evidenced extension is
an owner-local sampled sprite offset, independent of shared layer definitions,
with baseline/replacement/reset rules and accounting inside combined world and
checkpoint budgets. It remains a proposed follow-up, not an implemented field in
this draft. Other changed appearance scalars still use the existing dictionary.
