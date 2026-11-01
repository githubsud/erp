-- Goods on trust (بضاعة أمانة): partner shops (suppliers) whose goods we sell as their agent for a commission agreed in advance, the bottles
-- received from them and handed back (zero cost: they stay the shops' property) and the monthly settlement that turns what is owed into a
-- supplier invoice.

ALTER TABLE inv.item ADD CONSTRAINT fk_item_consignor
    FOREIGN KEY (tenant_id, consignor_id) REFERENCES pur.supplier (tenant_id, id) DEFERRABLE INITIALLY DEFERRED;

CREATE TABLE pur.consignment_agreement (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    company_id       uuid NOT NULL,
    supplier_id      uuid NOT NULL,
    commission_type  varchar(10) NOT NULL CHECK (commission_type IN ('percent', 'fixed')),
    commission       numeric(19,6) NOT NULL CHECK (commission >= 0 AND (commission_type = 'fixed' OR commission <= 100)),
    starts_on        date NOT NULL,
    ends_on          date CHECK (ends_on IS NULL OR ends_on >= starts_on),
    payout_day       int NOT NULL DEFAULT 5 CHECK (payout_day BETWEEN 1 AND 28),
    notes            varchar(1000),
    is_active        boolean NOT NULL DEFAULT true,
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    updated_at       timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, supplier_id),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, supplier_id) REFERENCES pur.supplier (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'consignment_agreement');

CREATE TABLE pur.consignment_movement (
    id             uuid PRIMARY KEY,
    tenant_id      uuid NOT NULL,
    company_id     uuid NOT NULL,
    movement_no    varchar(30) NOT NULL,
    movement_type  varchar(10) NOT NULL CHECK (movement_type IN ('receipt', 'return')),
    supplier_id    uuid NOT NULL,
    warehouse_id   uuid NOT NULL,
    movement_date  date NOT NULL,
    notes          varchar(1000),
    status         varchar(10) NOT NULL CHECK (status IN ('posted', 'reversed')),
    created_by     uuid NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_by     uuid,
    updated_at     timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, movement_no),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, supplier_id) REFERENCES pur.supplier (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_consignment_movement_supplier ON pur.consignment_movement (tenant_id, supplier_id, movement_date);
SELECT core.enable_tenant_rls('pur', 'consignment_movement');

CREATE TABLE pur.consignment_movement_line (
    id                       uuid PRIMARY KEY,
    tenant_id                uuid NOT NULL,
    consignment_movement_id  uuid NOT NULL,
    line_no                  int NOT NULL,
    item_id                  uuid NOT NULL,
    quantity                 numeric(19,6) NOT NULL CHECK (quantity > 0),
    created_by               uuid NOT NULL,
    created_at               timestamptz NOT NULL DEFAULT now(),
    updated_by               uuid,
    updated_at               timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, consignment_movement_id) REFERENCES pur.consignment_movement (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'consignment_movement_line');

CREATE TABLE pur.consignment_settlement (
    id                   uuid PRIMARY KEY,
    tenant_id            uuid NOT NULL,
    company_id           uuid NOT NULL,
    settlement_no        varchar(30) NOT NULL,
    supplier_id          uuid NOT NULL,
    period_start         date NOT NULL,
    period_end           date NOT NULL CHECK (period_end >= period_start),
    sales                numeric(19,4) NOT NULL,
    commission           numeric(19,4) NOT NULL,
    amount               numeric(19,4) NOT NULL CHECK (amount > 0),
    supplier_invoice_id  uuid NOT NULL,
    created_by           uuid NOT NULL,
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_by           uuid,
    updated_at           timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, settlement_no),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, supplier_id) REFERENCES pur.supplier (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, supplier_invoice_id) REFERENCES pur.supplier_invoice (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_consignment_settlement_supplier ON pur.consignment_settlement (tenant_id, supplier_id, period_end);
SELECT core.enable_tenant_rls('pur', 'consignment_settlement');
