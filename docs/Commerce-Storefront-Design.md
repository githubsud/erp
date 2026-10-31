# Commerce Module — Online Storefront & Shopper App

**Status:** Draft v0.1 · **Date:** 31 October 2026 · **Scope:** Native online store and shopper mobile app built on the ERP core

> Companion to [ERP-Blueprint-Qatar-SME.md](ERP-Blueprint-Qatar-SME.md). The blueprint plans e-commerce as **P3 connectors**
> (Shopify / WooCommerce / Salla → sales orders). This document adds a **native storefront** as the ERP's own sales channel.
> Connectors stay in the plan for tenants who already sell on those platforms; both paths end in the same `SalesOrder`.

---

## 0. Decisions at a glance

| Decision | Recommendation | One-line reason |
|---|---|---|
| Where it lives | New module `Commerce` (`Erp.Modules.Commerce` + `.Contracts`) | Same layering rules as every other module; references Contracts only |
| What it owns | Web presentation, carts, reservations, checkout, payment attempts, shopper accounts | Products, stock, prices, orders and the ledger stay in their owning modules |
| Order of record | A normal Sales `SalesOrder` with channel `online` | Delivery, invoicing, receipts and posting reuse existing code; no new accounting |
| Pricing | New `ISalesPricing` contract exposing today's `SalesLines.PriceAsync` | Web, POS and quotations always produce the same price, discount and tax |
| Stock | Soft reservation at checkout; stock moves only at delivery via `IStockService` | Prevents overselling without breaking rule 10 (valuation = Inventory account) |
| Payments | `IPaymentGateway` adapters (blueprint §4.11) + cash on delivery; inbox table for webhooks | Paid only after a verified webhook; exactly-once processing |
| Public tenancy | Store slug / custom domain → tenant, resolved by a dedicated storefront tenant context | Shoppers have no staff JWT; RLS must still apply to every query |
| Shopper identity | Separate shopper accounts, phone + SMS one-time code | Never mixed with staff `UserAccounts` / TOTP |
| Merchant UI | Inside existing `src/Erp.Web` (Angular, already a PWA via `ngsw`) | One app for staff; online orders sit next to POS and Sales |
| Shop website | New Angular app with SSR (`src/Erp.Storefront`) | Product pages must be indexable by search engines; Arabic-first RTL |
| Shopper app | Flutter (`apps/shopper`), same public API | One codebase for iOS and Android |

---

## 1. Why native, and why inside the ERP

Salla, Zid and Shopify sell a storefront; the merchant still re-keys orders, adjusts stock and reconciles gateway payouts by hand
or through a connector. Here the store *is* the ERP:

- An online order becomes a `SalesOrder`; delivery issues stock at weighted-average cost; the invoice and receipt post through
  `IPostingService`. Stock, margin and the bank are right without anyone touching them.
- One price list, one stock figure, one customer ledger across walk-in POS, credit sales and online.
- Local payments (QPay/NAPS, SkipCash, Dibsy, Fawran) and cash on delivery settle into **Payment Gateway Clearing** and clear in
  existing bank reconciliation.

The sales message for an SME: *"Your shop takes orders online, and your stock and books update themselves."*

---

## 2. Module boundaries

```
src/Modules/Commerce/Erp.Modules.Commerce            internal: stores, catalog publishing, carts, reservations,
                                                     checkout, payment attempts, shopper accounts, public + admin endpoints
src/Modules/Commerce/Erp.Modules.Commerce.Contracts  events: OnlineOrderPlaced, PaymentCaptured, OrderShipped
src/Modules/Sales/Erp.Modules.Sales.Contracts        NEW project: ISalesPricing, IOnlineOrderIntake, customer lookup
```

Commerce references `Inventory.Contracts`, `Sales.Contracts`, `Finance.Contracts` and nothing else. Add the dependency rule to
the architecture tests in `tests/Erp.UnitTests` so the boundary is enforced, not just documented.

| Concern | Owner | Commerce's role |
|---|---|---|
| Items, units, sales prices, stock on hand | Inventory | Reads via `IInventoryQueries` (`GetCatalogAsync`, `GetUnitsAsync`, `GetQuantityOnHandAsync`) |
| Price, discount, tax for a basket | Sales (new `ISalesPricing`) | Calls it for every cart change and again at checkout |
| Customer master | Sales | Links a shopper to a customer, or uses the walk-in customer for guests |
| Sales order → delivery → invoice → receipt | Sales | Creates the order through `IOnlineOrderIntake`; Sales runs the rest |
| Stock movement and cost | Inventory (`IStockService`) | Never called by Commerce directly; delivery does it |
| Journal entries | Finance (`IPostingService`) | Never called by Commerce directly; Sales posts receipts with role `PaymentGatewayClearing` |

### 2.1 New Sales contract

`SalesLines.PriceAsync` and `SalesOrderService` are `internal` today. Create `Erp.Modules.Sales.Contracts` with:

```csharp
public interface ISalesPricing
{
    Task<PricedBasket> PriceAsync(Guid companyId, Guid? customerId, IReadOnlyList<BasketLine> lines, CancellationToken ct = default);
}

public interface IOnlineOrderIntake
{
    /// Creates and confirms a SalesOrder (channel = online) inside the caller's transaction.
    Task<OnlineOrderResult> PlaceAsync(OnlineOrderRequest request, CancellationToken ct = default);

    /// Records the customer receipt for a captured payment (debits PaymentGatewayClearing or Cash for COD).
    Task RecordPaymentAsync(OnlinePaymentRequest request, CancellationToken ct = default);
}
```

`SalesOrder` gains `Channel` (`direct | pos | online`, snake_case text + `CHECK`) and an optional `ExternalRef` (the store order
number). Existing orders default to `direct`.

---

## 3. Tenancy for anonymous shoppers (highest-risk part)

Rule 4 says the tenant comes from the JWT only. Shoppers browsing a store have no staff JWT, so the storefront needs a second,
narrow way in:

1. **Store directory (not tenant-scoped):** `commerce.store_domain (host text PK, store_slug text UNIQUE, tenant_id, company_id, is_active)`.
   Readable by the app role; no other tenant data lives in it.
2. **`StorefrontTenantContext`:** a middleware on the `/api/store/{slug}/…` endpoint group resolves slug or `Host` header →
   `(tenant_id, company_id)` and calls `ITenantContext.Set(tenantId, companyId, SystemUser.Id)`. Update the XML doc on `Set` to list
   storefront resolution as an allowed caller. `TenantSessionInterceptor` then sets `app.tenant_id` as usual, so EF filters **and**
   PostgreSQL RLS still apply.
3. **Shopper tokens** carry `tenant_id`, `company_id` and `shopper_id` and a distinct audience (`erp-storefront`). Staff endpoints
   reject that audience; storefront endpoints reject staff tokens. A shopper token whose tenant differs from the resolved store is a 403.
4. **Public endpoints expose a whitelist only:** published items, prices, availability *bands* (in stock / low / out), store
   profile. Never cost, supplier, exact quantity or other customers.

**Release gate (add to blueprint §7.4):** integration tests that provision two tenants, then prove that store A's slug, cookies
and shopper token can never read store B's catalog, carts, orders or shoppers — including by guessing ids.

---

## 4. Data model (`commerce` schema)

All tenant tables follow the repo conventions: `id uuid`, `tenant_id`, audit columns, `UNIQUE (tenant_id, id)`, composite
`DEFERRABLE INITIALLY DEFERRED` FKs, `core.enable_tenant_rls(...)`. Migration range **0700–0799**; grants in `R__grants_commerce.sql`.

| Table | Purpose | Key columns |
|---|---|---|
| `store` | One online store per company | `company_id`, `slug`, `name_en`, `name_ar`, `currency` (QAR), `cod_enabled`, `min_order`, `delivery_fee`, `status` |
| `store_domain` | Host/slug → tenant (not tenant-scoped, see §3) | `host`, `store_slug`, `tenant_id`, `company_id` |
| `listing` | Web layer over an Inventory item | `item_id`, `slug`, `title_en/ar`, `description_en/ar`, `is_published`, `sort_order` |
| `listing_image` | Photos (object storage keys) | `listing_id`, `storage_key`, `sort_order` |
| `category` | Web navigation; maps to item groups | `item_group_id` (nullable), `name_en/ar`, `parent_id` |
| `shopper` | Shopper account | `phone` (unique per tenant), `name`, `customer_id` (nullable link to Sales) |
| `shopper_address` | Delivery addresses | Qatar address: zone, street, building (blue plate), unit, notes, `lat/lng` |
| `cart` / `cart_line` | Server-side basket | `shopper_id` or `anonymous_token`, `item_id`, `uom_id`, `quantity numeric(19,6)` |
| `reservation` | Soft stock hold | `item_id`, `warehouse_id`, `quantity`, `checkout_id`, `expires_at` |
| `checkout` | One attempt to buy a cart | `cart_id`, priced snapshot (JSONB), `total numeric(19,4)`, `payment_method`, `status`, `sales_order_id` |
| `payment_attempt` | Gateway interaction | `checkout_id`, `gateway`, `gateway_ref`, `amount`, `status`, `raw_status` |
| `payment_inbox` | Incoming webhooks, idempotent | `gateway`, `event_id` (UNIQUE per gateway), `payload`, `signature_valid`, `processed_at` |

Money `numeric(19,4)`, quantities `numeric(19,6)`, rounding with `Money.Round`. Enums as snake_case text with `CHECK`.

---

## 5. Flows

### 5.1 Browse and price
`GET /api/store/{slug}/catalog` → published listings joined with `IInventoryQueries.GetCatalogAsync` + `GetUnitsAsync`. Cache per
tenant (Redis, short TTL, busted by an outbox event when items or prices change). Prices shown come from `ISalesPricing` for a
guest basket; a logged-in shopper linked to a customer gets that customer's price list.

### 5.2 Checkout

```
cart ──► checkout (re-price via ISalesPricing, snapshot totals)
          │
          ├─ reserve stock (available = on hand − active reservations; reject if short)
          │
          ├─ COD ───────────────► place order (§5.3)
          │
          └─ online payment ───► IPaymentGateway.CreatePaymentLink ──► shopper pays
                                   │
                                   └─ webhook ─► payment_inbox (dedupe on event_id, verify signature)
                                                  └─ verified & amount matches ─► place order (§5.3)
```

The redirect back from the gateway only shows a "confirming payment" page; it never marks anything paid. A background job also
polls `QueryStatus` for attempts with no webhook after N minutes.

### 5.3 Place the order (one transaction)
Inside `db.InTransactionAsync`:
1. `IOnlineOrderIntake.PlaceAsync` → `SalesOrder` (channel `online`, status `Confirmed`, gapless `SoNo` via `INumberingService`).
2. For paid orders, `RecordPaymentAsync` → customer receipt; Sales posts **Dr PaymentGatewayClearing / Cr Accounts Receivable**.
3. Convert reservations to "allocated to `sales_order_id`".
4. Outbox: `OnlineOrderPlaced` (and `PaymentCaptured`).

### 5.4 Fulfil
Staff see the order in **Online orders** in `Erp.Web`, pick and pack, then create the delivery. Delivery is the existing Sales
document: it calls `IStockService.PostAsync` and posts cost (rule 10), then the invoice posts revenue. COD receipts are recorded
on delivery (debit `Cash` or the courier's clearing). Outbox: `OrderShipped`.

### 5.5 Settle
Gateway payouts arrive in the bank net of fees. Bank reconciliation matches the payout to **Payment Gateway Clearing**; the fee
difference posts to a **PaymentGatewayFees** expense role. Fawran references captured on receipts help auto-matching (blueprint §4.11).

### 5.6 Cancel and refund
Before delivery: cancel the order, release reservations, refund through the gateway, reverse the receipt (`ReverseAsync`, never
edit). After delivery: Sales return document (stock back through `IStockService`), credit note, refund.

---

## 6. Finance changes

| Account role | Used for | Default account (provisioned) |
|---|---|---|
| `PaymentGatewayClearing` | Captured online payments awaiting payout | Current asset |
| `PaymentGatewayFees` | Gateway commission on payout | Expense |
| `CodClearing` *(optional)* | Cash collected by couriers awaiting handover | Current asset |

Add the roles to `AccountRoles`, posting rules and `ChartOfAccountsTemplate`. Commerce never names an account (rule 1).

---

## 7. Payment gateways

Implements the blueprint's `IPaymentGateway` (create payment link, verify webhook signature, query status):

| Order | Provider | Notes |
|---|---|---|
| 1st | One local gateway end to end (QPay/NAPS **or** SkipCash) | Pick by merchant onboarding speed and fees ⚠️ VERIFY current terms |
| 2nd | Dibsy, Sadad, bank MPGS (QNB / CBQ / QIB) | Same adapter interface |
| Always | Cash on delivery | No gateway; receipt at delivery |

Rules: amounts in minor units at the adapter edge only; QAR everywhere else. Store only gateway references, never card data
(PCI scope stays with the gateway's hosted page). Gateway API keys are tenant settings in encrypted columns (`EncryptedStringConverter`).

---

## 8. Permissions

| Permission | Default roles |
|---|---|
| `commerce.store:manage` | Owner, Admin |
| `commerce.listing:edit` | Owner, Admin, Storekeeper |
| `commerce.order:view` / `commerce.order:fulfil` | Owner, Sales, Storekeeper |
| `commerce.refund:create` | Owner, Accountant |

Declared in `CommercePermissions : IPermissionProvider`. Tenant defaults (store record, delivery settings) in
`CommerceProvisioner : ITenantProvisioner` with `Order => 70` (after Sales = 50).

---

## 9. Frontends

### 9.1 Merchant screens (`src/Erp.Web`)
Store settings, listings (bulk-publish from inventory, photos, Arabic/English text), categories, **Online orders** queue with
pick/pack/deliver actions, refunds. Follows the frontend rules in `CLAUDE.md`: `I18nService` keys in `en.ts`/`ar.ts`, logical CSS,
`permissionGuard`, signals. Add an e2e test for the online-orders screen.

### 9.2 Shop website (`src/Erp.Storefront`)
Angular with SSR so listing and product pages are indexable; Arabic default with English toggle; RTL via logical CSS; product
JSON-LD; sitemap per store. Pages: home, category, product, cart, checkout, order status, account. Lighthouse mobile ≥ 90 as a
release target.

### 9.3 Shopper app (`apps/shopper`, Flutter)
Same `/api/store/{slug}` endpoints. MVP: browse, search, cart, checkout (gateway hosted page in a web view), order tracking,
saved addresses. Phase 2: push notifications on order status, reorder. White-label build per store is a later paid add-on.

---

## 10. Notifications and WhatsApp

Outbox events drive SMS/e-mail first (order received, shipped, delivered). WhatsApp Business messages are Phase 5 through an
`INotificationChannel` adapter. Order messages in Arabic and English using the store's names.

---

## 11. AI tools (read-only, rule 12)

`CommerceAiTools` over existing reports: top-selling listings, conversion by category, abandoned carts (counts and value only,
no shopper personal data), slow online items vs. stock on hand. Filtered by `commerce.order:view`.

---

## 12. Delivery plan

| Phase | Scope | Done when |
|---|---|---|
| 1 | Module skeleton, `store`, `store_domain`, `listing`, storefront tenant resolution, read-only public catalog API, Sales.Contracts + `ISalesPricing` | Two-tenant isolation tests pass; catalog prices equal POS prices for the same item |
| 2 | Carts, reservations, COD checkout → `SalesOrder` (channel online), Online orders screen in `Erp.Web` | An SME can take a COD order online and deliver it; stock and GL match |
| 3 | First `IPaymentGateway`, `payment_inbox`, finance roles, settlement in bank reconciliation, refunds | Duplicate and out-of-order webhooks never double-record; payout clears the clearing account to zero |
| 4 | `Erp.Storefront` SSR site, then Flutter shopper app | Product pages indexed; app on TestFlight / internal track |
| 5 | WhatsApp notifications, promotions/coupons, AI tools, additional gateways | — |

**Phase 2 is the first sellable increment.**

### 12.1 Acceptance criteria
- Two shoppers race for the last unit: exactly one checkout succeeds; the other sees "out of stock", never an oversold order.
- The same webhook delivered five times, or before the redirect returns, records one payment and one receipt.
- Cancelling a paid order before delivery leaves the order cancelled, reservations released, receipt reversed, refund issued, and
  `PaymentGatewayClearing` back to its prior balance.
- Every figure on an online order drills down: order → delivery → stock movement → journal entry (blueprint §4.10).
- No storefront response ever contains cost, supplier, exact stock quantity, or another tenant's data.

---

## 13. Open questions

1. One store per company, or several (e.g. per branch / warehouse)? The model above allows one; `store.warehouse_id` would be the extension.
2. Which gateway first? Depends on the onboarding requirements and fees offered to SMEs at the time ⚠️ VERIFY.
3. Delivery: flat fee per zone in MVP, or a courier integration (Snoonu, Talabat Mart-style aggregators, Qatar Post) in Phase 4?
4. Do guest checkouts create a Sales customer each time, or always post to the walk-in customer with shopper details on the order?
5. Consumer-protection requirements for online sales in Qatar (Arabic invoice, return policy display, pricing display) ⚠️ VERIFY with an adviser before launch.
