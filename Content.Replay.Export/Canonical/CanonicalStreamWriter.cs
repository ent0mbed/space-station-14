using System.Buffers.Binary;

namespace Content.Replay.Export.Canonical;

/// <summary>
/// Writes a bounded, forward-only protocol. A record is a one-byte kind and a little-endian
/// payload length followed by the payload. The writer never owns previous records.
/// </summary>
public sealed class CanonicalStreamWriter : IDisposable
{
    public const uint Magic = 0x31505257; // WRP1
    public const ushort Version = 1;
    public const int MaxRecordSize = 64 * 1024 * 1024;

    private readonly Stream _stream;
    private bool _disposed;

    public CanonicalStreamWriter(Stream stream)
    {
        _stream = stream;
        Span<byte> header = stackalloc byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
        _stream.Write(header);
    }

    public void Write(CanonicalRecordKind kind, ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (payload.Length > MaxRecordSize)
            throw new ArgumentOutOfRangeException(nameof(payload), "Canonical records must remain bounded.");

        Span<byte> header = stackalloc byte[5];
        header[0] = (byte) kind;
        BinaryPrimitives.WriteUInt32LittleEndian(header[1..], (uint) payload.Length);
        _stream.Write(header);
        _stream.Write(payload);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _stream.Dispose();
    }
}
