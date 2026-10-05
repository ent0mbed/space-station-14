using Robust.Client.GameObjects;

namespace Content.Replay.Diagnostic;

public sealed partial class CaptureRunner
{
    private ShaderCopyBinding? CaptureShaderCopy(Entity<SpriteComponent?> sprite,
        SpriteComponent.CopyToShaderParameters? parameters, int layerCount)
    {
        if (parameters == null)
            return null;

        // Resolve the native key now. Never serialize enum names as guessed layer indices or retain
        // the mutable native CopyToShaderParameters/ShaderInstance in the interned definition.
        var system = _entities.System<SpriteSystem>();
        int target;
        var found = parameters.LayerKey switch
        {
            string key => system.LayerMapTryGet(sprite, key, out target, logMissing: false),
            Enum key => system.LayerMapTryGet(sprite, key, out target, logMissing: false),
            _ => throw new InvalidDataException("Unsupported native shader-copy layer key type.")
        };
        if (!found || target < 0 || target >= layerCount)
            throw new InvalidDataException("Native shader-copy destination layer is missing or out of range.");
        if (parameters.ParameterTexture?.Length > Program.MaxShaderParameterNameCharacters
            || parameters.ParameterUV?.Length > Program.MaxShaderParameterNameCharacters)
            throw new InvalidDataException("Native shader-copy parameter-name budget exceeded.");

        // Field types encode the two native writes: Texture and Vector4(left,bottom,right,top).
        // Values depend on the selected source texture/atlas; they are not static uniforms.
        return new ShaderCopyBinding(target, parameters.ParameterTexture, parameters.ParameterUV);
    }

    private readonly record struct ShaderCopyBinding(int TargetLayerIndex,
        string? TextureParameter, string? UvParameter);
}
