namespace Content.Replay.Export;

internal sealed record ExportArguments(string Input, string Output)
{
    private static ExportArguments? _current;

    public static ExportArguments Current => _current
        ?? throw new InvalidOperationException("Export arguments have not been initialized.");

    public static ExportArguments Parse(string[] args)
    {
        string? input = null;
        string? output = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--input" when i + 1 < args.Length:
                    input = args[++i];
                    break;
                case "--output" when i + 1 < args.Length:
                    output = args[++i];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("Usage: Content.Replay.Export --input <replay.zip> --output <canonical.rplstream>");

        return _current = new ExportArguments(input, output);
    }
}
