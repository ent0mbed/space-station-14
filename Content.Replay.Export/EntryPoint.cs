using JetBrains.Annotations;
using Robust.Client;
using Robust.Shared.ContentPack;

namespace Content.Replay.Export;

[UsedImplicitly]
public sealed class EntryPoint : GameClient
{
    [Dependency] private readonly IGameController _controller = default!;
    [Dependency] private readonly ReplayExportRunner _runner = default!;

    public override void Init()
    {
        base.Init();
        Dependencies.Register<ReplayExportRunner>();
        Dependencies.BuildGraph();
        Dependencies.InjectDependencies(this);
    }

    public override void PostInit()
    {
        base.PostInit();
        _ = RunExport();
    }

    private async Task RunExport()
    {
        try
        {
            await _runner.RunAsync(ExportArguments.Current);
            _controller.Shutdown("Replay export completed");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            Environment.ExitCode = 1;
            _controller.Shutdown("Replay export failed");
        }
    }
}
