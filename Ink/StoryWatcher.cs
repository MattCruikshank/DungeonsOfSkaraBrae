using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DungeonsOfSkaraBrae.Ink;

public sealed class StoryWatcher : IHostedService, IDisposable
{
    private readonly InkCompiler _compiler;
    private readonly CompiledStorySource _source;
    private readonly string _storyRoot;
    private readonly ILogger<StoryWatcher> _logger;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private Timer? _debounce;
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(200);

    public StoryWatcher(InkCompiler compiler, CompiledStorySource source, string storyRoot, ILogger<StoryWatcher> logger)
    {
        _compiler = compiler;
        _source = source;
        _storyRoot = storyRoot;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Directory.CreateDirectory(_storyRoot);
        Directory.CreateDirectory(Path.Combine(_storyRoot, "knots"));

        await CompileNowAsync(_cts.Token);

        _watcher = new FileSystemWatcher(_storyRoot, "*.ink")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Changed += OnSourceEvent;
        _watcher.Created += OnSourceEvent;
        _watcher.Deleted += OnSourceEvent;
        _watcher.Renamed += OnSourceEvent;
        _watcher.EnableRaisingEvents = true;

        _debounce = new Timer(_ => _ = CompileNowAsync(_cts!.Token), null, Timeout.Infinite, Timeout.Infinite);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _watcher?.Dispose();
        _watcher = null;
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
        _cts?.Dispose();
    }

    private void OnSourceEvent(object sender, FileSystemEventArgs e)
    {
        var fullPath = e.FullPath.Replace('\\', '/');
        if (fullPath.Contains("/.build/", StringComparison.OrdinalIgnoreCase)) return;
        _debounce?.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
    }

    private async Task CompileNowAsync(CancellationToken ct)
    {
        try
        {
            var result = await _compiler.CompileAsync(ct);
            if (!result.Succeeded)
            {
                var stubs = StubMissingKnots(result.Errors);
                if (stubs.Count > 0)
                {
                    _logger.LogInformation("Auto-created stubs: {Stubs}", string.Join(", ", stubs));
                    result = await _compiler.CompileAsync(ct);
                }
            }
            _source.Update(result);
            if (result.Succeeded)
            {
                _logger.LogInformation("Ink compile OK ({Knots} knots, {Warnings} warnings)",
                    result.Story!.KnotToFile.Count, result.Warnings.Count);
            }
            else
            {
                _logger.LogWarning("Ink compile FAILED: {Errors}", string.Join(" | ", result.Errors));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Compile exception");
            _source.Update(new CompilationResult { Errors = new[] { ex.Message } });
        }
    }

    private static readonly Regex MissingTargetRegex = new(
        @"Divert target not found:\s*'?->\s*(\w+)'?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> ReservedKnotNames = new(StringComparer.Ordinal) { "END", "DONE" };

    private List<string> StubMissingKnots(IEnumerable<string> errors)
    {
        var created = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var knotsDir = Path.Combine(_storyRoot, "knots");
        Directory.CreateDirectory(knotsDir);

        foreach (var err in errors)
        {
            var m = MissingTargetRegex.Match(err);
            if (!m.Success) continue;
            var name = m.Groups[1].Value;
            if (ReservedKnotNames.Contains(name) || !seen.Add(name)) continue;

            var file = Path.Combine(knotsDir, $"{name}.ink");
            if (File.Exists(file)) continue;
            File.WriteAllText(file, $"=== {name} ===\nTODO: write `{name}`.\n-> END\n");
            created.Add(name);
        }
        return created;
    }
}
