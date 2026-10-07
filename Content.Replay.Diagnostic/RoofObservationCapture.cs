using Robust.Shared.Map.Components;
using Content.Shared.Light.Components;

namespace Content.Replay.Diagnostic;

public sealed partial class CaptureRunner
{
    private void ValidateRoofClosure(NativeRoofCapture capture)
    {
        foreach (var grid in capture.Grids)
        {
            if (!_fingerprints.ContainsKey(grid.OwnerId)
                || !_entities.TryGetEntity(new NetEntity(grid.OwnerId), out var uid) || uid is not { } owner
                || !_entities.TryGetComponent<MapGridComponent>(owner, out var native) || native.TileSize != grid.TileSize
                || _entities.HasComponent<ImplicitRoofComponent>(owner) != grid.Implicit)
                throw new InvalidDataException("Native resolved roof has missing/non-grid owner or mismatched tile size.");
            int? ancestor = grid.OwnerId;
            for (var depth = 0; ancestor is { } id; depth++)
                if (depth > MaxParentDepth || !_fingerprints.ContainsKey(id)
                    || !_projectedParents.TryGetValue(id, out ancestor))
                    throw new InvalidDataException("Incomplete native resolved roof pose/parent closure.");
        }
    }
}
