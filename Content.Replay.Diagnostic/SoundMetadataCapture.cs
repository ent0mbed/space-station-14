using Robust.Client.ResourceManagement;

namespace Content.Replay.Diagnostic;

public sealed partial class CaptureRunner
{
    private int _soundMetadataAvailable;
    private int _soundMetadataUnavailable;

    private int EnsureSoundResource(string path)
    {
        if (_resourceIds.TryGetValue(("sound", path), out var id))
            return id;

        // TryGetResource<T> loads uncached resources. Enumerate only the native cache here;
        // the normal native audio system already loaded stream metadata before presentation.
        foreach (var (resourcePath, resource) in _resources.GetAllResources<AudioResource>())
        {
            if (resourcePath.ToString() != path)
                continue;
            var stream = resource.AudioStream;
            if (stream.Length.Ticks < 0 || stream.ChannelCount < 1)
                throw new InvalidDataException("Invalid cached native sound-stream metadata.");
            _soundMetadataAvailable++;
            return EnsureResource("sound", path,
                new SoundMetadata(true, false, stream.Length.Ticks, stream.ChannelCount));
        }

        _soundMetadataUnavailable++;
        return EnsureResource("sound", path, new SoundMetadata(true, true, null, null));
    }

    private readonly record struct SoundMetadata(bool BodyUnavailable, bool MetadataUnavailable,
        long? Duration100ns, int? ChannelCount);
}
