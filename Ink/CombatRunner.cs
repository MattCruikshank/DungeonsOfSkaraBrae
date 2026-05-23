using System.Diagnostics;
using System.Text.Json;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace DungeonsOfSkaraBrae.Ink;

public sealed class CombatResult
{
    public bool Won { get; init; }
    public int PlayerHp { get; init; }
}

/// <summary>
/// Transpiles Game/combat.ts via esbuild and runs it inside a fresh V8 isolate.
/// The TS combat code calls back into the host (terminal I/O) via injected
/// async globals, so the whole fight is straight-line awaitable code on both sides.
/// </summary>
public sealed class CombatRunner
{
    private readonly string _esbuildPath;
    private readonly string _combatTsPath;

    private const string Prelude = @"
        globalThis.sendText    = (s)   => __host.SendText(String(s));
        globalThis.sendChoices = (arr) => __host.SendChoicesJson(JSON.stringify(arr));
        globalThis.readChoice  = ()    => __host.ReadChoice();
    ";

    public CombatRunner(string esbuildPath, string combatTsPath)
    {
        _esbuildPath = esbuildPath;
        _combatTsPath = combatTsPath;
    }

    public async Task<CombatResult> RunAsync(
        Func<string, Task> sendText,
        Func<IReadOnlyList<string>, Task> sendChoices,
        Func<Task<int>> readChoice,
        int playerHp,
        CancellationToken ct)
    {
        var js = await TranspileAsync(ct);

        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion);
        engine.AddHostObject("__host", new CombatHost(sendText, sendChoices, readChoice));
        engine.Execute(Prelude);
        engine.Execute(js);

        var result = await (Task<object>)engine.Evaluate($"combat({playerHp})");
        dynamic r = result;
        return new CombatResult
        {
            Won = (bool)r.won,
            PlayerHp = (int)Math.Round(Convert.ToDouble(r.playerHp)),
        };
    }

    private async Task<string> TranspileAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _esbuildPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(_combatTsPath);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start esbuild at {_esbuildPath}");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var stdout = await outTask;
        var stderr = await errTask;

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"esbuild failed: {stderr.Trim()}");
        return stdout;
    }

    // Exposed to JS as __host. Task-returning methods surface as JS promises
    // thanks to V8ScriptEngineFlags.EnableTaskPromiseConversion. Must be public —
    // ClearScript will not expose members of a non-public type.
    public sealed class CombatHost
    {
        private readonly Func<string, Task> _sendText;
        private readonly Func<IReadOnlyList<string>, Task> _sendChoices;
        private readonly Func<Task<int>> _readChoice;

        public CombatHost(Func<string, Task> sendText, Func<IReadOnlyList<string>, Task> sendChoices, Func<Task<int>> readChoice)
        {
            _sendText = sendText;
            _sendChoices = sendChoices;
            _readChoice = readChoice;
        }

        public Task SendText(string s) => _sendText(s);
        public Task SendChoicesJson(string json) => _sendChoices(JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>());
        public Task<int> ReadChoice() => _readChoice();
    }
}
