"""Demo customers, one per kind of business, each with a few weeks of realistic work — for sales visits and training.

    python scripts/seed-business-demos.py                    # all businesses, API on http://localhost:5080
    python scripts/seed-business-demos.py --only salon grocery
    python scripts/seed-business-demos.py --api http://localhost:5080

Each business is created through the back-office (as a customer with a plan), then filled in as its owner would:
staff and payroll with WPS, company licences, the business's own work (tailoring orders, salon appointments, till shifts,
purchases, invoices, projects…). Re-running skips businesses that already exist.

Sign in at http://localhost:4200 with company code = the business code, email owner@<code>.qa, password Demo-Password-2026.
The back-office (all customers, plans, subscription payments) is at http://localhost:4200/backoffice: admin@erp.qa, same password.
Standard library only.
"""
import argparse
import datetime as dt
import json
import sys
import urllib.error
import urllib.request
import uuid

PASSWORD = "Demo-Password-2026"
PLATFORM_KEY = "dev-platform-key"
ADMIN = "admin@erp.qa"

p = argparse.ArgumentParser()
p.add_argument("--api", default="http://localhost:5080")
p.add_argument("--only", nargs="*", help="business codes to seed (default: all)")
args = p.parse_args()
API = args.api.rstrip("/")
TODAY = dt.date.today()
token = None


def d(days=0):
    return (TODAY + dt.timedelta(days=days)).isoformat()


MONTH_START = TODAY.replace(day=1)
LAST_MONTH = (MONTH_START - dt.timedelta(days=1)).replace(day=1)


class ApiError(Exception):
    def __init__(self, code, detail):
        super().__init__(f"{code} {detail}")
        self.status = code
        self.detail = detail


def call(method, path, body=None, auth=True, headers=None, bearer=None, raw=None, content_type="application/json; charset=utf-8"):
    data = raw if raw is not None else (json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None)
    req = urllib.request.Request(API + path, data=data, method=method)
    req.add_header("Content-Type", content_type)
    if auth:
        req.add_header("Authorization", f"Bearer {bearer or token}")
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    try:
        with urllib.request.urlopen(req) as r:
            payload = r.read()
            return json.loads(payload) if payload and r.headers.get_content_type() == "application/json" else None
    except urllib.error.HTTPError as e:
        raise ApiError(e.code, e.read().decode("utf-8", "replace"))


def get(path): return call("GET", path)
def post(path, body=None): return call("POST", path, body if body is not None else {})
def put(path, body): return call("PUT", path, body)


def step(title): print(f"   • {title}")


def iban(bank, account):
    bban = bank + account.rjust(21, "0")
    digits = "".join(str(int(c, 36)) for c in bban + "QA00")
    return f"QA{98 - int(digits) % 97:02d}{bban}"


# ---------------------------------------------------------------- back-office
def backoffice():
    try:
        call("POST", "/api/platform/admins", {"email": ADMIN, "displayName": "ERP owner", "password": PASSWORD}, auth=False,
             headers={"X-Platform-Key": PLATFORM_KEY})
        print(f"• Back-office administrator {ADMIN} created")
    except ApiError as e:
        if e.status != 409:
            raise
    return call("POST", "/api/backoffice/auth/login", {"email": ADMIN, "password": PASSWORD}, auth=False)["accessToken"]


def create_customer(bo, b):
    """Creates the business as a customer; returns False if it already exists."""
    existing = call("GET", f"/api/backoffice/customers?search={b['code']}", bearer=bo)
    if any(c["code"] == b["code"] for c in existing):
        return None
    detail = call("POST", "/api/backoffice/customers", {
        "code": b["code"], "nameEn": b["en"], "nameAr": b["ar"], "crNo": b["cr"], "adminEmail": f"owner@{b['code']}.qa", "adminPassword": PASSWORD,
        "adminNameEn": b["owner_en"], "adminNameAr": b["owner_ar"], "planCode": b["plan"], "billingCycle": b.get("cycle", "Monthly"),
        "trialDays": b.get("trial", 0), "businessType": b["type"], "contactName": b["owner_en"], "contactPhone": b["phone"], "area": b["area"],
        "preferredLanguage": "ar"}, bearer=bo)
    cid = detail["customer"]["id"]
    if b.get("extra_modules"):
        plan = next(p for p in call("GET", "/api/backoffice/plans", bearer=bo) if p["code"] == b["plan"])
        s = detail["subscription"]
        call("PUT", f"/api/backoffice/customers/{cid}/subscription", {
            "planId": plan["id"], "modules": plan["modules"] + b["extra_modules"], "billingCycle": s["billingCycle"], "price": s["price"] + 50,
            "autoSuspend": True, "graceDays": 14}, bearer=bo)
        detail = call("GET", f"/api/backoffice/customers/{cid}", bearer=bo)
    billing = b.get("billing", "paid")
    if billing == "paid" and detail["customer"]["balance"] > 0:
        call("POST", f"/api/backoffice/customers/{cid}/payments", {"amount": detail["customer"]["balance"], "paidOn": d(), "method": "BankTransfer",
                                                                   "reference": f"TRX-{b['code'].upper()[:6]}"}, bearer=bo)
    if billing == "overdue":
        call("POST", f"/api/backoffice/customers/{cid}/payments", {"amount": detail["customer"]["balance"], "paidOn": d(), "method": "Cash"}, bearer=bo)
        call("POST", f"/api/backoffice/customers/{cid}/invoices", {"description": "Setup, data import and training / الإعداد والتدريب", "amount": 500,
                                                                   "issueDate": d(-20), "dueDate": d(-10)}, bearer=bo)
    return cid


def login(code):
    global token
    token = call("POST", "/api/auth/login", {"tenantCode": code, "email": f"owner@{code}.qa", "password": PASSWORD}, auth=False)["accessToken"]


# ---------------------------------------------------------------- shared building blocks
def accounts():
    return {a["code"]: a["id"] for a in get("/api/finance/accounts")}


def defaults():
    group = next(g["id"] for g in get("/api/inventory/item-groups") if g["code"] == "GEN")
    pcs = next(u["id"] for u in get("/api/inventory/uoms") if u["code"] == "PCS")
    main = next(w["id"] for w in get("/api/inventory/warehouses") if w["code"] == "MAIN")
    return group, pcs, main


def items(rows, group, pcs):
    """rows: (sku, en, ar, type, buy, sell, reorder, barcode[, track_batches])"""
    out = {}
    for r in rows:
        sku, en, ar, typ, buy, sell, reorder, barcode = r[:8]
        out[sku] = post("/api/inventory/items", {"sku": sku, "nameEn": en, "nameAr": ar, "itemType": typ, "itemGroupId": group, "uomId": pcs,
                                                 "purchasePrice": buy, "salesPrice": sell, "reorderLevel": reorder, "barcode": barcode,
                                                 "trackBatches": len(r) > 8 and r[8]})["id"]
    return out


def opening_stock(main, it, rows, date=None):
    lines = []
    for r in rows:
        sku, qty, cost = r[:3]
        line = {"itemId": it[sku], "quantity": qty, "unitCost": cost}
        if len(r) > 3:
            line.update({"batchNo": r[3], "expiryDate": r[4]})
        lines.append(line)
    post("/api/inventory/adjustments", {"warehouseId": main, "date": date or d(-30), "reason": "Opening", "notes": "Opening stock count", "lines": lines})


def company_docs(docs):
    for t, en, ar, no, issue, exp in docs:
        post("/api/hr/company-documents", {"docType": t, "nameEn": en, "nameAr": ar, "docNo": no, "issueDate": issue, "expiryDate": exp})


def staff(employees, acc, pay_last_month=True):
    """employees: (empNo, en, ar, nat, gender, job_en, job_ar, joined, basic, housing, transport, food, qid_expiry_days)"""
    s = get("/api/hr/settings")
    s.update({"employerEid": "1234567", "payerEid": "1234567", "payerBankShortName": "QNBA", "payerIban": iban("QNBA", "000000693123456")})
    put("/api/hr/settings", s)
    ids = {}
    for i, (no, en, ar, nat, gender, job_en, job_ar, joined, basic, housing, transport, food, qid_days) in enumerate(employees):
        qid = f"2{80 + i % 15}{nat_code(nat)}{(i * 7919 + len(en) * 31) % 100000:05d}"[:11].ljust(11, "1")
        e = post("/api/hr/employees", {
            "empNo": no, "nameEn": en, "nameAr": ar, "nationality": nat, "gender": gender, "joiningDate": joined, "jobTitleEn": job_en, "jobTitleAr": job_ar,
            "mobile": f"+974 5{qid[-7:]}", "qid": qid, "bankShortName": "QNBA", "iban": iban("QNBA", qid + "01"), "inWps": True,
            "socialInsuranceScheme": "Grsia" if nat == "QA" else "None"})
        ids[no] = e["id"]
        comps = [("BASIC", basic), ("HOUSING", housing), ("TRANSPORT", transport), ("FOOD", food)]
        post(f"/api/hr/employees/{e['id']}/contracts", {
            "startDate": joined, "contractType": "Unlimited", "probationMonths": 0, "accommodationProvided": housing == 0, "foodProvided": food == 0,
            "gratuityBasis": "Basic", "annualLeaveDays": 21, "components": [{"componentCode": k, "amount": v} for k, v in comps if v > 0]})
        post(f"/api/hr/employees/{e['id']}/documents", {"docType": "Qid", "docNo": qid, "expiryDate": d(qid_days)})
    if pay_last_month:
        run = post("/api/hr/payroll-runs", {"year": LAST_MONTH.year, "month": LAST_MONTH.month})
        for action in ("calculate", "approve", "post"):
            post(f"/api/hr/payroll-runs/{run['id']}/{action}")
        post(f"/api/hr/payroll-runs/{run['id']}/wps-files")
        post(f"/api/hr/payroll-runs/{run['id']}/mark-paid", {"paymentAccountId": acc["1120"], "paymentDate": MONTH_START.isoformat()})
    run = post("/api/hr/payroll-runs", {"year": TODAY.year, "month": TODAY.month})
    post(f"/api/hr/payroll-runs/{run['id']}/calculate")
    return ids


def nat_code(nat):
    return {"QA": "63", "IN": "35", "PH": "60", "NP": "52", "BD": "50", "PK": "58", "EG": "81", "SY": "76", "LK": "44", "SD": "73"}.get(nat, "40")


def customer(code, en, ar, phone=None, limit=None, terms=30):
    return post("/api/sales/customers", {"code": code, "nameEn": en, "nameAr": ar, "country": "QA", "phone": phone, "creditLimit": limit,
                                         "paymentTermsDays": terms})["id"]


def supplier(code, en, ar, terms=30):
    return post("/api/purchasing/suppliers", {"code": code, "nameEn": en, "nameAr": ar, "country": "QA", "isResident": True,
                                              "hasPermanentEstablishment": False, "paymentTermsDays": terms, "defaultWhtCategory": "None"})["id"]


def invoice(cust, date, lines, warehouse=None, post_it=True, ref=None):
    inv = post("/api/sales/invoices", {"customerId": cust, "invoiceDate": date, "warehouseId": warehouse, "customerRef": ref, "lines": lines})
    return post(f"/api/sales/invoices/{inv['id']}/post") if post_it else inv


def receipt(cust, date, account, inv, amount=None, method="Cash", ref=None):
    post("/api/sales/receipts", {"customerId": cust, "date": date, "method": method, "depositAccountId": account, "reference": ref,
                                 "allocations": [{"salesInvoiceId": inv["id"], "amount": amount or inv["total"]}]})


def buy(sup, main, it, lines, date, pay_account=None, ref="INV"):
    """PO → submit → receive → supplier invoice posted (→ paid)."""
    po = post("/api/purchasing/orders", {"supplierId": sup, "orderDate": date, "warehouseId": main,
                                         "lines": [{"itemId": it[k], "quantity": q, "unitPrice": pr} for k, q, pr in lines]})
    po = post(f"/api/purchasing/orders/{po['id']}/submit")
    if po["status"] == "PendingApproval":
        po = post(f"/api/purchasing/orders/{po['id']}/approve", {"comment": "OK"})
    grn = post("/api/purchasing/receipts", {"purchaseOrderId": po["id"], "receiptDate": date, "supplierDeliveryNote": f"DN-{ref}",
                                            "lines": [{"purchaseOrderLineId": l["id"], "quantity": l["quantity"]} for l in po["lines"]]})
    inv = post("/api/purchasing/invoices", {"supplierId": sup, "supplierRef": ref, "invoiceDate": date, "purchaseOrderId": po["id"], "lines": [
        {"lineType": "Goods", "goodsReceiptLineId": l["id"], "quantity": l["quantity"], "unitPrice": po["lines"][i]["unitPrice"]}
        for i, l in enumerate(grn["lines"])]})
    inv = post(f"/api/purchasing/invoices/{inv['id']}/post")
    if pay_account:
        post("/api/purchasing/payments", {"supplierId": sup, "date": date, "paymentAccountId": pay_account, "reference": f"PAY-{ref}",
                                          "allocations": [{"supplierInvoiceId": inv["id"], "amount": inv["openAmount"]}]})
    return inv


def expense(acc, date, account_code, amount, memo_en, memo_ar, pay="1110"):
    post("/api/finance/journal-vouchers", {"date": date, "memoEn": memo_en, "memoAr": memo_ar, "lines": [
        {"accountId": acc[account_code], "debit": amount, "credit": 0}, {"accountId": acc[pay], "debit": 0, "credit": amount}]})


def till(acc, main, code="T1", en="Till 1", ar="الصندوق 1"):
    return post("/api/sales/pos/registers", {"code": code, "nameEn": en, "nameAr": ar, "warehouseId": main, "cashAccountId": acc["1110"],
                                            "cardAccountId": acc["1115"]})


def shift(register, sales, close=True, float_=300, short=0):
    """sales: list of ([(itemId, qty)], 'cash'|'card'|customerId)"""
    s = post("/api/sales/pos/shifts", {"registerId": register, "openingFloat": float_})
    cash_taken = 0
    for lines, pay in sales:
        body = {"clientId": str(uuid.uuid4()), "shiftId": s["id"], "lines": [{"itemId": i, "quantity": q} for i, q in lines], "cash": 0, "card": 0}
        if pay in ("cash", "card"):
            total = sum(q * price_of(i) for i, q in lines)
            body["cash" if pay == "cash" else "card"] = round(total, 2)
            if pay == "cash":
                cash_taken += round(total, 2)
        else:
            body["customerId"] = pay
        post("/api/sales/pos/sales", body)
    if close:
        post(f"/api/sales/pos/shifts/{s['id']}/close", {"countedCash": round(float_ + cash_taken - short, 2), "notes": "Short 5 riyal" if short else None})
    return s


PRICES = {}


def price_of(item_id):
    return PRICES[item_id]


def remember_prices(it, rows):
    for r in rows:
        if r[5] is not None:
            PRICES[it[r[0]]] = r[5]


# ---------------------------------------------------------------- the businesses
def typing_office():
    acc = accounts()
    group, pcs, main = defaults()
    step("Company licences and staff (typists, PRO, accountant) with payroll and WPS")
    company_docs([
        ("CommercialRegistration", "Commercial Registration", "السجل التجاري", "145522", d(-340), d(25)),
        ("CommercialLicense", "Trade licence – Al Aziziya", "الرخصة التجارية – العزيزية", "TL-55120", d(-340), d(25)),
        ("Lease", "Shop lease – Al Aziziya St", "عقد إيجار المحل – شارع العزيزية", "LS-77102", d(-300), d(65)),
        ("EstablishmentCard", "Establishment card", "بطاقة المنشأة", "2345671", d(-200), d(165)),
    ])
    staff([
        ("T001", "Abdulla Al-Marri", "عبدالله المري", "QA", "Male", "Office manager", "مدير المكتب", "2021-03-01", 9000, 3000, 1000, 0, 400),
        ("T002", "Mohammed Rafiq", "محمد رفيق", "IN", "Male", "Typist", "طابع", "2022-06-15", 2500, 800, 300, 300, 40),
        ("T003", "Shabana Begum", "شبانة بيجوم", "BD", "Female", "Typist", "طابعة", "2023-01-10", 2300, 800, 300, 300, 200),
        ("T004", "Khalid Osman", "خالد عثمان", "SD", "Male", "PRO (government relations)", "مندوب علاقات حكومية", "2020-09-01", 4000, 1500, 800, 0, 18),
    ], acc)
    step("Service price list")
    rows = [
        ("TYP", "Typing & form filling", "طباعة وتعبئة نماذج", "Service", None, 30, None, None),
        ("VISA", "Work visa application", "طلب تأشيرة عمل", "Service", None, 150, None, None),
        ("QIDR", "QID renewal service", "خدمة تجديد البطاقة الشخصية", "Service", None, 75, None, None),
        ("WP", "Work permit renewal", "تجديد إذن العمل", "Service", None, 150, None, None),
        ("TRN", "Translation (per page)", "ترجمة (للصفحة)", "Service", None, 50, None, None),
        ("ATT", "Document attestation", "تصديق مستندات", "Service", None, 100, None, None),
        ("CR", "CR renewal follow-up", "متابعة تجديد السجل التجاري", "Service", None, 300, None, None),
    ]
    it = items(rows, group, pcs)
    step("Corporate clients on account and walk-ins")
    clients = {
        "ALNOOR": customer("ALNOOR", "Al Noor Cleaning Services", "النور لخدمات التنظيف", "+974 4455 1010", 10000, 30),
        "DOHAC": customer("DOHAC", "Doha Construction WLL", "الدوحة للإنشاءات ذ.م.م", "+974 4466 2020", 20000, 30),
        "BAKERY": customer("BAKERY", "Golden Bakery", "المخبز الذهبي", "+974 5577 3030", 5000, 15),
        "WALKIN": customer("WALKIN", "Walk-in customers", "عملاء نقدي", None, 0, 0),
    }
    step("A month of invoices: paid by transfer, card and cash; some still open, one overdue")
    i = invoice(clients["ALNOOR"], d(-40), [{"itemId": it["VISA"], "quantity": 6}, {"itemId": it["TYP"], "quantity": 6}])
    receipt(clients["ALNOOR"], d(-25), acc["1120"], i, method="BankTransfer", ref="QNB-88120")
    invoice(clients["DOHAC"], d(-45), [{"itemId": it["WP"], "quantity": 12}, {"itemId": it["QIDR"], "quantity": 8}], ref="DC-PO-118")
    i = invoice(clients["DOHAC"], d(-10), [{"itemId": it["CR"], "quantity": 1}, {"itemId": it["ATT"], "quantity": 4}])
    receipt(clients["DOHAC"], d(-3), acc["1120"], i, amount=400, method="BankTransfer")
    invoice(clients["BAKERY"], d(-5), [{"itemId": it["QIDR"], "quantity": 3}, {"itemId": it["TYP"], "quantity": 3}])
    for day, lines in [(-6, [("TYP", 2), ("TRN", 3)]), (-4, [("QIDR", 1)]), (-2, [("ATT", 2), ("TRN", 1)]), (-1, [("TYP", 4)]), (0, [("VISA", 1), ("TYP", 1)])]:
        i = invoice(clients["WALKIN"], d(day), [{"itemId": it[s], "quantity": q} for s, q in lines])
        receipt(clients["WALKIN"], d(day), acc["1110"], i)
    step("Expenses: rent, government fees paid for clients, utilities")
    expense(acc, d(-30), "5310", 9000, "Shop rent – month", "إيجار المحل للشهر", pay="1120")
    expense(acc, d(-12), "5330", 2350, "Government fees paid on behalf of clients (MOI, MOL)", "رسوم حكومية مدفوعة عن العملاء")
    expense(acc, d(-8), "5320", 640, "Kahramaa electricity and water", "كهرماء – كهرباء وماء")
    step("CRM: new leads and an opportunity; weekly receivables report by email")
    post("/api/sales/crm/leads", {"companyName": "Qatar Tiles Trading", "contactName": "Mr. Hassan", "phone": "+97455012345", "source": "WalkIn", "status": "New"})
    post("/api/sales/crm/leads", {"companyName": "Fresh Laundry", "contactName": "Sunil", "phone": "+97433012345", "source": "Referral", "status": "Contacted"})
    post("/api/sales/crm/opportunities", {"title": "Yearly PRO contract – 40 workers", "customerId": clients["DOHAC"], "stage": "Proposal", "expectedValue": 18000})
    post("/api/core/report-schedules", {"reportKey": "sal.aging", "frequency": "Weekly", "weekday": 0, "recipients": ["owner@typing-office.qa"]})


def abaya_tailor():
    acc = accounts()
    group, pcs, main = defaults()
    step("Licences and staff: tailors, cutter, sales")
    company_docs([
        ("CommercialRegistration", "Commercial Registration", "السجل التجاري", "152201", d(-200), d(165)),
        ("CommercialLicense", "Trade licence – Al Aziziya St", "الرخصة التجارية – شارع العزيزية", "TL-61201", d(-350), d(15)),
        ("Lease", "Shop lease", "عقد إيجار المحل", "LS-81201", d(-340), d(20)),
        ("CivilDefence", "Civil Defence certificate", "شهادة الدفاع المدني", "CD-61220", d(-330), d(35)),
    ])
    staff([
        ("A001", "Rahim Uddin", "رحيم الدين", "BD", "Male", "Head tailor", "رئيس الخياطين", "2019-04-01", 3200, 0, 300, 0, 120),
        ("A002", "Anwar Hossain", "أنور حسين", "BD", "Male", "Tailor", "خياط", "2021-08-15", 2200, 0, 0, 0, 60),
        ("A003", "Saleem Akhtar", "سليم أختر", "PK", "Male", "Cutter", "قصاص", "2022-02-01", 2600, 0, 0, 0, 22),
        ("A004", "Hiba Abdelrahman", "هبة عبدالرحمن", "SD", "Female", "Sales & measurements", "مبيعات وقياس", "2023-05-01", 3500, 1200, 500, 300, 300),
    ], acc)
    step("Fabric (stock, by the metre), ready-made abayas and stitching prices")
    rows = [
        ("CREPE", "Black crepe (m)", "كريب أسود (متر)", "Stock", 14, 30, 40, "6290001000011"),
        ("NIDA", "Nida fabric (m)", "قماش ندى (متر)", "Stock", 18, 38, 40, "6290001000028"),
        ("CHIFFON", "Chiffon (m)", "شيفون (متر)", "Stock", 10, 22, 30, "6290001000035"),
        ("ABY-RM-S", "Ready abaya – classic", "عباية جاهزة – كلاسيك", "Stock", 120, 260, 5, "6290001000042"),
        ("ABY-RM-E", "Ready abaya – embroidered", "عباية جاهزة – مطرزة", "Stock", 210, 450, 3, "6290001000059"),
        ("SHL", "Shayla (scarf)", "شيلة", "Stock", 15, 45, 10, "6290001000066"),
        ("STITCH", "Abaya stitching", "خياطة عباية", "Service", None, 250, None, None),
        ("ALTER", "Alteration", "تعديل", "Service", None, 40, None, None),
    ]
    it = items(rows, group, pcs)
    remember_prices(it, rows)
    opening_stock(main, it, [("CREPE", 220, 14), ("NIDA", 140, 18), ("CHIFFON", 90, 10), ("ABY-RM-S", 18, 120), ("ABY-RM-E", 6, 210), ("SHL", 40, 15)])
    step("Made-to-measure orders in every stage (one late, two ready to collect, one delivered)")
    cash = acc["1110"]
    orders = [
        ("Mariam Al-Kuwari", "55511201", -14, -2, "Abaya", "A001", "Ready", [("Abaya, stones on cuffs", 2, 250)], 200),
        ("Noora Al-Thani", "55511202", -10, 2, "Abaya", "A002", "Fitting", [("Abaya, wide sleeves", 1, 280)], 100),
        ("Fatima Hassan", "55511203", -8, 4, "Dress", "A001", "InProgress", [("Evening dress", 1, 450)], 200),
        ("Hessa Al-Mannai", "55511204", -20, -3, "Abaya", "A002", "InProgress", [("Abaya with embroidery", 1, 350)], 150),
        ("Aisha Saeed", "55511205", -3, 9, "Jalabiya", "A001", "Received", [("Ramadan jalabiya", 3, 180)], 300),
        ("Latifa Ahmed", "55511206", -16, -1, "Abaya", "A001", "Ready", [("Classic abaya", 1, 250)], 250),
        ("Sara Mohammed", "55511207", -25, -12, "Abaya", "A002", "Delivered", [("Abaya, pearl buttons", 1, 300)], 100),
    ]
    for name, phone, ordered, due, garment, tailor, status, lines, deposit in orders:
        o = post("/api/tailoring/orders", {
            "customerName": name, "phone": phone, "orderDate": d(ordered), "dueDate": d(due), "fittingDate": d(due - 3), "garment": garment,
            "measurements": {"length": "142", "shoulder": "39", "chest": "92", "sleeveLength": "58", "sleeveWidth": "30"},
            "styleNotes": lines[0][0], "tailorName": {"A001": "Rahim", "A002": "Anwar"}[tailor],
            "lines": [{"description": t, "quantity": q, "unitPrice": pr} for t, q, pr in lines] + [{"itemId": it["CREPE"], "quantity": 3, "unitPrice": 30}],
            "deposit": {"amount": deposit, "accountId": cash, "method": "Cash"}})
        path = {"Received": [], "InProgress": ["InProgress"], "Fitting": ["InProgress", "Fitting"], "Ready": ["InProgress", "Fitting", "Ready"],
                "Delivered": ["InProgress", "Fitting", "Ready"]}[status]
        for st in path:
            o = post(f"/api/tailoring/orders/{o['id']}/status", {"status": st})
        if status == "Delivered":
            post(f"/api/tailoring/orders/{o['id']}/deliver", {"date": d(due), "warehouseId": main,
                                                               "finalPayment": {"amount": o["balance"], "accountId": cash, "method": "Cash"}})
    step("Shop till: ready-made abayas and shaylas, three closed shifts")
    reg = till(acc, main)
    shift(reg["id"] if isinstance(reg, dict) else reg, [([(it["ABY-RM-S"], 1)], "card"), ([(it["SHL"], 2)], "cash"), ([(it["ABY-RM-E"], 1)], "card")])
    shift(reg["id"] if isinstance(reg, dict) else reg, [([(it["ABY-RM-S"], 2), (it["SHL"], 1)], "cash")], short=5)
    shift(reg["id"] if isinstance(reg, dict) else reg, [([(it["SHL"], 3)], "cash")], close=False)
    supplier_id = supplier("FABRIC", "Al Rayyan Fabrics Trading", "الريان لتجارة الأقمشة")
    buy(supplier_id, main, it, [("CREPE", 100, 13.5), ("NIDA", 60, 17.5)], d(-6), acc["1120"], ref="ARF-2210")


def salon():
    acc = accounts()
    group, pcs, main = defaults()
    step("Licences and staff with payroll")
    company_docs([
        ("CommercialRegistration", "Commercial Registration", "السجل التجاري", "160012", d(-100), d(265)),
        ("MunicipalityLicense", "Municipality health licence", "الرخصة الصحية للبلدية", "MH-22011", d(-340), d(28)),
        ("Lease", "Salon lease", "عقد إيجار الصالون", "LS-90112", d(-180), d(185)),
    ])
    staff([
        ("S001", "Grace Fernandes", "جريس فرنانديز", "IN", "Female", "Senior stylist", "مصففة أولى", "2020-01-01", 3500, 1000, 300, 0, 200),
        ("S002", "Maricel Santos", "ماريسيل سانتوس", "PH", "Female", "Stylist", "مصففة", "2022-03-01", 2600, 800, 300, 0, 45),
        ("S003", "Sujata Thapa", "سوجاتا ثابا", "NP", "Female", "Nail technician", "فنية أظافر", "2023-07-01", 2200, 800, 300, 0, 300),
        ("S004", "Rana Khalil", "رنا خليل", "SY", "Female", "Receptionist", "موظفة استقبال", "2024-02-01", 3000, 1000, 300, 300, 150),
    ], acc)
    step("Service menu")
    rows = [
        ("CUT", "Haircut & style", "قص وتسريح", "Service", None, 80, None, None),
        ("BLOW", "Blow-dry", "سشوار", "Service", None, 60, None, None),
        ("COLOR", "Hair colour", "صبغة شعر", "Service", None, 250, None, None),
        ("HIGH", "Highlights", "هايلايت", "Service", None, 350, None, None),
        ("MANI", "Manicure", "مناكير", "Service", None, 50, None, None),
        ("PEDI", "Pedicure", "باديكير", "Service", None, 70, None, None),
        ("THREAD", "Threading", "خيط", "Service", None, 20, None, None),
        ("HENNA", "Henna (hands)", "حناء (اليدين)", "Service", None, 100, None, None),
        ("BRIDE", "Bridal make-up", "مكياج عروس", "Service", None, 900, None, None),
    ]
    it = items(rows, group, pcs)
    step("Salon team with commission rates")
    st = {}
    for name, pct, color in [("Grace", 12, "#f8bbd0"), ("Maricel", 10, "#d1c4e9"), ("Sujata", 8, "#c8e6c9")]:
        st[name] = post("/api/salon/staff", {"name": name, "commissionPercent": pct, "color": color})
    step("Last week's visits checked out, today's and tomorrow's appointments")
    cash = acc["1110"]
    visits = [(-6, "Noora", "33110011", [("COLOR", "Grace"), ("BLOW", "Maricel")]), (-5, None, None, [("CUT", "Maricel")]),
              (-4, "Moza", "33110012", [("MANI", "Sujata"), ("PEDI", "Sujata")]), (-3, "Shaikha", "33110013", [("HIGH", "Grace")]),
              (-2, None, None, [("THREAD", "Sujata"), ("CUT", "Grace")]), (-1, "Al Jazi", "33110014", [("BRIDE", "Grace"), ("HENNA", "Sujata")])]
    for day, name, phone, lines in visits:
        post("/api/salon/checkout", {"customerName": name, "phone": phone, "date": d(day), "accountId": cash, "method": "Cash",
                                     "lines": [{"itemId": it[s], "staffId": st[w]} for s, w in lines]})
    for hour, minute, name, phone, svc, who, minutes in [(10, 0, "Hind", "33110021", "CUT", "Maricel", 45), (11, 0, "Noora", "33110011", "BLOW", "Maricel", 30),
                                                          (12, 30, "Dana", "33110022", "COLOR", "Grace", 120), (16, 0, "Reem", "33110023", "MANI", "Sujata", 45),
                                                          (17, 0, "Lulwa", "33110024", "PEDI", "Sujata", 60), (18, 0, "Maha", "33110025", "HIGH", "Grace", 150)]:
        post("/api/salon/appointments", {"customerName": name, "phone": phone, "staffId": st[who], "itemId": it[svc],
                                         "startsAt": f"{d()}T{hour:02d}:{minute:02d}:00+03:00", "minutes": minutes})
    for hour, name, svc, who in [(10, "Amal", "BRIDE", "Grace"), (15, "Wadha", "HENNA", "Sujata")]:
        post("/api/salon/appointments", {"customerName": name, "phone": "33110030", "staffId": st[who], "itemId": it[svc],
                                         "startsAt": f"{d(1)}T{hour:02d}:00:00+03:00", "minutes": 120})
    expense(acc, d(-9), "5360", 1850, "Salon products (shampoo, colour tubes)", "منتجات الصالون")


def grocery():
    acc = accounts()
    group, pcs, main = defaults()
    step("Licences and staff")
    company_docs([
        ("CommercialRegistration", "Commercial Registration", "السجل التجاري", "170331", d(-60), d(305)),
        ("MunicipalityLicense", "Food handling licence", "رخصة تداول المواد الغذائية", "FH-11203", d(-355), d(10)),
        ("Lease", "Shop lease", "عقد إيجار المحل", "LS-71230", d(-200), d(165)),
    ])
    staff([
        ("G001", "Shafeeq Rahman", "شفيق رحمن", "IN", "Male", "Shop manager", "مدير المحل", "2018-05-01", 3500, 1000, 300, 0, 75),
        ("G002", "Nizar Kunju", "نزار كونجو", "IN", "Male", "Cashier", "كاشير", "2021-11-01", 2200, 0, 0, 0, 33),
        ("G003", "Ram Bahadur", "رام بهادور", "NP", "Male", "Delivery driver", "سائق توصيل", "2022-09-01", 2000, 0, 300, 0, 160),
    ], acc)
    step("Items with barcodes (dairy tracked by batch and expiry) and opening stock")
    rows = [
        ("RICE5", "Basmati rice 5 kg", "أرز بسمتي 5 كغ", "Stock", 32, 45, 15, "6281001000014"),
        ("SUGAR2", "Sugar 2 kg", "سكر 2 كغ", "Stock", 6, 8.5, 20, "6281001000021"),
        ("OIL18", "Sunflower oil 1.8 L", "زيت دوار الشمس 1.8 لتر", "Stock", 9.5, 13, 12, "6281001000038"),
        ("WATER", "Water 1.5 L × 6", "ماء 1.5 لتر × 6", "Stock", 6, 9, 30, "6281001000045"),
        ("EGGS30", "Eggs (tray of 30)", "بيض (طبق 30)", "Stock", 14, 19, 10, "6281001000052"),
        ("BREAD", "Arabic bread", "خبز عربي", "Stock", 1.2, 2, 20, "6281001000069"),
        ("KARAK", "Karak tea mix 20 sachets", "شاي كرك 20 كيس", "Stock", 7, 11, 10, "6281001000076"),
        ("MILK2", "Fresh milk 2 L", "حليب طازج 2 لتر", "Stock", 7.5, 10.5, 15, "6281001000083", True),
        ("LABAN", "Laban 1 L", "لبن 1 لتر", "Stock", 3.5, 5, 15, "6281001000090", True),
    ]
    it = items(rows, group, pcs)
    remember_prices(it, rows)
    opening_stock(main, it, [("RICE5", 40, 32), ("SUGAR2", 60, 6), ("OIL18", 30, 9.5), ("WATER", 80, 6), ("EGGS30", 12, 14), ("BREAD", 60, 1.2),
                             ("KARAK", 25, 7)])
    opening_stock(main, it, [("MILK2", 24, 7.5, "M-2610A", d(3)), ("MILK2", 30, 7.5, "M-2618B", d(12)), ("LABAN", 30, 3.5, "L-2609", d(2))], d(-5))
    step("Suppliers: purchase orders received, invoiced and paid")
    s1 = supplier("FOODCO", "Gulf Foodstuff Distributors", "الخليج لتوزيع المواد الغذائية")
    s2 = supplier("DAIRY", "Doha Dairy Farms", "مزارع الدوحة للألبان", 15)
    buy(s1, main, it, [("RICE5", 30, 31.5), ("SUGAR2", 40, 5.9), ("WATER", 60, 5.8)], d(-12), acc["1120"], ref="GFD-7712")
    buy(s1, main, it, [("OIL18", 20, 9.4), ("KARAK", 20, 6.8)], d(-4), ref="GFD-7790")
    step("Regular customers buying on account (daftar)")
    villa = customer("V12", "Abu Ali – Villa 12", "أبو علي – فيلا 12", "55667788", 1500, 30)
    flat = customer("B7F3", "Umm Khalid – Building 7", "أم خالد – عمارة 7", "55667799", 800, 30)
    step("Till: four days of shifts (cash, card, on account), one with a 5 riyal shortage, today's shift open")
    reg = till(acc, main)
    rid = reg["id"] if isinstance(reg, dict) else reg
    shift(rid, [([(it["RICE5"], 1), (it["OIL18"], 1)], "cash"), ([(it["WATER"], 3)], "card"), ([(it["BREAD"], 5), (it["EGGS30"], 1)], villa)])
    shift(rid, [([(it["SUGAR2"], 2), (it["KARAK"], 1)], "cash"), ([(it["RICE5"], 2)], flat), ([(it["MILK2"], 2)], "card")], short=5)
    shift(rid, [([(it["WATER"], 2), (it["BREAD"], 10)], "cash"), ([(it["LABAN"], 3)], "cash")])
    shift(rid, [([(it["EGGS30"], 1), (it["MILK2"], 1)], "cash"), ([(it["OIL18"], 2)], villa)], close=False)


def restaurant():
    acc = accounts()
    group, pcs, main = defaults()
    step("Licences and kitchen and service staff, with rosters")
    company_docs([
        ("CommercialRegistration", "Commercial Registration", "السجل التجاري", "181220", d(-120), d(245)),
        ("MunicipalityLicense", "Food establishment licence", "رخصة منشأة غذائية", "FE-33012", d(-330), d(35)),
        ("CivilDefence", "Civil Defence (kitchen) certificate", "شهادة الدفاع المدني (المطبخ)", "CD-77120", d(-360), d(5)),
    ])
    emp = staff([
        ("R001", "Faisal Kareem", "فيصل كريم", "IN", "Male", "Head chef", "رئيس الطهاة", "2019-02-01", 4500, 0, 0, 0, 90),
        ("R002", "Biju Thomas", "بيجو توماس", "IN", "Male", "Cook", "طباخ", "2021-06-01", 2200, 0, 0, 0, 50),
        ("R003", "Abdul Kader", "عبدالقادر", "BD", "Male", "Kitchen helper", "مساعد مطبخ", "2023-01-01", 1500, 0, 0, 0, 25),
        ("R004", "Ronaldo Cruz", "رونالدو كروز", "PH", "Male", "Waiter", "نادل", "2022-04-01", 1800, 0, 0, 0, 140),
        ("R005", "Asif Iqbal", "آصف إقبال", "PK", "Male", "Cashier", "كاشير", "2022-10-01", 2200, 0, 0, 0, 190),
    ], acc)
    morning = post("/api/hr/shifts", {"code": "AM", "nameEn": "Morning", "nameAr": "صباحي", "startTime": "07:00", "endTime": "15:00", "graceMinutes": 10})
    evening = post("/api/hr/shifts", {"code": "PM", "nameEn": "Evening", "nameAr": "مسائي", "startTime": "15:00", "endTime": "23:00", "graceMinutes": 10})
    sunday = TODAY - dt.timedelta(days=(TODAY.weekday() + 1) % 7)
    post("/api/hr/rosters/assign", {"employeeIds": [emp["R001"], emp["R003"], emp["R005"]], "from": sunday.isoformat(),
                                    "to": (sunday + dt.timedelta(days=6)).isoformat(), "shiftId": morning["id"], "restDays": ["Friday"]})
    post("/api/hr/rosters/assign", {"employeeIds": [emp["R002"], emp["R004"]], "from": sunday.isoformat(), "to": (sunday + dt.timedelta(days=6)).isoformat(),
                                    "shiftId": evening["id"], "restDays": ["Thursday"]})
    step("Menu (service items) and ingredients (stock)")
    rows = [
        ("KARAK-T", "Karak tea", "شاي كرك", "Service", None, 2, None, "1001"),
        ("SHAW", "Chicken shawarma", "شاورما دجاج", "Service", None, 8, None, "1002"),
        ("BIRY", "Chicken biryani", "برياني دجاج", "Service", None, 22, None, "1003"),
        ("MANDI", "Lamb mandi", "مندي لحم", "Service", None, 45, None, "1004"),
        ("JUICE", "Fresh orange juice", "عصير برتقال طازج", "Service", None, 12, None, "1005"),
        ("FALAF", "Falafel sandwich", "ساندويتش فلافل", "Service", None, 5, None, "1006"),
        ("CHK", "Chicken (kg)", "دجاج (كغ)", "Stock", 14, None, 20, None),
        ("RICE40", "Basmati rice 40 kg", "أرز بسمتي 40 كغ", "Stock", 190, None, 2, None),
        ("LAMB", "Lamb (kg)", "لحم غنم (كغ)", "Stock", 38, None, 10, None),
    ]
    it = items(rows, group, pcs)
    remember_prices(it, rows)
    opening_stock(main, it, [("CHK", 60, 14), ("RICE40", 4, 190), ("LAMB", 25, 38)])
    s = supplier("MEAT", "Al Meera Meat Wholesale", "الميرة لتجارة اللحوم بالجملة", 7)
    buy(s, main, it, [("CHK", 80, 13.8), ("LAMB", 30, 37.5)], d(-3), acc["1120"], ref="AMW-5510")
    post("/api/inventory/adjustments", {"warehouseId": main, "date": d(-1), "reason": "Other", "notes": "Kitchen usage this week",
                                        "lines": [{"itemId": it["CHK"], "quantity": -70, "unitCost": None}, {"itemId": it["LAMB"], "quantity": -20, "unitCost": None},
                                                  {"itemId": it["RICE40"], "quantity": -2, "unitCost": None}]})
    step("Till shifts: lunch and dinner")
    reg = till(acc, main)
    rid = reg["id"] if isinstance(reg, dict) else reg
    shift(rid, [([(it["BIRY"], 2), (it["KARAK-T"], 2)], "cash"), ([(it["MANDI"], 1), (it["JUICE"], 2)], "card"),
                ([(it["SHAW"], 4), (it["KARAK-T"], 4)], "cash"), ([(it["FALAF"], 3)], "cash")])
    shift(rid, [([(it["MANDI"], 2), (it["BIRY"], 1)], "card"), ([(it["SHAW"], 6), (it["JUICE"], 2)], "cash")], short=5)
    shift(rid, [([(it["KARAK-T"], 6), (it["FALAF"], 2)], "cash")], close=False)
    expense(acc, d(-15), "5310", 15000, "Restaurant rent", "إيجار المطعم", pay="1120")
    expense(acc, d(-7), "5320", 2100, "Gas cylinders and electricity", "اسطوانات الغاز والكهرباء")


def furniture():
    acc = accounts()
    group, pcs, main = defaults()
    step("Licences and staff")
    company_docs([
        ("CommercialRegistration", "Commercial Registration", "السجل التجاري", "190044", d(-30), d(335)),
        ("CommercialLicense", "Trade licence – Al Najma St", "الرخصة التجارية – شارع النجمة", "TL-90442", d(-345), d(20)),
        ("Lease", "Showroom lease", "عقد إيجار المعرض", "LS-99012", d(-100), d(265)),
    ])
    staff([
        ("F001", "Yousef Al-Hajri", "يوسف الهاجري", "QA", "Male", "Showroom manager", "مدير المعرض", "2018-01-01", 12000, 4000, 1500, 0, 500),
        ("F002", "Prakash Nair", "براكاش ناير", "IN", "Male", "Salesman", "بائع", "2021-03-01", 3000, 1000, 500, 300, 80),
        ("F003", "Sajid Ali", "ساجد علي", "PK", "Male", "Delivery & installation", "توصيل وتركيب", "2022-05-01", 2000, 0, 0, 0, 12),
    ], acc)
    rows = [
        ("SOFA3", "3-seater sofa – grey", "كنبة 3 مقاعد – رمادي", "Stock", 1300, 2400, 2, "6290002000011"),
        ("BED-K", "King bed with mattress", "سرير كينج مع مرتبة", "Stock", 1600, 2900, 2, "6290002000028"),
        ("DIN6", "Dining set – 6 chairs", "طاولة طعام 6 كراسي", "Stock", 1800, 3200, 1, "6290002000035"),
        ("WARD", "Wardrobe 3 doors", "خزانة ملابس 3 أبواب", "Stock", 900, 1650, 2, "6290002000042"),
        ("MAJLIS", "Majlis set", "طقم مجلس", "Stock", 4200, 7500, 1, "6290002000059"),
        ("DELIV", "Delivery", "توصيل", "Service", None, 100, None, None),
        ("INST", "Installation", "تركيب", "Service", None, 150, None, None),
    ]
    it = items(rows, group, pcs)
    opening_stock(main, it, [("SOFA3", 6, 1300), ("BED-K", 4, 1600), ("DIN6", 3, 1800), ("WARD", 5, 900), ("MAJLIS", 2, 4200)])
    s = supplier("IMPORT", "Gulf Furniture Importers", "الخليج لاستيراد الأثاث", 45)
    buy(s, main, it, [("SOFA3", 4, 1280), ("MAJLIS", 2, 4150)], d(-9), ref="GFI-3301")
    step("Customers: quotation sent, order delivered and invoiced with a deposit, cash sales")
    villa = customer("ALSADA", "Al Sada family – Villa 33", "عائلة السادة – فيلا 33", "55990011", 20000, 30)
    hotel = customer("HOTEL", "Pearl Boutique Hotel", "فندق اللؤلؤة", "44990022", 50000, 45)
    walk = customer("WALKIN", "Walk-in customers", "عملاء نقدي", None, 0, 0)
    q = post("/api/sales/quotations", {"customerId": hotel, "quoteDate": d(-5), "notes": "12 rooms refurbishment", "lines": [
        {"itemId": it["BED-K"], "quantity": 12, "discountPercent": 10}, {"itemId": it["WARD"], "quantity": 12, "discountPercent": 10},
        {"itemId": it["INST"], "quantity": 12}]})
    post(f"/api/sales/quotations/{q['id']}/send")
    so = post("/api/sales/orders", {"customerId": villa, "orderDate": d(-8), "warehouseId": main, "lines": [
        {"itemId": it["MAJLIS"], "quantity": 1}, {"itemId": it["DIN6"], "quantity": 1}, {"itemId": it["DELIV"], "quantity": 1}]})
    so = post(f"/api/sales/orders/{so['id']}/confirm")
    post("/api/sales/receipts", {"customerId": villa, "date": d(-8), "method": "Card", "depositAccountId": acc["1120"], "reference": "Deposit",
                                 "allocations": [], "onAccount": 3000})
    stock_lines = [l for l in so["lines"] if l["itemType"] == "Stock"]
    dn = post("/api/sales/deliveries", {"salesOrderId": so["id"], "deliveryDate": d(-2), "vehicleNo": "TRK-221",
                                        "lines": [{"salesOrderLineId": l["id"], "quantity": l["quantity"]} for l in stock_lines]})
    inv = post(f"/api/sales/deliveries/{dn['id']}/invoice", {"invoiceDate": d(-2)})
    post(f"/api/sales/invoices/{inv['id']}/post")
    i = invoice(walk, d(-1), [{"itemId": it["SOFA3"], "quantity": 1}, {"itemId": it["DELIV"], "quantity": 1}], warehouse=main)
    receipt(walk, d(-1), acc["1110"], i)
    post("/api/sales/crm/leads", {"companyName": "Lusail Serviced Apartments", "contactName": "Ms. Dana", "phone": "+97444001122", "source": "Website", "status": "Qualified"})


def workshop():
    acc = accounts()
    group, pcs, main = defaults()
    step("Licences and mechanics")
    company_docs([
        ("CommercialRegistration", "Commercial Registration", "السجل التجاري", "200551", d(-90), d(275)),
        ("CivilDefence", "Civil Defence certificate", "شهادة الدفاع المدني", "CD-90021", d(-350), d(12)),
        ("Lease", "Workshop lease – Industrial Area", "عقد إيجار الورشة – المنطقة الصناعية", "LS-55010", d(-300), d(65)),
    ])
    staff([
        ("W001", "Senthil Kumar", "سينثيل كومار", "IN", "Male", "Chief mechanic", "رئيس الميكانيكيين", "2017-08-01", 4000, 0, 0, 0, 210),
        ("W002", "Ramesh Gurung", "راميش جورونج", "NP", "Male", "Mechanic", "ميكانيكي", "2020-02-01", 2500, 0, 0, 0, 30),
        ("W003", "Imran Shah", "عمران شاه", "PK", "Male", "Auto electrician", "كهربائي سيارات", "2021-09-01", 2800, 0, 0, 0, 95),
    ], acc)
    rows = [
        ("OIL5", "Engine oil 5W-30 (4 L)", "زيت محرك 5W-30 (4 لتر)", "Stock", 55, 95, 10, "6290003000011"),
        ("OILF", "Oil filter", "فلتر زيت", "Stock", 12, 30, 15, "6290003000028"),
        ("BRAKE", "Brake pads (front set)", "فحمات فرامل أمامية", "Stock", 70, 160, 6, "6290003000035"),
        ("BATT", "Battery 70Ah", "بطارية 70 أمبير", "Stock", 230, 380, 4, "6290003000042"),
        ("L-OIL", "Oil change labour", "أجرة تغيير زيت", "Service", None, 40, None, None),
        ("L-BRAKE", "Brake job labour", "أجرة صيانة فرامل", "Service", None, 120, None, None),
        ("L-AC", "AC service and gas", "صيانة المكيف والغاز", "Service", None, 200, None, None),
        ("L-DIAG", "Computer diagnostics", "فحص بالكمبيوتر", "Service", None, 100, None, None),
    ]
    it = items(rows, group, pcs)
    opening_stock(main, it, [("OIL5", 30, 55), ("OILF", 40, 12), ("BRAKE", 10, 70), ("BATT", 6, 230)])
    s = supplier("PARTS", "Qatar Auto Parts Co.", "شركة قطر لقطع غيار السيارات", 30)
    buy(s, main, it, [("OIL5", 20, 54), ("BRAKE", 8, 68)], d(-10), acc["1120"], ref="QAP-8812")
    step("Fleet customer on account and walk-in jobs (parts + labour)")
    fleet = customer("FLEET", "Doha Limousine Fleet (12 cars)", "أسطول ليموزين الدوحة (12 سيارة)", "44112233", 15000, 30)
    walk = customer("WALKIN", "Walk-in customers", "عملاء نقدي", None, 0, 0)
    for day, plate, lines in [(-9, "123456", [("OIL5", 1), ("OILF", 1), ("L-OIL", 1)]), (-6, "223344", [("BRAKE", 1), ("L-BRAKE", 1)]),
                              (-2, "556677", [("OIL5", 1), ("OILF", 1), ("L-OIL", 1), ("L-AC", 1)])]:
        invoice(fleet, d(day), [{"itemId": it[k], "quantity": q} for k, q in lines], warehouse=main, ref=f"Plate {plate}")
    for day, lines, method in [(-7, [("BATT", 1)], "Card"), (-3, [("L-DIAG", 1)], "Cash"), (0, [("OIL5", 1), ("OILF", 1), ("L-OIL", 1)], "Cash")]:
        i = invoice(walk, d(day), [{"itemId": it[k], "quantity": q} for k, q in lines], warehouse=main)
        receipt(walk, d(day), acc["1120" if method == "Card" else "1110"], i, method=method)


def contracting():
    acc = accounts()
    group, pcs, main = defaults()
    step("Licences, site staff and an excavator")
    company_docs([
        ("CommercialRegistration", "Commercial Registration", "السجل التجاري", "210771", d(-150), d(215)),
        ("ChamberCertificate", "Chamber of Commerce membership", "عضوية غرفة التجارة", "QCC-7712", d(-330), d(35)),
        ("Insurance", "Contractor's all-risk insurance", "تأمين جميع أخطار المقاولين", "CAR-2201", d(-200), d(165)),
    ])
    staff([
        ("C001", "Hamad Al-Naimi", "حمد النعيمي", "QA", "Male", "Projects director", "مدير المشاريع", "2016-01-01", 20000, 6000, 2000, 0, 600),
        ("C002", "Vijay Menon", "فيجاي مينون", "IN", "Male", "Site engineer", "مهندس موقع", "2019-04-01", 7000, 2500, 1000, 0, 100),
        ("C003", "Dil Bahadur", "دل بهادور", "NP", "Male", "Mason", "بنّاء", "2021-01-01", 1800, 0, 0, 0, 35),
        ("C004", "Karim Mia", "كريم ميا", "BD", "Male", "Labourer", "عامل", "2022-07-01", 1300, 0, 0, 0, 14),
    ], acc)
    cat = post("/api/finance/assets/categories", {"code": "EQP", "nameEn": "Plant & equipment", "nameAr": "الآلات والمعدات", "usefulLifeMonths": 60,
                                                  "assetAccountId": acc["1210"], "accumulatedAccountId": acc["1220"], "expenseAccountId": acc["5350"]})
    post("/api/finance/assets", {"nameEn": "Excavator CAT 320", "nameAr": "حفارة كاتربيلر 320", "categoryId": cat["id"],
                                 "acquisitionDate": LAST_MONTH.isoformat(), "cost": 180000, "salvageValue": 30000})
    step("Villa project: bill of quantities, two progress certificates (retention held)")
    owner = customer("ALMANA", "Mr. Saleh Al-Mana", "السيد صالح المانع", "55001122", 500000, 30)
    project = post("/api/sales/projects", {"code": "P-VILLA7", "nameEn": "Villa 7 – Al Gharrafa", "nameAr": "فيلا 7 – الغرافة", "customerId": owner,
                                           "retentionPercent": 10, "budgetCost": 600000, "status": "Active", "items": [
        {"section": "Structure", "descriptionEn": "Excavation and foundations", "descriptionAr": "الحفر والأساسات", "unit": "lot", "quantity": 1, "rate": 120000},
        {"section": "Structure", "descriptionEn": "Concrete frame", "descriptionAr": "الهيكل الخرساني", "unit": "m3", "quantity": 400, "rate": 650},
        {"section": "Finishes", "descriptionEn": "Block work", "descriptionAr": "أعمال البلوك", "unit": "m2", "quantity": 1200, "rate": 85}]})
    items_ = {i["descriptionEn"]: i["id"] for i in project["items"]}
    c1 = post(f"/api/sales/projects/{project['id']}/certificates", {"periodEnd": d(-30), "progress": [
        {"boqItemId": items_["Excavation and foundations"], "quantityToDate": 1}, {"boqItemId": items_["Concrete frame"], "quantityToDate": 120}]})
    post(f"/api/sales/projects/{project['id']}/certificates/{c1['id']}/invoice", {"date": d(-30)})
    c2 = post(f"/api/sales/projects/{project['id']}/certificates", {"periodEnd": d(-1), "progress": [
        {"boqItemId": items_["Concrete frame"], "quantityToDate": 260}, {"boqItemId": items_["Block work"], "quantityToDate": 300}]})
    post(f"/api/sales/projects/{project['id']}/certificates/{c2['id']}/invoice", {"date": d(-1)})
    step("Subcontract for block work with its first certificate")
    sub = supplier("BLOCKCO", "Al Khor Block Works", "الخور لأعمال البلوك")
    sc = post("/api/purchasing/subcontracts", {"code": "SC-BLOCK", "nameEn": "Block work – Villa 7", "nameAr": "أعمال البلوك – فيلا 7", "supplierId": sub,
                                               "projectId": project["id"], "retentionPercent": 10, "status": "Active", "items": [
        {"descriptionEn": "Block work 20 cm", "descriptionAr": "بلوك 20 سم", "unit": "m2", "quantity": 1200, "rate": 55}]})
    cert = post(f"/api/purchasing/subcontracts/{sc['id']}/certificates", {"periodEnd": d(-1), "progress": [{"itemId": sc["items"][0]["id"], "quantityToDate": 300}]})
    post(f"/api/purchasing/subcontracts/{sc['id']}/certificates/{cert['id']}/invoice", {"date": d(-1)})


BUSINESSES = [
    {"code": "typing-office", "type": "typing_office", "en": "Al Aziziya Typing & PRO Services", "ar": "العزيزية للطباعة وتخليص المعاملات",
     "owner_en": "Abdulla Al-Marri", "owner_ar": "عبدالله المري", "phone": "55001001", "area": "Al Aziziya", "cr": "145522", "plan": "BUSINESS",
     "seed": typing_office},
    {"code": "abaya-tailor", "type": "tailoring", "en": "Lamsat Abayas & Tailoring", "ar": "لمسات للعبايات والخياطة",
     "owner_en": "Umm Ahmed", "owner_ar": "أم أحمد", "phone": "55001002", "area": "Al Aziziya", "cr": "152201", "plan": "SHOP",
     "extra_modules": ["purchasing"], "seed": abaya_tailor},
    {"code": "salon", "type": "salon", "en": "Rose Ladies Salon", "ar": "صالون روز للسيدات",
     "owner_en": "Mrs. Hind", "owner_ar": "السيدة هند", "phone": "55001003", "area": "Al Aziziya", "cr": "160012", "plan": "SHOP", "trial": 14,
     "billing": "trial", "seed": salon},
    {"code": "grocery", "type": "grocery", "en": "Al Baraka Mini Mart", "ar": "ميني ماركت البركة",
     "owner_en": "Mohammed Saleh", "owner_ar": "محمد صالح", "phone": "55001004", "area": "Al Aziziya", "cr": "170331", "plan": "BUSINESS",
     "billing": "overdue", "seed": grocery},
    {"code": "restaurant", "type": "restaurant", "en": "Spice Garden Cafeteria", "ar": "كافتيريا حديقة البهارات",
     "owner_en": "Ali Hassan", "owner_ar": "علي حسن", "phone": "55001005", "area": "Al Aziziya", "cr": "181220", "plan": "BUSINESS",
     "cycle": "Annual", "seed": restaurant},
    {"code": "furniture", "type": "furniture", "en": "Home Style Furniture", "ar": "هوم ستايل للأثاث",
     "owner_en": "Yousef Al-Hajri", "owner_ar": "يوسف الهاجري", "phone": "55001006", "area": "Al Aziziya", "cr": "190044", "plan": "BUSINESS",
     "seed": furniture},
    {"code": "workshop", "type": "workshop", "en": "Fast Fix Auto Garage", "ar": "كراج فاست فكس للسيارات",
     "owner_en": "Senthil Kumar", "owner_ar": "سينثيل كومار", "phone": "55001007", "area": "Industrial Area", "cr": "200551", "plan": "SHOP",
     "extra_modules": ["purchasing"], "seed": workshop},
    {"code": "contracting", "type": "contracting", "en": "Gulf Build Contracting", "ar": "الخليج للمقاولات",
     "owner_en": "Hamad Al-Naimi", "owner_ar": "حمد النعيمي", "phone": "55001008", "area": "Al Gharrafa", "cr": "210771", "plan": "ENTERPRISE",
     "cycle": "Annual", "seed": contracting},
]


def main():
    bo = backoffice()
    chosen = [b for b in BUSINESSES if not args.only or b["code"] in args.only]
    for b in chosen:
        print(f"\n▶ {b['en']} ({b['code']})")
        cid = create_customer(bo, b)
        if cid is None:
            print("   already exists – skipped")
            continue
        login(b["code"])
        put("/api/approvals/settings", {"allowSelfApproval": True})   # owner-run shop: the owner approves their own orders
        try:
            b["seed"]()
        except ApiError as e:
            sys.exit(f"\n✗ {b['code']}: {e.status}\n{e.detail}")
    print("\nDone. Sign in at http://localhost:4200 (company code, owner@<code>.qa, " + PASSWORD + ")")
    print("Back-office: http://localhost:4200/backoffice (" + ADMIN + ")")
    for b in chosen:
        print(f"   {b['code']:<15} owner@{b['code']}.qa   {b['en']}")


main()
