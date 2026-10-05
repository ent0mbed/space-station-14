# Native audio pause timing and cached stream metadata

Audio timing was introduced in `ss14-diagnostic/0.4` and
`ss14-diagnostic-summary/0.4`, requiring
`["native-shader-copy-bindings/1", "native-audio-timing-metadata/1"]`.
The 0.3 shader-copy binding is unchanged. Strict older readers must reject 0.4;
consumers must preserve these fields and reject unsupported required capabilities
before claiming support. Tile companion schema remains 0.1 and its `sceneSchema`
matches 0.4 for that version. The current reader emits 0.5 with unchanged audio
values, an additional bounds capability and tile companion 0.2; see
[viewport inputs](viewport-inputs-05.md). These additions are diagnostic fields,
not a finalized wire format.

Each initial/start/change audio value adds `pauseTime100ns`. It is the exact nullable
`AudioComponent.PauseTime?.Ticks`: a signed 64-bit absolute native-clock timestamp
in 100 ns units, using the same clock as `startTime100ns`. Null remains null; zero
is a real value. It is not replay-relative or a playback offset. The existing
audio fingerprint includes the whole value, so a pause-time-only change produces
an ordered change event. Audio events remain separate from entity state coalescing.

Each sound resource definition contains immutable metadata:

```json
{"bodyUnavailable":true,"metadataUnavailable":false,"duration100ns":25000000,"channelCount":1}
```

`duration100ns` is the exact `AudioResource.AudioStream.Length.Ticks`; it is
nonnegative and may be zero. `channelCount` is the native positive channel count,
without a guessed mono/stereo default. Only already cached native AudioResource
entries are inspected when the resource is first referenced. Generic native
`TryGetResource<T>` would load uncached data and is deliberately not used here.
Uncached resources instead declare
`{"bodyUnavailable":true,"metadataUnavailable":true,"duration100ns":null,"channelCount":null}`.
Both fields are present or unavailable together. Definitions are immutable after
first reference; a later cache load does not rewrite an already emitted dictionary
entry. Sound bodies and client recording message payloads remain unavailable.
Summary `audio.soundMetadata` reports available and unavailable resource counts.

At RobustToolbox `36905986f6809420dbc78168fc494f91723d356b` / engine 289.0.3,
HeadlessAudioManager reads Ogg/WAV metadata and constructs AudioStream without
decoding PCM. This export neither loads additional streams nor reads dummy
IAudioSource playback position, gain, occlusion, or attenuation values. Ordinary
audio positions use the existing entity and parent transform closure.

Playback consumers must follow native audio semantics when that later slice is
implemented: native restore uses `(PauseTime ?? CurTime) - AudioStart`, then loop
handling and clamping to the stream length. Recorded pitch already incorporates
variation; do not multiply the restore offset by pitch or add PlayOffsetSeconds
again. Native gain conversion is `10^(volumeDb/10)`. These are interpretation
rules for preserved values, not extra inferred exporter fields. Missing stream
metadata must be surfaced rather than replaced with a guessed duration/channel.
