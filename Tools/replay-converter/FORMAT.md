# Web replay stream and segment invariants

## Canonical hand-off

The version-matched C# exporter writes a six-byte `WRP1` header followed by bounded records:

| Field | Encoding |
| --- | --- |
| kind | `uint8` |
| payload length | little-endian `uint32` |
| payload | exactly `payload length` bytes |

Records are forward-only. A consumer may release a payload after applying it to its current
presentation state. Resource records contain immutable CDN references, never image, audio, or font
bodies.

The stream ends with an explicit `End` record. EOF before that record is a failed conversion.

## Final replay objects

A final segment has two separately compressed objects:

* `segments/NNNNNNNN.keyframe.zst` is a complete presentation state used to enter the segment after
  a seek.
* `segments/NNNNNNNN.delta.zst` is the continuous update stream used during normal playback.

Sequential playback must not apply the next segment's keyframe. It prefetches and decodes the next
delta object and appends it to one continuous timeline. This prevents a scene reset, allocation
burst, and GPU rebuild at transport boundaries.

Closed segments are immutable jobs. A bounded Go worker pool compresses them independently and may
finish out of order; the segment ID defines deterministic index order. Bounding both the job and
result queues makes memory use depend on the current world and configured concurrency rather than
replay duration.
