namespace DungeonsOfSkaraBrae.Ink;

public sealed class CompiledStorySource
{
    private readonly object _gate = new();
    private CompiledStory? _current;
    private CompilationResult? _lastResult;
    private event Action<CompilationResult>? _changed;

    public CompiledStory? Current
    {
        get { lock (_gate) return _current; }
    }

    public CompilationResult? LastResult
    {
        get { lock (_gate) return _lastResult; }
    }

    public void Update(CompilationResult result)
    {
        Action<CompilationResult>? snapshot;
        lock (_gate)
        {
            _lastResult = result;
            if (result.Story is not null) _current = result.Story;
            snapshot = _changed;
        }
        snapshot?.Invoke(result);
    }

    public IDisposable Subscribe(Action<CompilationResult> handler)
    {
        lock (_gate) _changed += handler;
        return new Subscription(this, handler);
    }

    private void Unsubscribe(Action<CompilationResult> handler)
    {
        lock (_gate) _changed -= handler;
    }

    private sealed class Subscription : IDisposable
    {
        private readonly CompiledStorySource _source;
        private Action<CompilationResult>? _handler;

        public Subscription(CompiledStorySource source, Action<CompilationResult> handler)
        {
            _source = source;
            _handler = handler;
        }

        public void Dispose()
        {
            var h = Interlocked.Exchange(ref _handler, null);
            if (h is not null) _source.Unsubscribe(h);
        }
    }
}
