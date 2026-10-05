using Robust.Client.Graphics;

namespace Content.Replay.Diagnostic;

public sealed partial class CaptureRunner
{
    private static readonly object ShaderMetadata = new { sourceUnavailable = true };
    private static readonly object BodyMetadata = new { bodyUnavailable = true };
    private readonly Dictionary<(string Kind, string Path), int> _resourceIds = new();
    private readonly Dictionary<RSI, int> _rsiResourceIds = new(ReferenceEqualityComparer.Instance);
    private readonly List<object> _resourceDefinitions = new();
    private int _emittedResources;

    private int EnsureResource(string resourceKind, string path, object metadata)
    {
        if (_resourceIds.TryGetValue((resourceKind, path), out var id))
            return id;
        if (_resourceDefinitions.Count >= Program.MaxResourceDefinitions)
            throw new InvalidDataException("Diagnostic resource-definition count budget exceeded.");
        id = _resourceDefinitions.Count + 1;
        _resourceIds.Add((resourceKind, path), id);
        _resourceDefinitions.Add(new { kind = "resource-definition", resourceId = id,
            resourceKind, path, gameBuild = Program.GameBuild, bundleSha256 = Program.ResourceBundleSha256, metadata });
        return id;
    }

    private int EnsureRsi(RSI rsi)
    {
        if (_rsiResourceIds.TryGetValue(rsi, out var id))
            return id;
        var path = rsi.Path.ToString();
        _rsiPaths.Add(path);
        id = EnsureResource("rsi", path, new { size = new[] { rsi.Size.X, rsi.Size.Y },
            states = rsi.Select(state => new { id = state.StateId.Name,
                directions = (int) state.RsiDirections switch { 0 => 1, 1 => 4, 2 => 8,
                    _ => throw new InvalidDataException("Unsupported RSI direction count.") },
                delaysSeconds = state.GetDelays().ToArray() }).ToArray() });
        _rsiResourceIds.Add(rsi, id);
        return id;
    }

    private void WriteResourceDefinitions()
    {
        while (_emittedResources < _resourceDefinitions.Count)
            Write(_resourceDefinitions[_emittedResources++]);
    }
}
