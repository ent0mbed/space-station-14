using Robust.Client;
using Robust.Shared.ContentPack;
using Robust.Shared.Timing;

namespace Content.Replay.Diagnostic;

public sealed partial class EntryPoint : GameClient
{
    [Dependency] private IGameController _controller = default!;
    [Dependency] private CaptureRunner _runner = default!;
    private bool _started;

    public override void Init()
    {
        Dependencies.Register<CaptureRunner>();
        Dependencies.BuildGraph();
        Dependencies.InjectDependencies(this);
    }

    public override void Update(ModUpdateLevel level, FrameEventArgs args)
    {
        if (_started || level != ModUpdateLevel.FramePostEngine)
            return;
        _started = true;
        // The first frame ensures all content PostInit and entity-system initialization has completed.
        // Run synchronously on the engine thread: no wall-clock pacing or intervening render frames.
        try
        {
            _runner.RunAsync().GetAwaiter().GetResult();
            _controller.Shutdown("Diagnostic clip complete");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Environment.ExitCode = 1;
            _controller.Shutdown("Diagnostic clip failed");
        }
    }
}
