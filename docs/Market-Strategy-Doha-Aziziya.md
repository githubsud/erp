# Market strategy: small shops and SMEs in Doha (Al Aziziya first)

*October 2026. Desk research only: no shop interviews yet. Section 9 lists the checks to make on the street before we commit.*

## 1. Who is in Al Aziziya

Al Aziziya is a mixed residential and commercial district in western Doha, between Salwa Road and Aspire Zone. Its busiest strips are **Al Aziziya Street** and **Othman Bin Affan Street**.

| Segment | Where | Typical size | Notes |
|---|---|---|---|
| **Abaya shops and tailors** | Al Aziziya St (the street is known for them) | 2–15 staff | Made-to-measure orders, deposits, fittings, peaks before Eid and Ramadan |
| Ladies' clothing, beauty, jewellery | Al Mansoura St | 2–10 | Retail POS, stock by size/colour |
| **Barbers and beauty salons** | Throughout | 3–12 | Appointments, staff commission per service, packages |
| **Restaurants and cafeterias** (many Asian) | Throughout | 4–25 | Delivery apps (Talabat, Snoonu, Keeta, Carriage), cash and card |
| **Grocers / mini-marts (baqala)** | Throughout | 2–8 | Barcode POS, expiry dates, regular customers buying on credit |
| Furniture and hardware | Al Najma St | 3–20 | Quotations, deposits, delivery and installation |
| **Auto repair workshops** | Streets off Al Najma St | 3–20 | Job cards per vehicle, parts and labour |

Nationwide, trade (retail and wholesale) plus auto workshops make up the largest group of businesses in Qatar: about 56% of establishments in 2020. Shop rents in Aziziya run around **QAR 10,000–12,000 a month for 40–50 m²**. A software fee of QAR 150–300 a month is small next to that, but it has to save visible money.

**Typical owner profile:** Qatari owners or sponsors with an expat manager running the shop, mostly expat staff paid through WPS, the owner often away from the shop, and WhatsApp as the main business tool.

## 2. What these businesses actually struggle with

Ranked by how strongly they will feel it.

1. **Salaries and WPS without fines.** WPS applies to every private employer, SMEs included. Late payment can cost **up to QAR 6,000 per affected worker**, and the ministry can block transactions and visa quotas. Ministerial Decision 50/2026 (effective 8 Sep 2026, per secondary sources) fixes the pay date and a 7-day WPS deadline. *We already do this well; it is our strongest door-opener.*
2. **"I'm not in the shop — is my cash right?"** Owner-absent shops worry about cash leakage: refunds, discounts, voids, missing stock. They want a short daily figure on the phone.
3. **Renewals and expiries.** The CR, the municipality trade licence and the Chamber membership usually renew together every year, and the tenancy contract is the usual cause of rejection. Staff QIDs, passports and health cards expire too. Missing a date costs fines and blocked services.
4. **Stock and expiry.** Grocers lose money on expired goods; clothing shops on dead sizes and colours.
5. **Customer credit.** Grocers and some workshops sell to regulars on account (the old *daftar*), and collecting is hard.
6. **Delivery-app chaos (restaurants).** A tablet each for Talabat, Snoonu and Carriage, re-keying orders, and 20–30% commissions.
7. **The coming tax wave.** There is **no VAT yet**, but the GCC framework rate is 5% and the Finance Minister says it is being considered. The Cabinet approved the **e-invoicing draft law on 6 May 2026**, expected to phase in from around 2027–2028, starting with large taxpayers. "Be ready before VAT arrives" is a strong, honest sales line.
8. **Software adoption barriers.** In a 2026 Visa survey, only 43% of Qatari SMEs use an integrated business tool. Non-users said they are "too small" (18%), that it's too expensive (14%), or that they don't trust relying on one provider (14%). Student research adds complexity, lack of training and resistance to change.

**What this means for us:** the product must be cheap and obviously worth it, simple, Arabic-first, quick to start, and it must not lock customers in (they can export their data at any time).

## 3. Competition and price anchors

| Competitor | What it is | Price signal |
|---|---|---|
| Wafeq | Cloud accounting with e-invoicing focus | QAR 99–199/month annual, 119–249 monthly; AI invoice scans (20/month on Starter) |
| HASIF (Qatari, backed by Snoonu in May 2026) | AI accounting for SMEs | Not public |
| Foodics, Tamias (Snoonu POS partner) | Restaurant POS | Strong in restaurants: **don't lead with restaurants** |
| Zoho, Odoo, QuickBooks, Tally, Loyverse | Generic | Weak on WPS, gratuity and Arabic payroll |
| Typing / PRO offices (*al-manadeeb*) | Manual WPS, CR renewals | Charge per transaction: **a channel partner, not just a competitor** |

**Our edge:** most competitors are accounting-first or POS-first. **Nobody local combines WPS payroll, POS, stock and documents with an Arabic AI assistant** at small-shop prices.

## 4. What we already have vs. what Aziziya needs

| Need | Status |
|---|---|
| WPS file, payroll, gratuity, leave, GRSIA | ✅ built |
| Document and expiry tracking (staff and company) | ✅ built (add CR, trade licence, tenancy and civil-defence types to the company documents) |
| Retail POS with offline mode, barcode, cash/card, Z-report | ✅ built |
| Stock, batches/expiry, serials, reorder | ✅ built |
| Employee phone app (payslips, leave, expenses, GPS attendance) | ✅ built |
| Quotations, invoices, deposits, credit control | ✅ built |
| **Tailoring / abaya orders** (measurements, deposit, fitting, ready date, tailor workload) | ❌ **missing: build first for Aziziya** |
| **Salon appointments and staff commission per service** | ❌ missing |
| **POS sale on customer account** (daftar) and customer statement by WhatsApp | ⚠️ partly: credit customers exist in Sales but not at the till |
| **Owner daily summary on phone / WhatsApp** | ⚠️ dashboard exists; push summary missing |
| **Simple "Shop mode"** (setup wizard, hide accounting words) | ❌ missing |
| Workshop job cards (vehicle, plate, parts and labour) | ❌ missing (phase 2) |
| Restaurant tables, kitchen screen, delivery-app intake | ❌ missing (phase 3; crowded market) |
| VAT 5% and GTA e-invoicing | ⏳ wait for the law; keep the design ready |

## 5. AI features that will set us apart

The guiding rule: **AI suggests, people confirm.** The AI never posts to the ledger by itself (CLAUDE.md rule 1): it prepares a draft, and a person approves it.

| # | Feature | Why it sells in Aziziya | Effort |
|---|---|---|---|
| 1 | **Arabic (Gulf dialect) assistant**: "كم بعنا اليوم؟" "مين ما استلم راتبه؟" "وش الأصناف اللي بتخلص؟" | The owner gets answers without learning the software | M |
| 2 | **Nightly owner summary** (sales, cash difference, refunds, low stock, expiries, WPS countdown) in Arabic, on the phone or WhatsApp | Solves pain #2 in one message | S–M |
| 3 | **Photo → document**: snap a supplier invoice or receipt (Arabic or English) and get a draft supplier invoice or expense | Removes typing, the main reason shops quit software | M |
| 4 | **Cash-leakage alerts**: unusual refunds, voids or discounts per cashier, repeated cash shortages, stock that disappears | Owners pay for this immediately | M |
| 5 | **Seasonal demand and reorder**: learns Ramadan, Eid, school-season and summer patterns (abayas before Eid, groceries in Ramadan) | Less dead stock, fewer stock-outs | M |
| 6 | **Compliance guardian**: watches WPS deadlines, QID, CR, trade licence and tenancy expiries, and drafts the renewal checklist | Avoids fines; ties into our WPS strength | S |
| 7 | **Customer messages in the customer's language**: "Your abaya is ready", payment reminders, statements (Arabic, English, Urdu, Hindi, Malayalam, Tagalog…) | Staff stop typing messages by hand | S |
| 8 | **Talk-to-enter**: "سجل مصروف ٥٠ ريال بنزين" becomes a draft expense | Fast for busy owners | S (after #1) |
| 9 | **AI onboarding**: upload old Excel files or photos of item lists, customers and staff; AI maps them into the import | Go live in one visit | M |
| 10 | **30/60-day cash-flow forecast** from receivables, payables, payroll and rent | "Can I pay salaries next month?" | M |

**How it is built:** one AI module that calls Claude through a paid API. It uses read-only, tenant-scoped tools (the same permissions as the user), keeps a usage meter and monthly cap per company, logs every answer to the audit trail, and turns actions into drafts only. **This has a running cost per message**, which section 7 prices in. It needs your approval before I build it.

## 6. Proposed build order (needs your approval)

**Phase A: "Aziziya-ready" (≈4–6 weeks of work)**
1. **Shop mode and 10-minute setup wizard.** Choose the business type (abaya/tailor, salon, grocery, clothing, furniture, workshop, restaurant). That switches on only the needed modules, loads sample items and uses plain Arabic words.
2. **Tailoring and abaya orders.** Customer measurement profiles, order with style, fabric and photo, deposit and balance, fitting and ready dates, tailor assignment and workload, a "ready" message, and a receipt with an order QR code.
3. **Salon module.** Services price list, appointments calendar, walk-ins, staff commission per service (flows into payroll), and packages.
4. **POS on customer account** (daftar), credit limits, and customer statement and payment reminder by message.
5. **Company licence tracker.** CR, trade licence, Chamber, tenancy and civil-defence dates with reminders. The engine already exists; this adds the types and a renewal checklist.
6. **Owner phone view.** A one-screen daily summary inside the existing phone app (no WhatsApp cost yet).

**Phase B: AI (after Phase A, needs the AI budget approved)**
Items 2, 3, 6 and 1 from section 5 first (nightly summary, photo to document, compliance guardian, Arabic assistant), then leakage alerts and seasonal reorder.

**Phase C: when the market or the law moves**
VAT 5% and GTA e-invoicing (as soon as the law is gazetted), delivery-app order intake for restaurants, workshop job cards, and WhatsApp Business API delivery of messages.

## 7. Packaging and price (suggested, in QAR)

| Plan | For | Includes | Price |
|---|---|---|---|
| **Payroll** | Any company with staff | WPS, payroll, gratuity, leave, documents, employee app | **QAR 99/month up to 10 staff**, then QAR 8 per employee |
| **Shop** | Abaya, salon, grocery, clothing | Payroll + 1 POS till + stock + segment module + owner summary | **QAR 199/month** (annual: 1,990) |
| **Business** | 2–3 branches, furniture, workshops | Shop + sales/purchasing + accounting statements + 3 tills | **QAR 399/month** |
| **AI add-on** | All plans | Assistant, photo-to-document, alerts, with a monthly message allowance | **QAR 49–99/month** (covers the API cost) |

- **Launch offer for Aziziya:** the first 10 shops get 3 months free, free setup and data import, in exchange for a testimonial and feedback.
- **Always:** export all data to Excel at any time. This answers the "don't trust one provider" objection.

## 8. How to sell on the street

1. **Lead with WPS and fines**, not "ERP". Opening line: *"Pay salaries on time through WPS without fines, and know your shop's sales every night on your phone."*
2. **Demo on a tablet with that shop's segment loaded** (an abaya order, a salon appointment). Setup in front of the owner, in under 10 minutes.
3. **Channel partners.** Typing/PRO offices and small accounting firms already handle these shops' WPS and renewals; give them a referral share or a partner login. Accountants manage many small clients each.
4. **Government programme.** Apply to become an approved provider in **MCIT "SMEs Go Digital"** (SMEs up to 250 staff and QAR 100 m revenue; 59 approved providers, 173 SMEs onboarded; contact smesgodigital@mcit.gov.qa). This gives credibility and introductions.
5. **Languages.** Arabic and English in the product; leaflets also in Urdu, Hindi and Malayalam for shop managers.
6. **Card and QR payments.** Many shops accept cards through SADAD and similar providers, and Fawran instant transfers are growing fast (QAR 6.7 bn in June 2026, +145%). Integrating payments later raises stickiness; it is a paid integration.

## 9. Before committing: validate with 10 shop visits

Ask 3 abaya/tailor shops, 2 salons, 2 grocers, 1 furniture shop, 1 workshop and 1 restaurant:
1. How do you pay salaries and do WPS today? Who does it, and what does it cost? Any fines or late months?
2. How do you know today's sales and cash when you are not in the shop?
3. What do you use now (paper, Excel, a POS, an accountant)? What do you hate about it?
4. (Tailors) How do you record measurements, deposits and ready dates? How many orders are late before Eid?
5. (Salons) How do you pay staff commission?
6. (Grocers) How much do customers owe you on credit? How do you remind them?
7. Which renewals or expiries surprised you in the last year?
8. Would you pay QAR 199 a month if it removed X? What would make you say no?
9. Would you use a phone assistant that answers in Arabic? Would you send it photos of invoices?

## 10. What needs you (decisions or money)

- **Approve Phase A** (it changes what gets built next).
- **AI budget:** a Claude API account with monthly spend; the AI add-on price covers it.
- **WhatsApp Business API:** a provider and per-message costs (Phase C; the phone app is used until then).
- **Hosting** in a Qatar or GCC region before real customer data goes live (privacy law, PDPPL; sector rules are stricter for health and finance).
- **Adviser sign-off** of ⚠️ VERIFY payroll and tax rules before selling payroll.
- **MCIT provider application** (you, as the company owner).

## Sources
- Al Aziziya shopping streets: [Marhaba](https://marhaba.qa/qatars-flourishing-shopping-streets/); rents: [Qatar Living listing](https://www.qatarliving.com/en/properties/rent/commercial/retail/retail_shops_6_units_available_aziziya/291397), [Qatar Living listing](https://www.qatarliving.com/en/properties/rent/commercial/retail/commercial_shop_in_al_aziziya/230085)
- SME sector mix: [Gulf Times: business establishments](https://gulf-times.com/story/702409/Qatar-sees-131-growth-in-business-establishments-i), [QDB State of SMEs](https://www.qdb.qa/-/media/qdbapp/publications-pdf/the-satate-of-smes-in-qatar-2016_en-web---p.pdf)
- SME digital adoption: [Gulf Times: Visa survey](https://www.gulf-times.com/article/735200/business/digital-commerce-is-seen-deeply-embedded-in-qatars-sme-economy), [QNA: MCIT](https://qna.org.qa/en/News-Area/News/2026-2/19/mcit-advances-digital-transformation-in-small-and-medium-sized-enterprises)
- MCIT SMEs Go Digital: [MCIT](https://www.mcit.gov.qa/en/news/mcit-showcases-milestones-of-the-smes-go-digital-program-in-accelerating-sme-digital-transformation/), [Qatar Tribune](https://www.qatar-tribune.com/article/243312/nation/mcit-rallies-smes-and-digital-solution-providers-to-strengthen-digital-adoption/amp), [partners](https://godigital.mcit.gov.qa/index.php/en/partners)
- WPS: [greytHR guide 2026](https://www.greythr.com/middle-east/blog/qatar-wps-compliance-2026/), [Gulf News](https://gulfnews.com/world/gulf/qatar/qatar-vows-100-compliance-on-key-labour-reform-1.1922776), [Teamed](https://www.teamed.global/gulf-employment-law/qatar-wps-payroll-and-social-insurance)
- E-invoicing: [Wafeq](https://www.wafeq.com/en-qa/tax-and-reporting/qatar's-mandatory-e-invoicing-law-is-here:-what-the-gta's-2026-announcement-means-for-your-business), [Fiscal Solutions](https://www.fiscal-requirements.com/news/5418), [goroute](https://goroute.ai/blog/qatar-e-invoicing-mandate-2027/)
- VAT: [taxinme status tracker](https://www.taxinme.com/articles/qatar-vat-status-2026.php), [The New Arab](https://www.newarab.com/news/qatar-mulls-vat-amid-budget-pressures-iran-war), [vatupdate](https://www.vatupdate.com/2026/04/16/qatar-vat-update-no-vat-yet-existing-taxes-apply-no-refunds-for-tourists-or-businesses/)
- Licence renewal: [Qatar Living checklist](https://www.qatarliving.com/en/article/renewing-trade-license-qatar-checklist-rejections), [Doha News: civil-defence exemption](https://dohanews.co/?p=38694)
- Competitors and prices: [Wafeq pricing](https://help.wafeq.com/hc/en-ae/articles/22187364508700-What-are-your-pricing-plans-and-available-packages), [HASIF / Snoonu](https://www.wamda.com/2026/05/qatar-snoonu-backs-accounting-startup-hasif-support-sme-digitisation), [SADAD POS pricing](https://sadad.qa/en/?p=2920956), [SADAD restaurant POS guide](https://sadad.qa/en/best-restaurant-pos-system-doha/), [Snoonu + Tamias](https://www.qatar-tribune.com/article/232087/NATION/Snoonu-partners-with-Tamias-to--enhance-customer-experience), [delivery apps](https://blog.menuviel.com/most-popular-food-delivery-apps-in-qatar/)
- Tailoring software patterns: [Qoyod tailoring](https://www.qoyod.com/en/fields/accounting-software-for-tailoring/), [Orderry](https://orderry.com/tailor-shop-software/)
- Payments: [Gulf Times: Fawran](https://www.gulf-times.com/article/729909/business/fawran-transactions-value-jumps-145-to-qr6745bn-in-june-qcb), [Gulf News: QPay via Fawran](https://gulfnews.com/business/banking/qatar-central-bank-announces-qpay-services-through-fawran-payment-service-1.1729589858624)
- WhatsApp usage: [Doha News](https://dohanews.co/?p=35866), [The Peninsula](https://s.thepeninsula.qa/article/11/12/2014/whatsapp-most-favoured-social-media-in-qatar)
- Data protection: [InCountry](https://incountry.com/blog/qatar-data-protection-law-guide-for-global-companies), [PwC](https://www.pwc.com/m1/en/media-centre/articles/enhancing-the-digital-ecosystem-and-boosting-investment.html)
- AI in SME accounting: [beancount.io](https://beancount.io/blog/2026/04/01/how-ai-is-transforming-small-business-accounting), [ChatFin](https://chatfin.ai/blog/ai-in-accounting-and-finance-2026-how-ai-is-reshaping-financial-operations)
