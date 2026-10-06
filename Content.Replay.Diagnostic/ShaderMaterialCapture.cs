using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Robust.Client.Graphics;
using Robust.Shared.Maths;

namespace Content.Replay.Diagnostic;

public sealed partial class CaptureRunner
{
    private readonly Dictionary<ReplayShaderSnapshot, MaterialBinding> _materialSnapshots = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, MaterialBinding> _materialIdentities = new(StringComparer.Ordinal);
    private readonly DefinitionStore<object> _materialDefinitions = new(new());
    private readonly Dictionary<string, int> _materialUnavailable = new(StringComparer.Ordinal);
    private long _materialDefinitionBytes;

    private MaterialBinding CaptureMaterial(ShaderInstance? shader)
    {
        if (shader == null) return new(null, null, false);
        if (shader.Disposed) return UnavailableMaterial("disposed-instance");
        if (shader.Mutable) return UnavailableMaterial("mutable-instance");
        if (shader is not IReplayShaderSnapshot recording) return UnavailableMaterial("headless-recorder-unavailable");
        var snapshot = recording.CaptureReplaySnapshot();
        if (snapshot.Mutable) throw new InvalidDataException("Headless shader snapshot changed mutability.");
        if (snapshot.Program.Reloaded) return UnavailableMaterial("source-reload-not-supported");
        if (snapshot.Program.Preset != "Default") return UnavailableMaterial("raw-preset-not-supported");
        if (_materialSnapshots.TryGetValue(snapshot, out var cached)) return cached;
        if (snapshot.Program.RootPath == null) return UnavailableMaterial("source-root-unavailable");
        if (snapshot.Parameters.Count > Program.MaxShaderParameters)
            throw new InvalidDataException("Recorded material parameter budget exceeded.");

        var sourceId = EnsureShaderSource(snapshot.Program);
        var parameters = new List<MaterialParameter>();
        var unavailable = new List<UnavailableParameter>();
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in snapshot.Parameters)
        {
            CheckShaderName(parameter.Name);
            observed.Add(parameter.Name);
            if (parameter.UnavailableReason != null)
            {
                unavailable.Add(new(parameter.Name, parameter.Kind, parameter.UnavailableReason));
                continue;
            }
            var value = MaterialValue(parameter.Value);
            if (value == null)
                unavailable.Add(new(parameter.Name, parameter.Kind, "nonfinite-or-unsupported-value"));
            else
                parameters.Add(new(parameter.Name, parameter.Kind, value));
        }
        foreach (var uniform in snapshot.Program.Uniforms)
            if (!observed.Contains(uniform.Name))
                unavailable.Add(new(uniform.Name, uniform.Type, "no-observed-setter"));
        unavailable.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        var stencil = snapshot.Stencil;
        var valueRecord = new { shaderSourceId = sourceId, parameterCoverage = "recorded-immutable-subset",
            hasLighting = snapshot.HasLighting, blendMode = snapshot.BlendMode,
            stencil = new { enabled = stencil.Enabled, reference = stencil.Ref,
                writeMask = stencil.WriteMask, readMask = stencil.ReadMask,
                operation = stencil.Op.ToString(), function = stencil.Func.ToString() },
            parameters, unavailableParameters = unavailable,
            unavailableInputs = stencil.Enabled ? new[] { "stencil-render-inputs" } : Array.Empty<string>() };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(valueRecord, Json);
        var identity = Convert.ToHexString(SHA256.HashData(bytes));
        if (!_materialIdentities.TryGetValue(identity, out var binding))
        {
            if (_materialDefinitions.Count >= Program.MaxMaterialDefinitions
                || _materialDefinitionBytes + bytes.Length > Program.MaxMaterialDefinitionBytes)
                throw new InvalidDataException("Interned material-definition budget exceeded.");
            // Definitions own serialized copies; no entity or mutable shader instances are retained.
            var id = _materialDefinitions.Add(definitionId => new {
                kind = "material-definition", materialId = (int?) definitionId,
                value = JsonSerializer.Deserialize<JsonElement>(bytes) });
            binding = new(id, null, unavailable.Count != 0 || stencil.Enabled);
            _materialDefinitionBytes += bytes.Length;
            _materialIdentities.Add(identity, binding);
        }
        _materialSnapshots.Add(snapshot, binding);
        return binding;
    }

    // Scalar membership inspection only. An uncached immutable snapshot becomes an
    // appearance candidate; CaptureMaterial owns any actual serialization/interning.
    private bool TryReadCapturedMaterial(ShaderInstance? shader, out MaterialBinding binding)
    {
        binding = new(null, null, false);
        if (shader == null) return true;
        string? reason = shader.Disposed ? "disposed-instance" : shader.Mutable ? "mutable-instance" : null;
        if (reason == null && shader is not IReplayShaderSnapshot) reason = "headless-recorder-unavailable";
        if (reason != null) { binding = new(null, reason, true); return true; }
        var snapshot = ((IReplayShaderSnapshot) shader).CaptureReplaySnapshot();
        if (snapshot.Mutable) throw new InvalidDataException("Headless shader snapshot changed mutability.");
        reason = snapshot.Program.Reloaded ? "source-reload-not-supported"
            : snapshot.Program.Preset != "Default" ? "raw-preset-not-supported"
            : snapshot.Program.RootPath == null ? "source-root-unavailable" : null;
        if (reason != null) { binding = new(null, reason, true); return true; }
        return _materialSnapshots.TryGetValue(snapshot, out binding);
    }

    private MaterialBinding UnavailableMaterial(string reason)
    {
        _materialUnavailable.TryGetValue(reason, out var count);
        _materialUnavailable[reason] = count + 1;
        return new(null, reason, true);
    }

    private static void CheckShaderName(string name)
    {
        if (name.Length is 0 or > Program.MaxShaderParameterNameCharacters)
            throw new InvalidDataException("Shader parameter-name budget exceeded.");
    }

    private static object? MaterialValue(object? value) => value switch
    {
        float number when float.IsFinite(number) => number,
        int number => number,
        bool flag => flag,
        Vector2 vector when Finite(vector.X, vector.Y) => new { x = vector.X, y = vector.Y },
        Vector3 vector when Finite(vector.X, vector.Y, vector.Z) => new { x = vector.X, y = vector.Y, z = vector.Z },
        Vector4 vector when Finite(vector.X, vector.Y, vector.Z, vector.W) => new { x = vector.X, y = vector.Y, z = vector.Z, w = vector.W },
        Vector2i vector => new { x = vector.X, y = vector.Y },
        Color color when Finite(color.R, color.G, color.B, color.A) => new { r = color.R, g = color.G, b = color.B, a = color.A },
        _ => null
    };

    private static bool Finite(float x, float y) => float.IsFinite(x) && float.IsFinite(y);
    private static bool Finite(float x, float y, float z) => Finite(x, y) && float.IsFinite(z);
    private static bool Finite(float x, float y, float z, float w) => Finite(x, y, z) && float.IsFinite(w);

    private void WriteShaderDefinitions()
    {
        _shaderSourceDefinitions.WritePending(Write);
        _materialDefinitions.WritePending(Write);
    }

    private readonly record struct MaterialBinding(int? MaterialId, string? UnavailableReason, bool ParametersUnavailable);
    private sealed record MaterialParameter(string Name, string Kind, object Value);
    private sealed record UnavailableParameter(string Name, string Kind, string Reason);
}
