-- Salons and barbers: staff with commission rates, appointments by staff and time, and checkout of a visit (services done by
-- which staff) into a paid sales invoice. Commission is reported per staff and month.

CREATE TABLE sal.salon_staff (
    id                  uuid PRIMARY KEY,
    tenant_id           uuid NOT NULL,
    company_id          uuid NOT NULL,
    name                varchar(100) NOT NULL,
    commission_percent  numeric(19,6) NOT NULL DEFAULT 0 CHECK (commission_percent BETWEEN 0 AND 100),
    color               varchar(20),
    is_active           boolean NOT NULL DEFAULT true,
    created_by          uuid NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          uuid,
    updated_at          timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, name)
);
SELECT core.enable_tenant_rls('sal', 'salon_staff');

CREATE TABLE sal.salon_visit (
    id                uuid PRIMARY KEY,
    tenant_id         uuid NOT NULL,
    company_id        uuid NOT NULL,
    visit_no          varchar(40) NOT NULL,
    visit_date        date NOT NULL,
    customer_id       uuid NOT NULL,
    customer_name     varchar(200) NOT NULL,
    total             numeric(19,4) NOT NULL,
    commission        numeric(19,4) NOT NULL,
    sales_invoice_id  uuid NOT NULL,
    created_by        uuid NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_by        uuid,
    updated_at        timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, visit_no),
    FOREIGN KEY (tenant_id, customer_id) REFERENCES sal.customer (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, sales_invoice_id) REFERENCES sal.sales_invoice (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'salon_visit');

CREATE TABLE sal.salon_visit_line (
    id                  uuid PRIMARY KEY,
    tenant_id           uuid NOT NULL,
    visit_id            uuid NOT NULL,
    line_no             int NOT NULL,
    item_id             uuid NOT NULL,
    staff_id            uuid NOT NULL,
    price               numeric(19,4) NOT NULL CHECK (price >= 0),
    discount_percent    numeric(19,6) NOT NULL DEFAULT 0,
    amount              numeric(19,4) NOT NULL,
    commission_percent  numeric(19,6) NOT NULL,
    commission          numeric(19,4) NOT NULL,
    created_by          uuid NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          uuid,
    updated_at          timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, visit_id) REFERENCES sal.salon_visit (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, staff_id) REFERENCES sal.salon_staff (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'salon_visit_line');

CREATE TABLE sal.salon_appointment (
    id             uuid PRIMARY KEY,
    tenant_id      uuid NOT NULL,
    company_id     uuid NOT NULL,
    customer_name  varchar(200) NOT NULL,
    phone          varchar(40),
    staff_id       uuid NOT NULL,
    item_id        uuid,
    starts_at      timestamptz NOT NULL,
    minutes        int NOT NULL CHECK (minutes BETWEEN 5 AND 600),
    status         varchar(20) NOT NULL CHECK (status IN ('booked', 'arrived', 'done', 'cancelled', 'no_show')),
    notes          varchar(500),
    visit_id       uuid,
    created_by     uuid NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_by     uuid,
    updated_at     timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, staff_id) REFERENCES sal.salon_staff (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, visit_id) REFERENCES sal.salon_visit (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_salon_appointment_day ON sal.salon_appointment (tenant_id, company_id, starts_at);
SELECT core.enable_tenant_rls('sal', 'salon_appointment');

INSERT INTO core.role_permission (id, tenant_id, role_id, permission, created_by, created_at)
SELECT gen_random_uuid(), r.tenant_id, r.id, 'sal.salon:use', '00000000-0000-0000-0000-000000000000', now()
  FROM core.role r
 WHERE r.code IN ('salesperson', 'general_manager', 'accountant')
   AND NOT EXISTS (SELECT 1 FROM core.role_permission x WHERE x.tenant_id = r.tenant_id AND x.role_id = r.id AND x.permission = 'sal.salon:use');
