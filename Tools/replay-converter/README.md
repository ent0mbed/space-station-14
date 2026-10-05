# Web replay converter

This directory contains the Go half of the web replay conversion pipeline. The matching,
version-specific game client is `Content.Replay.Export`.

The hand-off is a forward-only framed stream. Both processes use bounded records and neither needs
to retain source replay history. Final replay segments will store seek keyframes separately from
continuous delta blocks: normal playback consumes only deltas at a segment boundary, while seeking
loads the keyframe for the selected segment.

The current first slice implements protocol validation, build metadata hand-off, bounded concurrent
Zstandard segment encoding, and separate keyframe/delta objects. Presentation-state capture and the
final segment assembler are intentionally the next layer; the exporter currently marks its stream
as `metadata-only` so incomplete output cannot be mistaken for a playable replay.
