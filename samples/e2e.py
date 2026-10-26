"""End-to-end test of the Marigold app in a real browser (Playwright).

Usage: python samples/e2e.py <base url> <photo folder> <out folder>
Adds the fictional sample person from samples/person.json, uploads the photos, waits for the model to describe them,
writes the remembrance, checks the ghost check and the ofrenda page, and saves screenshots to <out folder>.
Works with any model: it checks behaviour, not exact wording.
"""
import json, os, sys, time, urllib.request
from playwright.sync_api import sync_playwright, expect

base, photos, out = sys.argv[1].rstrip("/"), sys.argv[2], sys.argv[3]
os.makedirs(out, exist_ok=True)
here = os.path.dirname(os.path.abspath(__file__))
person = json.load(open(os.path.join(here, "person.json"), encoding="utf-8"))
files = sorted(os.path.join(photos, f) for f in os.listdir(photos) if f.lower().endswith((".jpg", ".jpeg", ".png")))
results = {}

def step(msg): print(f"• {msg}", flush=True)

with sync_playwright() as p:
    browser = p.chromium.launch()
    page = browser.new_page(viewport={"width": 1280, "height": 900})
    errors = []
    page.on("console", lambda m: errors.append(m.text) if m.type == "error" else None)
    page.on("pageerror", lambda e: errors.append(str(e)))
    page.set_default_timeout(20_000)

    step("open the app"); page.goto(base)
    expect(page.get_by_text("Who would you like to remember?")).to_be_visible()

    step("add the person"); page.fill("#newName", person["name"]); page.click("#addForm button")
    expect(page.locator("#fName")).to_have_value(person["name"])
    page.fill("#fYears", person["years"]); page.fill("#fRel", person["relation"])
    page.fill("#fMem", person["memories"]); page.fill("#fOff", ", ".join(person["offerings"]))
    page.locator("#fOff").blur()
    expect(page.locator("#saved")).to_have_text("Saved on this computer.")

    step(f"upload {len(files)} photos"); page.set_input_files("#file", files)
    expect(page.locator(".ph")).to_have_count(len(files))
    t0 = time.time()
    page.wait_for_function("document.querySelectorAll('.ph[data-status=waiting]').length === 0", timeout=1_800_000, polling=2000)
    results["describeSeconds"] = round(time.time() - t0, 1)
    assert page.locator(".ph[data-status=error]").count() == 0, "a photo failed: " + page.locator(".status.err").first.inner_text()
    page.screenshot(path=os.path.join(out, "app-photos.png"), full_page=True)

    step("write the remembrance"); t0 = time.time()
    page.click("#writeBtn")
    expect(page.locator("#rText")).to_be_visible(timeout=1_800_000)
    results["writeSeconds"] = round(time.time() - t0, 1)
    text = page.locator("#rText").input_value()
    assert len(text.split()) > 40, "remembrance too short: " + text
    ghost = page.locator(".ghosts")
    clean = page.locator(".clean")
    expect(ghost.or_(clean)).to_be_visible()
    results["ghosts"] = [c.inner_text() for c in page.locator(".ghosts .chip").all()]
    page.locator("#words").screenshot(path=os.path.join(out, "app-remembrance.png"))
    page.screenshot(path=os.path.join(out, "app-full.png"), full_page=True)

    if results["ghosts"]:
        step("remove the ghosts by hand → the check turns clean")
        import re
        def scrub(s):
            for g in results["ghosts"]:
                s = re.sub(r"(?<![\w])" + re.escape(g) + r"(?![\w])", "", s)
            return s
        page.fill("#rTitle", scrub(page.locator("#rTitle").input_value()))
        page.fill("#rText", scrub(text)); page.click("text=Save my edits")
        expect(page.locator(".clean")).to_be_visible()

    step("open the ofrenda")
    pid = page.evaluate("current.id")
    with page.context.expect_page() as pop:
        page.click("text=Open the ofrenda")
    of = pop.value; of.wait_for_load_state()
    expect(of.locator("h1")).to_have_text(person["name"])
    assert of.locator("figure img").count() == len(files)
    of.screenshot(path=os.path.join(out, "ofrenda.png"), full_page=True)
    of.set_viewport_size({"width": 390, "height": 844}); of.wait_for_timeout(300)
    assert of.evaluate("document.documentElement.scrollWidth") <= 390, "ofrenda scrolls sideways on a phone"
    of.screenshot(path=os.path.join(out, "ofrenda-phone.png"), full_page=True)

    step("download the ofrenda file")
    with page.expect_download() as dl:
        page.click("text=Download (.html)")
    path = os.path.join(out, "ofrenda.html"); dl.value.save_as(path)
    html = open(path, encoding="utf-8").read()
    assert "data:image/jpeg;base64," in html and "<script" not in html

    step("phone width: the app doesn't scroll sideways")
    page.set_viewport_size({"width": 390, "height": 844}); page.wait_for_timeout(300)
    assert page.evaluate("document.documentElement.scrollWidth") <= 390
    page.screenshot(path=os.path.join(out, "app-phone.png"))

    results["remembrance"] = page.locator("#rText").input_value()
    results["captions"] = [i.input_value() for i in page.locator(".ph textarea").all()]
    assert not errors, f"browser errors: {errors}"
    browser.close()

# The server must refuse changes from other websites.
def status(method, url, headers=None):
    req = urllib.request.Request(base + url, method=method, headers=headers or {}, data=b"{}" if method != "GET" else None)
    try: return urllib.request.urlopen(req).status
    except urllib.error.HTTPError as e: return e.code
assert status("DELETE", f"/api/people/{pid}") == 403, "change without the Marigold header must be refused"
assert status("GET", "/api/people", {"Host": "evil.example"}) == 400, "foreign Host must be refused"
assert status("GET", "/api/photo/../../etc/passwd") == 404

json.dump(results, open(os.path.join(out, "e2e.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
print(json.dumps(results, ensure_ascii=False, indent=2))
print("✓ end-to-end test passed")
