"""Download a few PUBLIC-DOMAIN photos from Wikimedia Commons for testing Marigold, and record their credits.

Usage: python samples/fetch_photos.py <out folder>
Only files whose licence is Public domain or CC0 are used. Each one's source page is written to <out>/CREDITS.md.
The people in these photos are strangers from archives; the memories in samples/person.json are fictional.
"""
import json, os, re, sys, time, urllib.parse, urllib.request

out = sys.argv[1]
os.makedirs(out, exist_ok=True)
UA = {"User-Agent": "MarigoldTests/0.1 (https://github.com/GurminderKaur/Marigold)"}
QUERIES = [
    ("01-kitchen", "woman baking kitchen 1940s photograph"),
    ("02-sewing", "treadle sewing machine woman photograph"),
    ("03-bread", "pan de muerto"),
    ("04-radio", "family listening radio 1940s"),
]

def get(url):
    for attempt in range(3):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=60) as r:
                return r.read()
        except Exception as e:
            print(f"  retry ({e})"); time.sleep(3 * (attempt + 1))
    raise RuntimeError("download failed: " + url)

def strip(html): return re.sub(r"<[^>]+>", "", html or "").strip()

credits, used = [], set()
for name, q in QUERIES:
    api = "https://commons.wikimedia.org/w/api.php?" + urllib.parse.urlencode({
        "action": "query", "format": "json", "generator": "search", "gsrnamespace": 6, "gsrlimit": 25,
        "gsrsearch": q + " filetype:bitmap", "prop": "imageinfo", "iiprop": "url|mime|extmetadata", "iiurlwidth": 1024,
        "iiextmetadatafilter": "LicenseShortName|Artist|ImageDescription",
    })
    pages = sorted(json.loads(get(api)).get("query", {}).get("pages", {}).values(), key=lambda p: p.get("index", 0))
    for p in pages:
        ii = (p.get("imageinfo") or [{}])[0]
        meta = ii.get("extmetadata", {})
        lic = meta.get("LicenseShortName", {}).get("value", "")
        if p["title"] in used or ii.get("mime") not in ("image/jpeg", "image/png"): continue
        if not (lic.lower().startswith("public domain") or lic.upper().startswith("PD") or lic.upper().startswith("CC0")): continue
        data = get(ii.get("thumburl") or ii["url"])
        ext = ".png" if data[:4] == b"\x89PNG" else ".jpg"
        open(os.path.join(out, name + ext), "wb").write(data)
        used.add(p["title"])
        credits.append({"file": name + ext, "title": p["title"], "page": ii.get("descriptionurl"), "licence": lic, "author": strip(meta.get("Artist", {}).get("value", ""))[:120]})
        print(f"✓ {name}{ext}: {p['title']} ({lic})")
        break
    else:
        print(f"✗ no public-domain photo found for '{q}'")
    time.sleep(1)

with open(os.path.join(out, "CREDITS.md"), "w", encoding="utf-8") as f:
    f.write("# Photo credits\n\nPublic-domain photos from Wikimedia Commons, used to test Marigold. The memories that go with them are fictional.\n\n")
    for c in credits:
        f.write(f"- `{c['file']}`: [{c['title']}]({c['page']}), {c['licence']}{', ' + c['author'] if c['author'] else ''}\n")
json.dump(credits, open(os.path.join(out, "credits.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
if len(credits) < 2: sys.exit("Not enough public-domain photos found.")
