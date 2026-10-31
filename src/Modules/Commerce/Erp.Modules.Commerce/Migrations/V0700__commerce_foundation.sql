-- Online store, Phase 1 (docs/Commerce-Storefront-Design.md §3, §4): one store per company, its public directory entry, web
-- categories, listings over inventory items and their photos. Carts, checkout and payments come in later phases.
CREATE SCHEMA IF NOT EXISTS commerce;

CREATE TABLE commerce.store (
    id            uuid PRIMARY KEY,
    tenant_id     uuid NOT NULL,
    company_id    uuid NOT NULL,
    slug          varchar(60) NOT NULL CHECK (slug ~ '^[a-z0-9]([a-z0-9-]*[a-z0-9])?$' AND length(slug) >= 3),
    name_en       varchar(200) NOT NULL,
    name_ar       varchar(200) NOT NULL,
    phone         varchar(40),
    currency      char(3) NOT NULL DEFAULT 'QAR' CHECK (currency = 'QAR'),
    cod_enabled   boolean NOT NULL DEFAULT true,
    min_order     numeric(19,4) NOT NULL DEFAULT 0 CHECK (min_order >= 0),
    delivery_fee  numeric(19,4) NOT NULL DEFAULT 0 CHECK (delivery_fee >= 0),
    status        varchar(20) NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'open', 'closed')),
    created_by    uuid NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_by    uuid,
    updated_at    timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id),
    UNIQUE (tenant_id, company_id, slug),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('commerce', 'store');

-- The public directory: a store's slug (and optional custom domain) → tenant and company, read before any tenant is known.
-- Not tenant-scoped and no RLS, so it holds nothing but the keys; the app writes it only together with commerce.store and
-- filters tenant_id explicitly when it does.
CREATE TABLE commerce.store_domain (
    store_slug  varchar(60) PRIMARY KEY,
    host        varchar(253) UNIQUE CHECK (host = lower(host)),
    tenant_id   uuid NOT NULL,
    company_id  uuid NOT NULL,
    store_id    uuid NOT NULL UNIQUE,
    is_active   boolean NOT NULL DEFAULT false,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz,
    FOREIGN KEY (tenant_id, store_id) REFERENCES commerce.store (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE commerce.category (
    id             uuid PRIMARY KEY,
    tenant_id      uuid NOT NULL,
    company_id     uuid NOT NULL,
    store_id       uuid NOT NULL,
    slug           varchar(80) NOT NULL CHECK (slug ~ '^[a-z0-9]([a-z0-9-]*[a-z0-9])?$'),
    name_en        varchar(200) NOT NULL,
    name_ar        varchar(200) NOT NULL,
    parent_id      uuid,
    item_group_id  uuid,
    sort_order     int NOT NULL DEFAULT 0,
    is_active      boolean NOT NULL DEFAULT true,
    created_by     uuid NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_by     uuid,
    updated_at     timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, store_id, slug),
    CHECK (parent_id IS NULL OR parent_id <> id),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, store_id) REFERENCES commerce.store (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, parent_id) REFERENCES commerce.category (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_group_id) REFERENCES inv.item_group (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('commerce', 'category');

-- The web layer over an inventory item: its own slug, titles and text. Price and stock always come from Sales and Inventory.
CREATE TABLE commerce.listing (
    id              uuid PRIMARY KEY,
    tenant_id       uuid NOT NULL,
    company_id      uuid NOT NULL,
    store_id        uuid NOT NULL,
    item_id         uuid NOT NULL,
    category_id     uuid,
    slug            varchar(120) NOT NULL CHECK (slug ~ '^[a-z0-9]([a-z0-9-]*[a-z0-9])?$'),
    title_en        varchar(200) NOT NULL,
    title_ar        varchar(200) NOT NULL,
    description_en  varchar(4000),
    description_ar  varchar(4000),
    is_published    boolean NOT NULL DEFAULT false,
    sort_order      int NOT NULL DEFAULT 0,
    created_by      uuid NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_by      uuid,
    updated_at      timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, store_id, slug),
    UNIQUE (tenant_id, store_id, item_id),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, store_id) REFERENCES commerce.store (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, category_id) REFERENCES commerce.category (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_listing_published ON commerce.listing (tenant_id, store_id, is_published, sort_order);
SELECT core.enable_tenant_rls('commerce', 'listing');

-- Photos by object-storage key; uploading comes with the storage decision (later phase).
CREATE TABLE commerce.listing_image (
    id           uuid PRIMARY KEY,
    tenant_id    uuid NOT NULL,
    listing_id   uuid NOT NULL,
    storage_key  varchar(500) NOT NULL,
    sort_order   int NOT NULL DEFAULT 0,
    created_by   uuid NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_by   uuid,
    updated_at   timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, listing_id) REFERENCES commerce.listing (tenant_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_listing_image_listing ON commerce.listing_image (tenant_id, listing_id, sort_order);
SELECT core.enable_tenant_rls('commerce', 'listing_image');

-- The Business and Enterprise plans include the online store; other customers get it added to their subscription by the back-office.
UPDATE core.plan SET modules = modules || ',commerce'
 WHERE code IN ('BUSINESS', 'ENTERPRISE') AND NOT ('commerce' = ANY (string_to_array(modules, ',')));
UPDATE core.subscription s SET modules = s.modules || ',commerce'
  FROM core.plan p
 WHERE p.id = s.plan_id AND p.code IN ('BUSINESS', 'ENTERPRISE') AND NOT ('commerce' = ANY (string_to_array(s.modules, ',')));

-- Existing companies get the commerce permissions on their default roles (new companies receive them from CommercePermissionProvider).
INSERT INTO core.role_permission (id, tenant_id, role_id, permission, created_by, created_at)
SELECT gen_random_uuid(), r.tenant_id, r.id, p.permission, '00000000-0000-0000-0000-000000000000', now()
  FROM (VALUES ('commerce.store:manage', 'general_manager'),
               ('commerce.listing:edit', 'general_manager'), ('commerce.listing:edit', 'storekeeper'),
               ('commerce.order:view', 'salesperson'), ('commerce.order:view', 'storekeeper'),
               ('commerce.order:fulfil', 'salesperson'), ('commerce.order:fulfil', 'storekeeper'),
               ('commerce.refund:create', 'accountant'))
       AS p(permission, role_code)
  JOIN core.role r ON r.code = p.role_code
 WHERE NOT EXISTS (SELECT 1 FROM core.role_permission x WHERE x.tenant_id = r.tenant_id AND x.role_id = r.id AND x.permission = p.permission);
