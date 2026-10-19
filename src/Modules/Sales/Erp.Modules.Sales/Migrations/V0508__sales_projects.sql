-- Contracting: projects (customer contracts) with a bill of quantities, monthly progress certificates billed as sales invoices,
-- retention held back on each invoice and released at handover. Project costs come from ledger lines tagged with the project.
CREATE TABLE sal.project (
    id                uuid PRIMARY KEY,
    tenant_id         uuid NOT NULL,
    company_id        uuid NOT NULL,
    code              varchar(30) NOT NULL,
    name_en           varchar(200) NOT NULL,
    name_ar           varchar(200) NOT NULL,
    customer_id       uuid NOT NULL,
    contract_value    numeric(19,4) NOT NULL CHECK (contract_value >= 0),
    retention_percent numeric(19,6) NOT NULL DEFAULT 0 CHECK (retention_percent >= 0 AND retention_percent <= 50),
    budget_cost       numeric(19,4) NOT NULL DEFAULT 0 CHECK (budget_cost >= 0),
    start_date        date,
    end_date          date,
    status            varchar(20) NOT NULL CHECK (status IN ('active', 'completed', 'closed')),
    notes             varchar(2000),
    created_by        uuid NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_by        uuid,
    updated_at        timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, code),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, customer_id) REFERENCES sal.customer (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'project');

CREATE TABLE sal.boq_item (
    id             uuid PRIMARY KEY,
    tenant_id      uuid NOT NULL,
    project_id     uuid NOT NULL,
    line_no        int NOT NULL,
    section        varchar(100),
    description_en varchar(500) NOT NULL,
    description_ar varchar(500) NOT NULL,
    unit           varchar(20) NOT NULL,
    quantity       numeric(19,6) NOT NULL CHECK (quantity > 0),
    rate           numeric(19,6) NOT NULL CHECK (rate >= 0),
    amount         numeric(19,4) NOT NULL,
    created_by     uuid NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_by     uuid,
    updated_at     timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES sal.project (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'boq_item');

CREATE TABLE sal.progress_certificate (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    project_id       uuid NOT NULL,
    certificate_no   varchar(40) NOT NULL,
    period_end       date NOT NULL,
    status           varchar(20) NOT NULL CHECK (status IN ('draft', 'invoiced', 'cancelled')),
    gross_to_date    numeric(19,4) NOT NULL DEFAULT 0,
    previous_gross   numeric(19,4) NOT NULL DEFAULT 0,
    gross            numeric(19,4) NOT NULL DEFAULT 0,
    retention        numeric(19,4) NOT NULL DEFAULT 0,
    net              numeric(19,4) NOT NULL DEFAULT 0,
    sales_invoice_id uuid,
    notes            varchar(1000),
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    updated_at       timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, project_id, certificate_no),
    FOREIGN KEY (tenant_id, project_id) REFERENCES sal.project (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, sales_invoice_id) REFERENCES sal.sales_invoice (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'progress_certificate');

CREATE TABLE sal.progress_line (
    id                      uuid PRIMARY KEY,
    tenant_id               uuid NOT NULL,
    progress_certificate_id uuid NOT NULL,
    boq_item_id             uuid NOT NULL,
    quantity_to_date        numeric(19,6) NOT NULL CHECK (quantity_to_date >= 0),
    previous_quantity       numeric(19,6) NOT NULL DEFAULT 0,
    amount_to_date          numeric(19,4) NOT NULL,
    amount                  numeric(19,4) NOT NULL,
    created_by              uuid NOT NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_by              uuid,
    updated_at              timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, progress_certificate_id) REFERENCES sal.progress_certificate (tenant_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, boq_item_id) REFERENCES sal.boq_item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'progress_line');

ALTER TABLE sal.sales_invoice ADD COLUMN project_id uuid;
ALTER TABLE sal.sales_invoice ADD COLUMN retention_amount numeric(19,4) NOT NULL DEFAULT 0 CHECK (retention_amount >= 0);
ALTER TABLE sal.sales_invoice ADD FOREIGN KEY (tenant_id, project_id) REFERENCES sal.project (tenant_id, id) DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE sal.sales_invoice DROP CONSTRAINT sales_invoice_check;
ALTER TABLE sal.sales_invoice ADD CONSTRAINT sales_invoice_check CHECK (paid_amount + credited_amount + retention_amount <= total);
ALTER TABLE sal.sales_invoice_line DROP CONSTRAINT sales_invoice_line_line_type_check;
ALTER TABLE sal.sales_invoice_line ADD CONSTRAINT sales_invoice_line_line_type_check
    CHECK (line_type IN ('delivered', 'direct', 'service', 'retention_release'));

INSERT INTO core.role_permission (id, tenant_id, role_id, permission, created_by, created_at)
SELECT gen_random_uuid(), r.tenant_id, r.id, p.permission, '00000000-0000-0000-0000-000000000000', now()
  FROM core.role r
  JOIN (VALUES ('accountant', 'sal.projects:manage'), ('general_manager', 'sal.projects:manage'), ('salesperson', 'sal.projects:manage'))
       AS p(role, permission) ON p.role = r.code
 WHERE NOT EXISTS (SELECT 1 FROM core.role_permission x WHERE x.tenant_id = r.tenant_id AND x.role_id = r.id AND x.permission = p.permission);
