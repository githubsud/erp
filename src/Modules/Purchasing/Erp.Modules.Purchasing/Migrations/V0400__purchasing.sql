-- Purchasing: suppliers, purchase orders with approval, goods receipts, supplier invoices (3-way match), landed cost
-- vouchers and supplier payments with withholding tax.
CREATE SCHEMA IF NOT EXISTS pur;

CREATE TABLE pur.settings (
    id                      uuid PRIMARY KEY,
    tenant_id               uuid NOT NULL,
    company_id              uuid NOT NULL,
    approval_threshold      numeric(19,4) NOT NULL DEFAULT 10000 CHECK (approval_threshold >= 0),
    price_tolerance_percent numeric(9,4) NOT NULL DEFAULT 2 CHECK (price_tolerance_percent >= 0),
    created_by              uuid NOT NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_by              uuid,
    updated_at              timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'settings');

CREATE TABLE pur.supplier (
    id                          uuid PRIMARY KEY,
    tenant_id                   uuid NOT NULL,
    company_id                  uuid NOT NULL,
    code                        varchar(30) NOT NULL,
    name_en                     varchar(200) NOT NULL,
    name_ar                     varchar(200) NOT NULL,
    country                     char(2) NOT NULL DEFAULT 'QA',
    is_resident                 boolean NOT NULL DEFAULT true,
    has_permanent_establishment boolean NOT NULL DEFAULT false,
    tax_card_no                 varchar(40),
    cr_no                       varchar(40),
    email                       varchar(200),
    phone                       varchar(40),
    payment_terms_days          int NOT NULL DEFAULT 30 CHECK (payment_terms_days BETWEEN 0 AND 365),
    default_wht_category        varchar(20) NOT NULL DEFAULT 'none',
    is_active                   boolean NOT NULL DEFAULT true,
    created_by                  uuid NOT NULL,
    created_at                  timestamptz NOT NULL DEFAULT now(),
    updated_by                  uuid,
    updated_at                  timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, code),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'supplier');

CREATE TABLE pur.purchase_order (
    id                   uuid PRIMARY KEY,
    tenant_id            uuid NOT NULL,
    company_id           uuid NOT NULL,
    po_no                varchar(40) NOT NULL,
    supplier_id          uuid NOT NULL,
    order_date           date NOT NULL,
    expected_date        date,
    warehouse_id         uuid NOT NULL,
    status               varchar(20) NOT NULL CHECK (status IN ('draft', 'pending_approval', 'approved', 'partially_received', 'received', 'closed', 'cancelled')),
    total                numeric(19,4) NOT NULL DEFAULT 0,
    notes                varchar(1000),
    requires_approval    boolean NOT NULL DEFAULT false,
    submitted_at         timestamptz,
    approved_by          uuid,
    approved_at          timestamptz,
    gta_notification_due date,
    gta_notification_ref varchar(60),
    created_by           uuid NOT NULL,
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_by           uuid,
    updated_at           timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, po_no),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, supplier_id) REFERENCES pur.supplier (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_purchase_order_supplier ON pur.purchase_order (tenant_id, supplier_id);
SELECT core.enable_tenant_rls('pur', 'purchase_order');

CREATE TABLE pur.purchase_order_line (
    id                uuid PRIMARY KEY,
    tenant_id         uuid NOT NULL,
    purchase_order_id uuid NOT NULL,
    line_no           int NOT NULL,
    item_id           uuid NOT NULL,
    description       varchar(500),
    quantity          numeric(19,6) NOT NULL CHECK (quantity > 0),
    unit_price        numeric(19,6) NOT NULL CHECK (unit_price >= 0),
    amount            numeric(19,4) NOT NULL,
    quantity_received numeric(19,6) NOT NULL DEFAULT 0 CHECK (quantity_received >= 0),
    quantity_invoiced numeric(19,6) NOT NULL DEFAULT 0 CHECK (quantity_invoiced >= 0),
    wht_category      varchar(20) NOT NULL DEFAULT 'none',
    created_by        uuid NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_by        uuid,
    updated_at        timestamptz,
    UNIQUE (tenant_id, id),
    CHECK (quantity_received <= quantity),
    FOREIGN KEY (tenant_id, purchase_order_id) REFERENCES pur.purchase_order (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'purchase_order_line');

CREATE TABLE pur.goods_receipt (
    id                     uuid PRIMARY KEY,
    tenant_id              uuid NOT NULL,
    company_id             uuid NOT NULL,
    grn_no                 varchar(40) NOT NULL,
    purchase_order_id      uuid NOT NULL,
    supplier_id            uuid NOT NULL,
    warehouse_id           uuid NOT NULL,
    receipt_date           date NOT NULL,
    supplier_delivery_note varchar(60),
    status                 varchar(20) NOT NULL CHECK (status IN ('posted', 'reversed')),
    total_value            numeric(19,4) NOT NULL DEFAULT 0,
    journal_entry_id       uuid,
    created_by             uuid NOT NULL,
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_by             uuid,
    updated_at             timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, grn_no),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, purchase_order_id) REFERENCES pur.purchase_order (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, supplier_id) REFERENCES pur.supplier (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'goods_receipt');

CREATE TABLE pur.goods_receipt_line (
    id                     uuid PRIMARY KEY,
    tenant_id              uuid NOT NULL,
    goods_receipt_id       uuid NOT NULL,
    line_no                int NOT NULL,
    purchase_order_line_id uuid NOT NULL,
    item_id                uuid NOT NULL,
    quantity               numeric(19,6) NOT NULL CHECK (quantity > 0),
    unit_cost              numeric(19,6) NOT NULL CHECK (unit_cost >= 0),
    value                  numeric(19,4) NOT NULL,
    quantity_invoiced      numeric(19,6) NOT NULL DEFAULT 0 CHECK (quantity_invoiced >= 0),
    created_by             uuid NOT NULL,
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_by             uuid,
    updated_at             timestamptz,
    UNIQUE (tenant_id, id),
    CHECK (quantity_invoiced <= quantity),
    FOREIGN KEY (tenant_id, goods_receipt_id) REFERENCES pur.goods_receipt (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, purchase_order_line_id) REFERENCES pur.purchase_order_line (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'goods_receipt_line');

CREATE TABLE pur.supplier_invoice (
    id                   uuid PRIMARY KEY,
    tenant_id            uuid NOT NULL,
    company_id           uuid NOT NULL,
    invoice_no           varchar(40) NOT NULL,
    supplier_id          uuid NOT NULL,
    supplier_ref         varchar(60) NOT NULL,
    invoice_date         date NOT NULL,
    due_date             date NOT NULL,
    purchase_order_id    uuid,
    total                numeric(19,4) NOT NULL CHECK (total > 0),
    wht_amount           numeric(19,4) NOT NULL DEFAULT 0 CHECK (wht_amount >= 0),
    paid_amount          numeric(19,4) NOT NULL DEFAULT 0 CHECK (paid_amount >= 0),
    wht_withheld         numeric(19,4) NOT NULL DEFAULT 0 CHECK (wht_withheld >= 0),
    status               varchar(20) NOT NULL CHECK (status IN ('draft', 'posted', 'partially_paid', 'paid', 'reversed')),
    match_status         varchar(20) NOT NULL CHECK (match_status IN ('not_applicable', 'matched', 'variance', 'approved')),
    variance_approved_by uuid,
    notes                varchar(1000),
    journal_entry_id     uuid,
    created_by           uuid NOT NULL,
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_by           uuid,
    updated_at           timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, invoice_no),
    CHECK (paid_amount <= total),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, supplier_id) REFERENCES pur.supplier (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, purchase_order_id) REFERENCES pur.purchase_order (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
-- The same supplier invoice cannot be entered twice (reversed ones can be re-entered).
CREATE UNIQUE INDEX ux_supplier_invoice_ref ON pur.supplier_invoice (tenant_id, supplier_id, lower(supplier_ref)) WHERE status <> 'reversed';
SELECT core.enable_tenant_rls('pur', 'supplier_invoice');

CREATE TABLE pur.supplier_invoice_line (
    id                     uuid PRIMARY KEY,
    tenant_id              uuid NOT NULL,
    supplier_invoice_id    uuid NOT NULL,
    line_no                int NOT NULL,
    line_type              varchar(20) NOT NULL CHECK (line_type IN ('goods', 'service', 'landed_cost')),
    goods_receipt_line_id  uuid,
    purchase_order_line_id uuid,
    item_id                uuid,
    description            varchar(500),
    quantity               numeric(19,6) NOT NULL CHECK (quantity > 0),
    unit_price             numeric(19,6) NOT NULL CHECK (unit_price >= 0),
    amount                 numeric(19,4) NOT NULL,
    receipt_unit_cost      numeric(19,6),
    variance               numeric(19,4) NOT NULL DEFAULT 0,
    wht_category           varchar(20) NOT NULL DEFAULT 'none',
    wht_rate               numeric(9,4) NOT NULL DEFAULT 0,
    wht_amount             numeric(19,4) NOT NULL DEFAULT 0,
    created_by             uuid NOT NULL,
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_by             uuid,
    updated_at             timestamptz,
    UNIQUE (tenant_id, id),
    CHECK (line_type <> 'goods' OR goods_receipt_line_id IS NOT NULL),
    FOREIGN KEY (tenant_id, supplier_invoice_id) REFERENCES pur.supplier_invoice (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, goods_receipt_line_id) REFERENCES pur.goods_receipt_line (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, purchase_order_line_id) REFERENCES pur.purchase_order_line (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'supplier_invoice_line');

CREATE TABLE pur.landed_cost_voucher (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    company_id       uuid NOT NULL,
    voucher_no       varchar(40) NOT NULL,
    voucher_date     date NOT NULL,
    method           varchar(20) NOT NULL CHECK (method IN ('value', 'quantity')),
    status           varchar(20) NOT NULL CHECK (status IN ('posted', 'reversed')),
    total_amount     numeric(19,4) NOT NULL,
    notes            varchar(1000),
    journal_entry_id uuid,
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    updated_at       timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, voucher_no),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'landed_cost_voucher');

CREATE TABLE pur.landed_cost_charge (
    id                     uuid PRIMARY KEY,
    tenant_id              uuid NOT NULL,
    landed_cost_voucher_id uuid NOT NULL,
    line_no                int NOT NULL,
    charge_type            varchar(20) NOT NULL CHECK (charge_type IN ('freight', 'clearing', 'customs_duty', 'insurance', 'other')),
    description            varchar(500),
    amount                 numeric(19,4) NOT NULL CHECK (amount > 0),
    supplier_invoice_id    uuid,
    created_by             uuid NOT NULL,
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_by             uuid,
    updated_at             timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, landed_cost_voucher_id) REFERENCES pur.landed_cost_voucher (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, supplier_invoice_id) REFERENCES pur.supplier_invoice (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'landed_cost_charge');

CREATE TABLE pur.landed_cost_allocation (
    id                     uuid PRIMARY KEY,
    tenant_id              uuid NOT NULL,
    landed_cost_voucher_id uuid NOT NULL,
    goods_receipt_line_id  uuid NOT NULL,
    item_id                uuid NOT NULL,
    basis                  numeric(19,6) NOT NULL,
    amount                 numeric(19,4) NOT NULL,
    to_inventory           numeric(19,4) NOT NULL,
    to_cost_of_sales       numeric(19,4) NOT NULL,
    created_by             uuid NOT NULL,
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_by             uuid,
    updated_at             timestamptz,
    UNIQUE (tenant_id, id),
    CHECK (to_inventory + to_cost_of_sales = amount),
    FOREIGN KEY (tenant_id, landed_cost_voucher_id) REFERENCES pur.landed_cost_voucher (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, goods_receipt_line_id) REFERENCES pur.goods_receipt_line (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'landed_cost_allocation');

CREATE TABLE pur.supplier_payment (
    id                 uuid PRIMARY KEY,
    tenant_id          uuid NOT NULL,
    company_id         uuid NOT NULL,
    payment_no         varchar(40) NOT NULL,
    supplier_id        uuid NOT NULL,
    payment_date       date NOT NULL,
    payment_account_id uuid NOT NULL,
    reference          varchar(60),
    gross_amount       numeric(19,4) NOT NULL CHECK (gross_amount > 0),
    wht_amount         numeric(19,4) NOT NULL DEFAULT 0 CHECK (wht_amount >= 0),
    net_amount         numeric(19,4) NOT NULL,
    status             varchar(20) NOT NULL CHECK (status IN ('posted', 'reversed')),
    journal_entry_id   uuid,
    created_by         uuid NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_by         uuid,
    updated_at         timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, payment_no),
    CHECK (net_amount = gross_amount - wht_amount),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, supplier_id) REFERENCES pur.supplier (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, payment_account_id) REFERENCES fin.account (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'supplier_payment');

CREATE TABLE pur.supplier_payment_allocation (
    id                  uuid PRIMARY KEY,
    tenant_id           uuid NOT NULL,
    supplier_payment_id uuid NOT NULL,
    supplier_invoice_id uuid NOT NULL,
    amount              numeric(19,4) NOT NULL CHECK (amount > 0),
    wht_amount          numeric(19,4) NOT NULL DEFAULT 0 CHECK (wht_amount >= 0),
    created_by          uuid NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          uuid,
    updated_at          timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, supplier_payment_id, supplier_invoice_id),
    FOREIGN KEY (tenant_id, supplier_payment_id) REFERENCES pur.supplier_payment (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, supplier_invoice_id) REFERENCES pur.supplier_invoice (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'supplier_payment_allocation');

INSERT INTO pur.settings (id, tenant_id, company_id, created_by)
SELECT gen_random_uuid(), c.tenant_id, c.id, '00000000-0000-0000-0000-000000000000' FROM core.company c;
