"""Takes the website's screenshots from the demo businesses, in Arabic (website/img/*.jpg).

    python scripts/website-screenshots.py [--api http://localhost:5081]

Needs the demo businesses (scripts/seed-business-demos.py) and Playwright for Python with Chrome installed:
    pip install playwright
Run it after a demo reset, so "today" on the screens is recent.
"""
import argparse
import datetime as dt
import json
import pathlib
import urllib.request

from playwright.sync_api import sync_playwright

PASSWORD = "Demo-Password-2026"
OUT = pathlib.Path(__file__).resolve().parent.parent / "website" / "img"

p = argparse.ArgumentParser()
p.add_argument("--api", default="http://localhost:5081")
args = p.parse_args()
BASE = args.api.rstrip("/")
OUT.mkdir(parents=True, exist_ok=True)

PHONE = {"viewport": {"width": 390, "height": 844}, "device_scale_factor": 2, "is_mobile": True, "has_touch": True}
DESKTOP = {"viewport": {"width": 1280, "height": 800}, "device_scale_factor": 1}


def api(code, path):
    """Calls the API as the business owner (used to put the first-day list away while the dashboard is photographed)."""
    def send(url, body, token=None):
        req = urllib.request.Request(BASE + url, data=json.dumps(body).encode(), method="POST",
                                     headers={"Content-Type": "application/json", **({"Authorization": f"Bearer {token}"} if token else {})})
        with urllib.request.urlopen(req) as r:
            text = r.read()
            return json.loads(text) if text else None
    token = send("/api/auth/login", {"tenantCode": code, "email": f"owner@{code}.qa", "password": PASSWORD})["accessToken"]
    send(path, {}, token)


def session(browser, code, device):
    ctx = browser.new_context(locale="ar-QA", timezone_id="Asia/Qatar", **device)
    ctx.add_init_script("localStorage.setItem('erp.lang', 'ar')")
    page = ctx.new_page()
    page.goto(BASE + "/login")
    page.fill("input[formcontrolname=tenantCode]", code)
    page.fill("input[formcontrolname=email]", f"owner@{code}.qa")
    page.fill("input[formcontrolname=password]", PASSWORD)
    page.click("button[type=submit]")
    page.wait_for_url(lambda u: "/login" not in u, timeout=180000)
    return ctx, page


def show(page, path, settle=1500):
    page.goto(BASE + path)
    try:
        page.wait_for_load_state("networkidle", timeout=20000)
    except Exception:
        pass
    page.wait_for_timeout(settle)


def shot(page, name):
    page.screenshot(path=str(OUT / f"{name}.jpg"), type="jpeg", quality=82)
    print("  ", name)


with sync_playwright() as pw:
    browser = pw.chromium.launch(channel="chrome", headless=True)

    # The owner's evening view, on the phone and on the shop computer: the figures, without the first-day list.
    api("grocery", "/api/core/getting-started/hide")
    try:
        ctx, page = session(browser, "grocery", DESKTOP)
        show(page, "/dashboard", 3000)
        page.locator(".stat").first.scroll_into_view_if_needed()
        page.evaluate("document.querySelector('mat-sidenav-content')?.scrollBy(0, -80)")
        page.wait_for_timeout(500)
        shot(page, "dashboard")
        ctx.close()
        ctx, page = session(browser, "grocery", PHONE)
        show(page, "/dashboard", 3000)
        page.locator(".stat").first.scroll_into_view_if_needed()
        page.evaluate("document.querySelector('mat-sidenav-content')?.scrollBy(0, -80)")
        page.wait_for_timeout(500)
        shot(page, "dashboard-phone")
    finally:
        api("grocery", "/api/core/getting-started/show")

    # The till on a phone, with a few items in the basket (nothing is sold).
    show(page, "/sales/pos")
    resume = page.get_by_role("button", name="متابعة الوردية")
    if resume.count():
        resume.first.click()
    else:
        page.get_by_role("button", name="فتح وردية").first.click()
        page.get_by_role("dialog").get_by_role("button", name="فتح وردية").click()
    page.wait_for_selector(".tile", timeout=30000)
    tiles = page.locator(".tile")
    for i in (0, 1, 1, 3):
        tiles.nth(i).click()
    page.locator(".cart").scroll_into_view_if_needed()
    page.wait_for_timeout(800)
    shot(page, "pos-phone")
    ctx.close()

    # Payroll with WPS: the latest run of the typing office.
    ctx, page = session(browser, "typing-office", DESKTOP)
    show(page, "/payroll")
    page.locator("tr.clickable").first.click()
    page.wait_for_url(lambda u: "/payroll/" in u)
    page.wait_for_timeout(2500)
    shot(page, "payroll")
    show(page, "/documents", 2500)
    shot(page, "documents")
    ctx.close()

    # Segment screens.
    ctx, page = session(browser, "abaya-tailor", DESKTOP)
    show(page, "/tailoring", 2500)
    shot(page, "tailoring")
    ctx.close()
    ctx, page = session(browser, "salon", DESKTOP)
    show(page, "/salon", 2500)
    # The latest day with bookings (the demo is filled in relative to the day it was seeded).
    for back in range(0, 10):
        if page.locator(".appt").count() >= 3:
            break
        page.locator("input[type=date]").fill((dt.date.today() - dt.timedelta(days=back + 1)).isoformat())
        page.locator("input[type=date]").dispatch_event("change")
        page.wait_for_timeout(1500)
    page.evaluate("document.activeElement?.blur()")
    shot(page, "salon")
    ctx.close()
    browser.close()
