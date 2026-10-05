using System.IO.Compression;
using System.Text;
using Content.Replay.Export.Canonical;
using Robust.Shared.Replays;

namespace Content.Replay.Export;

/// <summary>
/// Owns the standalone export process. This first stage establishes the streaming process boundary
/// and forwards build metadata without retaining replay history. Presentation tick capture is added
/// behind the same record protocol.
/// </summary>
internal sealed class ReplayExportRunner
{
    public async Task RunAsync(ExportArguments args)
    {
        await using var input = File.OpenRead(args.Input);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var metadataPath = $"{ReplayConstants.ReplayZipFolder}/{ReplayConstants.FileMeta}";
        var metadata = zip.GetEntry(metadataPath)
            ?? throw new InvalidDataException($"Replay is missing {metadataPath}.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args.Output))!);
        using var writer = new CanonicalStreamWriter(File.Create(args.Output));

        await using (var metadataStream = metadata.Open())
        using (var memory = new MemoryStream())
        {
            await metadataStream.CopyToAsync(memory);
            writer.Write(CanonicalRecordKind.Header, memory.GetBuffer().AsSpan(0, checked((int) memory.Length)));
        }

        // Keep the output consumable while presentation capture is implemented incrementally. The stream is
        // intentionally finalized instead of leaving a partially-written file that a packer could mistake as valid.
        writer.Write(CanonicalRecordKind.End, Encoding.UTF8.GetBytes("metadata-only"));
    }
}
