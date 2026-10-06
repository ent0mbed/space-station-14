using Content.Replay.Diagnostic;

var checks = 0;
void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
    checks++;
}
void Reject(Action action, string message)
{
    try { action(); }
    catch (InvalidDataException) { checks++; return; }
    throw new InvalidOperationException(message);
}
const string first = "00000000-0000-0000-0000-000000000001";
const string second = "00000000-0000-0000-0000-000000000002";
var inventory = new ViewerMetadataInventory();
ViewerStation[] station = [new(10, "", [20, 30])];
ViewerPlayer[] players = [new(first, "", "InGame", 123), new(second, "recorded", "Connected", null)];
// Gaps stand for non-chat messages retained in the playback-filtered list.
ViewerChat[] initialChat = [new("0:2", 2, "Local", 1, "[literal]", 999, null, false),
    new("0:5", 5, "Local", 1, "[literal]", null, "", true)];
var initial = inventory.Observe(station, players, initialChat);
Check(initial.PlayerUpserts.Count == 2 && initial.StationUpserts.Count == 1, "Initial roster/station baseline missing.");
Check(initial.ChatEvents.Count == 2 && initial.ChatEvents[0].EventId == "0:2"
    && initial.ChatEvents[1].EventId == "0:5", "Initial same-text events were coalesced or playback-list ordinals changed.");
Check(initial.ChatEvents[0].Text == "[literal]" && initial.ChatEvents[0].SpeakerNetEntityId == 999
    && initial.ChatEvents[0].SpeakerName == null && initial.ChatEvents[1].SpeakerName == ""
    && initial.ChatEvents[1].HideChat, "Native text, raw references, empty/null name or hidden-chat semantics changed.");

var noUpdate = inventory.Observe([new(10, "", [20, 30])], null, []);
Check(noUpdate.PlayerUpserts.Count == 0 && noUpdate.PlayerDeletes.Count == 0 && inventory.PlayerCount == 2,
    "No native roster update removed players.");
Check(noUpdate.StationUpserts.Count == 0, "Identical station membership emitted a replacement.");
var detached = inventory.Observe(station, [players[0] with { AttachedNetEntityId = null }, players[1]], []);
Check(detached.PlayerUpserts.Count == 1 && detached.PlayerUpserts[0].PlayerKey == first
    && detached.PlayerUpserts[0].AttachedNetEntityId == null && detached.PlayerDeletes.Count == 0,
    "Detach removed or renamed the player key.");
var bodyChange = inventory.Observe(station, [players[0] with { AttachedNetEntityId = 456 }, players[1]], []);
Check(bodyChange.PlayerUpserts.Count == 1 && bodyChange.PlayerUpserts[0].PlayerKey == first
    && bodyChange.PlayerUpserts[0].AttachedNetEntityId == 456, "Body change did not preserve the player key.");
var removed = inventory.Observe([new(10, "", [])], [players[1] with { PlayerName = "renamed" }], []);
Check(removed.PlayerDeletes.SequenceEqual([first]) && removed.PlayerUpserts.Single().PlayerName == "renamed",
    "Full native roster replacement did not remove absent players or preserve a rename.");
Check(removed.StationUpserts.Count == 1 && removed.StationUpserts[0].GridNetEntityIds.Length == 0
    && removed.StationDeletes.Count == 0, "Empty grid membership removed its station.");
var stationRemoved = inventory.Observe([], null, []);
Check(stationRemoved.StationDeletes.SequenceEqual([10]) && inventory.PlayerCount == 1,
    "Station disappearance or no-update player semantics changed.");
Check(inventory.ChatCount == 2 && stationRemoved.ChatEvents.Count == 0, "Chat was retained as state or counted twice.");
Check(ViewerMetadataPolicy.Text("", ViewerMetadataPolicy.MaxNamesCharacters, "name") == "", "Native empty name changed.");
Reject(() => ViewerMetadataPolicy.Text(new string('x', ViewerMetadataPolicy.MaxNamesCharacters + 1),
    ViewerMetadataPolicy.MaxNamesCharacters, "name"), "Oversized name accepted.");
Reject(() => ViewerMetadataPolicy.Text(new string('x', ViewerMetadataPolicy.MaxChatTextCharacters + 1),
    ViewerMetadataPolicy.MaxChatTextCharacters, "chat"), "Oversized chat accepted.");
Reject(() => inventory.Observe([], null, Enumerable.Repeat(initialChat[0], ViewerMetadataPolicy.MaxChatEventsPerFrame + 1).ToArray()),
    "Unbounded chat frame accepted.");
var emptyWorld = new ViewerMetadataInventory().Observe([], [new(first, "", "Connected", null)], []);
Check(ViewerMetadataPolicy.InitialSnapshotChunks(0, 0) == 1
    && emptyWorld.PlayerUpserts.Count == 1 && emptyWorld.PlayerUpserts[0].AttachedNetEntityId == null,
    "An empty world must emit one initial chunk carrying its detached-player baseline even without lighting.");
Console.WriteLine($"Viewer metadata checks passed: {checks}. Synthetic unit data only; no native replay or engine started.");
