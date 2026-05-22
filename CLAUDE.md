# Dungeons of Skara Brae

A C# ASP.NET Core web app that runs an Ink-scripted dialog/narrative game in an in-browser xterm.js terminal, with hot-reload-on-save authoring and an optional Monaco editor pane.

## Stack

- **.NET 10** ASP.NET Core (minimal API + WebSockets), Kestrel
- **inklecate v1.2.1** Windows release (checked in at `tools/`): `inklecate.exe`, `ink_compiler.dll`, and `ink-engine-runtime.dll`. The runtime DLL is referenced directly by the csproj so compiler and runtime versions are guaranteed to match.
- **xterm.js** + `xterm-addon-web-links` from CDN
- **Monaco editor** from CDN (gated by `?burger=true`)
- Platform: Windows + PowerShell. .NET 10 SDK at `C:\Program Files\dotnet\`.

## Directory layout

```
DungeonsOfSkaraBrae.csproj
Program.cs                       app entry, /ws endpoint, /api/knot, watcher
Ink/Assembler.cs                 globals + knots → main.ink
Ink/Compiler.cs                  inklecate runner
Ink/Session.cs                   per-connection Story + state migration
Ink/AnsiRenderer.cs              #tag → ANSI + OSC 8 hyperlinks
Story/globals.ink                VARs / LISTs / CONSTs
Story/knots/**/*.ink             one knot per file, recursive
Story/.build/                    generated main.ink + .json (gitignored)
tools/inklecate.exe              checked in
wwwroot/index.html
wwwroot/app.js                   xterm + monaco + WS client
wwwroot/ink-lang.js              monaco language def for ink
```

## Conventions

- **One knot per file.** Filename matches the `=== knot_name ===` declaration. Assembler warns on mismatch. Stitches under the knot live in the same file.
- **Subfolders under `Story/knots/`** are organizational only — Ink resolves knot names globally.
- **Never hand-edit `Story/.build/main.ink`** — it is regenerated every compile.
- **Globals live in `Story/globals.ink`** (VAR / LIST / CONST). The assembler INCLUDEs it first.
- **Entry point** is the knot named `start` (assembler emits `-> start` at the end of generated main.ink).

## Runtime model

- Each WebSocket connection = one ephemeral `Story` instance. **Reloading the page = fresh story from `-> start`.** No RESTART button.
- File watcher on `Story/` debounces ~200 ms, runs the assembler, runs inklecate, swaps a shared `CompiledStorySource`.
- On each live session's next turn: snapshot state JSON → build new `Story` from the new source → `LoadState`. On failure, log a dim-red `(reload failed: …)` to that terminal and keep the prior `Story` running.
- Inklecate errors on initial compile are shown in the terminal; no story runs until the next successful compile.

## ANSI / clickable choices

- `#tag` vocabulary supported by `AnsiRenderer`:
  - `#color:red|green|yellow|blue|magenta|cyan|white|gray`
  - `#bold`, `#dim`, `#clear`
  - Unknown tags pass through silently.
- Choices render as **OSC 8 hyperlinks** with `ink://choice/{index}` targets. xterm's web-links addon intercepts `ink://` and the client sends `{type:"choose", i}` over the WS.

## Wire protocol (JSON over WebSocket)

Server → client:
- `{type:"text", ansi:"…"}`
- `{type:"choices", items:[{i:0,text:"…"}, …]}`
- `{type:"knot", name:"…", source:"…"}`  — only when active knot changes
- `{type:"warn", ansi:"…"}`
- `{type:"end"}`

Client → server:
- `{type:"choose", i:2}`

## Editor pane (`?burger=true`)

- URL with `?burger=true` shows a Monaco pane in a resizable bottom split AND sets an HTTP-only `burger=1` cookie.
- `PUT /api/knot/{name}` requires **both** `IsDevelopment()` AND the `burger` cookie. Otherwise rejected.
- `GET /api/knot/{name}` returns file content. `GET /api/knots` returns the knot-name list (for autocomplete).
- Auto-create: when the player diverts to a knot with no file, server writes `Story/knots/{name}.ink` containing `=== name ===\nTODO\n-> END`.

## Save model (Monaco)

- **Ctrl+S only** — no debounced autosave, no save-on-blur.
- Any event that would change the file Monaco displays (player-driven knot switch OR Ctrl+click jump-to-knot) checks the buffer. If dirty: modal with `[Save] [Don't Save] [Always Save] [Never Save]`.
- "Always Save" / "Never Save" remembered in JS memory for the page session only; lost on reload. Same memory applies to both player-driven switches and Ctrl+click jumps.
- "Don't Save" discards the buffer.
- Same-knot choices (gathers, loops) never prompt.
- Window `beforeunload` triggers the native browser warning when the buffer is dirty.

## Monaco language features (v1)

- Syntax highlighting (tokenizer for `===`, `*`, `+`, `-`, `->`, `VAR`, `//`)
- Divert autocomplete: typing `-> ` lists known knot names from `GET /api/knots`
- Definition provider: Ctrl+click on a `-> target` navigates to that knot's file (routed through the dirty-buffer prompt)

## Security gates

- **Client-side**: the editor pane is hidden unless `?burger=true` was in the URL on page load.
- **Server-side**: `PUT /api/knot/{name}` rejects unless `IsDevelopment()` AND the `burger` cookie is set. Two independent gates so a misconfigured environment can't accidentally expose file-write.

## Running

```powershell
dotnet run
```

Then open `http://localhost:5000/` (or whatever Kestrel picks). Add `?burger=true` to reveal the editor.
