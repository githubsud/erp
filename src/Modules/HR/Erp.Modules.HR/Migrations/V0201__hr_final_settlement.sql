-- End-of-service final settlement (gratuity payout, leave encashment, unpaid salary, recoveries).

ALTER TABLE hr.settings
    ADD COLUMN leave_salary_basis varchar(30) NOT NULL DEFAULT 'basic'
        CHECK (leave_salary_basis IN ('basic', 'basic_plus_fixed_allowances'));

CREATE TABLE hr.final_settlement (
    id                       uuid PRIMARY KEY,
    tenant_id                uuid NOT NULL,
    company_id               uuid NOT NULL,
    employee_id              uuid NOT NULL,
    settlement_no            varchar(40) NOT NULL,
    last_working_day         date NOT NULL,
    reason                   varchar(30) NOT NULL CHECK (reason IN ('resignation', 'termination', 'end_of_contract', 'retirement', 'death')),
    status                   varchar(20) NOT NULL CHECK (status IN ('draft', 'posted', 'paid', 'reversed')),
    joining_date             date NOT NULL,
    service_days             int NOT NULL,
    excluded_days            int NOT NULL,
    basis_wage               numeric(19,4) NOT NULL,
    gratuity_days_per_year   numeric(9,4)  NOT NULL,
    eligible_for_gratuity    boolean NOT NULL,
    gratuity_amount          numeric(19,4) NOT NULL,
    gratuity_provision       numeric(19,4) NOT NULL,
    leave_balance_days       numeric(9,2)  NOT NULL,
    daily_leave_rate         numeric(19,6) NOT NULL,
    unpaid_salary_from       date,
    unpaid_salary_days       numeric(9,2) NOT NULL DEFAULT 0,
    total_earnings           numeric(19,4) NOT NULL,
    total_deductions         numeric(19,4) NOT NULL,
    net_payable              numeric(19,4) NOT NULL CHECK (net_payable >= 0),
    notes                    varchar(1000),
    employee_was_active      boolean NOT NULL,
    ended_contract_id        uuid,
    posted_at                timestamptz,
    journal_entry_id         uuid,
    paid_at                  timestamptz,
    payment_date             date,
    payment_account_id       uuid,
    payment_journal_entry_id uuid,
    created_by               uuid NOT NULL,
    created_at               timestamptz NOT NULL DEFAULT now(),
    updated_by               uuid,
    updated_at               timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, settlement_no),
    CHECK (net_payable = total_earnings - total_deductions),
    FOREIGN KEY (tenant_id, employee_id) REFERENCES hr.employee (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, payment_journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
-- One live settlement per employee.
CREATE UNIQUE INDEX ux_final_settlement_employee ON hr.final_settlement (tenant_id, employee_id) WHERE status <> 'reversed';
SELECT core.enable_tenant_rls('hr', 'final_settlement');

CREATE TABLE hr.final_settlement_line (
    id                  uuid PRIMARY KEY,
    tenant_id           uuid NOT NULL,
    final_settlement_id uuid NOT NULL,
    line_no             int NOT NULL,
    code                varchar(40)  NOT NULL,
    name_en             varchar(200) NOT NULL,
    name_ar             varchar(200) NOT NULL,
    kind                varchar(20)  NOT NULL CHECK (kind IN ('earning', 'deduction')),
    pay_component_id    uuid,
    quantity            numeric(19,6),
    rate                numeric(19,6),
    amount              numeric(19,4) NOT NULL CHECK (amount >= 0),
    created_by          uuid NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          uuid,
    updated_at          timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, final_settlement_id, line_no),
    FOREIGN KEY (tenant_id, final_settlement_id) REFERENCES hr.final_settlement (tenant_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('hr', 'final_settlement_line');
