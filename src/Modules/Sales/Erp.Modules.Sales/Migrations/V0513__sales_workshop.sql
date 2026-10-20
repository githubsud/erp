-- Car workshop: vehicles of customers (by plate), job cards with an estimate of parts and labour, deposits (customer receipts on
-- account) and the workflow up to delivery, where a sales invoice is posted (parts leave stock) and the deposits are applied.

CREATE TABLE sal.vehicle (
    id             uuid PRIMARY KEY,
    tenant_id      uuid NOT NULL,
    company_id     uuid NOT NULL,
    customer_id    uuid NOT NULL,
    plate_no       varchar(20) NOT NULL,
    make           varchar(60),
    model          varchar(60),
    year           int CHECK (year BETWEEN 1950 AND 2100),
    color          varchar(30),
    vin            varchar(30),
    last_mileage   int CHECK (last_mileage >= 0),
    created_by     uuid NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_by     uuid,
    updated_at     timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, plate_no),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, customer_id) REFERENCES sal.customer (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'vehicle');

CREATE TABLE sal.job_card (
    id                uuid PRIMARY KEY,
    tenant_id         uuid NOT NULL,
    company_id        uuid NOT NULL,
    job_no            varchar(40) NOT NULL,
    customer_id       uuid NOT NULL,
    customer_name     varchar(200) NOT NULL,
    phone             varchar(40),
    vehicle_id        uuid NOT NULL,
    plate_no          varchar(20) NOT NULL,
    received_on       date NOT NULL,
    promised_on       date,
    mileage           int CHECK (mileage >= 0),
    complaint         varchar(1000) NOT NULL,
    diagnosis         varchar(1000),
    technician_name   varchar(100),
    status            varchar(30) NOT NULL CHECK (status IN ('received', 'diagnosing', 'awaiting_approval', 'in_progress', 'waiting_parts', 'ready',
                                                             'delivered', 'cancelled')),
    approved_on       date,
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
    UNIQUE (tenant_id, company_id, job_no),
    FOREIGN KEY (tenant_id, customer_id) REFERENCES sal.customer (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, vehicle_id) REFERENCES sal.vehicle (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, sales_invoice_id) REFERENCES sal.sales_invoice (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_job_card_status ON sal.job_card (tenant_id, company_id, status, received_on);
SELECT core.enable_tenant_rls('sal', 'job_card');

CREATE TABLE sal.job_card_line (
    id           uuid PRIMARY KEY,
    tenant_id    uuid NOT NULL,
    job_card_id  uuid NOT NULL,
    line_no      int NOT NULL,
    kind         varchar(20) NOT NULL CHECK (kind IN ('labour', 'part')),
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
    FOREIGN KEY (tenant_id, job_card_id) REFERENCES sal.job_card (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'job_card_line');

CREATE TABLE sal.job_card_payment (
    id           uuid PRIMARY KEY,
    tenant_id    uuid NOT NULL,
    job_card_id  uuid NOT NULL,
    receipt_id   uuid NOT NULL,
    paid_on      date NOT NULL,
    amount       numeric(19,4) NOT NULL CHECK (amount > 0),
    created_by   uuid NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_by   uuid,
    updated_at   timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, job_card_id) REFERENCES sal.job_card (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, receipt_id) REFERENCES sal.customer_receipt (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'job_card_payment');

INSERT INTO core.role_permission (id, tenant_id, role_id, permission, created_by, created_at)
SELECT gen_random_uuid(), r.tenant_id, r.id, 'sal.workshop:use', '00000000-0000-0000-0000-000000000000', now()
  FROM core.role r
 WHERE r.code IN ('salesperson', 'general_manager', 'accountant')
   AND NOT EXISTS (SELECT 1 FROM core.role_permission x WHERE x.tenant_id = r.tenant_id AND x.role_id = r.id AND x.permission = 'sal.workshop:use');

-- The Enterprise plan includes the module; other customers get it added to their subscription by the back-office.
UPDATE core.plan SET modules = modules || ',workshop' WHERE code = 'ENTERPRISE' AND NOT ('workshop' = ANY (string_to_array(modules, ',')));
UPDATE core.subscription s SET modules = s.modules || ',workshop'
  FROM core.plan p
 WHERE p.id = s.plan_id AND p.code = 'ENTERPRISE' AND NOT ('workshop' = ANY (string_to_array(s.modules, ',')));
