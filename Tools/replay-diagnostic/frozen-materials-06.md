# Frozen material snapshots, diagnostic 0.6

Scene and summary schemas are `ss14-diagnostic/0.6` and
`ss14-diagnostic-summary/0.6`, requiring `native-frozen-material-snapshots/1`.
The converter-owned patch targets RobustToolbox commit
`36905986f6809420dbc78168fc494f91723d356b` (289.0.3); the engine gitlink stays unchanged.

Definitions precede their first referring sprite. IDs are positive, scoped to one
capture, and independent of resource IDs and shader prototype names:

```json
{"kind":"shader-source-definition","shaderSourceId":1,"value":{"identitySha256":"…","program":{"gameBuild":"…","bundleSha256":"…","engineCommit":"…","root":{"path":"/Textures/Shaders/displacement.swsl","sha256":"…","sizeBytes":724,"origin":"client-bundle"},"includes":[],"defines":[],"uniforms":[],"preset":"Default","parserLightMode":"Default","parserBlendMode":"Normal","engineShaderInputs":[],"hardwareDefinesUnavailable":true}}}
{"kind":"material-definition","materialId":1,"value":{"shaderSourceId":1,"parameterCoverage":"recorded-immutable-subset","hasLighting":true,"blendMode":"Normal","stencil":{"enabled":false,"reference":0,"writeMask":0,"readMask":0,"operation":"Keep","function":"Always"},"parameters":[{"name":"displacementSize","kind":"float32","value":127}],"unavailableParameters":[{"name":"displacementMap","kind":"sampler2D","reason":"no-observed-setter"},{"name":"displacementUV","kind":"vec4","reason":"no-observed-setter"}],"unavailableInputs":[]}}
```

Examples show shape, not captured hashes, declarations, byte counts, or stencil
defaults. `uniforms` contains native parser declarations, sorted by name, with
`name`, `type`, `precision`, nullable `arrayLength`, and nullable `initializer`.
Initializer text is never evaluated or represented as an observed parameter.
`includes` retains native parser order and duplicate paths. `defines` is sorted
by name. Engine shader inputs identify the actual embedded default vertex/fragment
wrappers and shader library, checked against pinned source hashes.

Source identity hashes the UTF-8 JSON bytes of the `program` object emitted by
this version of the adapter. Each root/include file also has its independent
original-byte SHA-256 and byte count. Loaded content is compared byte-for-byte
with the verified client ZIP; the stock default sprite root is checked against
its pinned engine hash. Missing or ambiguous source origins fail capture.
These identities do not describe a hardware-specific compiled GPU program.

Each layer/post shader gains nullable `materialId` and
`materialUnavailableReason`. Existing `shaderParametersUnavailable` remains
true for incomplete parameter coverage or unsupported inputs. A material ID
identifies the available immutable subset even when that flag is true. Consumers
must inspect unavailable fields rather than treating an ID as complete parity.
Existing shader-copy bindings remain separate: displacement sampler and UV
writes occur during native rendering and are absent from headless setter traces.

Supported observed setter kinds and values:

| Kind | Value |
|---|---|
| `float32`, `int32`, `bool` | JSON scalar |
| `vec2`, `vec3`, `vec4` | `x`, `y`, optionally `z`, `w` float32 fields |
| `ivec2` | `x`, `y` int32 fields |
| `color-srgb` | `r`, `g`, `b`, `a` float32 fields |

Color retains the native setter distinction from Vector4: native upload converts
Color RGB to linear space, while Vector4 does not. Nonfinite values, arrays,
matrices, and textures have explicit unavailable records and no invented values.
Unassigned uniforms use `no-observed-setter`, including those with initializers.
Stencil state is copied; enabled stencil and post-shader screen/render events
remain unsupported. Raw presets and source reloads are unsupported.

Only live immutable instances qualify. Mutable instances have no material ID and
`mutable-instance`, regardless of whether current setters happen to be scalar.
The patched headless shader duplicates recorded value copies and stencil into a
distinct mutable instance; freezing that copy permits a separate snapshot.
Disposal prevents new reads/writes/duplicates; already emitted definitions retain
their owned values. Arrays/textures are never retained by the recording seam.
Immutable parameters cannot change without replacing the instance. The existing
client-only sprite candidate limitation still applies to owner replacements;
this capability does not promise new mutable-parameter dirty tracking.

Bounds: 4,096 materials/16 MiB serialized values, 512 source definitions/8 MiB,
256 parameters/declarations and 256 characters per parameter name, 32 includes,
1 MiB per source file, and 8 MiB per source closure. Existing scene, replay block,
sprite, and resource bounds remain active. Definitions are interned by content;
parameter payloads are never repeated in sprite definitions.
