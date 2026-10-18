-- Inventory: items, warehouses and a weighted-average-cost stock ledger (one valuation per item per company).
CREATE SCHEMA IF NOT EXISTS inv;

CREATE TABLE inv.settings (
    id                   uuid PRIMARY KEY,
    tenant_id            uuid NOT NULL,
    company_id           uuid NOT NULL,
    allow_negative_stock boolean NOT NULL DEFAULT false,
    created_by           uuid NOT NULL,
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_by           uuid,
    updated_at           timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('inv', 'settings');

CREATE TABLE inv.uom (
    id         uuid PRIMARY KEY,
    tenant_id  uuid NOT NULL,
    company_id uuid NOT NULL,
    code       varchar(20) NOT NULL,
    name_en    varchar(100) NOT NULL,
    name_ar    varchar(100) NOT NULL,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_by uuid,
    updated_at timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, code),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('inv', 'uom');

CREATE TABLE inv.item_group (
    id         uuid PRIMARY KEY,
    tenant_id  uuid NOT NULL,
    company_id uuid NOT NULL,
    code       varchar(30) NOT NULL,
    name_en    varchar(200) NOT NULL,
    name_ar    varchar(200) NOT NULL,
    is_active  boolean NOT NULL DEFAULT true,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_by uuid,
    updated_at timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, code),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('inv', 'item_group');

CREATE TABLE inv.warehouse (
    id         uuid PRIMARY KEY,
    tenant_id  uuid NOT NULL,
    company_id uuid NOT NULL,
    branch_id  uuid,
    code       varchar(30) NOT NULL,
    name_en    varchar(200) NOT NULL,
    name_ar    varchar(200) NOT NULL,
    is_active  boolean NOT NULL DEFAULT true,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_by uuid,
    updated_at timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, code),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, branch_id) REFERENCES core.branch (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('inv', 'warehouse');

CREATE TABLE inv.item (
    id             uuid PRIMARY KEY,
    tenant_id      uuid NOT NULL,
    company_id     uuid NOT NULL,
    sku            varchar(50) NOT NULL,
    name_en        varchar(200) NOT NULL,
    name_ar        varchar(200) NOT NULL,
    item_type      varchar(20) NOT NULL CHECK (item_type IN ('stock', 'service')),
    item_group_id  uuid NOT NULL,
    uom_id         uuid NOT NULL,
    barcode        varchar(60),
    reorder_level  numeric(19,6) CHECK (reorder_level >= 0),
    purchase_price numeric(19,6) CHECK (purchase_price >= 0),
    sales_price    numeric(19,6) CHECK (sales_price >= 0),
    hs_code        varchar(20),
    is_active      boolean NOT NULL DEFAULT true,
    created_by     uuid NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_by     uuid,
    updated_at     timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, sku),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_group_id) REFERENCES inv.item_group (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, uom_id) REFERENCES inv.uom (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE UNIQUE INDEX ux_item_barcode ON inv.item (tenant_id, company_id, barcode) WHERE barcode IS NOT NULL;
SELECT core.enable_tenant_rls('inv', 'item');

CREATE TABLE inv.item_valuation (
    id           uuid PRIMARY KEY,
    tenant_id    uuid NOT NULL,
    item_id      uuid NOT NULL,
    quantity     numeric(19,6) NOT NULL DEFAULT 0,
    value        numeric(19,4) NOT NULL DEFAULT 0,
    average_cost numeric(19,6) NOT NULL DEFAULT 0,
    created_by   uuid NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_by   uuid,
    updated_at   timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, item_id),
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('inv', 'item_valuation');

CREATE TABLE inv.warehouse_stock (
    id           uuid PRIMARY KEY,
    tenant_id    uuid NOT NULL,
    item_id      uuid NOT NULL,
    warehouse_id uuid NOT NULL,
    quantity     numeric(19,6) NOT NULL DEFAULT 0,
    created_by   uuid NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_by   uuid,
    updated_at   timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, item_id, warehouse_id),
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('inv', 'warehouse_stock');

CREATE TABLE inv.stock_movement (
    id             uuid PRIMARY KEY,
    tenant_id      uuid NOT NULL,
    company_id     uuid NOT NULL,
    item_id        uuid NOT NULL,
    warehouse_id   uuid,
    movement_date  date NOT NULL,
    quantity       numeric(19,6) NOT NULL,
    unit_cost      numeric(19,6) NOT NULL,
    value          numeric(19,4) NOT NULL,
    quantity_after numeric(19,6) NOT NULL,
    value_after    numeric(19,4) NOT NULL,
    source_type    varchar(40) NOT NULL,
    source_id      uuid NOT NULL,
    source_no      varchar(40) NOT NULL,
    source_line_id uuid,
    reversal_of_id uuid,
    created_by     uuid NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_by     uuid,
    updated_at     timestamptz,
    UNIQUE (tenant_id, id),
    CHECK (warehouse_id IS NOT NULL OR quantity = 0),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, reversal_of_id) REFERENCES inv.stock_movement (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_stock_movement_item ON inv.stock_movement (tenant_id, item_id, movement_date);
CREATE INDEX ix_stock_movement_source ON inv.stock_movement (tenant_id, source_type, source_id);
CREATE UNIQUE INDEX ux_stock_movement_reversal ON inv.stock_movement (tenant_id, reversal_of_id) WHERE reversal_of_id IS NOT NULL;
SELECT core.enable_tenant_rls('inv', 'stock_movement');
SELECT core.make_immutable('inv', 'stock_movement');

CREATE TABLE inv.stock_adjustment (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    company_id       uuid NOT NULL,
    adjustment_no    varchar(40) NOT NULL,
    warehouse_id     uuid NOT NULL,
    adjustment_date  date NOT NULL,
    reason           varchar(20) NOT NULL CHECK (reason IN ('opening', 'damage', 'expiry', 'count', 'other')),
    status           varchar(20) NOT NULL CHECK (status IN ('posted', 'reversed')),
    notes            varchar(500),
    total_value      numeric(19,4) NOT NULL DEFAULT 0,
    stock_count_id   uuid,
    journal_entry_id uuid,
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    updated_at       timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, adjustment_no),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('inv', 'stock_adjustment');

CREATE TABLE inv.stock_adjustment_line (
    id                  uuid PRIMARY KEY,
    tenant_id           uuid NOT NULL,
    stock_adjustment_id uuid NOT NULL,
    line_no             int NOT NULL,
    item_id             uuid NOT NULL,
    quantity            numeric(19,6) NOT NULL CHECK (quantity <> 0),
    unit_cost           numeric(19,6) CHECK (unit_cost >= 0),
    value               numeric(19,4) NOT NULL,
    created_by          uuid NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          uuid,
    updated_at          timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, stock_adjustment_id) REFERENCES inv.stock_adjustment (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('inv', 'stock_adjustment_line');

CREATE TABLE inv.stock_transfer (
    id                uuid PRIMARY KEY,
    tenant_id         uuid NOT NULL,
    company_id        uuid NOT NULL,
    transfer_no       varchar(40) NOT NULL,
    from_warehouse_id uuid NOT NULL,
    to_warehouse_id   uuid NOT NULL,
    transfer_date     date NOT NULL,
    status            varchar(20) NOT NULL CHECK (status IN ('posted', 'reversed')),
    notes             varchar(500),
    created_by        uuid NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_by        uuid,
    updated_at        timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, transfer_no),
    CHECK (from_warehouse_id <> to_warehouse_id),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, from_warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, to_warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('inv', 'stock_transfer');

CREATE TABLE inv.stock_transfer_line (
    id                uuid PRIMARY KEY,
    tenant_id         uuid NOT NULL,
    stock_transfer_id uuid NOT NULL,
    line_no           int NOT NULL,
    item_id           uuid NOT NULL,
    quantity          numeric(19,6) NOT NULL CHECK (quantity > 0),
    value             numeric(19,4) NOT NULL,
    created_by        uuid NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_by        uuid,
    updated_at        timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, stock_transfer_id) REFERENCES inv.stock_transfer (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('inv', 'stock_transfer_line');

CREATE TABLE inv.stock_count (
    id                  uuid PRIMARY KEY,
    tenant_id           uuid NOT NULL,
    company_id          uuid NOT NULL,
    count_no            varchar(40) NOT NULL,
    warehouse_id        uuid NOT NULL,
    count_date          date NOT NULL,
    status              varchar(20) NOT NULL CHECK (status IN ('open', 'posted', 'cancelled')),
    notes               varchar(500),
    stock_adjustment_id uuid,
    created_by          uuid NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          uuid,
    updated_at          timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, count_no),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, stock_adjustment_id) REFERENCES inv.stock_adjustment (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('inv', 'stock_count');

ALTER TABLE inv.stock_adjustment
    ADD FOREIGN KEY (tenant_id, stock_count_id) REFERENCES inv.stock_count (tenant_id, id) DEFERRABLE INITIALLY DEFERRED;

CREATE TABLE inv.stock_count_line (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    stock_count_id   uuid NOT NULL,
    item_id          uuid NOT NULL,
    system_quantity  numeric(19,6) NOT NULL,
    counted_quantity numeric(19,6) CHECK (counted_quantity >= 0),
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    updated_at       timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, stock_count_id, item_id),
    FOREIGN KEY (tenant_id, stock_count_id) REFERENCES inv.stock_count (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('inv', 'stock_count_line');

-- Defaults for companies that existed before the Trade bundle (new companies are seeded at provisioning).
INSERT INTO inv.settings (id, tenant_id, company_id, created_by)
SELECT gen_random_uuid(), c.tenant_id, c.id, '00000000-0000-0000-0000-000000000000' FROM core.company c;

INSERT INTO inv.uom (id, tenant_id, company_id, code, name_en, name_ar, created_by)
SELECT gen_random_uuid(), c.tenant_id, c.id, u.code, u.name_en, u.name_ar, '00000000-0000-0000-0000-000000000000'
  FROM core.company c
 CROSS JOIN (VALUES ('PCS', 'Piece', 'قطعة'), ('BOX', 'Box', 'علبة'), ('CTN', 'Carton', 'كرتونة'), ('KG', 'Kilogram', 'كيلوغرام'),
                    ('L', 'Litre', 'لتر'), ('M', 'Metre', 'متر'), ('SET', 'Set', 'طقم'), ('HR', 'Hour', 'ساعة')) AS u(code, name_en, name_ar);

INSERT INTO inv.item_group (id, tenant_id, company_id, code, name_en, name_ar, created_by)
SELECT gen_random_uuid(), c.tenant_id, c.id, 'GEN', 'General', 'عام', '00000000-0000-0000-0000-000000000000' FROM core.company c;

INSERT INTO inv.warehouse (id, tenant_id, company_id, branch_id, code, name_en, name_ar, created_by)
SELECT gen_random_uuid(), c.tenant_id, c.id,
       (SELECT b.id FROM core.branch b WHERE b.tenant_id = c.tenant_id AND b.company_id = c.id ORDER BY b.created_at LIMIT 1),
       'MAIN', 'Main Warehouse', 'المستودع الرئيسي', '00000000-0000-0000-0000-000000000000'
  FROM core.company c;
