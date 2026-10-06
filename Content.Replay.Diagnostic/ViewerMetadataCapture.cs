using System.Globalization;
using Content.Shared.Chat;
using Content.Shared.Station.Components;
using Robust.Shared.GameStates;
using Robust.Shared.Replays;

namespace Content.Replay.Diagnostic;

public sealed partial class CaptureRunner
{
    private readonly ViewerMetadataInventory _viewerMetadata = new();
    private ViewerMetadataSystem? _viewerMetadataSystem;

    private static string NativeName(string value)
        => ViewerMetadataPolicy.Text(value, ViewerMetadataPolicy.MaxNamesCharacters, "entity/player name");

    private ViewerMetadataChanges CaptureViewerMetadata(GameState state, GameState initialState,
        ReplayMessage messages, List<object> upserts, List<object> audioEvents, bool initial)
    {
        var stations = new List<ViewerStation>();
        var memberships = 0;
        // Station membership includes paused owners/grids and is independent of sprite membership.
        var query = _entities.AllEntityQueryEnumerator<StationDataComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out var station, out var metadata))
        {
            if (metadata.EntityLifeStage < EntityLifeStage.Initialized
                || metadata.EntityLifeStage >= EntityLifeStage.Terminating) continue;
            if (stations.Count >= ViewerMetadataPolicy.MaxStations
                || station.Grids.Count > ViewerMetadataPolicy.MaxGridMemberships - memberships)
                throw new InvalidDataException("Native station membership budget exceeded.");
            memberships += station.Grids.Count;
            ProjectNative(uid, upserts, audioEvents, initial);
            var gridIds = new int[station.Grids.Count];
            var index = 0;
            foreach (var grid in station.Grids)
            {
                if (!_entities.TryGetComponent<MetaDataComponent>(grid, out var gridMetadata)
                    || !_entities.HasComponent<Robust.Shared.Map.Components.MapGridComponent>(grid))
                    throw new InvalidDataException("Native station has an unavailable member grid.");
                ProjectNative(grid, upserts, audioEvents, initial);
                gridIds[index++] = gridMetadata.NetEntity.Id;
            }
            Array.Sort(gridIds);
            stations.Add(new(metadata.NetEntity.Id, NativeName(metadata.EntityName), gridIds));
        }
        stations.Sort((a, b) => a.StationNetEntityId.CompareTo(b.StationNetEntityId));

        // At this engine pin player updates are full rosters when nonempty, not per-player deltas.
        // An empty native list means no update (including the native client's empty-roster limitation).
        var nativePlayers = initial ? initialState.PlayerStates.Value : state.PlayerStates.Value;
        List<ViewerPlayer>? roster = null;
        if (initial || nativePlayers.Count != 0)
        {
            if (nativePlayers.Count > ViewerMetadataPolicy.MaxPlayers)
                throw new InvalidDataException("Native player roster budget exceeded.");
            roster = new(nativePlayers.Count);
            foreach (var player in nativePlayers)
            {
                var controlled = player.ControlledEntity is { Valid: true } entity ? (int?) entity.Id : null;
                roster.Add(new(player.UserId.UserId.ToString("D"), NativeName(player.Name),
                    player.Status.ToString(), controlled));
                // Retain the exact native reference even if it cannot currently resolve.
                if (player.ControlledEntity is { Valid: true } net && _entities.TryGetEntity(net, out var owner)
                    && _entities.TryGetComponent<MetaDataComponent>(owner, out var ownerMetadata)
                    && ownerMetadata.EntityLifeStage >= EntityLifeStage.Initialized
                    && ownerMetadata.EntityLifeStage < EntityLifeStage.Terminating)
                    ProjectNative(owner.Value, upserts, audioEvents, initial);
            }
        }

        var chat = new List<ViewerChat>();
        // BufferedReplayDataProvider removed resource/prototype upload messages before
        // returning this playback list. Count every remaining message type for the ordinal.
        for (var messageIndex = 0; messageIndex < messages.Messages.Count; messageIndex++)
        {
            if (messages.Messages[messageIndex] is not ChatMessage message) continue;
            if (chat.Count >= ViewerMetadataPolicy.MaxChatEventsPerFrame)
                throw new InvalidDataException("Native chat frame event budget exceeded.");
            var speaker = message.SenderEntity.Valid ? (int?) message.SenderEntity.Id : null;
            string? name = null;
            if (message.SenderEntity.Valid && _entities.TryGetEntity(message.SenderEntity, out var owner)
                && _entities.TryGetComponent<MetaDataComponent>(owner, out var metadata)
                && metadata.EntityLifeStage >= EntityLifeStage.Initialized
                && metadata.EntityLifeStage < EntityLifeStage.Terminating)
                name = NativeName(metadata.EntityName);
            chat.Add(new(_captureSequence.ToString(CultureInfo.InvariantCulture) + ":"
                + messageIndex.ToString(CultureInfo.InvariantCulture), messageIndex,
                message.Channel.ToString(), (ushort) message.Channel,
                ViewerMetadataPolicy.Text(message.Message, ViewerMetadataPolicy.MaxChatTextCharacters, "chat text"),
                speaker, name, message.HideChat));
        }
        foreach (var renamed in _viewerMetadataSystem!.Renamed)
        {
            if (_entities.TryGetComponent<MetaDataComponent>(renamed, out var metadata)
                && metadata.EntityLifeStage >= EntityLifeStage.Initialized
                && metadata.EntityLifeStage < EntityLifeStage.Terminating
                && _fingerprints.ContainsKey(metadata.NetEntity.Id))
                ProjectNative(renamed, upserts, audioEvents, initial);
        }
        _viewerMetadataSystem.ClearRenamed();
        return _viewerMetadata.Observe(stations, roster, chat);
    }
}
