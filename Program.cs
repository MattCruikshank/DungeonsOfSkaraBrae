using System.Text.RegularExpressions;
using DungeonsOfSkaraBrae.Ink;

var builder = WebApplication.CreateBuilder(args);

var storyRoot = Path.Combine(builder.Environment.ContentRootPath, "Story");
var inklecateBin = OperatingSystem.IsWindows() ? "inklecate.exe" : "inklecate";
var inklecatePath = Path.Combine(builder.Environment.ContentRootPath, "tools", inklecateBin);
var esbuildBin = OperatingSystem.IsWindows() ? "esbuild.exe" : "esbuild";
var esbuildPath = Path.Combine(builder.Environment.ContentRootPath, "tools", esbuildBin);
var combatTsPath = Path.Combine(builder.Environment.ContentRootPath, "Game", "combat.ts");

builder.Services.AddSingleton(_ => new InkCompiler(inklecatePath, storyRoot));
builder.Services.AddSingleton(_ => new CombatRunner(esbuildPath, combatTsPath));
builder.Services.AddSingleton<CompiledStorySource>();
builder.Services.AddSingleton<StoryWatcher>(sp => new StoryWatcher(
    sp.GetRequiredService<InkCompiler>(),
    sp.GetRequiredService<CompiledStorySource>(),
    storyRoot,
    sp.GetRequiredService<ILogger<StoryWatcher>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<StoryWatcher>());

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

app.MapGet("/api/status", (CompiledStorySource src) =>
{
    var r = src.LastResult;
    return Results.Json(new
    {
        compiled = r?.Succeeded ?? false,
        knots = r?.Story?.KnotToFile.Keys.ToArray() ?? Array.Empty<string>(),
        warnings = r?.Warnings ?? (IReadOnlyList<string>)Array.Empty<string>(),
        errors = r?.Errors ?? (IReadOnlyList<string>)Array.Empty<string>(),
    });
});

app.Map("/ws", async (HttpContext ctx, CompiledStorySource src, CombatRunner combat, ILoggerFactory lf) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var session = new InkSession(ws, src, lf.CreateLogger<InkSession>(), combat);
    try { await session.RunAsync(ctx.RequestAborted); }
    catch (OperationCanceledException) { }
});

app.MapPost("/api/auth/burger", (HttpContext ctx) =>
{
    ctx.Response.Cookies.Append("burger", "1", new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = ctx.Request.IsHttps,
        Path = "/",
    });
    return Results.NoContent();
});

var knotNameRegex = new Regex(@"^\w+$", RegexOptions.Compiled);

app.MapGet("/api/knots", () =>
{
    var knotsDir = Path.Combine(storyRoot, "knots");
    if (!Directory.Exists(knotsDir)) return Results.Json(Array.Empty<string>());
    var names = Directory.EnumerateFiles(knotsDir, "*.ink", SearchOption.AllDirectories)
        .Select(f => Path.GetFileNameWithoutExtension(f))
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToArray();
    return Results.Json(names);
});

app.MapGet("/api/knot/{name}", async (string name) =>
{
    if (!knotNameRegex.IsMatch(name)) return Results.BadRequest("invalid knot name");
    var knotsDir = Path.Combine(storyRoot, "knots");
    var match = Directory.Exists(knotsDir)
        ? Directory.EnumerateFiles(knotsDir, $"{name}.ink", SearchOption.AllDirectories).FirstOrDefault()
        : null;
    if (match is null) return Results.NotFound();
    var content = await File.ReadAllTextAsync(match);
    return Results.Text(content, "text/plain; charset=utf-8");
});

app.MapPut("/api/knot/{name}", async (string name, HttpContext ctx) =>
{
    if (!app.Environment.IsDevelopment()) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (ctx.Request.Cookies["burger"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!knotNameRegex.IsMatch(name)) return Results.BadRequest("invalid knot name");

    var knotsDir = Path.Combine(storyRoot, "knots");
    Directory.CreateDirectory(knotsDir);
    var existing = Directory.EnumerateFiles(knotsDir, $"{name}.ink", SearchOption.AllDirectories).FirstOrDefault();
    var target = existing ?? Path.Combine(knotsDir, $"{name}.ink");

    using var reader = new StreamReader(ctx.Request.Body);
    var content = await reader.ReadToEndAsync();
    await File.WriteAllTextAsync(target, content);
    return Results.NoContent();
});

app.MapGet("/api/globals", async () =>
{
    var path = Path.Combine(storyRoot, "globals.ink");
    var content = File.Exists(path) ? await File.ReadAllTextAsync(path) : string.Empty;
    return Results.Text(content, "text/plain; charset=utf-8");
});

app.MapPut("/api/globals", async (HttpContext ctx) =>
{
    if (!app.Environment.IsDevelopment()) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (ctx.Request.Cookies["burger"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    Directory.CreateDirectory(storyRoot);
    using var reader = new StreamReader(ctx.Request.Body);
    var content = await reader.ReadToEndAsync();
    await File.WriteAllTextAsync(Path.Combine(storyRoot, "globals.ink"), content);
    return Results.NoContent();
});

app.Run();
