using System.Text;

namespace Marigold;

/// <summary>
/// The ghost check. A language model writing about someone you loved will sometimes "remember" things
/// that never happened: a town she never lived in, a year that's wrong, a name nobody had.
/// This finds names, places, numbers, months and weekdays in the written text that don't appear in anything
/// you wrote or in what the model saw in the photos, so you can remove them (or add them, if they're true).
/// It's plain string matching: predictable and explainable, not another model guessing.
/// </summary>
public static class Ghosts
{
    // Capitalized words that are never "facts" on their own.
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "i", "i'm", "i've", "i'd", "i'll", "oh", "ok", "o",
        "día", "dia", "muertos", "dead", "day", "ofrenda", "ofrendas", "marigold", "marigolds", "cempasúchil", "cempasuchil",
        "grandma", "grandpa", "grandmother", "grandfather", "mom", "mum", "mother", "dad", "father", "aunt", "uncle", "auntie",
        "abuela", "abuelo", "abuelita", "abuelito", "mamá", "papá", "mama", "papa", "tía", "tío", "tia", "tio",
        "nani", "nana", "nanaji", "naniji", "dadi", "dada", "dadaji", "dadiji", "bebe", "bapu", "ji", "god", "dios",
    };

    // Common words that start sentences; at the start of a sentence a capital letter tells us nothing.
    private static readonly HashSet<string> Starters = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "but", "or", "so", "yet", "for", "nor", "if", "when", "while", "after", "before", "as", "because",
        "since", "though", "although", "until", "once", "even", "still", "then", "now", "here", "there", "every", "each", "all",
        "some", "many", "most", "no", "not", "never", "always", "often", "sometimes", "in", "on", "at", "by", "with", "without",
        "from", "to", "of", "into", "through", "this", "that", "these", "those", "she", "he", "they", "we", "you", "it", "her",
        "his", "their", "our", "your", "its", "my", "who", "what", "where", "how", "why", "which", "may", "let", "today",
        "tonight", "tomorrow", "yesterday", "years", "later", "long", "rest", "thank", "thanks", "remember", "remembering",
        "in", "behind", "beside", "under", "above", "around", "across", "she'd", "he'd", "she's", "he's", "they're", "we're",
        // Spanish and French
        "el", "la", "los", "las", "un", "una", "y", "pero", "cuando", "cada", "su", "sus", "ella", "él", "ellos", "nosotros",
        "en", "con", "de", "del", "para", "por", "que", "siempre", "nunca", "hoy", "aquí", "allí", "le", "les", "et", "mais",
        "quand", "chaque", "sa", "son", "ses", "elle", "il", "nous", "dans", "avec", "pour", "toujours", "jamais", "aujourd'hui",
    };

    // English months/weekdays count only when capitalized ("may" and "march" are also verbs).
    private static readonly HashSet<string> CalendarEn = new(StringComparer.OrdinalIgnoreCase)
    {
        "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december",
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday", "mondays", "tuesdays", "wednesdays",
        "thursdays", "fridays", "saturdays", "sundays",
    };

    private static readonly HashSet<string> CalendarEs = new(StringComparer.OrdinalIgnoreCase)
    {
        "enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre",
        "lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo", "sábados", "domingos",
    };

    /// <summary>Everything the writer was allowed to use, as a bag of lower-case words.</summary>
    public static HashSet<string> Known(Person p)
    {
        var sb = new StringBuilder();
        sb.AppendLine(p.Name).AppendLine(p.Years).AppendLine(p.Relation).AppendLine(p.Memories);
        foreach (var o in p.Offerings) sb.AppendLine(o);
        foreach (var ph in p.Photos) sb.AppendLine(ph.Caption).AppendLine(ph.Description);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (w, _) in Words(sb.ToString())) { set.Add(w); set.Add(Stem(w)); }
        return set;
    }

    public static List<string> Find(string text, Person p) => Find(text, Known(p));

    public static List<string> Find(string text, HashSet<string> known)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (w, sentenceStart) in Words(text))
        {
            if (Allowed.Contains(w) || known.Contains(w) || known.Contains(Stem(w))) continue;
            bool number = w.Any(char.IsDigit) && w.Count(char.IsDigit) >= 2;           // years, ages: 1952, 85, 1950s
            bool calendar = CalendarEs.Contains(w)
                            || (CalendarEn.Contains(w) && char.IsUpper(w[0]) && !(sentenceStart && w.Equals("may", StringComparison.OrdinalIgnoreCase)));  // "every Sunday", "in May"
            bool proper = char.IsUpper(w[0]) && !(sentenceStart && Starters.Contains(w)) && !(sentenceStart && !calendar && !LooksProper(w, text));
            if ((number || calendar || proper) && seen.Add(w)) found.Add(w);
        }
        return found;
    }

    // At the start of a sentence, only count a capitalized word if it is also capitalized somewhere mid-sentence,
    // or it looks like a name (e.g. contains another capital: "McAllister").
    private static bool LooksProper(string w, string text)
    {
        if (w.Skip(1).Any(char.IsUpper)) return true;
        return Words(text).Any(x => !x.SentenceStart && x.Word == w);
    }

    private static string Stem(string w)
    {
        w = w.ToLowerInvariant();
        if (w.EndsWith("'s") || w.EndsWith("’s")) w = w[..^2];
        if (w.Length > 4 && w.EndsWith("es")) return w[..^2];
        if (w.Length > 3 && w.EndsWith('s')) return w[..^1];
        return w;
    }

    /// <summary>Split into words (letters, digits, apostrophes inside a word); note which ones start a sentence.</summary>
    public static IEnumerable<(string Word, bool SentenceStart)> Words(string text)
    {
        bool start = true;
        var sb = new StringBuilder();
        for (int i = 0; i <= text.Length; i++)
        {
            char c = i < text.Length ? text[i] : ' ';
            bool inner = (c == '\'' || c == '’') && sb.Length > 0 && i + 1 < text.Length && char.IsLetter(text[i + 1]);
            if (char.IsLetterOrDigit(c) || inner || (c is '̀' or '́' or '̃' or '̈') || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark)
            {
                sb.Append(c == '’' ? '\'' : c);
                continue;
            }
            if (sb.Length > 0)
            {
                var w = sb.ToString();
                if (w.EndsWith("'s")) w = w[..^2];
                yield return (w, start);
                sb.Clear();
                start = false;
            }
            if (c is '.' or '!' or '?' or '\n' or ':' or '¡' or '¿' or '"' or '“') start = true;
        }
    }
}
