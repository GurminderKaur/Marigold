using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Marigold;

/// <summary>
/// Keeps people and their photos in one folder on this computer (default ~/.marigold).
/// Photos are copied in, so the ofrenda keeps working even if the originals move.
/// A single background worker asks the model to look at new photos, one at a time.
/// </summary>
public sealed partial class Store
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public const long MaxPhotoBytes = 15_000_000;
    private readonly object _lock = new();
    private readonly List<Person> _people;
    private readonly Channel<(string Person, string Photo)> _queue = Channel.CreateUnbounded<(string, string)>();
    public string DataDir { get; }
    public Ai Ai { get; }

    public Store(string dataDir, Ai ai)
    {
        DataDir = dataDir; Ai = ai;
        Directory.CreateDirectory(Path.Combine(dataDir, "photos"));
        try { _people = File.Exists(PeopleFile) ? JsonSerializer.Deserialize<List<Person>>(File.ReadAllText(PeopleFile), Json) ?? new() : new(); }
        catch (JsonException)
        {
            // Never lose someone's memories to a bad file: keep a copy and start fresh.
            File.Copy(PeopleFile, PeopleFile + ".broken-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: false);
            _people = new();
        }
    }

    private string PeopleFile => Path.Combine(DataDir, "people.json");

    [GeneratedRegex("^[a-f0-9]{12}$")] private static partial Regex IdRx();
    public static bool ValidId(string id) => IdRx().IsMatch(id);
    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    // ── People ─────────────────────────────────────────────────────────────

    public List<Person> All() { lock (_lock) return _people.OrderBy(p => p.CreatedUtc).Select(Clone).ToList(); }
    public Person? Get(string id) { lock (_lock) return _people.FirstOrDefault(p => p.Id == id) is { } p ? Clone(p) : null; }

    public Person Add(PersonEdit e)
    {
        var p = new Person { Id = NewId() };
        Apply(p, e);
        if (string.IsNullOrWhiteSpace(p.Name)) throw new ArgumentException("Please give them a name.");
        lock (_lock) { _people.Add(p); Save(); return Clone(p); }
    }

    public Person? Update(string id, PersonEdit e)
    {
        lock (_lock)
        {
            var p = _people.FirstOrDefault(x => x.Id == id);
            if (p == null) return null;
            Apply(p, e);
            if (string.IsNullOrWhiteSpace(p.Name)) throw new ArgumentException("Please give them a name.");
            if (p.Remembrance != null) p.Remembrance.Ghosts = Ghosts.Find(p.Remembrance.Title + "\n" + p.Remembrance.Text, p);
            Save();
            return Clone(p);
        }
    }

    private static void Apply(Person p, PersonEdit e)
    {
        if (e.Name != null) p.Name = Trim(e.Name, 80);
        if (e.Years != null) p.Years = Trim(e.Years, 40);
        if (e.Relation != null) p.Relation = Trim(e.Relation, 80);
        if (e.Memories != null) p.Memories = e.Memories.Length > 4000 ? e.Memories[..4000] : e.Memories;
        if (e.Offerings != null) p.Offerings = e.Offerings.Select(o => Trim(o, 80)).Where(o => o.Length > 0).Distinct().Take(20).ToList();
        if (e.Language != null) p.Language = Languages.Contains(e.Language) ? e.Language : "English";
    }

    public static readonly string[] Languages = { "English", "Español", "Français", "Português", "ਪੰਜਾਬੀ (Punjabi)", "हिन्दी (Hindi)" };

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var p = _people.FirstOrDefault(x => x.Id == id);
            if (p == null) return false;
            _people.Remove(p);
            Save();
            try { Directory.Delete(PhotoDir(id), recursive: true); } catch { }
            return true;
        }
    }

    // ── Photos ─────────────────────────────────────────────────────────────

    /// <summary>JPEG or PNG only, checked by the file's first bytes (not its name).</summary>
    public static string? ImageExt(ReadOnlySpan<byte> b) =>
        b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? ".jpg" :
        b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? ".png" : null;

    public Photo AddPhoto(string personId, byte[] bytes)
    {
        if (bytes.Length > MaxPhotoBytes) throw new ArgumentException("That photo is too large (15 MB max).");
        var ext = ImageExt(bytes) ?? throw new ArgumentException("Only JPEG and PNG photos are supported.");
        lock (_lock)
        {
            var p = _people.FirstOrDefault(x => x.Id == personId) ?? throw new KeyNotFoundException();
            if (p.Photos.Count >= 12) throw new ArgumentException("An ofrenda can hold 12 photos.");
            var ph = new Photo { Id = NewId(), Ext = ext };
            Directory.CreateDirectory(PhotoDir(personId));
            File.WriteAllBytes(PhotoPath(personId, ph), bytes);
            p.Photos.Add(ph);
            Save();
            _queue.Writer.TryWrite((personId, ph.Id));
            return Clone(ph);
        }
    }

    public string PhotoDir(string personId) => Path.Combine(DataDir, "photos", personId);
    public string PhotoPath(string personId, Photo ph) => Path.Combine(PhotoDir(personId), ph.Id + ph.Ext);

    public Photo? SetCaption(string personId, string photoId, string caption)
    {
        lock (_lock)
        {
            var p = _people.FirstOrDefault(x => x.Id == personId);
            var ph = p?.Photos.FirstOrDefault(x => x.Id == photoId);
            if (p == null || ph == null) return null;
            ph.Caption = Trim(caption, 120); ph.CaptionEdited = true;
            if (p.Remembrance != null) p.Remembrance.Ghosts = Ghosts.Find(p.Remembrance.Title + "\n" + p.Remembrance.Text, p);
            Save();
            return Clone(ph);
        }
    }

    public bool DeletePhoto(string personId, string photoId)
    {
        lock (_lock)
        {
            var p = _people.FirstOrDefault(x => x.Id == personId);
            var ph = p?.Photos.FirstOrDefault(x => x.Id == photoId);
            if (p == null || ph == null) return false;
            p.Photos.Remove(ph);
            Save();
            try { File.Delete(PhotoPath(personId, ph)); } catch { }
            return true;
        }
    }

    public bool Redescribe(string personId, string photoId)
    {
        lock (_lock)
        {
            var ph = _people.FirstOrDefault(x => x.Id == personId)?.Photos.FirstOrDefault(x => x.Id == photoId);
            if (ph == null) return false;
            ph.Status = PhotoStatus.Waiting; ph.Error = null;
            Save();
        }
        return _queue.Writer.TryWrite((personId, photoId));
    }

    /// <summary>Background worker: describe waiting photos one by one (also ones left waiting when the app was closed).</summary>
    public async Task RunDescriberAsync(CancellationToken ct)
    {
        lock (_lock)
            foreach (var p in _people) foreach (var ph in p.Photos.Where(x => x.Status == PhotoStatus.Waiting)) _queue.Writer.TryWrite((p.Id, ph.Id));
        await foreach (var (pid, phid) in _queue.Reader.ReadAllAsync(ct))
            await DescribeOneAsync(pid, phid, ct);
    }

    public async Task DescribeOneAsync(string personId, string photoId, CancellationToken ct = default)
    {
        string path;
        lock (_lock)
        {
            var ph0 = _people.FirstOrDefault(x => x.Id == personId)?.Photos.FirstOrDefault(x => x.Id == photoId);
            if (ph0 == null || ph0.Status != PhotoStatus.Waiting) return;
            path = PhotoPath(personId, ph0);
        }
        try
        {
            var (caption, description, era, secs) = await Ai.DescribeAsync(await File.ReadAllBytesAsync(path, ct), ct);
            lock (_lock)
            {
                var ph = _people.FirstOrDefault(x => x.Id == personId)?.Photos.FirstOrDefault(x => x.Id == photoId);
                if (ph == null) return;
                if (!ph.CaptionEdited) ph.Caption = caption;
                ph.Description = description; ph.Era = era; ph.Seconds = Math.Round(secs, 1);
                ph.Status = PhotoStatus.Described; ph.Error = null;
                Save();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            lock (_lock)
            {
                var ph = _people.FirstOrDefault(x => x.Id == personId)?.Photos.FirstOrDefault(x => x.Id == photoId);
                if (ph == null) return;
                ph.Status = PhotoStatus.Error;
                ph.Error = ex is HttpRequestException ? "Couldn't reach Ollama. Is the Ollama app running?" : ex.Message;
                Save();
            }
        }
    }

    // ── Remembrance ────────────────────────────────────────────────────────

    public async Task<Person?> WriteAsync(string personId, CancellationToken ct = default)
    {
        var p = Get(personId);
        if (p == null) return null;
        var (title, text, secs) = await Ai.WriteAsync(p, ct);
        lock (_lock)
        {
            var live = _people.FirstOrDefault(x => x.Id == personId);
            if (live == null) return null;
            live.Remembrance = new Remembrance
            {
                Title = title, Text = text, Model = Ai.Model, Seconds = Math.Round(secs, 1),
                Ghosts = Ghosts.Find(title + "\n" + text, live),
            };
            Save();
            return Clone(live);
        }
    }

    public Person? EditRemembrance(string personId, string title, string text)
    {
        lock (_lock)
        {
            var p = _people.FirstOrDefault(x => x.Id == personId);
            if (p == null) return null;
            p.Remembrance ??= new Remembrance { Model = "" };
            p.Remembrance.Title = Trim(title, 120);
            p.Remembrance.Text = text.Length > 4000 ? text[..4000] : text.Replace("\r\n", "\n");
            p.Remembrance.Edited = true;
            p.Remembrance.Ghosts = Ghosts.Find(p.Remembrance.Title + "\n" + p.Remembrance.Text, p);
            Save();
            return Clone(p);
        }
    }

    // ── Storage ────────────────────────────────────────────────────────────

    private void Save()
    {
        File.WriteAllText(PeopleFile + ".tmp", JsonSerializer.Serialize(_people, Json));
        File.Move(PeopleFile + ".tmp", PeopleFile, overwrite: true);
    }

    private static string Trim(string s, int n) { s = s.Trim(); return s.Length <= n ? s : s[..n]; }
    private static T Clone<T>(T x) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(x, Json), Json)!;
}
