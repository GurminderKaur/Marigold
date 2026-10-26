using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Marigold;

/// <summary>
/// Talks to a local open-weight model (default: Gemma 3 4B via Ollama on this computer).
/// Two jobs: look at a photo and say what is visibly there; write a remembrance from what YOU wrote.
/// </summary>
public sealed class Ai
{
    private readonly HttpClient _http;
    public string Model { get; }

    public Ai(string model, string baseUrl)
    {
        Model = model;
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/')), Timeout = TimeSpan.FromMinutes(15) };
    }

    public async Task<string?> CheckAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync("/api/tags"));
            var names = doc.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString() ?? "").ToList();
            bool ok = names.Any(n => n == Model || n == Model + ":latest" || (!Model.Contains(':') && n.StartsWith(Model + ":")));
            return ok ? null : $"Ollama is running, but the model \"{Model}\" isn't downloaded. Run: ollama pull {Model}";
        }
        catch { return "Ollama isn't running. Start the Ollama app (or run 'ollama serve'), then refresh."; }
    }

    // ── Looking at a photo ─────────────────────────────────────────────────

    private static readonly JsonObject PhotoSchema = (JsonObject)JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "caption":     { "type": "string" },
            "description": { "type": "string" },
            "era":         { "type": "string" }
          },
          "required": ["caption", "description", "era"]
        }
        """)!;

    private const string PhotoPrompt = """
        This is an old family photo someone is placing on an ofrenda (a remembrance altar).
        Describe only what you can actually see. Reply with:
        - caption: 3 to 8 words, gentle and plain, e.g. "In the kitchen, rolling out dough". No names.
        - description: 1 or 2 sentences: people (how many, what they're doing, what they're wearing), the place, notable objects.
        - era: your best guess of the decade from clothing, cars, photo style, e.g. "1950s", or "" if you can't tell.
        Never guess anyone's name, relationship, age, or the city. Don't invent anything you can't see.
        """;

    public async Task<(string Caption, string Description, string Era, double Seconds)> DescribeAsync(byte[] image, CancellationToken ct = default)
    {
        var message = new JsonObject { ["role"] = "user", ["content"] = PhotoPrompt, ["images"] = new JsonArray(Convert.ToBase64String(image)) };
        var (json, secs) = await ChatAsync(message, PhotoSchema, temperature: 0, ct);
        var e = ParseObject(json);
        return (Clip(Str(e, "caption"), 80), Clip(Str(e, "description"), 400), Clip(Str(e, "era"), 20), secs);
    }

    // ── Writing a remembrance ──────────────────────────────────────────────

    private static readonly JsonObject WriteSchema = (JsonObject)JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "title":        { "type": "string" },
            "remembrance":  { "type": "string" }
          },
          "required": ["title", "remembrance"]
        }
        """)!;

    public static string WritePrompt(Person p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"""
            Write a short remembrance for an ofrenda, the altar families build for Día de los Muertos to welcome back the people they love.
            Language: {p.Language}. Length: 110 to 170 words, in 2 or 3 short paragraphs. Warm, simple and specific; not flowery, not sad for the sake of it.
            Write about them in the third person, the way a family member would read it aloud.

            STRICT RULE: use ONLY the facts below. Do not add any name, place, date, year, age, job, food, song, or event that is not written below.
            If a detail isn't given, leave it out. It is better to be short than to invent. You may mention what is visible in the photos.
            Weave in their favourite things as the offerings placed on the ofrenda for them.
            title: 2 to 6 words, using their name, e.g. "For [name], who always kept the kettle on".

            FACTS (written by their family):
            """);
        sb.AppendLine($"Name: {p.Name}");
        if (!string.IsNullOrWhiteSpace(p.Years)) sb.AppendLine($"Years: {p.Years}");
        if (!string.IsNullOrWhiteSpace(p.Relation)) sb.AppendLine($"Who they were to the writer: {p.Relation}");
        if (p.Offerings.Count > 0) sb.AppendLine($"Favourite things, placed on the ofrenda: {string.Join("; ", p.Offerings)}");
        if (!string.IsNullOrWhiteSpace(p.Memories)) sb.AppendLine($"Memories:\n\"\"\"\n{p.Memories.Trim()}\n\"\"\"");
        var shown = p.Photos.Where(x => x.Status == PhotoStatus.Described || x.CaptionEdited).ToList();
        if (shown.Count > 0)
        {
            sb.AppendLine("Photos on the ofrenda:");
            foreach (var ph in shown) sb.AppendLine($"- {ph.Caption}{(string.IsNullOrWhiteSpace(ph.Description) ? "" : ": " + ph.Description)}");
        }
        return sb.ToString();
    }

    public async Task<(string Title, string Text, double Seconds)> WriteAsync(Person p, CancellationToken ct = default)
    {
        var message = new JsonObject { ["role"] = "user", ["content"] = WritePrompt(p) };
        var (json, secs) = await ChatAsync(message, WriteSchema, temperature: 0.4, ct);
        var e = ParseObject(json);
        var text = Str(e, "remembrance").Replace("\r\n", "\n").Trim();
        if (text.Length == 0) throw new InvalidOperationException("The model returned an empty remembrance. Try again.");
        return (Clip(Str(e, "title"), 80), Clip(text, 2000), secs);
    }

    // ── Plumbing ───────────────────────────────────────────────────────────

    private async Task<(string Json, double Seconds)> ChatAsync(JsonObject message, JsonObject schema, double temperature, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["stream"] = false,
            ["format"] = schema.DeepClone(),
            ["options"] = new JsonObject { ["temperature"] = temperature, ["num_ctx"] = 4096, ["seed"] = 7 },
            ["messages"] = new JsonArray(message),
        };
        var sw = Stopwatch.StartNew();
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/api/chat", content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Ollama returned {(int)resp.StatusCode}: {(text.Length > 200 ? text[..200] : text)}");
        using var doc = JsonDocument.Parse(text);
        return (doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "{}", sw.Elapsed.TotalSeconds);
    }

    /// <summary>Lenient: finds the JSON object even if the model wraps it in chatter.</summary>
    public static JsonElement ParseObject(string json)
    {
        int a = json.IndexOf('{'), b = json.LastIndexOf('}');
        if (a < 0 || b <= a) throw new InvalidOperationException("The model didn't return JSON.");
        using var doc = JsonDocument.Parse(json[a..(b + 1)]);
        return doc.RootElement.Clone();
    }

    public static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";
}
