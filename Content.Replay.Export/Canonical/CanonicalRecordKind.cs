namespace Content.Replay.Export.Canonical;

/// <summary>
/// Records in the streaming hand-off between the version-matched game client and the Go packer.
/// Large resource bodies are deliberately not part of this protocol.
/// </summary>
public enum CanonicalRecordKind : byte
{
    Header = 1,
    Tick = 2,
    Upsert = 3,
    Delete = 4,
    Resource = 5,
    End = 255,
}
