using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Robust.Client.Graphics;

namespace Content.Replay.Diagnostic;

public sealed partial class CaptureRunner
{
    private const string EngineCommit = Program.EngineCommit;
    private const string StockRoot = ReplayResources.StockShaderPath;
    private const string StockRootSHA256 = ReplayResources.StockShaderHash;
    private readonly Dictionary<ReplayShaderProgram, int> _shaderPrograms = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, int> _shaderSourceIdentities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShaderFile> _shaderFiles = new(StringComparer.Ordinal);
    private readonly DefinitionStore<object> _shaderSourceDefinitions = new(new());
    private ZipArchive? _shaderBundle;
    private ShaderFile[]? _engineShaderInputs;
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
            id = _shaderSourceDefinitions.Add(definitionId => new {
                kind = "shader-source-definition", shaderSourceId = definitionId,
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
        _engineShaderInputs = ReplayResources.EngineInputs.Select(item =>
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
