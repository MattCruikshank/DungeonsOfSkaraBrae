# Plan

## What v1 ships

A single-project ASP.NET Core app that:

1. Serves an xterm.js terminal in the browser, driven by a WebSocket.
2. Runs an Ink-scripted narrative game in that terminal. Choices are clickable hyperlinks and keyboard-selectable (arrow keys + Enter). Cursor hidden, focus on load, `Ctrl+R` reloads the page.
3. Hot-reloads the Ink whenever a source file changes. On reload, variables are preserved and the runtime re-enters the start of the current knot, so the author's edits become immediately visible.
4. Exposes a Monaco editor pane (gated by `?burger=true`) with Ink syntax highlighting, divert autocomplete, Ctrl+click jump-to-knot, Ctrl+S save, dirty-buffer confirmation prompt, and a clickable title picker for `globals.ink` and every knot.
5. Auto-creates stub `.ink` files when a divert targets a knot that doesn't exist yet.
6. Runs on Windows, Linux, and macOS — `tools/fetch-inklecate.{ps1,sh}` grabs the right binary; the runtime DLL is managed-code and portable.

Repo: https://github.com/MattCruikshank/DungeonsOfSkaraBrae

## Design decisions (as built)

| Question | Decision |
|---|---|
| .NET version | .NET 10 |
| Ink compilation | Shell out to `inklecate` v1.2.1 (Windows/Linux/macOS variants in `tools/`, fetched by setup script, gitignored) |
| Ink runtime | `ink-engine-runtime.dll` bundled with the inklecate release, referenced directly by csproj |
| Clickable choices | OSC 8 hyperlinks with `ink://choice/{i}` scheme; underline suppressed via CSS for cleaner rendering |
| Keyboard nav | Arrow ↑/↓ to move selection, Enter to pick; first choice highlighted by default |
| Cursor | Hidden via `\x1b[?25l` on connect; `cursorInactiveStyle: 'none'` |
| State on hot reload | Snapshot variables → build new story → divert to current knot's start → re-emit narrative + choices. Tolerates variable schema drift silently. Falls back to "keep prior story" if the current knot was removed/renamed. |
| Sessions | Multi-user, per-connection, **ephemeral** (no persistence, no resume) |
| RESTART | Not a button — reloading the page = new WS = fresh story |
| Tag styling | Small ANSI vocabulary: `#color:*`, `#bold`, `#dim`, `#clear` |
| Source layout | Globals in `Story/globals.ink`; one knot per file under `Story/knots/**`; subfolders allowed |
| Generated main.ink | Assembler writes `Story/.build/main.ink` (gitignored, never hand-edited): INCLUDE for globals + INCLUDE per knot + `-> start` |
| Entry point | Knot named `start` |
| Missing-knot behavior | Auto-create stub `=== name ===\nTODO\n-> END` on first failed compile referencing it; reserved names (`END`, `DONE`) are skipped |
| Editor delivery | Monaco from CDN, in a resizable bottom split pane |
| Editor gate | `?burger=true` on URL (client UI) AND `burger` cookie + `IsDevelopment()` (server PUT). Two independent gates. |
| Editor smarts | Highlighting + divert autocomplete + Ctrl+click jump-to-knot |
| Title picker | Clickable title button drops down a menu with `globals.ink` and every knot; Esc/click-outside/click-title-again closes |
| Save trigger | Ctrl+S only — no debounced autosave, no save-on-blur |
| Knot-leaving prompt | Modal: Save / Don't Save / Always Save / Never Save. "Always/Never" remembered for page session only |
| "Don't Save" | Discards the buffer |
| Same-knot choices | No prompt |
| Tab close / reload | Standard browser `beforeunload` warning when dirty |
| Frontend toolchain | None — CDN script tags, plain JS |
| Line endings | `.gitattributes` forces LF for cross-platform shell-script + ps1 compatibility |

## Wire protocol

JSON over WebSocket. Server → client:

- `{type:"text", ansi:"…"}` — narrative line, pre-rendered with `#tag` → ANSI
- `{type:"choices", items:[{i:0, text:"…"}, …]}` — raw choice text; client wraps with OSC 8 + selection styling
- `{type:"knot", name:"…", source:"…"}` — active knot changed (only fires when the knot name actually changes)
- `{type:"warn", ansi:"…"}` — out-of-band notification (e.g. `(ink reloaded)`, reload-failed messages)
- `{type:"end"}` — story reached `-> END`

Client → server:

- `{type:"choose", i:2}` — player picked choice index 2

## REST API

- `POST /api/auth/burger`     → sets HttpOnly `burger=1` cookie when client sees `?burger=true`. 204.
- `GET  /api/knots`           → JSON array of knot names from the filesystem (`Story/knots/**/*.ink`)
- `GET  /api/knot/{name}`     → raw file contents. 404 if file is missing.
- `PUT  /api/knot/{name}`     → write file. Requires `IsDevelopment()` AND the `burger` cookie. 403 otherwise.
- `GET  /api/globals`         → `Story/globals.ink` contents (empty string if file missing)
- `PUT  /api/globals`         → write `Story/globals.ink`. Same gates as `PUT /api/knot/{name}`.
- `GET  /api/status`          → compile state (succeeded / errors / warnings / knot list). Used for debugging.

## Task history

| # | Task | Status |
|---|------|--------|
| 1 | Scaffold .NET 10 project + inklecate fetch | ✅ |
| 2 | Ink assembler (globals + knot files → main.ink) | ✅ |
| 3 | File watcher + inklecate pipeline with state-preserving reload | ✅ |
| 4 | WebSocket session loop with ANSI + OSC 8 choice links | ✅ |
| 5 | Frontend: xterm.js + clickable choices | ✅ |
| 6 | Knot tracking + auto-create missing knot files | ✅ |
| 7 | Monaco editor pane with Ink language, autocomplete, jump-to-knot | ✅ |
| 8 | Knot read/write REST API + edit save flow | ✅ |
| 9 | ~~Seed Story/globals.ink + small Skara Brae sample~~ | Dropped — author started writing the real game directly. |
| 10 | ~~End-to-end smoke test~~ | Dropped — covered by interactive testing during the build. |
| 11 | Clickable editor title with globals/knot picker | ✅ |

## Key invariants

- `Story/.build/` is purely derived — gitignored and disposable.
- Filename of each knot file must match its `=== name ===`. The assembler warns on mismatch and uses the in-file declaration as authoritative.
- A failed hot reload never breaks an active session — the prior compiled story keeps running until the next successful compile.
- File-write endpoint is locked behind two independent gates (env + cookie) so a single misconfiguration can't expose it.
- One knot per file. Multiple knots in one file is tolerated but warned.

## Game state architecture (combat, inventory, beyond v1)

Ink's variable model is intentionally minimal: `VAR` declarations are flat globals, with no nested objects, no arrays, no maps. Workable for narrative flags, but it doesn't scale to RPG-style state like "three skeletons in the room each with their own HP and status effects." The recommended approach is hybrid:

### What lives in Ink VARs

Small state the narrative branches on directly: `has_torch`, `talked_to_priest`, `gold`, `player_hp`. Anything you'll write `{ has_torch: ... }` against. Ink's saved state JSON captures these for free — hot reload preserves them, and any save/load mechanism gets them in `state.ToJson()`.

### What lives in C#

Anything structured, iterable, or typed: inventory, encounter rosters, NPC stat tables, quest log progress, world geography, combat math.

```csharp
public sealed record Health(int Current, int Max);
public sealed record StatusEffect(string Kind, int RoundsRemaining);
public sealed record Enemy(string Id, string Name, Health Health, List<StatusEffect> Statuses);
public sealed class Encounter { public List<Enemy> Enemies { get; } = new(); }
```

These services should be **scoped to the `InkSession`** — same lifetime as the WebSocket connection. That gives them ephemeral-per-connection semantics matching everything else, AND preserves C# state across hot reloads automatically (the session object isn't replaced; only the `Story` inside it is).

### The bridge: EXTERNAL functions

Ink declares `EXTERNAL` functions; the session binds them to C# lambdas after constructing each `Story`. They can return numbers, strings, bools, or divert paths. Typical signatures:

```ink
EXTERNAL roll(sides)
EXTERNAL hp(target_id)
EXTERNAL attack(attacker_id, defender_id)
EXTERNAL alive(target_id)
EXTERNAL has_item(item_id)
```

```csharp
fresh.BindExternalFunction("roll", (int s) => Random.Shared.Next(1, s + 1));
fresh.BindExternalFunction("hp", (string id) => _combat.Hp(id));
// etc.
```

Bindings have to be re-applied each time a fresh `Story` is constructed (i.e. inside `HandleReloadAsync` and in the initial `RunAsync`). A small `Bindings.Apply(story, services)` helper avoids duplication.

### One gotcha: the rewind-on-reload + side effects

Our hot reload re-enters the current knot. If the rewound section contains an external call with side effects (`~ damage("player", roll(6))`), that call re-runs on every save. Two ways to handle:

1. **One-shot guard pattern in Ink**:
   ```ink
   { not damage_done:
     ~ damage("player", roll(6))
     ~ damage_done = true
   }
   ```
   Once-only execution; survives reloads because `damage_done` is a normal Ink VAR.

2. **Idempotent EXTERNALs**: design the C# side so calling `damage("player", 6)` twice in the same logical turn is a no-op. Trickier to enforce; the one-shot pattern is cleaner.

### Save / load (when we get there)

Beyond v1, persistence would serialize both halves side by side:

```json
{
  "ink":  "<output of story.state.ToJson()>",
  "game": "<output of System.Text.Json.JsonSerializer.Serialize(gameState)>"
}
```

JSON deserialization tolerates schema drift well (unknown fields ignored, missing fields default), so the C# half is forgiving to evolve. Ink's `LoadState` is less forgiving — it can throw on schema changes — which is one reason to keep only stable narrative flags in Ink VARs.

### Migration plan

Start with everything in Ink VARs. The first time you reach for `skeleton7_health_current` or similar, that's the signal to move that subsystem into a C# service behind EXTERNALs. Combat will probably be the first migration; inventory the second. Narrative-branching flags stay in Ink forever.

## Out of scope (for v1)

- Persistence of game state across reconnects.
- Multi-author collaboration / conflict resolution on edits.
- Inklecate compile errors surfaced into Monaco with squigglies (errors currently show as terminal warnings only).
- Auth — the editor is gated by dev env + cookie, intended for the local author only.

## Open items to revisit

- Per-knot buffers in Monaco (currently single buffer; "Don't Save" discards).
- Surface inklecate compile errors into Monaco with diagnostic markers.
- Whether to persist `Always Save` / `Never Save` across page reloads (currently page-session only).
- Hybrid game state migration (combat first — see above).
- Optional save/load endpoint that serializes Ink state + C# game state together.
