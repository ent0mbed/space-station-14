using Robust.Client;

namespace Content.Replay.Export;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        ExportArguments.Parse(args);
        ContentStart.StartLibrary(["--headless"], new GameControllerOptions
        {
            Sandboxing = false,
            ContentModulePrefix = "Content.",
            ContentBuildDirectory = "Content.Replay.Export",
            DefaultWindowTitle = "SS14 Replay Export",
            UserDataDirectoryName = "Space Station 14 Replay Export",
            ConfigFileName = "replay-export.toml",
        });
    }
}
