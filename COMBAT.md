# Combat (proposal)

The narrative is Ink. Combat is C#. The transition between them is the question this doc answers.

## Goal

Ink should be able to write `~ combat("skeleton_7")` and have an interactive combat session run in the terminal — sending text, presenting choices, awaiting input, looping until the fight resolves — then hand control back to Ink with a result variable set (e.g. `combat_won`).

## Recommendation: async/await, not a hand-rolled state machine

`async/await` *is* a state machine — the C# compiler generates one, with all the dispatch and suspension/resumption logic. When you write:

```csharp
await SendTextAsync($"A {enemy.Name} appears!", ct);
while (enemy.Alive && _player.Alive) {
    await SendChoicesAsync(new[] { "Attack", "Defend", "Flee" }, ct);
    var pick = await ReadNextChoiceAsync(ct);
    // …apply pick, enemy turn, etc.
}
```

…it reads like a script. The hand-rolled equivalent would be one enum value per "between-await" position and a giant `HandleEvent(state, ev)` switch. Every change to combat flow forces you to rewire the graph. Avoid unless you have to.

State machines earn their keep when (a) you can't use async, (b) hard real-time / interrupt semantics, or (c) the state graph is something designers should visualize directly. None of that applies here.

## The architectural twist: EXTERNAL functions are synchronous

Ink's `Continue()` is fully synchronous, and EXTERNAL functions are invoked from inside it. The C# implementation of `combat(enemyId)` **cannot itself `await`** — it has to return immediately. So combat is *triggered* from the EXTERNAL, not *run* there.

### Pattern: flag-and-defer

1. `EXTERNAL combat(enemyId)` does one thing — stash `{ enemyId }` on the session as "combat requested" and return `0`.
2. The session's drain loop checks the flag after each `Continue()`. If set, it stops draining Ink and `await`s your async combat method.
3. Combat runs as straight-line async code: send text, send choices, await `ReadNextChoiceAsync(ct)`, apply, loop. HP / status / encounter roster live on plain C# objects on the session.
4. When combat ends, write a result back: `_story.variablesState["combat_won"] = won;`, then return.
5. Drain loop resumes. Ink processes the line right after `~ combat(...)`, where the author wrote `{ combat_won: You triumph! | The world fades. }`. Branching just works.

### The author's view from Ink

```ink
=== fight_skeleton ===
The skeleton lunges!
~ combat("skeleton_7")
{ combat_won:
   You finish it off, panting. -> next_scene
- else:
   The world fades to black. -> game_over
}
```

No special syntax — combat is invisible from Ink's point of view except for the variable that comes back.

## The one helper you need: `ReadNextChoiceAsync`

`InkSession` already has a `Channel<SessionEvent>` carrying `ChooseEvent` and `ReloadEvent`. Combat needs to pull `ChooseEvent`s from that channel. Don't fork the channel — write a helper that pulls and routes:

```csharp
async Task<int> ReadNextChoiceAsync(CancellationToken ct) {
    await foreach (var ev in _events.Reader.ReadAllAsync(ct)) {
        switch (ev) {
            case ChooseEvent c: return c.Index;
            case ReloadEvent r: await HandleReloadAsync(r.Result, ct); break;
        }
    }
    throw new OperationCanceledException();
}
```

The same helper is used by the Ink-side choose loop *and* the combat loop. One source of truth for "what did the player pick."

## Three gotchas worth knowing upfront

1. **Make `combat()` idempotent on re-call.** Hot reload re-enters the current knot, so if you save while mid-fight, Ink will re-run `~ combat("skeleton_7")`. Have the EXTERNAL no-op if a combat with that enemy ID is already in progress for this session. Rewind-on-save during a fight then just re-emits the intro narrative harmlessly.

2. **Reload events during combat are routed by the helper above.** Combat doesn't need to know they happened; `HandleReloadAsync` rebuilds the `Story` and restores variables, and your C# combat state isn't touched because it lives on the session.

3. **Don't `Continue()` from inside combat.** The drain loop is suspended until your async combat method returns. Combat does its own text/choices via the WS helpers. Once it returns, Ink takes over again naturally.

## Minimum new surface area

```csharp
// Extracted from what InkSession already does internally:
Task SendTextAsync(string ansiLine, CancellationToken ct);
Task SendChoicesAsync(IReadOnlyList<string> rawTexts, CancellationToken ct);
Task<int> ReadNextChoiceAsync(CancellationToken ct);

// Session-scoped service where all combat logic lives:
interface ICombatService {
    bool IsActive(string enemyId);
    Task RunAsync(string enemyId, CancellationToken ct);
}
```

The first three are extracted from `InkSession` internals. The fourth is the new piece, and is where all your combat code goes.

## Wiring the EXTERNAL binding

Bindings must be re-applied each time a fresh `Story` is constructed (`RunAsync` initial path AND `HandleReloadAsync`). A small helper avoids duplication:

```csharp
static void ApplyBindings(InkStory story, ICombatService combat, ...) {
    story.BindExternalFunction("combat", (string enemyId) => {
        if (!combat.IsActive(enemyId)) combat.Request(enemyId);
        return 0;
    });
    story.BindExternalFunction("hp", (string id) => combat.Hp(id));
    // …etc.
}
```

`combat.Request(enemyId)` sets the deferred flag the drain loop checks for.

## Where this leaves us

A small refactor of `InkSession` (extract the three helpers, add a combat-request flag, check it after each `Continue()`), plus a new `CombatService`, plus EXTERNAL bindings. No architectural reshuffle, no state-machine framework, no new dependencies.
