# Qatar SME ERP

Cloud ERP for Qatari SMEs, built **Payroll & HR first**: WPS salary files, monthly end-of-service gratuity
accrual, QID/visa/company-document expiry tracking, on an accounting-grade, bilingual (Arabic/English),
multi-tenant core.

Design document: [docs/ERP-Blueprint-Qatar-SME.md](docs/ERP-Blueprint-Qatar-SME.md) · Engineering rules: [CLAUDE.md](CLAUDE.md)

## What works today

| Area | Capabilities |
|---|---|
| Platform | Tenant provisioning in one call, company code + email login with optional two-factor (authenticator app) and rotating refresh tokens, roles & granular permissions, field masking, audit trail, gapless numbering, transactional outbox, row-level security |
| Finance | Bilingual IFRS-style chart of accounts (with payroll accounts), fiscal years/periods with close/reopen, central posting service, journal vouchers with reversal, trial balance, account ledger, Excel/Tally-friendly journal CSV export |
| HR | Employees (QID/passport/IBAN encrypted, IBAN mod-97 + QID validation), departments, contracts with minimum-wage/allowance checks, employee & company documents with expiry list and daily reminders, leave requests/approval/annual balance |
| Final settlement | Gratuity payout (eligibility after 1 year, provision released), unused-leave encashment or excess-leave recovery, salary since the last payroll, notice pay, air ticket, automatic recovery of outstanding advances; post / pay / reverse |
| Documents | Bilingual PDF payslips and final settlement statements with amount in words (English & Arabic) |
| Onboarding | Excel import of employees with contracts and document expiries (bilingual template, validate first, all-or-nothing) |
| Notifications | Bilingual emails (SMTP) for document expiry, posted payroll and missed WPS deadlines |
| Advances | Salary advances/loans to employees, outstanding balance, recovery through payroll or settlement |
| Web app | Angular 22, Arabic-first (RTL) with one-click switch to English, Material 3 design, works on phones; dashboard, employees (profile, contract, documents, leave, advances, final settlement), payroll runs with WPS/PDF downloads, overtime inputs, leave approvals, document expiry, gratuity report, chart of accounts, trial balance, journal, settings, users |
| Payroll | Monthly runs: calculate → approve → post → WPS SIF file → mark paid (or reverse); proration on a 30-day basis, unpaid leave, sick leave in full/half/unpaid bands (art. 82), GRSIA social insurance for Qatari staff (employee 7% / employer 14%, configurable), overtime by type, bonuses/deductions with 50% cap, salary journal by component and cost centre, gratuity accrual with monthly true-up (GRSIA nationals excluded), gratuity liability report |

## Quick start

Prerequisites: .NET SDK 10, Docker Desktop.

```bash
docker compose up -d                     # PostgreSQL 17 on localhost:5433
dotnet run --project src/Erp.Api         # migrates the database, listens on http://localhost:5080
```

Then start the web app (Node 22):

```bash
cd src/Erp.Web
npm install
npx ng serve            # http://localhost:4200 (proxies /api to :5080)
```

Open **http://localhost:5080/scalar** for the interactive API, or run the requests in [scripts/demo.http](scripts/demo.http)
(VS Code REST Client / Visual Studio / Rider) to provision a company, hire employees and run a full payroll month.

## Deployment

See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md): API container (`Dockerfile`, `--migrate-only` release step) and PostgreSQL in
Azure Qatar Central, web app on Cloudflare Pages.

### Deploying the web app (e.g. Cloudflare Pages)

`npx ng build` produces static files in `src/Erp.Web/dist/erp-web/browser` (build command `npm ci && npx ng build`,
root `src/Erp.Web`). Point the app at the API by editing `config.json` in the output (`{ "apiBaseUrl": "https://api.example.qa" }`)
and add the site's origin to the API setting `Cors:AllowedOrigins`. `_redirects` already routes deep links to `index.html`.

## Tests

```bash
dotnet test                                   # backend
cd src/Erp.Web && npx ng test --watch=false   # frontend
```

- **Unit tests** — payroll, gratuity, IBAN/QID, SIF layout, architecture rules.
- **Integration tests** — run against real PostgreSQL. They use `ERP_TEST_POSTGRES` (an owner connection string; each run creates and drops
  its own database) or `localhost:5433`, falling back to Testcontainers. They cover tenant isolation (API and RLS), ledger immutability,
  balance-at-commit, closed periods, concurrent gapless numbering, full payroll cycle, reversal, masking and background jobs.

## Configuration

| Setting | Purpose |
|---|---|
| `Database:OwnerConnectionString` | Migrations only (DDL) |
| `Database:AppConnectionString` | Runtime role `erp_app` (no table ownership, no BYPASSRLS) |
| `Database:WorkerConnectionString` | Outbox dispatcher role `erp_worker` |
| `Jwt:SigningKey` | ≥ 32 bytes |
| `Security:FieldEncryptionKey`, `Security:BlindIndexKey` | base64 32-byte keys for QID/IBAN encryption and search |
| `Platform:ApiKey` | Protects `POST /api/platform/tenants` |

Development values are in `src/Erp.Api/appsettings.Development.json`. **Never reuse them in production**; use Key Vault / user secrets.
