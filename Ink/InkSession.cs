using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using InkStory = Ink.Runtime.Story;

namespace DungeonsOfSkaraBrae.Ink;

public sealed class InkSession
{
    private readonly WebSocket _socket;
    private readonly CompiledStorySource _source;
    private readonly ILogger _logger;
    private readonly CombatRunner _combat;
    private readonly Channel<SessionEvent> _events = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = true });

    private InkStory? _story;
    private CompiledStory? _currentCompiled;
    private string? _currentKnot;
    private IDisposable? _subscription;

    private bool _combatRequested;
    private bool _inCombat;
    private string? _combatResolveKnot;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = null };

    public InkSession(WebSocket socket, CompiledStorySource source, ILogger logger, CombatRunner combat)
    {
        _socket = socket;
        _source = source;
        _logger = logger;
        _combat = combat;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var first = await WaitForFirstCompiledStoryAsync(ct);
        if (first is null) return;

        _currentCompiled = first;
        _story = BuildStory(first.Json);
        _subscription = _source.Subscribe(r => _events.Writer.TryWrite(new ReloadEvent(r)));

        var receiveTask = ReceiveLoopAsync(ct);

        try
        {
            await DrainAndEmitAsync(ct);
            while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var choice = await ReadNextChoiceAsync(ct);
                if (choice < 0) break; // channel completed → disconnect
                if (_story is not null && choice < _story.currentChoices.Count)
                {
                    _story.ChooseChoiceIndex(choice);
                    await DrainAndEmitAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session loop error");
            await SafeSendWarnAsync(AnsiRenderer.Warning($"(session error: {ex.Message})"), ct);
        }
        finally
        {
            _subscription?.Dispose();
            _events.Writer.TryComplete();
            try { await receiveTask; } catch { }
        }
    }

    private InkStory BuildStory(string json)
    {
        var story = new InkStory(json);
        // combat(resolveKnot): defer to the host combat loop, then divert to
        // `resolveKnot` once the fight resolves. The divert is what makes Ink
        // evaluate combat_won AFTER combat instead of racing ahead past the call.
        story.BindExternalFunction<string>("combat", resolveKnot =>
        {
            _combatRequested = true;
            _combatResolveKnot = resolveKnot;
            return 0;
        }, lookaheadSafe: false);
        return story;
    }

    private async Task<CompiledStory?> WaitForFirstCompiledStoryAsync(CancellationToken ct)
    {
        var existing = _source.Current;
        if (existing is not null) return existing;

        var last = _source.LastResult;
        if (last is not null && last.Errors.Count > 0)
        {
            await SendAsync(new { type = "warn", ansi = AnsiRenderer.Warning("(waiting for ink to compile…) " + string.Join(" | ", last.Errors)) }, ct);
        }
        else
        {
            await SendAsync(new { type = "warn", ansi = AnsiRenderer.Notice("(waiting for ink to compile…)") }, ct);
        }

        var tcs = new TaskCompletionSource<CompiledStory>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetCanceled());
        using var sub = _source.Subscribe(r =>
        {
            if (r.Story is not null) tcs.TrySetResult(r.Story);
        });
        try { return await tcs.Task; }
        catch (OperationCanceledException) { return null; }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[8192];
        while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buf, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                    _events.Writer.TryComplete();
                    return;
                }
                ms.Write(buf, 0, result.Count);
            } while (!result.EndOfMessage);

            var text = Encoding.UTF8.GetString(ms.ToArray());
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "choose"
                    && doc.RootElement.TryGetProperty("i", out var iEl) && iEl.TryGetInt32(out var i))
                {
                    _events.Writer.TryWrite(new ChooseEvent(i));
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Bad client message: {Text}", text);
            }
        }
        _events.Writer.TryComplete();
    }

    /// Single consumer of the event channel. Returns the next player choice index,
    /// handling hot-reload events inline along the way. Returns -1 if the channel
    /// completes (disconnect). Reload events are ignored while combat is active.
    private async Task<int> ReadNextChoiceAsync(CancellationToken ct)
    {
        await foreach (var ev in _events.Reader.ReadAllAsync(ct))
        {
            switch (ev)
            {
                case ChooseEvent c:
                    return c.Index;
                case ReloadEvent r when !_inCombat:
                    await HandleReloadAsync(r.Result, ct);
                    break;
                case ReloadEvent:
                    // mid-combat: skip; recompiled story will be picked up after the fight
                    break;
            }
        }
        return -1;
    }

    private async Task HandleReloadAsync(CompilationResult result, CancellationToken ct)
    {
        if (!result.Succeeded || _story is null) return;

        var priorKnot = _currentKnot;
        var varSnapshot = new Dictionary<string, object?>();
        foreach (var name in _story.variablesState) varSnapshot[name] = _story.variablesState[name];

        try
        {
            var fresh = BuildStory(result.Story!.Json);
            foreach (var (name, value) in varSnapshot)
            {
                try { fresh.variablesState[name] = value; } catch { /* var removed or type changed; ignore */ }
            }
            if (!string.IsNullOrEmpty(priorKnot) && result.Story.KnotToFile.ContainsKey(priorKnot))
            {
                fresh.ChoosePathString(priorKnot);
            }

            _story = fresh;
            _currentCompiled = result.Story;
            _currentKnot = null; // force re-emit on next drain

            await SendAsync(new { type = "warn", ansi = AnsiRenderer.Notice("(ink reloaded)") }, ct);
            await DrainAndEmitAsync(ct);
        }
        catch (Exception ex)
        {
            await SendAsync(new { type = "warn", ansi = AnsiRenderer.Warning($"(reload failed: {ex.Message}) — keeping prior story") }, ct);
        }
    }

    private async Task DrainAndEmitAsync(CancellationToken ct)
    {
        if (_story is null) return;
        while (_story.canContinue)
        {
            var text = _story.Continue().TrimEnd('\r', '\n');
            var tags = _story.currentTags;
            if (text.Length > 0 || (tags is not null && tags.Count > 0))
            {
                await SendTextAsync(AnsiRenderer.Line(text, tags), ct);
            }
            await EmitKnotIfChangedAsync(force: false, ct);

            if (_combatRequested)
            {
                _combatRequested = false;
                await RunCombatAsync(ct);
            }
        }

        if (_story.currentChoices.Count > 0)
        {
            await SendChoicesAsync(_story.currentChoices.Select(c => c.text).ToList(), ct);
        }
        else
        {
            await SendAsync(new { type = "end" }, ct);
        }
    }

    private async Task RunCombatAsync(CancellationToken ct)
    {
        if (_story is null) return;
        _inCombat = true;
        try
        {
            var hp = GetIntVar("player_hp", 20);
            var result = await _combat.RunAsync(
                sendText: s => SendTextAsync(s, ct),
                sendChoices: items => SendChoicesAsync(items, ct),
                readChoice: () => ReadNextChoiceAsync(ct),
                playerHp: hp,
                ct);
            SetVar("player_hp", result.PlayerHp);
            SetVar("combat_won", result.Won);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Combat error");
            await SafeSendWarnAsync(AnsiRenderer.Warning($"(combat error: {ex.Message})"), ct);
            SetVar("combat_won", false);
        }
        finally
        {
            _inCombat = false;
            var resolve = _combatResolveKnot;
            _combatResolveKnot = null;
            if (!string.IsNullOrEmpty(resolve) && _currentCompiled?.KnotToFile.ContainsKey(resolve.Split('.')[0]) == true)
            {
                try { _story!.ChoosePathString(resolve); }
                catch (Exception ex) { _logger.LogError(ex, "combat resolve divert failed for {Knot}", resolve); }
            }
        }
    }

    private int GetIntVar(string name, int fallback)
    {
        try
        {
            var v = _story!.variablesState[name];
            return v is null ? fallback : Convert.ToInt32(v);
        }
        catch { return fallback; }
    }

    private void SetVar(string name, object value)
    {
        try { _story!.variablesState[name] = value; } catch { /* var not declared in ink; ignore */ }
    }

    private Task SendTextAsync(string ansiLine, CancellationToken ct)
        => SendAsync(new { type = "text", ansi = ansiLine }, ct);

    private Task SendChoicesAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var items = new List<object>(texts.Count);
        for (var i = 0; i < texts.Count; i++) items.Add(new { i, text = texts[i] });
        return SendAsync(new { type = "choices", items }, ct);
    }

    private async Task EmitKnotIfChangedAsync(bool force, CancellationToken ct)
    {
        if (_story is null || _currentCompiled is null) return;
        var path = _story.state.currentPathString;
        if (string.IsNullOrEmpty(path) && _story.currentChoices.Count > 0)
        {
            path = _story.currentChoices[0].sourcePath;
        }
        if (string.IsNullOrEmpty(path)) return;
        var knot = path!.Split('.')[0];
        if (!_currentCompiled.KnotToFile.TryGetValue(knot, out var filePath)) return;
        if (!force && knot == _currentKnot) return;
        _currentKnot = knot;

        string? source = null;
        if (File.Exists(filePath))
        {
            try { source = await File.ReadAllTextAsync(filePath, ct); } catch { }
        }
        await SendAsync(new { type = "knot", name = knot, source }, ct);
    }

    private async Task SendAsync(object payload, CancellationToken ct)
    {
        if (_socket.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private async Task SafeSendWarnAsync(string ansi, CancellationToken ct)
    {
        try { await SendAsync(new { type = "warn", ansi }, ct); } catch { }
    }
}

internal abstract record SessionEvent;
internal sealed record ChooseEvent(int Index) : SessionEvent;
internal sealed record ReloadEvent(CompilationResult Result) : SessionEvent;
