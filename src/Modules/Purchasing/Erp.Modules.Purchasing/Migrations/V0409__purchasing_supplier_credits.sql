-- Debit notes on paid invoices leave a supplier credit (applied to other invoices or refunded by the supplier); debit notes on
-- invoices with withholding tax reduce the tax still to be withheld.
ALTER TABLE pur.debit_note ADD COLUMN applied_amount numeric(19,4);
ALTER TABLE pur.debit_note ADD COLUMN applied_base numeric(19,4);
ALTER TABLE pur.debit_note ADD COLUMN unapplied_amount numeric(19,4) NOT NULL DEFAULT 0 CHECK (unapplied_amount >= 0);
ALTER TABLE pur.debit_note ADD COLUMN wht_reduction numeric(19,4) NOT NULL DEFAULT 0;
UPDATE pur.debit_note SET applied_amount = total, applied_base = total_base;
ALTER TABLE pur.debit_note ALTER COLUMN applied_amount SET NOT NULL;
ALTER TABLE pur.debit_note ALTER COLUMN applied_base SET NOT NULL;

CREATE TABLE pur.debit_application (
    id                  uuid PRIMARY KEY,
    tenant_id           uuid NOT NULL,
    company_id          uuid NOT NULL,
    supplier_id         uuid NOT NULL,
    debit_note_id       uuid NOT NULL,
    supplier_invoice_id uuid NOT NULL,
    application_date    date NOT NULL,
    amount              numeric(19,4) NOT NULL CHECK (amount > 0),
    status              varchar(20) NOT NULL CHECK (status IN ('active', 'reversed')),
    created_by          uuid NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          uuid,
    updated_at          timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, supplier_id) REFERENCES pur.supplier (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, debit_note_id) REFERENCES pur.debit_note (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, supplier_invoice_id) REFERENCES pur.supplier_invoice (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'debit_application');

CREATE TABLE pur.supplier_refund (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    company_id       uuid NOT NULL,
    refund_no        varchar(40) NOT NULL,
    supplier_id      uuid NOT NULL,
    debit_note_id    uuid NOT NULL,
    refund_date      date NOT NULL,
    account_id       uuid NOT NULL,
    amount           numeric(19,4) NOT NULL CHECK (amount > 0),
    reference        varchar(60),
    status           varchar(20) NOT NULL CHECK (status IN ('posted', 'reversed')),
    journal_entry_id uuid,
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    updated_at       timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, refund_no),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, supplier_id) REFERENCES pur.supplier (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, debit_note_id) REFERENCES pur.debit_note (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, account_id) REFERENCES fin.account (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'supplier_refund');
