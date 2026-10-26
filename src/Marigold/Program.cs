using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Marigold;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = System.Text.Encoding.UTF8;

var opt = Options.Parse(args);
if (opt.Has("help")) { Options.Help(); return 0; }
if (opt.Command == "selftest") return SelfTest.Run();

var ai = new Ai(opt.Model, opt.Ollama);
if (opt.Command == "demo") return await Demo.Run(opt, ai);
if (opt.Command is not (null or "serve")) { Options.Help(); return 1; }

var store = new Store(opt.DataDir, ai);

// ── Local web app ───────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://127.0.0.1:{opt.Port}");   // this computer only
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = Store.MaxPhotoBytes + 1024);
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
var app = builder.Build();

// Only answer requests addressed to this machine (blocks DNS-rebinding), and require a custom header
// on every change, so another website open in your browser can't add, edit or delete anything.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Host.Host is not ("127.0.0.1" or "localhost")) { ctx.Response.StatusCode = 400; return; }
    if (!HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Headers["X-Marigold"] != "1") { ctx.Response.StatusCode = 403; return; }
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

var page = ReadEmbedded("index.html");
app.MapGet("/", () => Results.Content(page, "text/html; charset=utf-8"));

app.MapGet("/api/status", async () =>
{
    var problem = await ai.CheckAsync();
    return new { ready = problem == null, problem, model = ai.Model, dataDir = store.DataDir, version = "0.1.0", languages = Store.Languages };
});

IResult Bad(string msg) => Results.BadRequest(new { error = msg });

app.MapGet("/api/people", () => store.All().Select(p => new { p.Id, p.Name, p.Years, photos = p.Photos.Count, written = p.Remembrance != null }));
app.MapPost("/api/people", (PersonEdit e) =>
{
    try { return Results.Ok(store.Add(e)); } catch (ArgumentException ex) { return Bad(ex.Message); }
});
app.MapGet("/api/people/{id}", (string id) => Store.ValidId(id) && store.Get(id) is { } p ? Results.Ok(p) : Results.NotFound());
app.MapPut("/api/people/{id}", (string id, PersonEdit e) =>
{
    if (!Store.ValidId(id)) return Results.NotFound();
    try { return store.Update(id, e) is { } p ? Results.Ok(p) : Results.NotFound(); }
    catch (ArgumentException ex) { return Bad(ex.Message); }
});
app.MapDelete("/api/people/{id}", (string id) => Store.ValidId(id) && store.Delete(id) ? Results.Ok() : Results.NotFound());

app.MapPost("/api/people/{id}/photos", async (string id, HttpRequest req) =>
{
    if (!Store.ValidId(id)) return Results.NotFound();
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    try { return Results.Ok(store.AddPhoto(id, ms.ToArray())); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException ex) { return Bad(ex.Message); }
});
app.MapPut("/api/people/{id}/photos/{pid}", (string id, string pid, CaptionEdit e) =>
    Store.ValidId(id) && Store.ValidId(pid) && store.SetCaption(id, pid, e.Caption ?? "") is { } ph ? Results.Ok(ph) : Results.NotFound());
app.MapDelete("/api/people/{id}/photos/{pid}", (string id, string pid) =>
    Store.ValidId(id) && Store.ValidId(pid) && store.DeletePhoto(id, pid) ? Results.Ok() : Results.NotFound());
app.MapPost("/api/people/{id}/photos/{pid}/describe", (string id, string pid) =>
    Store.ValidId(id) && Store.ValidId(pid) && store.Redescribe(id, pid) ? Results.Ok() : Results.NotFound());

// Photos are only served by id from Marigold's own folder (never an arbitrary path).
app.MapGet("/api/photo/{id}/{pid}", (string id, string pid) =>
{
    if (!Store.ValidId(id) || !Store.ValidId(pid)) return Results.NotFound();
    var ph = store.Get(id)?.Photos.FirstOrDefault(x => x.Id == pid);
    var path = ph == null ? null : store.PhotoPath(id, ph);
    return path != null && File.Exists(path) ? Results.File(path, ph!.Ext == ".png" ? "image/png" : "image/jpeg") : Results.NotFound();
});

app.MapPost("/api/people/{id}/write", async (string id, CancellationToken ct) =>
{
    if (!Store.ValidId(id)) return Results.NotFound();
    try { return await store.WriteAsync(id, ct) is { } p ? Results.Ok(p) : Results.NotFound(); }
    catch (HttpRequestException) { return Bad("Couldn't reach Ollama. Is the Ollama app running?"); }
    catch (InvalidOperationException ex) { return Bad(ex.Message); }
    catch (JsonException) { return Bad("The model's answer wasn't readable. Click Write again."); }
});
app.MapPut("/api/people/{id}/remembrance", (string id, TextEdit e) =>
    Store.ValidId(id) && store.EditRemembrance(id, e.Title ?? "", e.Text ?? "") is { } p ? Results.Ok(p) : Results.NotFound());

// The finished ofrenda: one self-contained page. ?download=1 saves it as a file.
app.MapGet("/ofrenda/{id}", (string id, string? download) =>
{
    if (!Store.ValidId(id) || store.Get(id) is not { } p) return Results.NotFound();
    var html = Ofrenda.Render(p, ph => { var f = store.PhotoPath(id, ph); return File.Exists(f) ? File.ReadAllBytes(f) : null; });
    var bytes = System.Text.Encoding.UTF8.GetBytes(html);
    return download == "1"
        ? Results.File(bytes, "text/html; charset=utf-8", Demo.FileName(p.Name))
        : Results.Bytes(bytes, "text/html; charset=utf-8");
});

var cts = new CancellationTokenSource();
_ = Task.Run(() => store.RunDescriberAsync(cts.Token));

var url = $"http://127.0.0.1:{opt.Port}";
Console.WriteLine($"""

  🌼 Marigold is running at {url}
  Model: {ai.Model} (local, via Ollama). Photos and memories never leave this computer.
  Everything is kept in: {store.DataDir}
  Press Ctrl+C to stop.

""");
if (!opt.Has("no-browser")) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }
await app.RunAsync();
cts.Cancel();
return 0;

static string ReadEmbedded(string name)
{
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
    using var r = new StreamReader(s);
    return r.ReadToEnd();
}

namespace Marigold
{
    internal sealed class Options
    {
        public string? Command { get; private set; }
        public List<string> Positional { get; } = new();
        private readonly Dictionary<string, string?> _named = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "help", "no-browser" };

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--"))
                {
                    var k = a[2..]; string? v = null;
                    if (k.Contains('=')) { v = k[(k.IndexOf('=') + 1)..]; k = k[..k.IndexOf('=')]; }
                    else if (!Flags.Contains(k) && i + 1 < args.Length && !args[i + 1].StartsWith("--")) v = args[++i];
                    o._named[k] = v;
                }
                else if (o.Command == null) o.Command = a.ToLowerInvariant();
                else o.Positional.Add(a);
            }
            return o;
        }

        public bool Has(string k) => _named.ContainsKey(k);
        public string? Get(string k) => _named.TryGetValue(k, out var v) ? v : null;
        private string? GetOrEnv(string k, string env) => Get(k) ?? Environment.GetEnvironmentVariable(env);
        public int Port => int.TryParse(Get("port"), out var p) ? p : 5180;
        public string Model => GetOrEnv("model", "MARIGOLD_MODEL") ?? "gemma3:4b";
        public string Ollama => GetOrEnv("ollama", "OLLAMA_HOST") is { } h ? (h.StartsWith("http") ? h : "http://" + h) : "http://localhost:11434";
        public string DataDir => GetOrEnv("data", "MARIGOLD_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".marigold");

        public static void Help() => Console.WriteLine("""
            🌼 Marigold — a private, offline ofrenda for the people you remember.

              marigold                        Open the app in your browser (http://127.0.0.1:5180)
              marigold demo <person.json> <photo folder> --out ofrenda.html [--json result.json]
                                              Build an ofrenda from the command line (used by the tests)
              marigold selftest               Run the built-in checks

            Options
              --model gemma3:4b               Any Ollama vision model (env MARIGOLD_MODEL)
              --port 5180  --no-browser  --ollama http://localhost:11434  --data <folder>
            """);
    }

    internal static class Demo
    {
        public static string FileName(string name)
        {
            var safe = new string((name ?? "").Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-').ToArray()).Trim();
            return (safe.Length == 0 ? "ofrenda" : safe) + " - ofrenda.html";
        }

        public static async Task<int> Run(Options o, Ai ai)
        {
            if (o.Positional.Count < 2 || o.Get("out") is not { } outPath)
            { Console.Error.WriteLine("Usage: marigold demo <person.json> <photo folder> --out ofrenda.html [--json result.json]"); return 1; }
            if (await ai.CheckAsync() is { } problem) { Console.Error.WriteLine(problem); return 2; }

            var data = o.Get("data") ?? Path.Combine(Path.GetTempPath(), "marigold-demo-" + Guid.NewGuid().ToString("N")[..8]);
            var store = new Store(data, ai);
            var edit = JsonSerializer.Deserialize<PersonEdit>(File.ReadAllText(o.Positional[0]), Store.Json)!;
            var person = store.Add(edit);
            Console.WriteLine($"🌼 {person.Name}: looking at photos with {ai.Model} (local)…");

            var files = Directory.EnumerateFiles(o.Positional[1])
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            var names = new Dictionary<string, string>();
            foreach (var f in files)
            {
                var ph = store.AddPhoto(person.Id, File.ReadAllBytes(f));
                names[ph.Id] = Path.GetFileName(f);
                await store.DescribeOneAsync(person.Id, ph.Id);
                var done = store.Get(person.Id)!.Photos.First(x => x.Id == ph.Id);
                Console.WriteLine($"  {Path.GetFileName(f),-28} {(done.Status == PhotoStatus.Error ? "ERROR " + done.Error : $"\"{done.Caption}\" ({done.Era}) {done.Seconds}s")}");
            }

            Console.WriteLine("Writing the remembrance…");
            var p = await store.WriteAsync(person.Id) ?? throw new InvalidOperationException();
            var r = p.Remembrance!;
            int words = r.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            Console.WriteLine($"\n{r.Title}\n\n{r.Text}\n\n{words} words in {r.Seconds}s. Ghosts (not in the facts): {(r.Ghosts.Count == 0 ? "none" : string.Join(", ", r.Ghosts))}");

            var html = Ofrenda.Render(p, ph => File.ReadAllBytes(store.PhotoPath(p.Id, ph)));
            File.WriteAllText(Path.GetFullPath(outPath), html);
            Console.WriteLine($"Ofrenda written to {Path.GetFullPath(outPath)}");

            if (o.Get("json") is { } jsonPath)
            {
                var result = new
                {
                    model = ai.Model,
                    person = new { p.Name, p.Years, p.Relation, p.Offerings, p.Language },
                    photos = p.Photos.Select(x => new { file = names.GetValueOrDefault(x.Id), x.Caption, x.Description, x.Era, x.Seconds, status = x.Status.ToString(), x.Error }),
                    remembrance = new { r.Title, r.Text, r.Ghosts, r.Seconds, words },
                    prompt = Ai.WritePrompt(p),
                };
                File.WriteAllText(Path.GetFullPath(jsonPath), JsonSerializer.Serialize(result, Store.Json));
            }
            return p.Photos.Any(x => x.Status == PhotoStatus.Error) ? 3 : 0;
        }
    }
}
