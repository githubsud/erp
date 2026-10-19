-- Customer credits: receipts with an amount on account, credit notes beyond what is open on the invoice. Credits are applied to
-- other invoices later (no ledger entry: both sides are receivables) or refunded through the bank or cash.
ALTER TABLE sal.customer_receipt ADD COLUMN unapplied_amount numeric(19,4) NOT NULL DEFAULT 0 CHECK (unapplied_amount >= 0);

ALTER TABLE sal.credit_note ADD COLUMN applied_amount numeric(19,4);
ALTER TABLE sal.credit_note ADD COLUMN unapplied_amount numeric(19,4) NOT NULL DEFAULT 0 CHECK (unapplied_amount >= 0);
UPDATE sal.credit_note SET applied_amount = total;
ALTER TABLE sal.credit_note ALTER COLUMN applied_amount SET NOT NULL;

CREATE TABLE sal.credit_application (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    company_id       uuid NOT NULL,
    customer_id      uuid NOT NULL,
    source_type      varchar(20) NOT NULL CHECK (source_type IN ('receipt', 'credit_note')),
    source_id        uuid NOT NULL,
    sales_invoice_id uuid NOT NULL,
    application_date date NOT NULL,
    amount           numeric(19,4) NOT NULL CHECK (amount > 0),
    status           varchar(20) NOT NULL CHECK (status IN ('active', 'reversed')),
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    updated_at       timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, customer_id) REFERENCES sal.customer (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, sales_invoice_id) REFERENCES sal.sales_invoice (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_credit_application_source ON sal.credit_application (tenant_id, source_type, source_id);
SELECT core.enable_tenant_rls('sal', 'credit_application');

CREATE TABLE sal.customer_refund (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    company_id       uuid NOT NULL,
    refund_no        varchar(40) NOT NULL,
    customer_id      uuid NOT NULL,
    source_type      varchar(20) NOT NULL CHECK (source_type IN ('receipt', 'credit_note')),
    source_id        uuid NOT NULL,
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
    FOREIGN KEY (tenant_id, customer_id) REFERENCES sal.customer (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, account_id) REFERENCES fin.account (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'customer_refund');
