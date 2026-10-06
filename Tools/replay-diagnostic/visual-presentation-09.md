# Opt-in visual presentation (diagnostic 0.9)

`export --presentation exact|visual` selects presentation independently of
`--profile ten-second|minute-preview` and `--seconds`. Exact remains the default:
`ss14-diagnostic/0.8`, `ss14-diagnostic-summary/0.8`, and
`native-sprite-presentation-samples/1`, with existing countdown-sensitive equality.

Visual uses `ss14-diagnostic/0.9`, `ss14-diagnostic-summary/0.9`, and the exclusive
`native-sprite-visual-presentation-samples/1` capability. It requires matching
Go/web consumers and package minor 11. It preserves the other required shader,
audio-timing, bounds, frozen-material and Blank capabilities. Tile schema remains
0.2, with its existing edge capability and the selected scene schema in its header
and completion summary. This source slice does not implement the consumers.

The complete owner presentation record has the same fields as exact 0.8.
Visual comparison includes sprite ID, offset, unavailable reason, ordered phase
membership, frame, auto-animation and reversed state. Only countdown is ignored;
timestamp alone already does not dirty exact samples. A changed visual value
emits the complete actual native countdown and current sample time. Silence
retains the previous complete record, including its older countdown and timestamp.
Consumers hold native frames and never extrapolate or refresh retained samples.

Complete baselines follow only wire-visible transitions: initial/new exported
sprite owner, exported no-sprite to sprite, or sprite ID A to B. Entity deletion
or exported sprite loss clears retained presentation. Same entity/sprite IDs
retain the sample unless visual equality changes; PVS re-entry, native UID or
component recreation, and a countdown-only reset grant no baseline exemption.
Checkpoints copy the exact retained record, including countdown and timestamp.

Native queueing, frame updates, fused inspection, phase membership and unavailable
reasons remain unchanged. Blank layers, shader/material data, native bounds,
entity upserts/deletes, source clocks, tile records and every ordered audio event
keep their existing semantics. This byte-suppression policy makes no claim of
capture-time savings or original-player render-phase parity. Viewer supported
preview is an independent admission option.

Focused engine-free policy/baseline checks:

```sh
dotnet run --project Tools/replay-diagnostic/presentation-policy-tests/PresentationPolicyTests.csproj -c Release
```
