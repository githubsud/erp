-- Monthly GRSIA contribution payments (employee + employer shares), clearing social insurance payable.
CREATE TABLE hr.grsia_payment (
    id                uuid PRIMARY KEY,
    tenant_id         uuid NOT NULL,
    company_id        uuid NOT NULL,
    year              int NOT NULL,
    month             int NOT NULL CHECK (month BETWEEN 1 AND 12),
    amount            numeric(19,4) NOT NULL CHECK (amount > 0),
    payment_date      date NOT NULL,
    account_id        uuid NOT NULL,
    reference         varchar(100),
    journal_entry_id  uuid NOT NULL,
    created_by        uuid NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_by        uuid,
    updated_at        timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, year, month),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, account_id) REFERENCES fin.account (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('hr', 'grsia_payment');
