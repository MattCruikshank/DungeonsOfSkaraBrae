# Dungeons of Skara Brae

A C# ASP.NET Core dev tool for authoring [Ink](https://www.inklestudios.com/ink/)-scripted narrative games. The story runs in an xterm.js terminal in your browser; choices are clickable hyperlinks or arrow-key + Enter. Edits to your `.ink` source files hot-reload while preserving variable state. With `?burger=true` you get a Monaco editor in a split pane with syntax highlighting, divert autocomplete, and Ctrl+click jump-to-knot.

## Stack

- .NET 10 ASP.NET Core (minimal API + WebSockets), Kestrel
- [Inklecate](https://github.com/inkle/ink) v1.2.1 for compilation (checked-in via setup script)
- xterm.js + Monaco editor, both from CDN

## Setup

```bash
# 1. Fetch the inklecate compiler + runtime DLLs for your OS (one-time).
#    Windows / macOS / Linux all supported.
pwsh tools/fetch-inklecate.ps1     # if you have PowerShell installed
# or
./tools/fetch-inklecate.sh         # Linux / macOS / Git Bash

# 2. Build and run.
dotnet run
```

Then open the URL Kestrel prints (default `http://localhost:5000`). Add `?burger=true` to reveal the Monaco editor pane.

## Layout

```
Program.cs                       app entry, /ws endpoint, REST API, watcher
Ink/                             assembler, compiler runner, session, watcher
Story/globals.ink                VAR / LIST / CONST declarations
Story/knots/**/*.ink             one knot per file, recursive
Story/.build/                    generated (gitignored)
tools/inklecate.exe + dlls       fetched, gitignored
wwwroot/                         xterm + Monaco frontend
```

See [CLAUDE.md](./CLAUDE.md) for the full project guide and [PLAN.md](./PLAN.md) for the design decisions and task list.

## Authoring loop

1. Edit a `.ink` file (in Monaco with `?burger=true`, or in your editor of choice).
2. Save. The file watcher recompiles, the WebSocket session restores your variables, and re-enters the start of your current knot — so your edits are immediately visible.
3. Diverting to a knot that doesn't exist yet auto-creates a stub file.

## License

MIT.
