# Qatar SME ERP — engineering rules

Multi-tenant SaaS ERP for Qatari SMEs. **Current product focus: Payroll & HR first** (WPS, gratuity, documents),
on top of an accounting-grade finance core. Full design: [docs/ERP-Blueprint-Qatar-SME.md](docs/ERP-Blueprint-Qatar-SME.md).

## Stack
.NET 10 / ASP.NET Core minimal APIs · EF Core 10 + Npgsql · PostgreSQL 17 · xUnit + Shouldly · (next) Angular frontend.
No MediatR / AutoMapper / MassTransit (commercial licences). Prefer MIT/Apache dependencies.

## Layout
```
src/BuildingBlocks/Erp.SharedKernel   domain base types, errors, cross-module abstractions (no dependencies)
src/BuildingBlocks/Erp.Infrastructure DbContext, tenancy, audit, migrator, numbering, outbox, encryption, web plumbing
src/Modules/<Name>/Erp.Modules.<Name>            module implementation (internal), its SQL migrations, endpoints
src/Modules/<Name>/Erp.Modules.<Name>.Contracts  the ONLY thing other modules may reference
src/Erp.Api                            host; ModuleCatalog lists the modules
tests/Erp.UnitTests                    pure logic + architecture rules
tests/Erp.IntegrationTests             real PostgreSQL; each test provisions its own tenant
```

## Non-negotiable rules
1. **Only `IPostingService` writes the ledger.** Modules send business facts with account *roles*; posting rules map roles → accounts. Modules never pick GL accounts or compute tax/cost themselves.
2. **Atomic posting.** Document status, journal entry and outbox messages commit in one transaction (`db.InTransactionAsync`). The posting service refuses to run outside a transaction.
3. **Ledger is insert-only.** `JournalEntry`/`JournalLine` are `IImmutable`; DB triggers + grants enforce it. Corrections = reversal entries (`ReverseAsync`) or new documents.
4. **Tenant isolation, two layers.** Tenant comes from the JWT only (`ITenantContext`). EF global filters + PostgreSQL RLS (`app.tenant_id` set on every connection). Raw SQL must still filter `tenant_id` explicitly.
5. **Money is `decimal` / `numeric(19,4)`**, rounded with `Money.Round` (half away from zero). Quantities/rates `numeric(19,6)`. Never float/double.
6. **Bilingual by default.** Master data has `NameEn` + `NameAr` (use `Bilingual.Require`). Errors are `BusinessRuleException(code, messageEn, messageAr)`.
7. **Configurable, not hardcoded.** Legal rates (gratuity days, overtime multipliers, minimum wage, deduction cap, WPS deadline) live in settings tables.
8. **Gapless numbering** via `INumberingService` inside the posting transaction; number only after validation passes.
9. **Sensitive data** (QID, passport, IBAN): encrypted columns (`EncryptedStringConverter`), `[Sensitive]` (masked in audit), blind index for search, masked in API without `hr.sensitive:view`.
10. **Stock moves only through `IStockService`** (Inventory.Contracts): it locks the item valuation row, applies weighted-average cost and writes the insert-only `inv.stock_movement`; callers post the returned values with `IPostingService` in the same transaction. Reversals mirror movements exactly, so stock valuation always equals the Inventory account.
11. **Foreign currency**: documents keep amounts in their currency with an `ExchangeRate` to QAR and `*Base` QAR amounts; the ledger is posted in QAR, settlements clear the remaining base exactly and differences go to `AccountRoles.ExchangeDifference`.

## Database conventions (checked by `PlatformTests.Every_tenant_table_follows_the_schema_conventions`)
- Hand-written SQL migrations: `Migrations/V####__name.sql` (once, global version order) and `R__name.sql` (re-run: grants). **Never edit an applied V-script** — add a new one. Version ranges: infra 0001–0009, core 0010–0099, finance 0100–0199, HR 0200–0299, inventory 0300–0399, purchasing 0400–0499, sales 0500–0599.
- Every tenant table: `id uuid PK`, `tenant_id`, `created_by`, `created_at`, `updated_by`, `updated_at`, `UNIQUE (tenant_id, id)`, then `SELECT core.enable_tenant_rls('schema','table');`
- Foreign keys are composite `(tenant_id, x_id)` and **`DEFERRABLE INITIALLY DEFERRED`** (EF doesn't know cross-module insert order).
- Business keys unique per tenant: `UNIQUE (tenant_id, company_id, code)`.
- Enums are stored as snake_case text (`SnakeCaseEnumConverter`) with matching `CHECK` constraints.
- Raw SQL result types: column aliases must be **snake_case** (naming convention applies to them).
- Optimistic concurrency: `uint Version` mapped with `.HasXminConcurrency()`.
- Grants in each module's `R__grants_<module>.sql`; ledgers/audit get no UPDATE/DELETE for `erp_app`.

## C# conventions
- Entities: `TenantEntity` (audit columns stamped by `AuditingInterceptor`); `init` for identity fields.
- Inside LINQ query syntax, don't name variables `from`/`to` (contextual keywords) — use `periodStart`/`periodEnd`.
- Endpoints: `RequirePermission("module.resource:action")`; permissions declared in the module's `IPermissionProvider` with default roles.
- New tenant defaults go in the module's `ITenantProvisioner` (Finance = 10, HR = 20).

## Frontend (src/Erp.Web — Angular 22, zoneless, standalone, Material 3)
- **Arabic-first.** Every user-visible string goes through `I18nService` (`'key' | t`). Add keys to `en.ts`; `ar.ts` is typed against it so a missing Arabic key fails the build.
- Bilingual records: `{{ record | bi }}` / `| bi:'jobTitle'`; money: `| money`; dates: `| d` (dd/MM/yyyy); API dates are `yyyy-MM-dd` strings (use `core/dates.ts`, never `Date.toISOString()`).
- Layout uses logical CSS only (`margin-inline-start`, `text-align: end`); numbers/codes/IBANs get class `ltr`; directional icons get `flip-rtl`.
- State in signals; data via `ApiService` (typed in `core/models.ts`); errors via `NotifyService` (shows `message_ar` in Arabic).
- Permissions: hide actions with `auth.can('perm')`; routes use `permissionGuard`. The server still enforces everything.
- Downloads (PDF/SIF/CSV) go through `api.download(...)` which keeps the server file name.

## Running
```
docker compose up -d                      # PostgreSQL 17 on localhost:5433
dotnet run --project src/Erp.Api          # http://localhost:5080/scalar (migrates on start in Development)
cd src/Erp.Web && npx ng serve            # http://localhost:4200, proxies /api
dotnet test                               # integration tests use ERP_TEST_POSTGRES or localhost:5433, else Testcontainers
```

## Regulatory caution
Qatar rules marked ⚠️ VERIFY in the blueprint (WPS SIF layout, Decision 50/2026 deadlines, overtime rates, WHT/excise, e-invoicing)
must be confirmed with a Qatari payroll/tax adviser before being shipped as defaults. The SIF layout lives only in `Wps/SifGenerator.cs`.
