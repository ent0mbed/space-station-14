# Native layer shader-copy binding

The diagnostic scene and success summary use `ss14-diagnostic/0.3` and
`ss14-diagnostic-summary/0.3`. Both declare
`requiredCapabilities: ["native-shader-copy-bindings/1"]`. This version adds a
binding to the existing interned sprite layer without changing entity, clock,
audio, resource, or tile record semantics. Older strict readers, including
`expand-02.py`, reject 0.3. Consumers must reject unsupported scene versions and
required capabilities; dropping this binding produces an incorrect sprite.

The current reader emits 0.4 with the same binding and an additional required
[native audio timing/metadata capability](audio-timing-04.md). This note describes
the 0.3 introduction; accepting the copy capability alone does not authorize
ignoring 0.4 audio fields.

Every layer retains `copyToShader` and adds `copyToShaderBinding`. The binding
is null exactly when `copyToShader` is false. Otherwise it is:

```json
{"targetLayerIndex":2,"textureParameter":"displacementMap","uvParameter":"displacementUV"}
```

`targetLayerIndex` is the resolved native destination index within this same
sprite definition, in 0..layerCount-1. It is resolved using the original string
or enum key in the native layer map, not a serialized key or guessed index.
Missing, out-of-range, and unsupported key types fail capture. Parameter names
are copied exactly and bounded to 256 UTF-16 code units each, declared in
`dictionaryLimits.shaderParameterNameCharacters`. A null name means that write
is absent; consumers must not fill it with a default. Empty strings are preserved
as native strings and do not mean null.

The fields have fixed types and value sources defined by the pinned native
`SpriteSystem.HandleShaderLayer` operation:

- `textureParameter`: a texture/sampler binding to the underlying texture of
  this source layer's resolved RSI direction/frame or explicit/fallback texture.
- `uvParameter`: a `vec4` binding to that selected texture's normalized atlas
  rectangle, in `(left,bottom,right,top)` order. A UV-only binding is permitted.

The source copy layer is not drawn. Process bindings in native layer order;
multiple writes to the same destination/parameter obey that order. If the
destination has no shader, the native operation does nothing. Otherwise it
duplicates an immutable destination shader before setting parameters. The
export records the pre-draw native material state; it does not claim the GPU
copy writes already happened in the headless capture.

This captures the complete public binding configuration, not literal GPU
texture handles, atlas coordinates, or arbitrary shader uniforms. Consumers
must resolve the source texture and atlas using the existing layer fields and
verified resources. If they cannot resolve a texture, support the destination
shader, or implement its parameter names/types and missing uniform values, they
must reject the material explicitly rather than guess defaults. A required
capability acknowledges preservation of binding semantics, not universal
shader support.

`shaderParametersUnavailable` and `resources.missing` continue to declare the
missing runtime parameter values and shader-source closure. At RobustToolbox
`36905986f6809420dbc78168fc494f91723d356b`, `ShaderInstance` exposes setters and
`ShaderPrototype` keeps its parsed defaults private; neither offers a public
parameter reader. In particular this export does not invent `displacementSize`
from a shader name or treat a prototype YAML default as a live instance value.
Those parameters need a separately supported native capture seam before a
consumer can claim exact displacement rendering.

The binding is a value record containing only an index and immutable strings.
All fields participate in sprite equality and hashing, so changed destinations
or parameter names yield a new interned definition. No native mutable parameter
object or shader instance is shared through the exported dictionary.
