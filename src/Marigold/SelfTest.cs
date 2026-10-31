namespace Marigold;

/// <summary>Built-in checks for the parts that must never go wrong: the ghost check, photo handling, storage, the ofrenda page.</summary>
public static class SelfTest
{
    private static int _pass, _fail;

    public static int Run()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "marigold-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var rosa = new Person
            {
                Name = "Rosa Delgado", Years = "1931 – 2019", Relation = "my grandmother",
                Memories = "She ran a small bakery and got up at four every morning. She always said \"Come in, there's coffee.\" She sewed our Halloween costumes on her old machine.",
                Offerings = new() { "pan de muerto", "café de olla", "boleros on the radio" },
                Photos = { new Photo { Caption = "In the kitchen, rolling out dough", Description = "A woman in an apron rolls dough at a wooden table." } },
            };

            // ── Ghost check ────────────────────────────────────────────────
            Eq("ghosts: faithful text has none", Ghosts.Find("Rosa Delgado got up at four every morning to open her small bakery. She always said, \"Come in, there's coffee.\" Today we set out pan de muerto and café de olla for her.", rosa).Count, 0);
            Seq("ghosts: invented city and year caught", Ghosts.Find("Rosa was born in Guadalajara in 1929 and loved boleros.", rosa), new[] { "Guadalajara", "1929" });
            Seq("ghosts: invented weekday caught", Ghosts.Find("Every Sunday she baked for the whole street.", rosa), new[] { "Sunday" });
            Seq("ghosts: invented person caught (possessive too)", Ghosts.Find("She and Miguel's family danced to boleros.", rosa), new[] { "Miguel" });
            Eq("ghosts: sentence-start words aren't names", Ghosts.Find("Coffee was always on. Mornings started early. Her hands smelled of dough.", rosa).Count, 0);
            Eq("ghosts: years she lived are known", Ghosts.Find("Rosa (1931 – 2019) is remembered.", rosa).Count, 0);
            Eq("ghosts: 'May she rest' is not a month", Ghosts.Find("May she rest with the marigolds.", rosa).Count, 0);
            Seq("ghosts: 'in May' is a month", Ghosts.Find("She married in May.", rosa), new[] { "May" });
            Eq("ghosts: family words allowed (Abuela, Día de los Muertos)", Ghosts.Find("For Abuela, on Día de los Muertos.", rosa).Count, 0);
            Seq("ghosts: Spanish month caught", Ghosts.Find("Nació en marzo.", rosa), new[] { "marzo" });
            Eq("ghosts: plurals match ('Halloween costumes' → costume)", Ghosts.Find("She sewed every costume for Halloween.", rosa).Count, 0);

            // ── Model output parsing ───────────────────────────────────────
            var e = Ai.ParseObject("Sure! {\"caption\":\"At the table\",\"description\":\"Two people.\",\"era\":\"1950s\"} hope that helps");
            Eq("parse: JSON found inside chatter", Ai.Str(e, "caption"), "At the table");
            var st = Ai.SplitTitle("\n## **Title: For Rosa, who always had coffee on**\n\nRosa got up at four.\n\n\n\nShe sewed costumes.\n");
            Eq("answer: title line split off (markdown stripped)", st.Title, "For Rosa, who always had coffee on");
            Eq("answer: paragraphs kept", st.Text, "Rosa got up at four.\n\nShe sewed costumes.");
            True("prompt: facts are in the writer prompt", Ai.WritePrompt(rosa).Contains("pan de muerto") && Ai.WritePrompt(rosa).Contains("ONLY the facts"));

            // ── Photos are checked by content, not name ────────────────────
            Eq("photo: JPEG recognized", Store.ImageExt(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 }), ".jpg");
            Eq("photo: PNG recognized", Store.ImageExt(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }), ".png");
            Eq("photo: HTML pretending to be a photo refused", Store.ImageExt("<html><script>"u8.ToArray()), (string?)null);
            True("ids: path tricks refused", !Store.ValidId("../../etc") && !Store.ValidId("abc") && Store.ValidId("0123456789ab"));

            // ── Storage ────────────────────────────────────────────────────
            var store = new Store(tmp, new Ai("none", "http://127.0.0.1:9"));
            var p = store.Add(new PersonEdit("Rosa Delgado", "1931 – 2019", "my grandmother", rosa.Memories, new() { "pan de muerto", " ", "pan de muerto", "café de olla" }, "Klingon"));
            Eq("store: offerings trimmed and de-duplicated", string.Join("|", p.Offerings), "pan de muerto|café de olla");
            Eq("store: unknown language falls back to English", p.Language, "English");
            var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };
            var ph = store.AddPhoto(p.Id, jpeg);
            True("store: photo copied into Marigold's folder", File.Exists(store.PhotoPath(p.Id, ph)));
            bool refused = false;
            try { store.AddPhoto(p.Id, "GIF89a"u8.ToArray()); } catch (ArgumentException) { refused = true; }
            True("store: non-photo upload refused", refused);
            store.SetCaption(p.Id, ph.Id, "  Rolling dough  ");
            var edited = store.EditRemembrance(p.Id, "For Rosa", "Rosa baked in Guadalajara.\nShe always had coffee on.")!;
            Seq("store: editing re-runs the ghost check", edited.Remembrance!.Ghosts, new[] { "Guadalajara" });
            store.Update(p.Id, new PersonEdit(null, null, null, rosa.Memories + " She was born in Guadalajara.", null, null));
            var reloaded = new Store(tmp, new Ai("none", "http://127.0.0.1:9")).Get(p.Id)!;
            True("store: adding the fact to memories clears the ghost", reloaded.Remembrance!.Ghosts.Count == 0);
            Eq("store: survives a restart (caption kept)", reloaded.Photos.Single().Caption, "Rolling dough");

            // ── The ofrenda page ───────────────────────────────────────────
            reloaded.Name = "Rosa <script>alert(1)</script>";
            var html = Ofrenda.Render(reloaded, _ => jpeg);
            True("ofrenda: names are HTML-escaped", !html.Contains("<script>") && html.Contains("&lt;script&gt;"));
            True("ofrenda: photo embedded (works offline)", html.Contains("data:image/jpeg;base64,"));
            True("ofrenda: no external links or scripts", !html.Contains("http://") && !html.Contains("https://") && !html.Contains("<script"));
            True("ofrenda: offerings listed", html.Contains("café de olla"));
            Eq("download: safe file name", Demo.FileName("Rosa / Delgado: 1931"), "Rosa  Delgado 1931 - ofrenda.html");

            True("store: delete removes Marigold's copy", store.Delete(p.Id) && !Directory.Exists(store.PhotoDir(p.Id)));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    private static void Seq(string name, IEnumerable<string> actual, IEnumerable<string> expected) =>
        Eq(name, string.Join(", ", actual), string.Join(", ", expected));

    private static void Eq<T>(string name, T actual, T expected)
    {
        bool ok = EqualityComparer<T>.Default.Equals(actual, expected);
        Report(name, ok, ok ? "" : $"expected [{expected}], got [{actual}]");
    }
    private static void True(string name, bool c) => Report(name, c, "");
    private static void Report(string name, bool ok, string detail)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"{(ok ? "  ✓" : "  ✗")} {name}{(ok ? "" : "  — " + detail)}");
        if (!ok && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            Console.WriteLine($"::error title=Self-test failed::{name} {detail}");
    }
}
