-- Tailoring and abaya orders: measurement profiles per customer and garment, orders with deposits (customer receipts on account),
-- workflow up to delivery, where a sales invoice is issued and the deposits are applied to it.

CREATE TABLE sal.tailor_measurement (
    id            uuid PRIMARY KEY,
    tenant_id     uuid NOT NULL,
    customer_id   uuid NOT NULL,
    garment       varchar(20) NOT NULL CHECK (garment IN ('abaya', 'dress', 'thobe', 'jalabiya', 'other')),
    measurements  varchar(2000) NOT NULL,
    notes         varchar(500),
    created_by    uuid NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_by    uuid,
    updated_at    timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, customer_id, garment),
    FOREIGN KEY (tenant_id, customer_id) REFERENCES sal.customer (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'tailor_measurement');

CREATE TABLE sal.tailoring_order (
    id                uuid PRIMARY KEY,
    tenant_id         uuid NOT NULL,
    company_id        uuid NOT NULL,
    order_no          varchar(40) NOT NULL,
    customer_id       uuid NOT NULL,
    customer_name     varchar(200) NOT NULL,
    phone             varchar(40),
    order_date        date NOT NULL,
    fitting_date      date,
    due_date          date NOT NULL,
    garment           varchar(20) NOT NULL CHECK (garment IN ('abaya', 'dress', 'thobe', 'jalabiya', 'other')),
    measurements      varchar(2000),
    style_notes       varchar(1000),
    tailor_name       varchar(100),
    status            varchar(20) NOT NULL CHECK (status IN ('received', 'in_progress', 'fitting', 'ready', 'delivered', 'cancelled')),
    total             numeric(19,4) NOT NULL DEFAULT 0,
    paid              numeric(19,4) NOT NULL DEFAULT 0,
    ready_on          date,
    delivered_on      date,
    sales_invoice_id  uuid,
    created_by        uuid NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_by        uuid,
    updated_at        timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, order_no),
    FOREIGN KEY (tenant_id, customer_id) REFERENCES sal.customer (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, sales_invoice_id) REFERENCES sal.sales_invoice (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_tailoring_order_status ON sal.tailoring_order (tenant_id, company_id, status, due_date);
SELECT core.enable_tenant_rls('sal', 'tailoring_order');

CREATE TABLE sal.tailoring_order_line (
    id           uuid PRIMARY KEY,
    tenant_id    uuid NOT NULL,
    order_id     uuid NOT NULL,
    line_no      int NOT NULL,
    item_id      uuid,
    description  varchar(300) NOT NULL,
    quantity     numeric(19,6) NOT NULL CHECK (quantity > 0),
    unit_price   numeric(19,6) NOT NULL CHECK (unit_price >= 0),
    amount       numeric(19,4) NOT NULL,
    created_by   uuid NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_by   uuid,
    updated_at   timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, order_id) REFERENCES sal.tailoring_order (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'tailoring_order_line');

CREATE TABLE sal.tailoring_payment (
    id           uuid PRIMARY KEY,
    tenant_id    uuid NOT NULL,
    order_id     uuid NOT NULL,
    receipt_id   uuid NOT NULL,
    paid_on      date NOT NULL,
    amount       numeric(19,4) NOT NULL CHECK (amount > 0),
    created_by   uuid NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_by   uuid,
    updated_at   timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, order_id) REFERENCES sal.tailoring_order (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, receipt_id) REFERENCES sal.customer_receipt (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'tailoring_payment');

INSERT INTO core.role_permission (id, tenant_id, role_id, permission, created_by, created_at)
SELECT gen_random_uuid(), r.tenant_id, r.id, 'sal.tailoring:use', '00000000-0000-0000-0000-000000000000', now()
  FROM core.role r
 WHERE r.code IN ('salesperson', 'general_manager', 'accountant')
   AND NOT EXISTS (SELECT 1 FROM core.role_permission x WHERE x.tenant_id = r.tenant_id AND x.role_id = r.id AND x.permission = 'sal.tailoring:use');
