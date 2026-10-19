-- Subcontracts: a bill of quantities with a subcontractor, cumulative progress certificates billed as supplier invoices, with
-- retention and tax-clearance retention held back from each invoice and released later.

ALTER TABLE pur.supplier_invoice
    ADD COLUMN retention_amount     numeric(19,4) NOT NULL DEFAULT 0 CHECK (retention_amount >= 0),
    ADD COLUMN tax_retention_amount numeric(19,4) NOT NULL DEFAULT 0 CHECK (tax_retention_amount >= 0);

ALTER TABLE pur.supplier_invoice_line DROP CONSTRAINT IF EXISTS supplier_invoice_line_line_type_check;
ALTER TABLE pur.supplier_invoice_line
    ADD CONSTRAINT supplier_invoice_line_line_type_check CHECK (line_type IN ('goods', 'service', 'landed_cost', 'retention_release', 'tax_release'));

-- ⚠️ VERIFY with a Qatari tax adviser: the share of each payment held until the subcontractor presents a tax clearance certificate.
ALTER TABLE pur.settings
    ADD COLUMN tax_clearance_retention_percent numeric(9,4) NOT NULL DEFAULT 3 CHECK (tax_clearance_retention_percent BETWEEN 0 AND 100);

CREATE TABLE pur.subcontract (
    id                        uuid PRIMARY KEY,
    tenant_id                 uuid NOT NULL,
    company_id                uuid NOT NULL,
    code                      varchar(30) NOT NULL,
    name_en                   varchar(200) NOT NULL,
    name_ar                   varchar(200) NOT NULL,
    supplier_id               uuid NOT NULL,
    project_id                uuid,
    contract_value            numeric(19,4) NOT NULL DEFAULT 0,
    retention_percent         numeric(19,6) NOT NULL DEFAULT 0 CHECK (retention_percent BETWEEN 0 AND 50),
    tax_retention_percent     numeric(19,6) NOT NULL DEFAULT 0 CHECK (tax_retention_percent BETWEEN 0 AND 100),
    tax_clearance_received_on date,
    status                    varchar(20) NOT NULL CHECK (status IN ('active', 'completed', 'closed')),
    notes                     varchar(1000),
    created_by                uuid NOT NULL,
    created_at                timestamptz NOT NULL DEFAULT now(),
    updated_by                uuid,
    updated_at                timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, code),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, supplier_id) REFERENCES pur.supplier (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'subcontract');

CREATE TABLE pur.subcontract_item (
    id              uuid PRIMARY KEY,
    tenant_id       uuid NOT NULL,
    subcontract_id  uuid NOT NULL,
    line_no         int NOT NULL,
    section         varchar(100),
    description_en  varchar(500) NOT NULL,
    description_ar  varchar(500) NOT NULL,
    unit            varchar(20) NOT NULL,
    quantity        numeric(19,6) NOT NULL CHECK (quantity > 0),
    rate            numeric(19,6) NOT NULL CHECK (rate >= 0),
    amount          numeric(19,4) NOT NULL,
    created_by      uuid NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_by      uuid,
    updated_at      timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, subcontract_id) REFERENCES pur.subcontract (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'subcontract_item');

CREATE TABLE pur.subcontract_certificate (
    id                   uuid PRIMARY KEY,
    tenant_id            uuid NOT NULL,
    subcontract_id       uuid NOT NULL,
    certificate_no       varchar(50) NOT NULL,
    period_end           date NOT NULL,
    status               varchar(20) NOT NULL CHECK (status IN ('draft', 'invoiced', 'cancelled')),
    gross_to_date        numeric(19,4) NOT NULL DEFAULT 0,
    previous_gross       numeric(19,4) NOT NULL DEFAULT 0,
    gross                numeric(19,4) NOT NULL DEFAULT 0,
    retention            numeric(19,4) NOT NULL DEFAULT 0,
    tax_retention        numeric(19,4) NOT NULL DEFAULT 0,
    net                  numeric(19,4) NOT NULL DEFAULT 0,
    supplier_invoice_id  uuid,
    notes                varchar(1000),
    created_by           uuid NOT NULL,
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_by           uuid,
    updated_at           timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, certificate_no),
    FOREIGN KEY (tenant_id, subcontract_id) REFERENCES pur.subcontract (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, supplier_invoice_id) REFERENCES pur.supplier_invoice (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'subcontract_certificate');

CREATE TABLE pur.subcontract_progress (
    id                  uuid PRIMARY KEY,
    tenant_id           uuid NOT NULL,
    certificate_id      uuid NOT NULL,
    item_id             uuid NOT NULL,
    quantity_to_date    numeric(19,6) NOT NULL,
    previous_quantity   numeric(19,6) NOT NULL,
    amount_to_date      numeric(19,4) NOT NULL,
    amount              numeric(19,4) NOT NULL,
    created_by          uuid NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          uuid,
    updated_at          timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, certificate_id) REFERENCES pur.subcontract_certificate (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES pur.subcontract_item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'subcontract_progress');

ALTER TABLE pur.supplier_invoice
    ADD COLUMN subcontract_id uuid,
    ADD FOREIGN KEY (tenant_id, subcontract_id) REFERENCES pur.subcontract (tenant_id, id) DEFERRABLE INITIALLY DEFERRED;

INSERT INTO core.role_permission (id, tenant_id, role_id, permission, created_by, created_at)
SELECT gen_random_uuid(), r.tenant_id, r.id, 'pur.subcontracts:manage', '00000000-0000-0000-0000-000000000000', now()
  FROM core.role r
 WHERE r.code IN ('purchaser', 'general_manager')
   AND NOT EXISTS (SELECT 1 FROM core.role_permission x WHERE x.tenant_id = r.tenant_id AND x.role_id = r.id AND x.permission = 'pur.subcontracts:manage');
