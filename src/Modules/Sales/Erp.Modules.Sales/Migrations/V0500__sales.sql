-- Sales: customers with credit limits, price lists, quotations, sales orders with credit control, delivery notes (stock
-- issue), sales invoices, credit notes (returns and allowances) and customer receipts including post-dated cheques.
CREATE SCHEMA IF NOT EXISTS sal;

CREATE TABLE sal.settings (
    id                      uuid PRIMARY KEY,
    tenant_id               uuid NOT NULL,
    company_id              uuid NOT NULL,
    credit_control          varchar(10) NOT NULL DEFAULT 'block' CHECK (credit_control IN ('none', 'warn', 'block')),
    quotation_validity_days int NOT NULL DEFAULT 30 CHECK (quotation_validity_days BETWEEN 1 AND 365),
    created_by              uuid NOT NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_by              uuid,
    updated_at              timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'settings');

CREATE TABLE sal.price_list (
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
SELECT core.enable_tenant_rls('sal', 'price_list');

CREATE TABLE sal.price_list_item (
    id            uuid PRIMARY KEY,
    tenant_id     uuid NOT NULL,
    price_list_id uuid NOT NULL,
    item_id       uuid NOT NULL,
    unit_price    numeric(19,6) NOT NULL CHECK (unit_price >= 0),
    created_by    uuid NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_by    uuid,
    updated_at    timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, price_list_id, item_id),
    FOREIGN KEY (tenant_id, price_list_id) REFERENCES sal.price_list (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'price_list_item');

CREATE TABLE sal.customer (
    id                  uuid PRIMARY KEY,
    tenant_id           uuid NOT NULL,
    company_id          uuid NOT NULL,
    code                varchar(30) NOT NULL,
    name_en             varchar(200) NOT NULL,
    name_ar             varchar(200) NOT NULL,
    country             char(2) NOT NULL DEFAULT 'QA',
    tax_card_no         varchar(40),
    cr_no               varchar(40),
    email               varchar(200),
    phone               varchar(40),
    address_en          varchar(500),
    address_ar          varchar(500),
    payment_terms_days  int NOT NULL DEFAULT 30 CHECK (payment_terms_days BETWEEN 0 AND 365),
    credit_limit        numeric(19,4) CHECK (credit_limit >= 0),
    price_list_id       uuid,
    salesperson_user_id uuid,
    is_active           boolean NOT NULL DEFAULT true,
    created_by          uuid NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          uuid,
    updated_at          timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, code),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, price_list_id) REFERENCES sal.price_list (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, salesperson_user_id) REFERENCES core.app_user (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'customer');

CREATE TABLE sal.quotation (
    id           uuid PRIMARY KEY,
    tenant_id    uuid NOT NULL,
    company_id   uuid NOT NULL,
    quote_no     varchar(40) NOT NULL,
    customer_id  uuid NOT NULL,
    quote_date   date NOT NULL,
    valid_until  date NOT NULL,
    status       varchar(20) NOT NULL CHECK (status IN ('draft', 'sent', 'accepted', 'rejected')),
    total        numeric(19,4) NOT NULL DEFAULT 0,
    notes        varchar(1000),
    sales_order_id uuid,
    created_by   uuid NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_by   uuid,
    updated_at   timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, quote_no),
    CHECK (valid_until >= quote_date),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, customer_id) REFERENCES sal.customer (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_quotation_customer ON sal.quotation (tenant_id, customer_id);
SELECT core.enable_tenant_rls('sal', 'quotation');

CREATE TABLE sal.quotation_line (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    quotation_id     uuid NOT NULL,
    line_no          int NOT NULL,
    item_id          uuid NOT NULL,
    description      varchar(500),
    quantity         numeric(19,6) NOT NULL CHECK (quantity > 0),
    unit_price       numeric(19,6) NOT NULL CHECK (unit_price >= 0),
    discount_percent numeric(9,4) NOT NULL DEFAULT 0 CHECK (discount_percent BETWEEN 0 AND 100),
    amount           numeric(19,4) NOT NULL,
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    updated_at       timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, quotation_id) REFERENCES sal.quotation (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'quotation_line');

CREATE TABLE sal.sales_order (
    id                 uuid PRIMARY KEY,
    tenant_id          uuid NOT NULL,
    company_id         uuid NOT NULL,
    so_no              varchar(40) NOT NULL,
    customer_id        uuid NOT NULL,
    order_date         date NOT NULL,
    expected_date      date,
    warehouse_id       uuid NOT NULL,
    customer_ref       varchar(60),
    quotation_id       uuid,
    status             varchar(30) NOT NULL CHECK (status IN ('draft', 'credit_hold', 'confirmed', 'partially_delivered', 'delivered', 'closed', 'cancelled')),
    total              numeric(19,4) NOT NULL DEFAULT 0,
    notes              varchar(1000),
    credit_warning     varchar(500),
    confirmed_at       timestamptz,
    credit_approved_by uuid,
    credit_approved_at timestamptz,
    created_by         uuid NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_by         uuid,
    updated_at         timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, so_no),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, customer_id) REFERENCES sal.customer (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, quotation_id) REFERENCES sal.quotation (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_sales_order_customer ON sal.sales_order (tenant_id, customer_id);
SELECT core.enable_tenant_rls('sal', 'sales_order');

ALTER TABLE sal.quotation ADD FOREIGN KEY (tenant_id, sales_order_id) REFERENCES sal.sales_order (tenant_id, id) DEFERRABLE INITIALLY DEFERRED;

CREATE TABLE sal.sales_order_line (
    id                 uuid PRIMARY KEY,
    tenant_id          uuid NOT NULL,
    sales_order_id     uuid NOT NULL,
    line_no            int NOT NULL,
    item_id            uuid NOT NULL,
    description        varchar(500),
    quantity           numeric(19,6) NOT NULL CHECK (quantity > 0),
    unit_price         numeric(19,6) NOT NULL CHECK (unit_price >= 0),
    discount_percent   numeric(9,4) NOT NULL DEFAULT 0 CHECK (discount_percent BETWEEN 0 AND 100),
    amount             numeric(19,4) NOT NULL,
    quantity_delivered numeric(19,6) NOT NULL DEFAULT 0 CHECK (quantity_delivered >= 0),
    quantity_invoiced  numeric(19,6) NOT NULL DEFAULT 0 CHECK (quantity_invoiced >= 0),
    created_by         uuid NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_by         uuid,
    updated_at         timestamptz,
    UNIQUE (tenant_id, id),
    CHECK (quantity_delivered <= quantity),
    CHECK (quantity_invoiced <= quantity),
    FOREIGN KEY (tenant_id, sales_order_id) REFERENCES sal.sales_order (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'sales_order_line');

CREATE TABLE sal.delivery_note (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    company_id       uuid NOT NULL,
    dn_no            varchar(40) NOT NULL,
    sales_order_id   uuid NOT NULL,
    customer_id      uuid NOT NULL,
    warehouse_id     uuid NOT NULL,
    delivery_date    date NOT NULL,
    delivery_address varchar(500),
    vehicle_no       varchar(30),
    notes            varchar(1000),
    status           varchar(20) NOT NULL CHECK (status IN ('posted', 'reversed')),
    total_cost       numeric(19,4) NOT NULL DEFAULT 0,
    journal_entry_id uuid,
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    updated_at       timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, dn_no),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, sales_order_id) REFERENCES sal.sales_order (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, customer_id) REFERENCES sal.customer (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'delivery_note');

CREATE TABLE sal.delivery_note_line (
    id                  uuid PRIMARY KEY,
    tenant_id           uuid NOT NULL,
    delivery_note_id    uuid NOT NULL,
    line_no             int NOT NULL,
    sales_order_line_id uuid NOT NULL,
    item_id             uuid NOT NULL,
    quantity            numeric(19,6) NOT NULL CHECK (quantity > 0),
    unit_cost           numeric(19,6) NOT NULL DEFAULT 0 CHECK (unit_cost >= 0),
    cost                numeric(19,4) NOT NULL DEFAULT 0,
    quantity_invoiced   numeric(19,6) NOT NULL DEFAULT 0 CHECK (quantity_invoiced >= 0),
    created_by          uuid NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          uuid,
    updated_at          timestamptz,
    UNIQUE (tenant_id, id),
    CHECK (quantity_invoiced <= quantity),
    FOREIGN KEY (tenant_id, delivery_note_id) REFERENCES sal.delivery_note (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, sales_order_line_id) REFERENCES sal.sales_order_line (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'delivery_note_line');

CREATE TABLE sal.sales_invoice (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    company_id       uuid NOT NULL,
    invoice_no       varchar(40) NOT NULL,
    customer_id      uuid NOT NULL,
    invoice_date     date NOT NULL,
    due_date         date NOT NULL,
    sales_order_id   uuid,
    warehouse_id     uuid,
    customer_ref     varchar(60),
    total            numeric(19,4) NOT NULL CHECK (total > 0),
    discount_total   numeric(19,4) NOT NULL DEFAULT 0 CHECK (discount_total >= 0),
    paid_amount      numeric(19,4) NOT NULL DEFAULT 0 CHECK (paid_amount >= 0),
    credited_amount  numeric(19,4) NOT NULL DEFAULT 0 CHECK (credited_amount >= 0),
    status           varchar(20) NOT NULL CHECK (status IN ('draft', 'posted', 'partially_paid', 'paid', 'reversed')),
    notes            varchar(1000),
    -- E-invoicing readiness: a stable document UUID and the moment of issue (no QR/hash until the GTA publishes a spec).
    einvoice_uuid    uuid,
    issued_at        timestamptz,
    journal_entry_id uuid,
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    updated_at       timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, invoice_no),
    CHECK (paid_amount + credited_amount <= total),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, customer_id) REFERENCES sal.customer (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, sales_order_id) REFERENCES sal.sales_order (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_sales_invoice_customer ON sal.sales_invoice (tenant_id, customer_id);
SELECT core.enable_tenant_rls('sal', 'sales_invoice');

CREATE TABLE sal.sales_invoice_line (
    id                    uuid PRIMARY KEY,
    tenant_id             uuid NOT NULL,
    sales_invoice_id      uuid NOT NULL,
    line_no               int NOT NULL,
    line_type             varchar(20) NOT NULL CHECK (line_type IN ('delivered', 'direct', 'service')),
    delivery_note_line_id uuid,
    sales_order_line_id   uuid,
    item_id               uuid,
    description           varchar(500),
    quantity              numeric(19,6) NOT NULL CHECK (quantity > 0),
    unit_price            numeric(19,6) NOT NULL CHECK (unit_price >= 0),
    discount_percent      numeric(9,4) NOT NULL DEFAULT 0 CHECK (discount_percent BETWEEN 0 AND 100),
    amount                numeric(19,4) NOT NULL,
    unit_cost             numeric(19,6),
    cost                  numeric(19,4) NOT NULL DEFAULT 0,
    quantity_returned     numeric(19,6) NOT NULL DEFAULT 0 CHECK (quantity_returned >= 0),
    created_by            uuid NOT NULL,
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_by            uuid,
    updated_at            timestamptz,
    UNIQUE (tenant_id, id),
    CHECK (quantity_returned <= quantity),
    CHECK (line_type <> 'delivered' OR delivery_note_line_id IS NOT NULL),
    CHECK (line_type <> 'direct' OR item_id IS NOT NULL),
    FOREIGN KEY (tenant_id, sales_invoice_id) REFERENCES sal.sales_invoice (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, delivery_note_line_id) REFERENCES sal.delivery_note_line (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, sales_order_line_id) REFERENCES sal.sales_order_line (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'sales_invoice_line');

CREATE TABLE sal.credit_note (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    company_id       uuid NOT NULL,
    credit_note_no   varchar(40) NOT NULL,
    customer_id      uuid NOT NULL,
    sales_invoice_id uuid NOT NULL,
    note_date        date NOT NULL,
    warehouse_id     uuid,
    reason           varchar(500) NOT NULL,
    total            numeric(19,4) NOT NULL CHECK (total > 0),
    cost_returned    numeric(19,4) NOT NULL DEFAULT 0,
    status           varchar(20) NOT NULL CHECK (status IN ('posted', 'reversed')),
    einvoice_uuid    uuid,
    journal_entry_id uuid,
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    updated_at       timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, credit_note_no),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, customer_id) REFERENCES sal.customer (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, sales_invoice_id) REFERENCES sal.sales_invoice (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'credit_note');

CREATE TABLE sal.credit_note_line (
    id                    uuid PRIMARY KEY,
    tenant_id             uuid NOT NULL,
    credit_note_id        uuid NOT NULL,
    line_no               int NOT NULL,
    line_type             varchar(20) NOT NULL CHECK (line_type IN ('return', 'allowance')),
    sales_invoice_line_id uuid,
    item_id               uuid,
    description           varchar(500),
    quantity              numeric(19,6) NOT NULL CHECK (quantity > 0),
    unit_price            numeric(19,6) NOT NULL CHECK (unit_price >= 0),
    amount                numeric(19,4) NOT NULL,
    unit_cost             numeric(19,6),
    cost                  numeric(19,4) NOT NULL DEFAULT 0,
    created_by            uuid NOT NULL,
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_by            uuid,
    updated_at            timestamptz,
    UNIQUE (tenant_id, id),
    CHECK (line_type <> 'return' OR sales_invoice_line_id IS NOT NULL),
    FOREIGN KEY (tenant_id, credit_note_id) REFERENCES sal.credit_note (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, sales_invoice_line_id) REFERENCES sal.sales_invoice_line (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'credit_note_line');

CREATE TABLE sal.customer_receipt (
    id                  uuid PRIMARY KEY,
    tenant_id           uuid NOT NULL,
    company_id          uuid NOT NULL,
    receipt_no          varchar(40) NOT NULL,
    customer_id         uuid NOT NULL,
    receipt_date        date NOT NULL,
    method              varchar(20) NOT NULL CHECK (method IN ('cash', 'bank_transfer', 'cheque', 'card', 'post_dated_cheque')),
    deposit_account_id  uuid,
    reference           varchar(60),
    cheque_no           varchar(30),
    cheque_date         date,
    cheque_bank         varchar(60),
    amount              numeric(19,4) NOT NULL CHECK (amount > 0),
    status              varchar(20) NOT NULL CHECK (status IN ('posted', 'pdc_held', 'pdc_cleared', 'bounced', 'reversed')),
    cleared_date        date,
    cleared_account_id  uuid,
    status_reason       varchar(500),
    journal_entry_id    uuid,
    created_by          uuid NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          uuid,
    updated_at          timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, receipt_no),
    CHECK (method = 'post_dated_cheque' OR deposit_account_id IS NOT NULL),
    CHECK (method <> 'post_dated_cheque' OR (cheque_no IS NOT NULL AND cheque_date IS NOT NULL)),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, customer_id) REFERENCES sal.customer (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, deposit_account_id) REFERENCES fin.account (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, cleared_account_id) REFERENCES fin.account (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_customer_receipt_customer ON sal.customer_receipt (tenant_id, customer_id);
SELECT core.enable_tenant_rls('sal', 'customer_receipt');

CREATE TABLE sal.customer_receipt_allocation (
    id                  uuid PRIMARY KEY,
    tenant_id           uuid NOT NULL,
    customer_receipt_id uuid NOT NULL,
    sales_invoice_id    uuid NOT NULL,
    amount              numeric(19,4) NOT NULL CHECK (amount > 0),
    created_by          uuid NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          uuid,
    updated_at          timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, customer_receipt_id, sales_invoice_id),
    FOREIGN KEY (tenant_id, customer_receipt_id) REFERENCES sal.customer_receipt (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, sales_invoice_id) REFERENCES sal.sales_invoice (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'customer_receipt_allocation');

INSERT INTO sal.settings (id, tenant_id, company_id, created_by)
SELECT gen_random_uuid(), c.tenant_id, c.id, '00000000-0000-0000-0000-000000000000' FROM core.company c;
