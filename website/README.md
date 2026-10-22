# Website

A one-page site for shop owners, Arabic first with an English switch. Plain files: no build step, nothing loaded from
other sites (the Arabic font is in `fonts/`, licensed under the SIL Open Font License).

- **Before sharing:** put your WhatsApp number and email in the `CONTACT` block at the bottom of `index.html`. Until
  then the WhatsApp buttons only scroll to the contact section and the email button is hidden.
- **Preview:** open `index.html` in a browser. `index.html?lang=en` opens it in English.
- **Prices and the launch offer** match `docs/Sales-Leaflet.html` and the strategy document; change all three together.
- **Screenshots** (`img/`) are real screens of the demo businesses. To take them again after a demo reset:
  `python scripts/website-screenshots.py --api http://localhost:5081` (needs Playwright for Python and Chrome).
- **Publishing** waits until the project moves to hosting (local only for now). It can go on any static host, for
  example Cloudflare Pages, with a domain of about QAR 40–60 a year.
