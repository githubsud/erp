-- Sourcing: requests for quotation compared side by side and awarded to purchase orders; blanket agreements (agreed prices and
-- quantities for a period) released as purchase orders.

CREATE TABLE pur.rfq (
    id            uuid PRIMARY KEY,
    tenant_id     uuid NOT NULL,
    company_id    uuid NOT NULL,
    rfq_no        varchar(40) NOT NULL,
    rfq_date      date NOT NULL,
    reply_by      date,
    warehouse_id  uuid NOT NULL,
    status        varchar(20) NOT NULL CHECK (status IN ('open', 'awarded', 'cancelled')),
    notes         varchar(1000),
    created_by    uuid NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_by    uuid,
    updated_at    timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, rfq_no),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'rfq');

CREATE TABLE pur.rfq_line (
    id           uuid PRIMARY KEY,
    tenant_id    uuid NOT NULL,
    rfq_id       uuid NOT NULL,
    line_no      int NOT NULL,
    item_id      uuid NOT NULL,
    description  varchar(500),
    quantity     numeric(19,6) NOT NULL CHECK (quantity > 0),
    created_by   uuid NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_by   uuid,
    updated_at   timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, rfq_id) REFERENCES pur.rfq (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'rfq_line');

CREATE TABLE pur.rfq_supplier (
    id                 uuid PRIMARY KEY,
    tenant_id          uuid NOT NULL,
    rfq_id             uuid NOT NULL,
    supplier_id        uuid NOT NULL,
    purchase_order_id  uuid,
    created_by         uuid NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_by         uuid,
    updated_at         timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, rfq_id, supplier_id),
    FOREIGN KEY (tenant_id, rfq_id) REFERENCES pur.rfq (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, supplier_id) REFERENCES pur.supplier (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'rfq_supplier');

CREATE TABLE pur.rfq_quote (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    rfq_line_id      uuid NOT NULL,
    supplier_id      uuid NOT NULL,
    unit_price       numeric(19,6) NOT NULL CHECK (unit_price >= 0),
    lead_time_days   int,
    awarded          boolean NOT NULL DEFAULT false,
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    updated_at       timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, rfq_line_id, supplier_id),
    FOREIGN KEY (tenant_id, rfq_line_id) REFERENCES pur.rfq_line (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, supplier_id) REFERENCES pur.supplier (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'rfq_quote');

CREATE TABLE pur.blanket_agreement (
    id            uuid PRIMARY KEY,
    tenant_id     uuid NOT NULL,
    company_id    uuid NOT NULL,
    agreement_no  varchar(40) NOT NULL,
    supplier_id   uuid NOT NULL,
    valid_from    date NOT NULL,
    valid_to      date NOT NULL,
    status        varchar(20) NOT NULL CHECK (status IN ('active', 'closed')),
    notes         varchar(1000),
    created_by    uuid NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_by    uuid,
    updated_at    timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, agreement_no),
    CHECK (valid_to >= valid_from),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, supplier_id) REFERENCES pur.supplier (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'blanket_agreement');

CREATE TABLE pur.blanket_line (
    id             uuid PRIMARY KEY,
    tenant_id      uuid NOT NULL,
    agreement_id   uuid NOT NULL,
    line_no        int NOT NULL,
    item_id        uuid NOT NULL,
    unit_price     numeric(19,6) NOT NULL CHECK (unit_price >= 0),
    max_quantity   numeric(19,6) NOT NULL CHECK (max_quantity > 0),
    created_by     uuid NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_by     uuid,
    updated_at     timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, agreement_id, item_id),
    FOREIGN KEY (tenant_id, agreement_id) REFERENCES pur.blanket_agreement (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'blanket_line');

ALTER TABLE pur.purchase_order
    ADD COLUMN blanket_agreement_id uuid,
    ADD COLUMN rfq_id uuid,
    ADD FOREIGN KEY (tenant_id, blanket_agreement_id) REFERENCES pur.blanket_agreement (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    ADD FOREIGN KEY (tenant_id, rfq_id) REFERENCES pur.rfq (tenant_id, id) DEFERRABLE INITIALLY DEFERRED;
