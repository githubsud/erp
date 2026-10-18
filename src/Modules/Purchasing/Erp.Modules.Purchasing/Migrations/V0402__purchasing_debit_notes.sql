-- Debit notes to suppliers: goods returned (stock out) and price allowances, against a posted supplier invoice.
ALTER TABLE pur.supplier_invoice ADD COLUMN debited_amount numeric(19,4) NOT NULL DEFAULT 0 CHECK (debited_amount >= 0);
ALTER TABLE pur.supplier_invoice DROP CONSTRAINT supplier_invoice_check;
ALTER TABLE pur.supplier_invoice ADD CONSTRAINT supplier_invoice_settled_check CHECK (paid_amount + debited_amount <= total);
ALTER TABLE pur.supplier_invoice_line ADD COLUMN quantity_returned numeric(19,6) NOT NULL DEFAULT 0 CHECK (quantity_returned >= 0);

CREATE TABLE pur.debit_note (
    id                  uuid PRIMARY KEY,
    tenant_id           uuid NOT NULL,
    company_id          uuid NOT NULL,
    debit_note_no       varchar(40) NOT NULL,
    supplier_id         uuid NOT NULL,
    supplier_invoice_id uuid NOT NULL,
    note_date           date NOT NULL,
    warehouse_id        uuid,
    reason              varchar(500) NOT NULL,
    total               numeric(19,4) NOT NULL CHECK (total > 0),
    status              varchar(20) NOT NULL CHECK (status IN ('posted', 'reversed')),
    journal_entry_id    uuid,
    created_by          uuid NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          uuid,
    updated_at          timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, debit_note_no),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, supplier_id) REFERENCES pur.supplier (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, supplier_invoice_id) REFERENCES pur.supplier_invoice (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'debit_note');

CREATE TABLE pur.debit_note_line (
    id                       uuid PRIMARY KEY,
    tenant_id                uuid NOT NULL,
    debit_note_id            uuid NOT NULL,
    line_no                  int NOT NULL,
    line_type                varchar(20) NOT NULL CHECK (line_type IN ('return', 'allowance')),
    supplier_invoice_line_id uuid,
    item_id                  uuid,
    description              varchar(500),
    quantity                 numeric(19,6) NOT NULL CHECK (quantity > 0),
    unit_price               numeric(19,6) NOT NULL CHECK (unit_price >= 0),
    amount                   numeric(19,4) NOT NULL,
    stock_value              numeric(19,4) NOT NULL DEFAULT 0,
    created_by               uuid NOT NULL,
    created_at               timestamptz NOT NULL DEFAULT now(),
    updated_by               uuid,
    updated_at               timestamptz,
    UNIQUE (tenant_id, id),
    CHECK (line_type <> 'return' OR supplier_invoice_line_id IS NOT NULL),
    FOREIGN KEY (tenant_id, debit_note_id) REFERENCES pur.debit_note (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, supplier_invoice_line_id) REFERENCES pur.supplier_invoice_line (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'debit_note_line');
