using System.Text;

namespace Marigold;

/// <summary>
/// Builds the ofrenda as ONE self-contained HTML file: photos embedded, no scripts, no web fonts, no links.
/// It opens in any browser, prints to PDF, and doesn't depend on Marigold (or any service) still existing.
/// </summary>
public static class Ofrenda
{
    public static string Render(Person p, Func<Photo, byte[]?> photoBytes)
    {
        static string H(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
        var lang = p.Language switch
        {
            "Español" => "es", "Français" => "fr", "Português" => "pt", "ਪੰਜਾਬੀ (Punjabi)" => "pa", "हिन्दी (Hindi)" => "hi", _ => "en",
        };
        var kicker = lang switch { "es" => "En memoria", "fr" => "En mémoire", "pt" => "Em memória", "pa" => "ਯਾਦ ਵਿੱਚ", "hi" => "स्मृति में", _ => "In loving memory" };
        var onIt = lang switch { "es" => "En la ofrenda", "fr" => "Sur l’ofrenda", "pt" => "Na oferenda", _ => "On the ofrenda for them" };
        var sb = new StringBuilder();
        sb.Append($"""
            <!doctype html>
            <html lang="{lang}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="generator" content="Marigold">
            <title>{H(p.Name)} · ofrenda</title>
            <style>{Css}</style>
            </head>
            <body>
            <div class="picado" aria-hidden="true">{string.Concat(Enumerable.Range(0, 9).Select(i => $"<span class=\"f f{i % 5}\"></span>"))}</div>
            <main>
              <header>
                <p class="kicker">{H(kicker)}</p>
                <h1>{H(p.Name)}</h1>
            """);
        if (!string.IsNullOrWhiteSpace(p.Years)) sb.Append($"<p class=\"years\">{H(p.Years)}</p>");
        if (!string.IsNullOrWhiteSpace(p.Relation)) sb.Append($"<p class=\"relation\">{H(p.Relation)}</p>");
        sb.Append("</header><div class=\"garland\" aria-hidden=\"true\"></div>");

        var photos = p.Photos.Select(ph => (ph, bytes: photoBytes(ph))).Where(x => x.bytes != null).ToList();
        if (photos.Count > 0)
        {
            sb.Append("<section class=\"photos\">");
            foreach (var (ph, bytes) in photos)
            {
                var mime = ph.Ext == ".png" ? "image/png" : "image/jpeg";
                sb.Append($"<figure><img src=\"data:{mime};base64,{Convert.ToBase64String(bytes!)}\" alt=\"{H(string.IsNullOrWhiteSpace(ph.Description) ? ph.Caption : ph.Description)}\">");
                if (!string.IsNullOrWhiteSpace(ph.Caption)) sb.Append($"<figcaption>{H(ph.Caption)}</figcaption>");
                sb.Append("</figure>");
            }
            sb.Append("</section>");
        }

        if (p.Remembrance is { } r && !string.IsNullOrWhiteSpace(r.Text))
        {
            sb.Append("<section class=\"words\">");
            if (!string.IsNullOrWhiteSpace(r.Title)) sb.Append($"<h2>{H(r.Title)}</h2>");
            foreach (var para in r.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                sb.Append($"<p>{H(para)}</p>");
            sb.Append("</section>");
        }

        if (p.Offerings.Count > 0)
        {
            sb.Append($"<section class=\"offerings\"><h2>{H(onIt)}</h2><ul>");
            foreach (var o in p.Offerings) sb.Append($"<li>{H(o)}</li>");
            sb.Append("</ul></section>");
        }

        sb.Append($"""
              <div class="candles" aria-hidden="true"><i></i><i></i><i></i></div>
            </main>
            <footer>Made with Marigold on {DateTime.Now:MMMM d, yyyy}. Kept on our own computer.</footer>
            </body>
            </html>
            """);
        return sb.ToString();
    }

    private const string Css = """
        :root { --night:#1d1230; --plum:#2c1a47; --marigold:#f59e0b; --gold:#fbbf24; --rose:#db2777; --teal:#14b8a6; --paper:#fff8ec; --ink:#2b2118; }
        * { box-sizing: border-box; }
        html { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
        body { margin:0; background: radial-gradient(1200px 600px at 50% -10%, #3b2463, var(--night)); color: var(--paper);
               font: 17px/1.6 Georgia, "Iowan Old Style", "Palatino Linotype", serif; min-height:100vh; }
        .picado { display:flex; justify-content:center; gap:6px; padding:0 8px; overflow:hidden; border-top:3px solid #c9a46a; }
        .picado .f { width:72px; height:84px; flex:0 0 auto; clip-path: polygon(0 0,100% 0,100% 82%,88% 100%,75% 82%,62% 100%,50% 82%,38% 100%,25% 82%,12% 100%,0 82%);
                     -webkit-mask: radial-gradient(circle at 50% 42%, transparent 9px, #000 10px) , radial-gradient(circle at 25% 25%, transparent 5px, #000 6px);
                     -webkit-mask-composite: source-in; mask-composite: intersect; }
        .f0{background:var(--rose)} .f1{background:var(--marigold)} .f2{background:#8b5cf6} .f3{background:var(--teal)} .f4{background:var(--gold)}
        main { max-width: 860px; margin: 0 auto; padding: 24px 16px 40px; }
        header { text-align:center; }
        .kicker { letter-spacing:.18em; text-transform:uppercase; font-size:12px; color:#e9d5b5; margin:8px 0 4px; }
        h1 { font-size: clamp(34px, 7vw, 56px); line-height:1.1; margin:0; color: var(--gold); font-weight:normal; text-wrap: balance; }
        .years { margin:6px 0 0; font-size:20px; color:#f3e8d2; }
        .relation { margin:2px 0 0; font-style:italic; color:#e9d5b5; }
        .garland { height:22px; margin:22px auto; max-width:620px;
                   background: radial-gradient(circle at 11px 11px, var(--marigold) 8px, #d97706 9px, transparent 10px) center 0/22px 22px; background-repeat: space no-repeat; }
        .photos { display:flex; flex-wrap:wrap; justify-content:center; gap:22px; margin: 8px 0 26px; }
        figure { margin:0; background:#fdfaf3; padding:10px 10px 6px; width: min(240px, 44vw); box-shadow: 0 10px 24px rgba(0,0,0,.45); transform: rotate(-1.5deg); break-inside: avoid; }
        figure:nth-child(even) { transform: rotate(1.8deg); }
        figure img { display:block; width:100%; aspect-ratio: 4/5; object-fit: cover; filter: sepia(.15); }
        figcaption { color: var(--ink); font-style: italic; font-size:14px; text-align:center; padding:6px 2px 2px; }
        .words { background: var(--paper); color: var(--ink); padding: 26px 28px; border-radius: 6px; max-width: 640px; margin: 0 auto 26px;
                 box-shadow: 0 0 0 6px rgba(245,158,11,.35), 0 14px 40px rgba(0,0,0,.4); }
        .words h2 { text-wrap: balance; margin:0 0 10px; font-weight:normal; font-size:24px; color:#9a3412; text-align:center; }
        .words p { margin: 0 0 12px; }
        .offerings { text-align:center; }
        .offerings h2 { font-weight:normal; font-size:18px; letter-spacing:.06em; color:#e9d5b5; margin: 0 0 10px; }
        .offerings ul { list-style:none; padding:0; margin:0 auto; display:flex; flex-wrap:wrap; gap:10px; justify-content:center; max-width:680px; }
        .offerings li { background: rgba(255,255,255,.08); border:1px solid rgba(251,191,36,.45); border-radius:999px; padding:6px 14px 6px 34px; position:relative; }
        .offerings li::before { content:""; position:absolute; left:10px; top:50%; width:16px; height:16px; margin-top:-8px; border-radius:50%;
                                background: radial-gradient(circle, #b45309 3px, var(--marigold) 4px, var(--gold) 8px); }
        .candles { display:flex; justify-content:center; gap:48px; margin-top:34px; }
        .candles i { position:relative; width:22px; height:58px; background: linear-gradient(#fff7e0, #f1dcb0); border-radius:3px; }
        .candles i::before { content:""; position:absolute; left:50%; top:-26px; width:14px; height:24px; margin-left:-7px; border-radius:50% 50% 45% 45%;
                             background: radial-gradient(circle at 50% 70%, #fff 0 3px, #fde68a 4px, #f59e0b 9px, transparent 11px); animation: flicker 2.4s infinite ease-in-out; }
        .candles i:nth-child(2)::before { animation-delay: .7s } .candles i:nth-child(3)::before { animation-delay: 1.3s }
        @keyframes flicker { 0%,100% { transform: scale(1) rotate(-2deg); opacity:1 } 50% { transform: scale(.92,1.06) rotate(2deg); opacity:.85 } }
        @media (prefers-reduced-motion: reduce) { .candles i::before { animation: none; } }
        footer { text-align:center; font-size:12px; color:#bfa98a; padding: 0 16px 28px; }
        @media print { body { background: var(--night); } .candles i::before { animation:none } figure, .words { break-inside: avoid; } }
        @media (max-width: 480px) { .photos { gap:14px; } figure { width: calc(50% - 7px); padding: 7px 7px 4px; } figcaption { font-size:12.5px; } .words { padding: 20px 18px; } .picado .f { width:52px; height:62px; } .candles { gap:32px; } }
        """;
}
