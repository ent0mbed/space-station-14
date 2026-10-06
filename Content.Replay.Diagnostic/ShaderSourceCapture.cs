using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Robust.Client.Graphics;

namespace Content.Replay.Diagnostic;

public sealed partial class CaptureRunner
{
    private const string EngineCommit = "36905986f6809420dbc78168fc494f91723d356b";
    private const string StockRoot = "/Shaders/Internal/default-sprite.swsl";
    private const string StockRootSHA256 = "21941e32b407ddf3a9ce5d113d4d03067c13923a43a42f6160b8af1b2366ea99";
    private readonly Dictionary<ReplayShaderProgram, int> _shaderPrograms = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, int> _shaderSourceIdentities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShaderFile> _shaderFiles = new(StringComparer.Ordinal);
    private readonly List<object> _shaderSourceDefinitions = new();
    private ZipArchive? _shaderBundle;
    private ShaderFile[]? _engineShaderInputs;
    private int _emittedShaderSources;
    private long _shaderSourceDefinitionBytes;

    private int EnsureShaderSource(ReplayShaderProgram program)
    {
        if (_shaderPrograms.TryGetValue(program, out var cached)) return cached;
        if (program.Uniforms.Count > Program.MaxShaderParameters || program.Includes.Count > Program.MaxShaderIncludes)
            throw new InvalidDataException("Shader declaration/include budget exceeded.");
        foreach (var uniform in program.Uniforms) CheckShaderName(uniform.Name);
        var root = ReadShaderFile(program.RootPath!);
        var includes = program.Includes.Select(ReadShaderFile).ToArray();
        if (root.SizeBytes + includes.Sum(file => (long) file.SizeBytes) > Program.MaxShaderClosureBytes)
            throw new InvalidDataException("Shader source-closure byte budget exceeded.");
        var sourceProgram = new { gameBuild = Program.GameBuild, bundleSha256 = Program.ResourceBundleSha256,
            engineCommit = EngineCommit, root, includes,
            defines = program.Defines.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new { name = pair.Key, value = pair.Value }).ToArray(),
            uniforms = program.Uniforms, preset = program.Preset,
            parserLightMode = program.LightMode, parserBlendMode = program.BlendMode,
            engineShaderInputs = EngineShaderInputs(), hardwareDefinesUnavailable = true };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(sourceProgram, Json);
        var identity = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!_shaderSourceIdentities.TryGetValue(identity, out var id))
        {
            if (_shaderSourceDefinitions.Count >= Program.MaxShaderSourceDefinitions
                || _shaderSourceDefinitionBytes + bytes.Length > Program.MaxShaderSourceDefinitionBytes)
                throw new InvalidDataException("Shader source-definition budget exceeded.");
            id = _shaderSourceDefinitions.Count + 1;
            _shaderSourceDefinitions.Add(new { kind = "shader-source-definition", shaderSourceId = id,
                value = new { identitySha256 = identity, program = sourceProgram } });
            _shaderSourceIdentities.Add(identity, id);
            _shaderSourceDefinitionBytes += bytes.Length;
        }
        _shaderPrograms.Add(program, id);
        return id;
    }

    private ShaderFile ReadShaderFile(string path)
    {
        if (_shaderFiles.TryGetValue(path, out var cached)) return cached;
        if (!path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal) || path.Contains('\\'))
            throw new InvalidDataException("Unsupported shader resource path.");
        using var input = _resources.ContentFileRead(path);
        var bytes = ReadShaderBytes(input);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        _shaderBundle ??= ZipFile.OpenRead(Program.Resources);
        var matches = _shaderBundle.Entries.Where(entry => entry.FullName == path[1..]).ToArray();
        string origin;
        if (matches.Length == 1)
        {
            using var original = matches[0].Open();
            if (!bytes.AsSpan().SequenceEqual(ReadShaderBytes(original)))
                throw new InvalidDataException($"Loaded shader differs from verified client bundle: {path}");
            origin = "client-bundle";
        }
        else if (matches.Length == 0 && path == StockRoot && hash == StockRootSHA256)
            origin = "engine-commit";
        else
            throw new InvalidDataException($"Shader source lacks an unambiguous pinned origin: {path}");
        var result = new ShaderFile(path, hash, bytes.Length, origin);
        _shaderFiles.Add(path, result);
        return result;
    }

    private ShaderFile[] EngineShaderInputs()
    {
        if (_engineShaderInputs != null) return _engineShaderInputs;
        (string Name, string Hash)[] expected = [
            ("z-library.glsl", "ae0b33140c4d6e33af7943ef0432b9397d9996d39fda1c1e9d0ed3b7847e3e89"),
            ("base-default.frag", "c94e068b83eada24ba707f6528d04c6166cc745c28d7f0568b3167d26fbb25ff"),
            ("base-default.vert", "22c714895843b6e99b70216e5b8a1b346b12d7f47bce9687b6d2946c815ba95c")];
        _engineShaderInputs = expected.Select(item =>
        {
            var name = "Robust.Client.Graphics.Clyde.Shaders." + item.Name;
            using var input = typeof(ShaderInstance).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidDataException($"Missing engine shader input {name}");
            var bytes = ReadShaderBytes(input);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (hash != item.Hash) throw new InvalidDataException($"Pinned engine shader input differs: {name}");
            return new ShaderFile(name, hash, bytes.Length, "engine-assembly");
        }).ToArray();
        return _engineShaderInputs;
    }

    private static byte[] ReadShaderBytes(Stream input)
    {
        using var output = new MemoryStream();
        Span<byte> buffer = stackalloc byte[8192];
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            if (output.Length + count > Program.MaxShaderSourceFileBytes)
                throw new InvalidDataException("Shader source-file byte budget exceeded.");
            output.Write(buffer[..count]);
        }
        return output.ToArray();
    }

    private sealed record ShaderFile(string Path, string Sha256, int SizeBytes, string Origin);
}
