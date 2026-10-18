"""Seed a demo company with realistic sample data in every module (HR, payroll, finance, inventory, purchasing).

Scenario: "Demo Trading WLL", a Doha building-materials trader with a head office and an Industrial Area yard.
Records are left in different states (draft / pending / posted / paid / reversed) so every screen and action has an example.

    python scripts/seed-demo.py                     # API on http://localhost:5080, company "demo"
    python scripts/seed-demo.py --company acme --api http://localhost:5080
    python scripts/seed-demo.py --sales-only        # add the sales scenario to an already seeded company

The company is provisioned if it does not exist yet. Run it once per company: business keys (employee numbers,
SKUs, supplier codes) are unique, so a second run stops at the first duplicate.
Standard library only.
"""
import argparse
import json
import sys
import urllib.error
import urllib.request

PASSWORD = "Demo-Password-2026"
PLATFORM_KEY = "dev-platform-key"

p = argparse.ArgumentParser()
p.add_argument("--api", default="http://localhost:5080")
p.add_argument("--company", default="demo")
p.add_argument("--sales-only", action="store_true", help="only add customers, quotations, orders, invoices and receipts")
args = p.parse_args()
API = args.api.rstrip("/")
token = None


def call(method, path, body=None, auth=True, headers=None, ok_statuses=(200, 201, 204)):
    data = json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None
    req = urllib.request.Request(API + path, data=data, method=method)
    req.add_header("Content-Type", "application/json; charset=utf-8")
    if auth:
        req.add_header("Authorization", f"Bearer {token}")
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    try:
        with urllib.request.urlopen(req) as r:
            raw = r.read()
            return json.loads(raw) if raw and r.headers.get_content_type() == "application/json" else None
    except urllib.error.HTTPError as e:
        if e.code in ok_statuses:
            return None
        detail = e.read().decode("utf-8", "replace")
        sys.exit(f"\n✗ {method} {path} -> {e.code}\n{detail}\nrequest: {json.dumps(body, ensure_ascii=False)}")


def get(path): return call("GET", path)
def post(path, body=None): return call("POST", path, body if body is not None else {})
def put(path, body): return call("PUT", path, body)


def step(title): print(f"• {title}")


def iban(bank, account):
    """A valid Qatari IBAN (mod-97 check digits) for bank code + up to 21-char account."""
    bban = bank + account.rjust(21, "0")
    digits = "".join(str(int(c, 36)) for c in bban + "QA00")
    return f"QA{98 - int(digits) % 97:02d}{bban}"


# ---------------------------------------------------------------- company + login
def login():
    global token
    r = call("POST", "/api/auth/login", {"tenantCode": args.company, "email": "owner@demo.qa", "password": PASSWORD},
             auth=False, ok_statuses=())
    token = r["accessToken"]


try:
    login()
    step(f"Company '{args.company}' exists, logged in")
except SystemExit:
    step(f"Provisioning company '{args.company}'")
    call("POST", "/api/platform/tenants", {
        "code": args.company, "nameEn": "Demo Trading WLL", "nameAr": "شركة العرض للتجارة ذ.م.م", "crNo": "123456",
        "establishmentId": "1234567", "adminEmail": "owner@demo.qa", "adminPassword": PASSWORD,
        "adminNameEn": "Demo Owner", "adminNameAr": "مالك الشركة", "preferredLanguage": "ar"},
        auth=False, headers={"X-Platform-Key": PLATFORM_KEY})
    login()


def seed_sales():
    """Customers, a price list, quotations, orders (one on credit hold), deliveries, invoices, a counter sale, a credit note
    and receipts including a post-dated cheque in hand and a bounced one."""
    accounts = {a["code"]: a["id"] for a in get("/api/finance/accounts")}
    bank, cash = accounts["1120"], accounts["1110"]
    wh = {w["code"]: w["id"] for w in get("/api/inventory/warehouses")}
    items = {i["sku"]: i for i in get("/api/inventory/items?pageSize=500")["items"]}
    if "SVC-DEL" not in items:
        group = next(g["id"] for g in get("/api/inventory/item-groups") if g["code"] in ("SVC", "GEN"))
        pcs = next(u["id"] for u in get("/api/inventory/uoms") if u["code"] == "PCS")
        items["SVC-DEL"] = post("/api/inventory/items", {"sku": "SVC-DEL", "nameEn": "Delivery to site", "nameAr": "توصيل إلى الموقع",
                                                         "itemType": "Service", "itemGroupId": group, "uomId": pcs, "salesPrice": 150})
    it = {k: v["id"] for k, v in items.items()}

    step("Sales: contractor price list and customers")
    contractors = post("/api/sales/price-lists", {"code": "CONTR", "nameEn": "Contractors", "nameAr": "المقاولون", "isActive": True, "items": [
        {"itemId": it["CEM-50"], "unitPrice": 20.5}, {"itemId": it["STL-16"], "unitPrice": 2800}, {"itemId": it["TIL-6060"], "unitPrice": 42}]})["id"]
    cust = {}
    for code, en, ar, limit, terms, pl, tin, cr, phone, addr_en, addr_ar in [
        ("ALWAKRA", "Al Wakra Contracting WLL", "الوكرة للمقاولات ذ.م.م", 150000, 60, contractors, "5000887766", "77120", "+974 4466 1200",
         "Industrial Area, Street 24, Doha", "المنطقة الصناعية، شارع 24، الدوحة"),
        ("LUSAIL", "Lusail Interiors", "لوسيل للديكور", 40000, 30, None, "5000331122", "81234", "+974 4455 7788",
         "Marina District, Lusail", "حي المارينا، لوسيل"),
        ("DVD", "Doha Villas Development", "الدوحة لتطوير الفلل", 20000, 45, contractors, None, "90881", "+974 5512 3344",
         "Al Thumama, Doha", "الثمامة، الدوحة"),
        ("CASH", "Walk-in customer", "عميل نقدي", 0, 0, None, None, None, None, None, None),
    ]:
        cust[code] = post("/api/sales/customers", {"code": code, "nameEn": en, "nameAr": ar, "country": "QA", "creditLimit": limit,
                                                   "paymentTermsDays": terms, "priceListId": pl, "taxCardNo": tin, "crNo": cr, "phone": phone,
                                                   "addressEn": addr_en, "addressAr": addr_ar,
                                                   "email": None if code == "CASH" else f"accounts@{code.lower()}.example"})["id"]

    step("Sales: quotations (one open, one converted to an order)")
    q = post("/api/sales/quotations", {"customerId": cust["LUSAIL"], "quoteDate": "2026-10-12", "notes": "Villa 14 fit-out, Lusail", "lines": [
        {"itemId": it["TIL-6060"], "quantity": 300, "discountPercent": 5}, {"itemId": it["PNT-18W"], "quantity": 20},
        {"itemId": it["SVC-DEL"], "quantity": 2}]})
    post(f"/api/sales/quotations/{q['id']}/send")
    q = post("/api/sales/quotations", {"customerId": cust["ALWAKRA"], "quoteDate": "2026-09-20", "validUntil": "2026-10-20", "lines": [
        {"itemId": it["CEM-50"], "quantity": 200}, {"itemId": it["STL-16"], "quantity": 5, "discountPercent": 2},
        {"itemId": it["SVC-DEL"], "quantity": 1}]})
    post(f"/api/sales/quotations/{q['id']}/send")
    so = post(f"/api/sales/quotations/{q['id']}/convert", {"warehouseId": wh["YARD"], "orderDate": "2026-09-22", "customerRef": "AWC-PO-5512"})

    step("Sales: Al Wakra order confirmed, part delivered and invoiced; paid partly by transfer and by a post-dated cheque")
    so = post(f"/api/sales/orders/{so['id']}/confirm")
    by_sku = {l["sku"]: l for l in so["lines"]}
    dn = post("/api/sales/deliveries", {"salesOrderId": so["id"], "deliveryDate": "2026-09-24", "vehicleNo": "TRK-4412", "lines": [
        {"salesOrderLineId": by_sku["CEM-50"]["id"], "quantity": 120}, {"salesOrderLineId": by_sku["STL-16"]["id"], "quantity": 5}]})
    inv = post(f"/api/sales/deliveries/{dn['id']}/invoice", {"invoiceDate": "2026-09-24"})
    inv = post(f"/api/sales/invoices/{inv['id']}/post")
    post("/api/sales/receipts", {"customerId": cust["ALWAKRA"], "date": "2026-10-05", "method": "BankTransfer", "depositAccountId": bank,
                                 "reference": "QNB-TT-77812", "allocations": [{"salesInvoiceId": inv["id"], "amount": 5000}]})
    post("/api/sales/receipts", {"customerId": cust["ALWAKRA"], "date": "2026-10-15", "method": "PostDatedCheque", "chequeNo": "001245",
                                 "chequeDate": "2026-11-15", "chequeBank": "Commercial Bank", "allocations": [{"salesInvoiceId": inv["id"], "amount": 8000}]})

    step("Sales: Doha Villas order over its credit limit (waiting for a manager)")
    so = post("/api/sales/orders", {"customerId": cust["DVD"], "orderDate": "2026-10-16", "warehouseId": wh["MAIN"], "customerRef": "DVD-0091",
                                    "lines": [{"itemId": it["TIL-6060"], "quantity": 500}, {"itemId": it["PNT-18W"], "quantity": 10}]})
    post(f"/api/sales/orders/{so['id']}/confirm")

    step("Sales: Lusail - invoiced in September (now overdue), allowance for broken tiles, bounced cheque")
    so = post("/api/sales/orders", {"customerId": cust["LUSAIL"], "orderDate": "2026-09-01", "warehouseId": wh["MAIN"],
                                    "lines": [{"itemId": it["TIL-6060"], "quantity": 100}, {"itemId": it["SVC-DEL"], "quantity": 1}]})
    so = post(f"/api/sales/orders/{so['id']}/confirm")
    tiles = next(l for l in so["lines"] if l["sku"] == "TIL-6060")
    service = next(l for l in so["lines"] if l["sku"] == "SVC-DEL")
    dn = post("/api/sales/deliveries", {"salesOrderId": so["id"], "deliveryDate": "2026-09-02",
                                        "lines": [{"salesOrderLineId": tiles["id"], "quantity": 100}]})
    inv = post("/api/sales/invoices", {"customerId": cust["LUSAIL"], "invoiceDate": "2026-09-02", "lines": [
        {"deliveryNoteLineId": dn["lines"][0]["id"], "quantity": 100}, {"salesOrderLineId": service["id"], "quantity": 1}]})
    inv = post(f"/api/sales/invoices/{inv['id']}/post")
    post("/api/sales/credit-notes", {"salesInvoiceId": inv["id"], "date": "2026-09-05", "reason": "6 boxes broken on delivery", "lines": [
        {"lineType": "Allowance", "description": "Broken tiles - price allowance", "quantity": 1, "unitPrice": 250}]})
    r = post("/api/sales/receipts", {"customerId": cust["LUSAIL"], "date": "2026-09-20", "method": "PostDatedCheque", "chequeNo": "300871",
                                     "chequeDate": "2026-10-01", "chequeBank": "Doha Bank", "allocations": [{"salesInvoiceId": inv["id"], "amount": 2000}]})
    post(f"/api/sales/receipts/{r['id']}/deposit", {"date": "2026-10-01", "bankAccountId": bank})
    post(f"/api/sales/receipts/{r['id']}/bounce", {"date": "2026-10-04", "reason": "Returned by bank - insufficient funds"})

    step("Sales: Lusail paint and drills delivered, not yet invoiced")
    so = post("/api/sales/orders", {"customerId": cust["LUSAIL"], "orderDate": "2026-10-14", "warehouseId": wh["MAIN"], "customerRef": "LI-2210",
                                    "lines": [{"itemId": it["PNT-18W"], "quantity": 8}, {"itemId": it["DRL-18V"], "quantity": 2}]})
    so = post(f"/api/sales/orders/{so['id']}/confirm")
    post("/api/sales/deliveries", {"salesOrderId": so["id"], "deliveryDate": "2026-10-15", "lines": [
        {"salesOrderLineId": l["id"], "quantity": l["quantity"]} for l in so["lines"]]})

    step("Sales: counter sale to a walk-in customer, paid in cash")
    inv = post("/api/sales/invoices", {"customerId": cust["CASH"], "invoiceDate": "2026-10-17", "warehouseId": wh["YARD"], "lines": [
        {"itemId": it["CEM-50"], "quantity": 10}, {"itemId": it["SVC-DEL"], "quantity": 1, "unitPrice": 100}]})
    inv = post(f"/api/sales/invoices/{inv['id']}/post")
    post("/api/sales/receipts", {"customerId": cust["CASH"], "date": "2026-10-17", "method": "Cash", "depositAccountId": cash,
                                 "allocations": [{"salesInvoiceId": inv["id"], "amount": inv["total"]}]})


if args.sales_only:
    seed_sales()
    print(f"\n✓ Sales sample data added to '{args.company}'.")
    sys.exit(0)

if get("/api/hr/employees")["total"]:
    sys.exit("This company already has employees - seed a fresh company instead (use --company <new-code>).")

accounts = {a["code"]: a["id"] for a in get("/api/finance/accounts")}
CASH, BANK = accounts["1110"], accounts["1120"]

# ---------------------------------------------------------------- core: company, branch, users
step("Company profile, branch, users")
company = get("/api/core/company")
put("/api/core/company", {**company, "tin": "5000123456", "addressEn": "Building 12, Street 340, Salwa Road, Doha",
                          "addressAr": "مبنى 12، شارع 340، طريق سلوى، الدوحة", "phone": "+974 4412 3456", "email": "info@demo.qa"})
ind_branch = post("/api/core/branches", {"code": "IND", "nameEn": "Industrial Area Branch", "nameAr": "فرع المنطقة الصناعية"})["id"]
for email, en, ar, role in [
    ("hr@demo.qa", "Layla Haddad", "ليلى حداد", "hr_manager"),
    ("payroll@demo.qa", "Omar Farouk", "عمر فاروق", "payroll_officer"),
    ("accounts@demo.qa", "Maria Santos", "ماريا سانتوس", "accountant"),
    ("store@demo.qa", "Ramesh Kumar", "راميش كومار", "storekeeper"),
    ("buyer@demo.qa", "Khalid Nasser", "خالد ناصر", "purchaser"),
    ("gm@demo.qa", "Mohammed Al-Kuwari", "محمد الكواري", "general_manager"),
    ("sales@demo.qa", "Ahmed Hassan", "أحمد حسن", "salesperson"),
]:
    post("/api/core/users", {"email": email, "displayNameEn": en, "displayNameAr": ar, "password": PASSWORD,
                             "preferredLanguage": "ar", "roles": [role]})

# ---------------------------------------------------------------- finance: cost centres, journal vouchers
step("Cost centres and journal vouchers")
cc = {c: post("/api/finance/cost-centers", {"code": c, "nameEn": en, "nameAr": ar})["id"] for c, en, ar in [
    ("ADM", "Administration", "الإدارة"), ("SAL", "Sales", "المبيعات"), ("OPS", "Warehouse & Logistics", "المستودعات والخدمات اللوجستية")]}


def jv(date, memo_en, memo_ar, lines):
    return post("/api/finance/journal-vouchers", {"date": date, "memoEn": memo_en, "memoAr": memo_ar, "lines": [
        {"accountId": accounts[a], "debit": d, "credit": c, "costCenterId": cc.get(k) if k else None, "description": desc}
        for a, d, c, k, desc in lines]})


jv("2026-01-01", "Share capital paid in by the partners", "رأس المال المدفوع من الشركاء", [
    ("1120", 500000, 0, None, "QNB current account"), ("3110", 0, 500000, None, None)])
jv("2026-01-01", "Opening cash float", "رصيد الصندوق الافتتاحي", [
    ("1110", 20000, 0, None, None), ("3150", 0, 20000, None, None)])
for month in ("09", "10"):
    jv(f"2026-{month}-01", f"Showroom and yard rent 2026-{month}", f"إيجار المعرض والساحة 2026-{month}", [
        ("5310", 10000, 0, "SAL", "Salwa Road showroom"), ("5310", 5000, 0, "OPS", "Industrial Area yard"),
        ("1120", 0, 15000, None, "Cheque to landlord")])
jv("2026-09-30", "Kahramaa electricity and water - September", "كهرماء كهرباء وماء - سبتمبر", [
    ("5320", 1450, 0, "SAL", None), ("5320", 900, 0, "OPS", None), ("1120", 0, 2350, None, None)])
jv("2026-09-30", "Bank charges - September", "رسوم بنكية - سبتمبر", [("5340", 85, 0, "ADM", None), ("1120", 0, 85, None, None)])
wrong = jv("2026-10-05", "Visa fees (entered twice by mistake)", "رسوم تأشيرات (أدخلت مرتين بالخطأ)", [
    ("5330", 2200, 0, "ADM", None), ("1110", 0, 2200, None, None)])
post(f"/api/finance/journal-vouchers/{wrong['id']}/reverse", {"date": "2026-10-05", "reason": "Duplicate of the MOI receipt"})
jv("2026-10-05", "Visa fees - two new employees (MOI)", "رسوم تأشيرات - موظفان جديدان (وزارة الداخلية)", [
    ("5330", 2200, 0, "ADM", "MOI receipt 778812"), ("1110", 0, 2200, None, None)])

# ---------------------------------------------------------------- HR: settings, departments, company documents
step("HR settings (WPS), departments, company documents")
s = get("/api/hr/settings")
s.update({"employerEid": "1234567", "payerEid": "1234567", "payerBankShortName": "QNBA",
          "payerIban": iban("QNBA", "000000693123456"), "sifFormatVersion": "1"})
put("/api/hr/settings", s)
dept = {c: post("/api/hr/departments", {"code": c, "nameEn": en, "nameAr": ar})["id"] for c, en, ar in [
    ("MGMT", "Management", "الإدارة العليا"), ("FIN", "Finance & HR", "المالية والموارد البشرية"),
    ("SALES", "Sales", "المبيعات"), ("WH", "Warehouse & Logistics", "المستودعات والخدمات اللوجستية")]}
for t, en, ar, no, issue, exp in [
    ("CommercialRegistration", "Commercial Registration", "السجل التجاري", "123456", "2025-03-01", "2027-02-28"),
    ("CommercialLicense", "Trade licence - Salwa Road", "الرخصة التجارية - طريق سلوى", "TL-88213", "2025-12-01", "2026-11-30"),
    ("EstablishmentCard", "Establishment (computer) card", "بطاقة المنشأة", "1234567", "2025-10-26", "2026-10-25"),
    ("Lease", "Industrial Area yard lease", "عقد إيجار ساحة المنطقة الصناعية", "LS-2025-114", "2025-02-01", "2027-01-31"),
    ("CivilDefence", "Civil Defence certificate", "شهادة الدفاع المدني", "CD-55102", "2025-09-30", "2026-09-30"),
]:
    post("/api/hr/company-documents", {"docType": t, "nameEn": en, "nameAr": ar, "docNo": no, "issueDate": issue, "expiryDate": exp})

# ---------------------------------------------------------------- HR: employees, contracts, documents
step("Employees with contracts and documents")
EMPLOYEES = [
    # empNo, en, ar, nat, gender, dob, joined, dept, cc, job en, job ar, qid, bank, social insurance,
    #   contract type, end, accommodation, food, components, air ticket, documents
    ("E001", "Mohammed Al-Kuwari", "محمد الكواري", "QA", "Male", "1980-04-12", "2022-01-01", "MGMT", "ADM",
     "General Manager", "المدير العام", "28063412345", "QIIB", "Grsia",
     "Unlimited", None, False, False, [("BASIC", 25000), ("HOUSING", 8000), ("TRANSPORT", 3000), ("FOOD", 1000)], None,
     [("Qid", "28063412345", "2028-04-11")]),
    ("E002", "Maria Santos", "ماريا سانتوس", "PH", "Female", "1990-07-22", "2023-02-15", "FIN", "ADM",
     "Accountant", "محاسبة", "29060812345", "CBQA", "None",
     "Unlimited", None, False, False, [("BASIC", 6500), ("HOUSING", 2500), ("TRANSPORT", 800), ("FOOD", 400)], 2400,
     [("Qid", "29060812345", "2027-02-14"), ("Passport", "P7654321A", "2026-11-05")]),
    ("E003", "Ramesh Kumar", "راميش كومار", "IN", "Male", "1986-01-30", "2024-03-01", "WH", "OPS",
     "Storekeeper", "أمين مستودع", "28435612345", "QNBA", "None",
     "Unlimited", None, False, True, [("BASIC", 3000), ("HOUSING", 1000), ("TRANSPORT", 500)], 1800,
     [("Qid", "28435612345", "2026-12-15"), ("HealthCard", "HC-302211", "2027-03-01")]),
    ("E004", "Ahmed Hassan", "أحمد حسن", "EG", "Male", "1993-11-05", "2025-06-01", "SALES", "SAL",
     "Sales Executive", "مندوب مبيعات", "29381812345", "DOHB", "None",
     "Limited", "2027-05-31", False, False, [("BASIC", 5000), ("HOUSING", 2000), ("TRANSPORT", 1000), ("FOOD", 300)], 2000,
     [("Qid", "29381812345", "2027-06-30"), ("WorkPermit", "WP-118822", "2027-05-31"), ("DrivingLicence", "DL-559910", "2030-06-01")]),
    ("E005", "Sanjay Thapa", "سانجاي ثابا", "NP", "Male", "1988-09-14", "2021-05-10", "WH", "OPS",
     "Heavy Driver", "سائق معدات ثقيلة", "28852412345", "QNBA", "None",
     "Unlimited", None, True, True, [("BASIC", 2200), ("TRANSPORT", 300)], 1500,
     [("Qid", "28852412345", "2027-05-09"), ("HealthCard", "HC-199231", "2026-10-01"), ("DrivingLicence", "DL-330122", "2026-11-20")]),
    ("E006", "Fatima Al-Sayed", "فاطمة السيد", "JO", "Female", "1995-03-08", "2026-08-01", "FIN", "ADM",
     "HR Officer", "مسؤولة موارد بشرية", "29540012345", "MARK", "None",
     "Limited", "2028-07-31", False, False, [("BASIC", 7000), ("HOUSING", 3000), ("TRANSPORT", 1000), ("FOOD", 500)], 2600,
     [("Qid", "29540012345", "2028-07-31"), ("Passport", "J1234567", "2031-01-10")]),
    ("E007", "Joseph D'Souza", "جوزيف دسوزا", "IN", "Male", "1984-06-02", "2020-01-15", "SALES", "SAL",
     "Senior Sales Executive", "مندوب مبيعات أول", "28435699999", "CBQA", "None",
     "Unlimited", None, False, False, [("BASIC", 6000), ("HOUSING", 2500), ("TRANSPORT", 1000), ("FOOD", 300)], 2000,
     [("Qid", "28435699999", "2027-01-14")]),
]
emp = {}
for (no, en, ar, nat, gender, dob, joined, d, c, job_en, job_ar, qid, bank, si,
     ctype, end, accom, food, comps, ticket, docs) in EMPLOYEES:
    e = post("/api/hr/employees", {
        "empNo": no, "nameEn": en, "nameAr": ar, "nationality": nat, "gender": gender, "dateOfBirth": dob, "joiningDate": joined,
        "departmentId": dept[d], "costCenterId": cc[c], "jobTitleEn": job_en, "jobTitleAr": job_ar,
        "mobile": f"+974 5{qid[-7:]}", "email": f"{en.split()[0].lower()}@demo.qa", "qid": qid,
        "passportNo": None, "bankShortName": bank, "iban": iban(bank, qid + "01"), "inWps": True, "socialInsuranceScheme": si})
    emp[no] = e["id"]
    post(f"/api/hr/employees/{e['id']}/contracts", {
        "startDate": joined, "endDate": end, "contractType": ctype, "probationMonths": 6 if joined >= "2026-01-01" else 0,
        "accommodationProvided": accom, "foodProvided": food, "gratuityBasis": "Basic", "annualLeaveDays": 30 if joined < "2021-06-01" else 21,
        "components": [{"componentCode": k, "amount": v} for k, v in comps], "annualAirTicketAmount": ticket})
    for t, docno, exp in docs:
        post(f"/api/hr/employees/{e['id']}/documents", {"docType": t, "docNo": docno, "expiryDate": exp})

# ---------------------------------------------------------------- HR: advances, leave, September payroll
step("Salary advance, September leave and overtime")
post(f"/api/hr/employees/{emp['E005']}/advances", {"date": "2026-09-03", "amount": 3000, "paymentAccountId": CASH,
                                                   "monthlyRecovery": 500, "notes": "Family emergency in Nepal"})
sick = post("/api/hr/leave-requests", {"employeeId": emp["E005"], "leaveTypeCode": "SICK", "fromDate": "2026-09-14",
                                       "toDate": "2026-09-16", "reason": "Fever - Hamad clinic certificate"})
post(f"/api/hr/leave-requests/{sick['id']}/approve")
post("/api/hr/payroll-inputs", {"employeeId": emp["E003"], "year": 2026, "month": 9, "componentCode": "OVERTIME",
                                "overtimeType": "Normal", "hours": 6, "notes": "Container unloading"})

step("September payroll: calculate → approve → post → WPS file → paid")
run = post("/api/hr/payroll-runs", {"year": 2026, "month": 9})
for action in ("calculate", "approve", "post"):
    post(f"/api/hr/payroll-runs/{run['id']}/{action}")
post(f"/api/hr/payroll-runs/{run['id']}/wps-files")
post(f"/api/hr/payroll-runs/{run['id']}/mark-paid", {"paymentAccountId": BANK, "paymentDate": "2026-10-03"})

step("Final settlement for Joseph D'Souza (resigned 30/09/2026) - posted, not yet paid")
fs = post(f"/api/hr/employees/{emp['E007']}/final-settlements", {
    "lastWorkingDay": "2026-09-30", "reason": "Resignation", "noticePay": 0, "airTicket": 2000,
    "notes": "Resignation letter dated 31/08/2026, one month notice served"})
post(f"/api/hr/final-settlements/{fs['id']}/post")

step("October leave (approved / pending / rejected) and payroll inputs")
for e, t, f, to, reason, action in [
    ("E003", "UNPAID", "2026-10-05", "2026-10-06", "Personal matter", "approve"),
    ("E002", "ANNUAL", "2026-10-20", "2026-10-29", "Annual vacation - Manila", "approve"),
    ("E006", "EMERGENCY", "2026-10-12", "2026-10-12", "Family emergency", "reject"),
    ("E004", "ANNUAL", "2026-12-20", "2026-12-31", "Year-end vacation - Cairo", None),
    ("E005", "ANNUAL", "2026-11-15", "2026-12-05", "Home leave - Kathmandu", None),
]:
    lr = post("/api/hr/leave-requests", {"employeeId": emp[e], "leaveTypeCode": t, "fromDate": f, "toDate": to, "reason": reason})
    if action:
        post(f"/api/hr/leave-requests/{lr['id']}/{action}")
for e, code, ot, hours, amount, notes in [
    ("E003", "OVERTIME", "Normal", 10, None, "Stock count preparation"),
    ("E003", "OVERTIME", "Night", 4, None, "Night delivery"),
    ("E005", "OVERTIME", "RestDay", 8, None, "Friday site delivery"),
    ("E005", "OVERTIME", "Holiday", 6, None, None),
    ("E004", "BONUS", None, None, 1500, "Q3 sales target achieved"),
    ("E002", "OTHER_DED", None, None, 200, "Mobile bill excess"),
]:
    post("/api/hr/payroll-inputs", {"employeeId": emp[e], "year": 2026, "month": 10, "componentCode": code,
                                    "overtimeType": ot, "hours": hours, "amount": amount, "notes": notes})

step("October payroll: calculated, waiting for your approval")
run = post("/api/hr/payroll-runs", {"year": 2026, "month": 10})
post(f"/api/hr/payroll-runs/{run['id']}/calculate")

# ---------------------------------------------------------------- inventory: masters, opening stock, transfers
step("Inventory: units, groups, warehouses, items")
uom = {u["code"]: u["id"] for u in get("/api/inventory/uoms")}
for c, en, ar in [("BAG", "Bag", "كيس"), ("TON", "Ton", "طن"), ("SQM", "Square metre", "متر مربع")]:
    uom[c] = post("/api/inventory/uoms", {"code": c, "nameEn": en, "nameAr": ar})["id"]
grp = {c: post("/api/inventory/item-groups", {"code": c, "nameEn": en, "nameAr": ar})["id"] for c, en, ar in [
    ("CEMENT", "Cement & Concrete", "الأسمنت والخرسانة"), ("STEEL", "Steel & Rebar", "الحديد وحديد التسليح"),
    ("TILES", "Tiles & Ceramics", "البلاط والسيراميك"), ("PAINT", "Paints", "الدهانات"),
    ("TOOLS", "Tools", "العدد والأدوات"), ("SVC", "Services", "الخدمات")]}
MAIN = next(w["id"] for w in get("/api/inventory/warehouses") if w["code"] == "MAIN")
YARD = post("/api/inventory/warehouses", {"code": "YARD", "nameEn": "Industrial Area Yard", "nameAr": "ساحة المنطقة الصناعية",
                                          "branchId": ind_branch})["id"]
item = {}
for sku, en, ar, typ, g, u, reorder, buy, sell, hs, barcode in [
    ("CEM-50", "Cement bag 50 kg", "كيس أسمنت 50 كغ", "Stock", "CEMENT", "BAG", 40, 18, 22, "252329", "6281000000017"),
    ("STL-10", "Rebar 10 mm", "حديد تسليح 10 مم", "Stock", "STEEL", "TON", 5, 2600, 2950, "721420", None),
    ("STL-16", "Rebar 16 mm", "حديد تسليح 16 مم", "Stock", "STEEL", "TON", 5, 2550, 2900, "721420", None),
    ("TIL-6060", "Porcelain tile 60x60 beige", "بلاط بورسلان 60×60 بيج", "Stock", "TILES", "SQM", 200, 32, 45, "690721", None),
    ("PNT-18W", "Emulsion paint white 18 L", "دهان أملشن أبيض 18 لتر", "Stock", "PAINT", "PCS", 20, 95, 125, "320910", "6281000000123"),
    ("DRL-18V", "Cordless drill 18 V", "مثقاب لاسلكي 18 فولت", "Stock", "TOOLS", "PCS", 3, 320, 420, "846721", "6281000000222"),
    ("SVC-CONS", "Technical consultancy", "استشارات فنية", "Service", "SVC", "HR", None, 400, None, None, None),
]:
    item[sku] = post("/api/inventory/items", {
        "sku": sku, "nameEn": en, "nameAr": ar, "itemType": typ, "itemGroupId": grp[g], "uomId": uom[u], "barcode": barcode,
        "reorderLevel": reorder, "purchasePrice": buy, "salesPrice": sell, "hsCode": hs})["id"]

step("Opening stock, transfer, damage write-off, open stock count")
post("/api/inventory/adjustments", {"warehouseId": MAIN, "date": "2026-09-01", "reason": "Opening", "notes": "Stock take 31/08/2026",
                                    "lines": [{"itemId": item[k], "quantity": q, "unitCost": c} for k, q, c in [
                                        ("CEM-50", 20, 17.5), ("STL-10", 8, 2580), ("TIL-6060", 500, 31), ("PNT-18W", 30, 92), ("DRL-18V", 10, 300)]]})
post("/api/inventory/adjustments", {"warehouseId": YARD, "date": "2026-09-01", "reason": "Opening", "notes": "Stock take 31/08/2026",
                                    "lines": [{"itemId": item[k], "quantity": q, "unitCost": c} for k, q, c in [
                                        ("CEM-50", 150, 17.8), ("STL-16", 12, 2540)]]})
post("/api/inventory/transfers", {"fromWarehouseId": MAIN, "toWarehouseId": YARD, "date": "2026-09-20", "notes": "Site order for Lusail customer",
                                  "lines": [{"itemId": item["TIL-6060"], "quantity": 100}, {"itemId": item["PNT-18W"], "quantity": 5}]})
post("/api/inventory/adjustments", {"warehouseId": MAIN, "date": "2026-10-02", "reason": "Damage", "notes": "Buckets crushed by forklift",
                                    "lines": [{"itemId": item["PNT-18W"], "quantity": -2, "unitCost": None}]})

# ---------------------------------------------------------------- purchasing
step("Suppliers")
sup = {}
for code, en, ar, country, resident, pe, terms, wht, crno in [
    ("DBM", "Doha Building Materials", "الدوحة لمواد البناء", "QA", True, False, 30, "None", "45001"),
    ("QSTEEL", "Qatar Steel Trading", "قطر لتجارة الحديد", "QA", True, False, 60, "None", "38812"),
    ("ITILE", "Ceramiche Italia SRL", "سيراميكي إيطاليا", "IT", False, False, 45, "None", None),
    ("GCL", "Gulf Clearing & Logistics", "الخليج للتخليص والخدمات اللوجستية", "QA", True, False, 30, "None", "51120"),
    ("TECHC", "Gulf Tech Consulting FZ-LLC", "الخليج للاستشارات التقنية", "AE", False, False, 30, "TechnicalFees", None),
]:
    sup[code] = post("/api/purchasing/suppliers", {
        "code": code, "nameEn": en, "nameAr": ar, "country": country, "isResident": resident, "hasPermanentEstablishment": pe,
        "crNo": crno, "email": f"accounts@{code.lower()}.example", "phone": "+974 4400 0000" if country == "QA" else None,
        "paymentTermsDays": terms, "defaultWhtCategory": wht})["id"]


def order(supplier, date, wh, lines, notes=None, expected=None):
    return post("/api/purchasing/orders", {"supplierId": sup[supplier], "orderDate": date, "expectedDate": expected, "warehouseId": wh,
                                           "notes": notes, "lines": [{"itemId": item[k], "quantity": q, "unitPrice": pr} for k, q, pr in lines]})


def receive(po, date, qtys, note):
    return post("/api/purchasing/receipts", {"purchaseOrderId": po["id"], "receiptDate": date, "supplierDeliveryNote": note,
                                             "lines": [{"purchaseOrderLineId": l["id"], "quantity": q} for l, q in zip(po["lines"], qtys)]})


def invoice(supplier, ref, date, lines, po=None):
    return post("/api/purchasing/invoices", {"supplierId": sup[supplier], "supplierRef": ref, "invoiceDate": date,
                                             "purchaseOrderId": po, "lines": lines})


step("PO 1 - cement from DBM: partly received, invoiced within tolerance, paid")
po = order("DBM", "2026-10-01", MAIN, [("CEM-50", 100, 18)], expected="2026-10-03")
post(f"/api/purchasing/orders/{po['id']}/submit")
grn = receive(po, "2026-10-03", [60], "DN-7781")
inv = invoice("DBM", "DBM-1001", "2026-10-04", [{"lineType": "Goods", "goodsReceiptLineId": grn["lines"][0]["id"], "quantity": 60, "unitPrice": 18.2}])
inv = post(f"/api/purchasing/invoices/{inv['id']}/post")
post("/api/purchasing/payments", {"supplierId": sup["DBM"], "date": "2026-10-10", "paymentAccountId": BANK, "reference": "CHQ-000123",
                                  "allocations": [{"supplierInvoiceId": inv["id"], "amount": inv["openAmount"]}]})

step("PO 2 - rebar from Qatar Steel: above approval threshold, approved, received, invoice held for price variance")
po = order("QSTEEL", "2026-10-06", YARD, [("STL-10", 10, 2600), ("STL-16", 10, 2550)], notes="For Al Wakra villa project")
post(f"/api/purchasing/orders/{po['id']}/submit")
post(f"/api/purchasing/orders/{po['id']}/approve")
grn = receive(po, "2026-10-08", [10, 10], "QS-DN-44120")
invoice("QSTEEL", "QST/26/0918", "2026-10-09", [
    {"lineType": "Goods", "goodsReceiptLineId": grn["lines"][0]["id"], "quantity": 10, "unitPrice": 2700},
    {"lineType": "Goods", "goodsReceiptLineId": grn["lines"][1]["id"], "quantity": 10, "unitPrice": 2550}])

step("PO 3 - tiles imported from Italy (non-resident): GTA notification, landed cost, part payment")
po = order("ITILE", "2026-09-15", MAIN, [("TIL-6060", 1000, 28)], notes="CIF Hamad Port", expected="2026-10-07")
post(f"/api/purchasing/orders/{po['id']}/submit")
post(f"/api/purchasing/orders/{po['id']}/approve")
put(f"/api/purchasing/orders/{po['id']}/gta-notification", {"reference": "GTA-CN-2026-004512"})
grn = receive(po, "2026-10-07", [1000], "BL MSCU7812345")
inv = invoice("ITILE", "CI-2026-0412", "2026-10-07", [{"lineType": "Goods", "goodsReceiptLineId": grn["lines"][0]["id"], "quantity": 1000, "unitPrice": 28}])
inv = post(f"/api/purchasing/invoices/{inv['id']}/post")
post("/api/purchasing/payments", {"supplierId": sup["ITILE"], "date": "2026-10-12", "paymentAccountId": BANK, "reference": "TT-QNB-99812",
                                  "allocations": [{"supplierInvoiceId": inv["id"], "amount": 15000}]})
clr = invoice("GCL", "GCL-5521", "2026-10-08", [
    {"lineType": "LandedCost", "description": "Sea freight Genoa - Hamad Port", "quantity": 1, "unitPrice": 2400},
    {"lineType": "LandedCost", "description": "Customs duty 5%", "quantity": 1, "unitPrice": 1400},
    {"lineType": "Service", "description": "Clearing agent fee", "quantity": 1, "unitPrice": 350}])
clr = post(f"/api/purchasing/invoices/{clr['id']}/post")
post("/api/purchasing/landed-costs", {"date": "2026-10-08", "method": "Value", "goodsReceiptIds": [grn["id"]], "notes": "Shipment MSCU7812345",
                                      "charges": [{"chargeType": "Freight", "description": "Sea freight", "amount": 2400, "supplierInvoiceId": clr["id"]},
                                                  {"chargeType": "CustomsDuty", "description": "Customs duty", "amount": 1400, "supplierInvoiceId": clr["id"]}]})

step("Consultancy from a non-resident (5% WHT), paid net")
inv = invoice("TECHC", "GTC-INV-0087", "2026-09-28", [
    {"lineType": "Service", "itemId": item["SVC-CONS"], "description": "Warehouse process review - September", "quantity": 30, "unitPrice": 400}])
inv = post(f"/api/purchasing/invoices/{inv['id']}/post")
post("/api/purchasing/payments", {"supplierId": sup["TECHC"], "date": "2026-10-15", "paymentAccountId": BANK, "reference": "TT-QNB-99840",
                                  "allocations": [{"supplierInvoiceId": inv["id"], "amount": inv["openAmount"]}]})

step("PO 4 - drills approved, not yet received; PO 5 - paint still a draft")
po = order("DBM", "2026-10-14", MAIN, [("DRL-18V", 5, 320)], expected="2026-10-21")
post(f"/api/purchasing/orders/{po['id']}/submit")
order("DBM", "2026-10-16", MAIN, [("PNT-18W", 50, 95)], notes="Waiting for the quotation to be confirmed")

step("Stock count in the main warehouse (open, partly counted)")
count = post("/api/inventory/counts", {"warehouseId": MAIN, "date": "2026-10-17", "notes": "Monthly cycle count"})
counted = {item["CEM-50"]: 59, item["TIL-6060"]: 1398, item["DRL-18V"]: 10}
put(f"/api/inventory/counts/{count['id']}/lines", [{"itemId": i, "countedQuantity": q} for i, q in counted.items()])

seed_sales()

print(f"\n✓ Done. Sign in at http://localhost:4200 with company '{args.company}', owner@demo.qa / {PASSWORD}")
print("  Other users (same password): hr@ payroll@ accounts@ store@ buyer@ gm@ sales@ demo.qa")
