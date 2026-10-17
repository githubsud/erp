-- Salary advances / loans paid to employees; recovered through payroll (ADVANCE_RECOVERY) or the final settlement.

CREATE TABLE hr.employee_advance (
    id                 uuid PRIMARY KEY,
    tenant_id          uuid NOT NULL,
    company_id         uuid NOT NULL,
    employee_id        uuid NOT NULL,
    advance_no         varchar(40) NOT NULL,
    advance_date       date NOT NULL,
    amount             numeric(19,4) NOT NULL CHECK (amount > 0),
    monthly_recovery   numeric(19,4),
    payment_account_id uuid NOT NULL,
    status             varchar(20) NOT NULL CHECK (status IN ('posted', 'reversed')),
    notes              varchar(500),
    journal_entry_id   uuid NOT NULL,
    created_by         uuid NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_by         uuid,
    updated_at         timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, advance_no),
    FOREIGN KEY (tenant_id, employee_id) REFERENCES hr.employee (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('hr', 'employee_advance');
