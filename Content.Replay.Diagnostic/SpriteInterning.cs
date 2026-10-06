using System.Text.Json;
using Robust.Client.GameObjects;
using Robust.Shared.Maths;

namespace Content.Replay.Diagnostic;

public sealed partial class CaptureRunner
{
    // These buffers contain export values, never mutable native layer/shader instances.
    private readonly LayerValue[] _layerScratch = new LayerValue[256];
    private readonly PostShaderValue[] _postScratch = new PostShaderValue[64];
    private readonly DefinitionStore<SpriteDefinition> _spriteDefinitions = new(new());
    private readonly Dictionary<int, List<SpriteDefinition>> _spriteBuckets = new();
    private readonly Dictionary<int, int> _previousSprites = new();
    private long _spriteDefinitionBytes;
    private int _spriteCandidates;
    private int _previousSpriteReuses;
    private int _sharedSpriteReuses;

    private int CaptureSprite(EntityUid uid, int entityId, SpriteComponent component, bool initial)
    {
        _spriteCandidates++;
        var nativeLayers = component.GetReplayAnimationLayers(_layerScratch.Length);
        for (var index = 0; index < nativeLayers.Length; index++)
        {
            var layer = nativeLayers[index];
            var rsi = layer.RSI ?? component.BaseRSI;
            if (rsi != null)
                EnsureRsi(rsi);
            var shader = layer.ShaderPrototype?.ToString();
            if (shader != null)
            {
                _shaderPrototypes.Add(shader);
                EnsureResource("shader-prototype", shader, ShaderMetadata);
            }
            if (initial && (shader != null || layer.Shader != null))
                _shaderLayers++;
            var texturePath = ResolveTexture(layer.Texture);
            var material = CaptureMaterial(layer.Shader);
            if (texturePath != null)
                EnsureResource("image", texturePath, BodyMetadata);
            _layerScratch[index] = ReadLayerValue(uid, component, layer, index, nativeLayers.Length, material);
        }
        var spriteSystem = _entities.System<SpriteSystem>();
        var nativePosts = spriteSystem.GetPostShaders(component);
        if (nativePosts.Count > _postScratch.Length)
            throw new InvalidDataException("Diagnostic post-shader budget exceeded.");
        for (var index = 0; index < nativePosts.Count; index++)
        {
            var post = nativePosts[index];
            var material = CaptureMaterial(post.Shader);
            _postScratch[index] = ReadPostValue(post, material);
        }
        if (initial)
        {
            _sprites++;
            _layers += nativeLayers.Length;
        }
        var head = ReadSpriteHead(component, BoundsValue.From(spriteSystem.GetLocalBounds((uid, component))));
        var layers = _layerScratch.AsSpan(0, nativeLayers.Length);
        var posts = _postScratch.AsSpan(0, nativePosts.Count);

        // BoundsDirty and PostShaderOrderDirty are not complete visual revisions. Compare the actual
        // projected values after native presentation; no speculative network-dirty shortcut.
        if (_previousSprites.TryGetValue(entityId, out var previous)
            && _spriteDefinitions.Get(previous).Matches(head, layers, posts))
        {
            _previousSpriteReuses++;
            return previous;
        }
        var hash = new HashCode();
        hash.Add(head);
        foreach (var layer in layers) hash.Add(layer);
        foreach (var post in posts) hash.Add(post);
        var key = hash.ToHashCode();
        if (_spriteBuckets.TryGetValue(key, out var bucket))
            foreach (var existing in bucket)
                if (existing.Matches(head, layers, posts))
                {
                    _sharedSpriteReuses++;
                    _previousSprites[entityId] = existing.Id;
                    return existing.Id;
                }

        if (_spriteDefinitions.Count >= Program.MaxSpriteDefinitions)
            throw new InvalidDataException("Diagnostic sprite-definition count budget exceeded.");
        // Allocate immutable arrays and serialize only on an exact structural cache miss.
        var ownedLayers = layers.ToArray();
        var ownedPosts = posts.ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new {
            head.Visible, head.ContainerOccluded, head.DrawDepth, head.RenderOrder,
            head.Color, head.Scale, head.Rotation, head.NoRotation, head.SnapCardinals,
            head.EnableDirectionOverride, head.DirectionOverride, head.GranularLayersRendering, head.Loop,
            nativeLocalBounds = head.NativeLocalBounds.ToArray(),
            layers = ownedLayers, postShaders = ownedPosts }, Json);
        if (_spriteDefinitionBytes + bytes.Length > Program.MaxSpriteDefinitionBytes)
            throw new InvalidDataException("Diagnostic sprite-definition byte budget exceeded.");
        var id = _spriteDefinitions.Add(definitionId =>
            new SpriteDefinition(definitionId, head, ownedLayers, ownedPosts, bytes));
        var definition = _spriteDefinitions.Get(id);
        (_spriteBuckets.TryGetValue(key, out bucket) ? bucket : _spriteBuckets[key] = new()).Add(definition);
        _spriteDefinitionBytes += bytes.Length;
        _previousSprites[entityId] = definition.Id;
        return definition.Id;
    }

    private LayerValue ReadLayerValue(EntityUid uid, SpriteComponent component, SpriteComponent.Layer layer,
        int index, int count, MaterialBinding material)
        => new(index, layer.Visible, ColorValue.From(layer.Color), VectorValue.From(layer.Scale),
            VectorValue.From(layer.Offset), layer.Rotation.Theta, (layer.RSI ?? component.BaseRSI)?.Path.ToString(),
            layer.State.Name, ResolveTexture(layer.Texture), layer.Blank, layer.Loop, layer.Cycle,
            EnumName(layer.DirOffset), EnumName(layer.RenderingStrategy), layer.ShaderPrototype?.ToString(),
            layer.Shader != null, layer.Shader?.Mutable, material.ParametersUnavailable,
            layer.CopyToShaderParameters != null, CaptureShaderCopy((uid, component), layer.CopyToShaderParameters, count),
            material.MaterialId, material.UnavailableReason);

    private static PostShaderValue ReadPostValue(SpriteComponent.PostShaderEntry post, MaterialBinding material)
        => new(post.Id, post.Shader != null, post.GetScreenTexture, post.RaiseShaderEvent,
            material.ParametersUnavailable || post.GetScreenTexture || post.RaiseShaderEvent,
            material.MaterialId, post.GetScreenTexture || post.RaiseShaderEvent
                ? "render-only-inputs-not-captured" : material.UnavailableReason);

    private static SpriteHead ReadSpriteHead(SpriteComponent component, BoundsValue bounds)
        => new(component.Visible, component.ContainerOccluded, component.DrawDepth, component.RenderOrder,
            ColorValue.From(component.Color), VectorValue.From(component.Scale),
            component.Rotation.Theta, component.NoRotation, component.SnapCardinals, component.EnableDirectionOverride,
            EnumName(component.DirectionOverride), component.GranularLayersRendering, component.Loop, bounds);

    // Tail of the fused scalar inspection, after its shared layer/phase walk.
    private bool NativePostAppearanceMatches(SpriteComponent component, SpriteDefinition definition)
    {
        var posts = _entities.System<SpriteSystem>().GetPostShaders(component);
        if (posts.Count != definition.Posts.Length) return false;
        for (var index = 0; index < posts.Count; index++)
            if (!TryReadCapturedMaterial(posts[index].Shader, out var material)
                || ReadPostValue(posts[index], material) != definition.Posts[index]) return false;
        return true;
    }

    private void WriteSpriteDefinitions()
    {
        _spriteDefinitions.WritePending(WriteSpriteDefinition);
    }

    private void WriteSpriteDefinition(SpriteDefinition definition)
    {
        CheckOutputBudget(definition.Bytes.Length + 128);
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        using (var writer = new Utf8JsonWriter(_output))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "sprite-definition");
            writer.WriteNumber("spriteId", definition.Id);
            writer.WritePropertyName("value");
            writer.WriteRawValue(definition.Bytes, skipInputValidation: true);
            writer.WriteEndObject();
            writer.Flush();
        }
        _output.WriteByte((byte) '\n');
        _outputMs += System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private sealed record SpriteDefinition(int Id, SpriteHead Head, LayerValue[] Layers,
        PostShaderValue[] Posts, byte[] Bytes)
    {
        public bool Matches(SpriteHead head, ReadOnlySpan<LayerValue> layers, ReadOnlySpan<PostShaderValue> posts)
            => Head == head && layers.SequenceEqual(Layers) && posts.SequenceEqual(Posts);
    }

    private readonly record struct SpriteHead(bool Visible, bool ContainerOccluded, int DrawDepth,
        uint RenderOrder, ColorValue Color, VectorValue Scale, double Rotation,
        bool NoRotation, bool SnapCardinals, bool EnableDirectionOverride, string DirectionOverride,
        bool GranularLayersRendering, bool Loop, BoundsValue NativeLocalBounds);

    // Native CPU bounds include sprite scale and contributing layers, not sprite offset/rotation.
    // They are not a guarantee about arbitrary shader expansion.
    private readonly record struct BoundsValue(float Left, float Bottom, float Right, float Top)
    {
        public static BoundsValue From(Box2 bounds)
        {
            if (!float.IsFinite(bounds.Left) || !float.IsFinite(bounds.Bottom)
                || !float.IsFinite(bounds.Right) || !float.IsFinite(bounds.Top)
                || bounds.Left > bounds.Right || bounds.Bottom > bounds.Top)
                throw new InvalidDataException("Native sprite local bounds are nonfinite or inverted.");
            return new(bounds.Left, bounds.Bottom, bounds.Right, bounds.Top);
        }

        public float[] ToArray() => [Left, Bottom, Right, Top];
    }

    // Blank is native draw eligibility, not inferred from unresolved exported resource paths.
    // It participates in serialization, structural hashing and independent appearance comparison.
    private readonly record struct LayerValue(int Index, bool Visible, ColorValue Color, VectorValue Scale,
        VectorValue Offset, double Rotation, string? RsiPath, string? RsiState, string? TexturePath,
        bool Blank, bool Loop, bool Cycle,
        string DirectionOffset, string RenderingStrategy, string? ShaderPrototype, bool HasShader,
        bool? MaterialMutable, bool ShaderParametersUnavailable, bool CopyToShader,
        ShaderCopyBinding? CopyToShaderBinding, int? MaterialId, string? MaterialUnavailableReason);

    private readonly record struct PostShaderValue(string Id, bool HasShader, bool GetScreenTexture,
        bool RaiseShaderEvent, bool ShaderParametersUnavailable, int? MaterialId, string? MaterialUnavailableReason);

    private readonly record struct VectorValue(float X, float Y)
    {
        public static VectorValue From(System.Numerics.Vector2 value) => new(value.X, value.Y);
    }

    private readonly record struct ColorValue(float R, float G, float B, float A)
    {
        public static ColorValue From(Robust.Shared.Maths.Color value) => new(value.R, value.G, value.B, value.A);
    }

    private static string EnumName<T>(T value) where T : struct, Enum
        => EnumNames<T>.Names.TryGetValue(value, out var name) ? name : value.ToString();

    private static class EnumNames<T> where T : struct, Enum
    {
        public static readonly Dictionary<T, string> Names = Enum.GetValues<T>().ToDictionary(v => v, v => v.ToString());
    }
}
