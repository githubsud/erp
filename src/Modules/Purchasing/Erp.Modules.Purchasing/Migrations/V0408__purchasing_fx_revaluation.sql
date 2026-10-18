-- Month-end revaluation of open foreign-currency payables at closing rates. The entry reverses itself the next day,
-- so invoices keep their booked QAR amounts and settlements still clear them exactly.
CREATE TABLE pur.fx_revaluation (
    id                uuid PRIMARY KEY,
    tenant_id         uuid NOT NULL,
    company_id        uuid NOT NULL,
    revaluation_no    varchar(40) NOT NULL,
    revaluation_date  date NOT NULL,
    reversal_date     date NOT NULL,
    rates             varchar(500) NOT NULL,
    total_difference  numeric(19,4) NOT NULL,
    journal_entry_id  uuid,
    created_by        uuid NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_by        uuid,
    updated_at        timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, revaluation_no),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'fx_revaluation');
