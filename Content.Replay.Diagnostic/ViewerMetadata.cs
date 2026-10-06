namespace Content.Replay.Diagnostic;

internal static class ViewerMetadataPolicy
{
    public const string Capability = "native-viewer-metadata-chat/1";
    public const int MaxNamesCharacters = 4096;
    public const int MaxPlayers = 4096;
    public const int MaxStations = 4096;
    public const int MaxGridMemberships = 250_000;
    public const int MaxChatTextCharacters = 16_384;
    public const int MaxChatEventsPerFrame = 4096;
    public const int MaxChatEvents = 100_000;
    public const long MaxRetainedBytes = 32L * 1024 * 1024;
    public const long MaxFrameBytes = 16L * 1024 * 1024;

    // Even an empty world needs a snapshot record for its detached-player/station baseline.
    public static int InitialSnapshotChunks(int entities, int presentations)
        => Math.Max(1, Math.Max((entities + 999) / 1000, (presentations + 999) / 1000));

    public static string Text(string value, int maximum, string role)
    {
        if (value == null || value.Length > maximum)
            throw new InvalidDataException($"Native {role} exceeds the viewer metadata text budget.");
        return value;
    }
}

internal sealed record ViewerPlayer(string PlayerKey, string PlayerName, string Status, int? AttachedNetEntityId);
internal sealed record ViewerStation(int StationNetEntityId, string Name, int[] GridNetEntityIds);
// messageIndex is the ordinal in GetMessages(index).Messages after native upload filtering,
// not the original recorded message list. IDs are replay-scoped sequence:messageIndex keys.
// Time comes from the containing frame,
// not a native chat emission timestamp. Text is immutable native post-accent plain text.
internal sealed record ViewerChat(string EventId, int MessageIndex, string Channel, int ChannelValue,
    string Text, int? SpeakerNetEntityId, string? SpeakerName, bool HideChat);
internal sealed record ViewerMetadataChanges(IReadOnlyList<ViewerStation> StationUpserts,
    IReadOnlyList<int> StationDeletes, IReadOnlyList<ViewerPlayer> PlayerUpserts,
    IReadOnlyList<string> PlayerDeletes, IReadOnlyList<ViewerChat> ChatEvents);

internal sealed class ViewerMetadataInventory
{
    private Dictionary<string, ViewerPlayer> _players = new(StringComparer.Ordinal);
    private Dictionary<int, ViewerStation> _stations = new();
    public int PlayerCount => _players.Count;
    public int StationCount => _stations.Count;
    public long RetainedBytes { get; private set; }
    public int ChatCount { get; private set; }

    public ViewerMetadataChanges Observe(IReadOnlyList<ViewerStation> stations,
        IReadOnlyList<ViewerPlayer>? roster, IReadOnlyList<ViewerChat> chat)
    {
        if (stations.Count > ViewerMetadataPolicy.MaxStations || roster?.Count > ViewerMetadataPolicy.MaxPlayers
            || chat.Count > ViewerMetadataPolicy.MaxChatEventsPerFrame
            || ChatCount > ViewerMetadataPolicy.MaxChatEvents - chat.Count)
            throw new InvalidDataException("Viewer metadata membership/event budget exceeded.");
        var nextStations = stations.ToDictionary(value => value.StationNetEntityId);
        var nextPlayers = roster == null ? _players : roster.ToDictionary(value => value.PlayerKey, StringComparer.Ordinal);
        var memberships = 0;
        long bytes = 0;
        foreach (var station in stations)
        {
            memberships = checked(memberships + station.GridNetEntityIds.Length);
            bytes = checked(bytes + 128 + 2L * station.Name.Length + 4L * station.GridNetEntityIds.Length);
        }
        foreach (var player in nextPlayers.Values)
            bytes = checked(bytes + 128 + 2L * (player.PlayerKey.Length + player.PlayerName.Length + player.Status.Length));
        if (memberships > ViewerMetadataPolicy.MaxGridMemberships || bytes > ViewerMetadataPolicy.MaxRetainedBytes)
            throw new InvalidDataException("Viewer metadata retained budget exceeded.");

        var stationUpserts = stations.Where(value => !_stations.TryGetValue(value.StationNetEntityId, out var previous)
            || previous.Name != value.Name || !previous.GridNetEntityIds.AsSpan().SequenceEqual(value.GridNetEntityIds)).ToArray();
        var stationDeletes = _stations.Keys.Where(id => !nextStations.ContainsKey(id)).Order().ToArray();
        var playerUpserts = roster == null ? [] : nextPlayers.Values.Where(value =>
            !_players.TryGetValue(value.PlayerKey, out var previous) || previous != value).OrderBy(value => value.PlayerKey, StringComparer.Ordinal).ToArray();
        var playerDeletes = roster == null ? [] : _players.Keys.Where(key => !nextPlayers.ContainsKey(key)).Order(StringComparer.Ordinal).ToArray();
        // Conservative JSON allowance (six bytes per UTF-16 code unit for escapes),
        // in addition to the unchanged 64 MiB record and 1 GiB scene guards.
        long frameBytes = 0;
        foreach (var value in stationUpserts)
            frameBytes = checked(frameBytes + 256 + 6L * value.Name.Length + 12L * value.GridNetEntityIds.Length);
        foreach (var value in playerUpserts)
            frameBytes = checked(frameBytes + 256 + 6L * (value.PlayerKey.Length + value.PlayerName.Length + value.Status.Length));
        frameBytes = checked(frameBytes + 12L * stationDeletes.Length + 256L * playerDeletes.Length);
        foreach (var value in chat)
            frameBytes = checked(frameBytes + 256 + 6L * (value.EventId.Length + value.Channel.Length + value.Text.Length + (value.SpeakerName?.Length ?? 0)));
        if (frameBytes > ViewerMetadataPolicy.MaxFrameBytes)
            throw new InvalidDataException("Viewer metadata frame budget exceeded.");
        _stations = nextStations;
        _players = nextPlayers;
        RetainedBytes = bytes;
        ChatCount += chat.Count;
        return new(stationUpserts, stationDeletes, playerUpserts, playerDeletes, chat);
    }
}
