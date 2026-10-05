# Bounded minute preview

The default `ten-second` profile remains at 10 seconds, 601 native states and
32 native blocks. `--seconds` still defaults to 10, including when a profile is
selected. To request one minute explicitly:

```sh
dotnet Content.Replay.Diagnostic.dll --input REPLAY.zip --resources MATCHING-CLIENT.zip --output PRIVATE-DIRECTORY --profile minute-preview --seconds 60 --tiles true
```

`minute-preview` admits at most 60 simulated seconds, 1801 states and 64 native
blocks. It requires the recorded tick rate to remain 30 Hz: metadata and every
recorded tick-rate assignment in init messages and streamed states are checked,
before those messages enter checkpoint generation. Checking every assignment
avoids ambiguity from native checkpoint message reversal. The initial
state plus 1800 tick periods fits that state budget. These are named immutable
policies shared by CLI admission and the pinned native reader, not caller-supplied
guard overrides. Unknown profiles/options, nonfinite/nonpositive durations and
durations above the selected profile fail before replay/resource I/O.

Both profiles retain the existing 64 MiB per-native-block and 256 MiB cumulative
declared decoded-byte guards, two-block playback cache, 1 GiB scene output guard,
256 MiB tile output guard and existing definition/resource/entity/tile limits.
A minute may fail an independent byte/resource guard; selecting a longer duration
does not raise it. Replay cleanup and one-process-per-job behavior are unchanged.

Scene and summary keep their 0.5 semantics and add `clipProfile`,
`requestedSeconds` and `clipLimits` admission metadata. No audio event coalescing
or rendering semantics change. Partial outputs without a success summary remain
failed captures. The owned `robust-289.0.3.patch` must be applied to the pinned
RobustToolbox revision and its client assembly rebuilt before this adapter.
