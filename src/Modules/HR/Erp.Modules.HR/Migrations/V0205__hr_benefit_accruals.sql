-- Monthly provisions for annual leave salary and air tickets (IAS 19 short-term benefits), released at settlement.

CREATE TABLE hr.benefit_accrual (
    id                 uuid PRIMARY KEY,
    tenant_id          uuid NOT NULL,
    employee_id        uuid NOT NULL,
    payroll_run_id     uuid NOT NULL,
    kind               varchar(20) NOT NULL CHECK (kind IN ('leave_salary', 'air_ticket')),
    period_end         date NOT NULL,
    quantity           numeric(19,6),           -- unused leave days (leave_salary)
    rate               numeric(19,6),           -- daily rate (leave_salary) or monthly accrual (air_ticket)
    liability          numeric(19,4) NOT NULL,
    previous_liability numeric(19,4) NOT NULL,
    expense            numeric(19,4) NOT NULL,  -- amount charged to expense this month (negative = released)
    status             varchar(10) NOT NULL DEFAULT 'posted' CHECK (status IN ('posted', 'reversed')),
    created_by         uuid NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_by         uuid,
    updated_at         timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, employee_id) REFERENCES hr.employee (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, payroll_run_id) REFERENCES hr.payroll_run (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE UNIQUE INDEX ux_benefit_accrual_period ON hr.benefit_accrual (tenant_id, employee_id, kind, period_end) WHERE status = 'posted';
SELECT core.enable_tenant_rls('hr', 'benefit_accrual');

ALTER TABLE hr.employment_contract
    ADD COLUMN annual_air_ticket_amount numeric(19,4) CHECK (annual_air_ticket_amount >= 0);

ALTER TABLE hr.settings
    ADD COLUMN accrue_leave_salary boolean NOT NULL DEFAULT true;

ALTER TABLE hr.payroll_run
    ADD COLUMN total_leave_accrual  numeric(19,4) NOT NULL DEFAULT 0,
    ADD COLUMN total_ticket_accrual numeric(19,4) NOT NULL DEFAULT 0;

ALTER TABLE hr.final_settlement
    ADD COLUMN leave_provision  numeric(19,4) NOT NULL DEFAULT 0,
    ADD COLUMN ticket_provision numeric(19,4) NOT NULL DEFAULT 0;

ALTER TABLE hr.payslip
    ADD COLUMN emailed_at timestamptz;

-- Air ticket paid through payroll: charged against the provision, not to expense.
INSERT INTO hr.pay_component (id, tenant_id, company_id, code, name_en, name_ar, kind, wps_field, is_fixed, prorate_by_days,
                              in_gratuity_basis, posting_role, is_system, is_active, created_by, created_at)
SELECT gen_random_uuid(), s.tenant_id, s.company_id, 'AIR_TICKET', 'Air ticket', 'تذكرة السفر',
       'earning', 'extra_income', false, false, false, 'hr.air_ticket_provision', true, true, '00000000-0000-0000-0000-000000000000', now()
  FROM hr.settings s
 WHERE NOT EXISTS (SELECT 1 FROM hr.pay_component c WHERE c.tenant_id = s.tenant_id AND c.company_id = s.company_id AND c.code = 'AIR_TICKET');

GRANT SELECT, INSERT, UPDATE ON hr.benefit_accrual TO erp_app;
