using System.Diagnostics;

namespace DungeonsOfSkaraBrae.Ink;

public sealed class CompiledStory
{
    public required string Json { get; init; }
    public required IReadOnlyDictionary<string, string> KnotToFile { get; init; }
    public DateTime CompiledAt { get; } = DateTime.UtcNow;
}

public sealed class CompilationResult
{
    public CompiledStory? Story { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public bool Succeeded => Story is not null;
}

public sealed class InkCompiler
{
    private readonly string _inklecatePath;
    private readonly string _storyRoot;

    public InkCompiler(string inklecatePath, string storyRoot)
    {
        _inklecatePath = inklecatePath;
        _storyRoot = storyRoot;
    }

    public async Task<CompilationResult> CompileAsync(CancellationToken ct = default)
    {
        var assembly = Assembler.Assemble(_storyRoot);
        var buildDir = Path.GetDirectoryName(assembly.MainInkPath)!;
        var jsonOut = Path.Combine(buildDir, "main.ink.json");

        if (File.Exists(jsonOut)) File.Delete(jsonOut);

        var psi = new ProcessStartInfo
        {
            FileName = _inklecatePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = buildDir,
        };
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(jsonOut);
        psi.ArgumentList.Add(assembly.MainInkPath);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start inklecate at {_inklecatePath}");

        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var warnings = new List<string>(assembly.Warnings);
        var errors = new List<string>();
        ParseInklecateOutput(stdout, warnings, errors);
        ParseInklecateOutput(stderr, warnings, errors);

        if (p.ExitCode != 0 || !File.Exists(jsonOut))
        {
            if (errors.Count == 0 && p.ExitCode != 0)
            {
                errors.Add($"inklecate exited with code {p.ExitCode}");
            }
            return new CompilationResult { Warnings = warnings, Errors = errors };
        }

        var json = await File.ReadAllTextAsync(jsonOut, ct);

        var knotToFile = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in assembly.Knots)
        {
            knotToFile.TryAdd(entry.KnotName, entry.FilePath);
        }

        return new CompilationResult
        {
            Story = new CompiledStory { Json = json, KnotToFile = knotToFile },
            Warnings = warnings,
            Errors = errors,
        };
    }

    private static void ParseInklecateOutput(string output, List<string> warnings, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(output)) return;
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("RUNTIME ERROR", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Failed", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(line);
            }
            else if (line.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(line);
            }
        }
    }
}
