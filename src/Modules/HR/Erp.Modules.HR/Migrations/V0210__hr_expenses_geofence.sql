-- Field staff from their phones: expense claims (with a receipt photo) reimbursed through payroll, and attendance punches checked
-- against work-site geofences.

CREATE TABLE hr.expense_claim (
    id                    uuid PRIMARY KEY,
    tenant_id             uuid NOT NULL,
    company_id            uuid NOT NULL,
    employee_id           uuid NOT NULL,
    claim_no              varchar(40) NOT NULL,
    expense_date          date NOT NULL,
    category              varchar(20) NOT NULL CHECK (category IN ('fuel', 'meals', 'transport', 'accommodation', 'supplies', 'phone', 'other')),
    description           varchar(500) NOT NULL,
    amount                numeric(19,4) NOT NULL CHECK (amount > 0),
    status                varchar(20) NOT NULL CHECK (status IN ('submitted', 'approved', 'rejected', 'cancelled')),
    receipt               bytea,
    receipt_content_type  varchar(100),
    receipt_file_name     varchar(200),
    decided_by            uuid,
    decided_at            timestamptz,
    decision_note         varchar(500),
    payroll_year          int,
    payroll_month         int,
    payroll_input_id      uuid,
    created_by            uuid NOT NULL,
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_by            uuid,
    updated_at            timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, claim_no),
    FOREIGN KEY (tenant_id, employee_id) REFERENCES hr.employee (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('hr', 'expense_claim');

ALTER TABLE hr.payroll_input DROP CONSTRAINT IF EXISTS payroll_input_source_check;
ALTER TABLE hr.payroll_input ADD CONSTRAINT payroll_input_source_check CHECK (source IN ('manual', 'attendance', 'expense'));

-- Reimbursement pay component for existing companies (new companies get it at provisioning), costed to purchased services.
INSERT INTO hr.pay_component (id, tenant_id, company_id, code, name_en, name_ar, kind, wps_field, is_fixed, prorate_by_days,
                              in_gratuity_basis, posting_role, is_system, is_active, created_by, created_at)
SELECT gen_random_uuid(), s.tenant_id, s.company_id, 'EXPENSE_CLAIM', 'Expense reimbursement', 'تعويض مصروفات', 'earning', 'extra_income',
       false, false, false, 'hr.payroll_expense', true, true, '00000000-0000-0000-0000-000000000000', now()
  FROM hr.settings s
 WHERE NOT EXISTS (SELECT 1 FROM hr.pay_component c WHERE c.tenant_id = s.tenant_id AND c.company_id = s.company_id AND c.code = 'EXPENSE_CLAIM');

INSERT INTO fin.posting_rule (id, tenant_id, company_id, account_role, role_key, account_id, created_by, created_at)
SELECT gen_random_uuid(), c.tenant_id, c.company_id, 'hr.payroll_expense', c.id, a.id, '00000000-0000-0000-0000-000000000000', now()
  FROM hr.pay_component c
  JOIN fin.account a ON a.tenant_id = c.tenant_id AND a.company_id = c.company_id AND a.code = '5360'
 WHERE c.code = 'EXPENSE_CLAIM'
   AND NOT EXISTS (SELECT 1 FROM fin.posting_rule p WHERE p.tenant_id = c.tenant_id AND p.company_id = c.company_id
                                                     AND p.account_role = 'hr.payroll_expense' AND p.role_key = c.id);

CREATE TABLE hr.work_site (
    id          uuid PRIMARY KEY,
    tenant_id   uuid NOT NULL,
    company_id  uuid NOT NULL,
    code        varchar(20) NOT NULL,
    name_en     varchar(200) NOT NULL,
    name_ar     varchar(200) NOT NULL,
    latitude    numeric(9,6) NOT NULL CHECK (latitude BETWEEN -90 AND 90),
    longitude   numeric(9,6) NOT NULL CHECK (longitude BETWEEN -180 AND 180),
    radius_m    int NOT NULL CHECK (radius_m BETWEEN 20 AND 5000),
    is_active   boolean NOT NULL DEFAULT true,
    created_by  uuid NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  uuid,
    updated_at  timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, code),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('hr', 'work_site');

CREATE TABLE hr.attendance_punch (
    id           uuid PRIMARY KEY,
    tenant_id    uuid NOT NULL,
    employee_id  uuid NOT NULL,
    punched_at   timestamptz NOT NULL,
    kind         varchar(10) NOT NULL CHECK (kind IN ('in', 'out')),
    latitude     numeric(9,6) NOT NULL,
    longitude    numeric(9,6) NOT NULL,
    accuracy_m   int,
    site_id      uuid,
    distance_m   int,
    accepted     boolean NOT NULL,
    created_by   uuid NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_by   uuid,
    updated_at   timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, employee_id) REFERENCES hr.employee (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, site_id) REFERENCES hr.work_site (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_attendance_punch_employee ON hr.attendance_punch (tenant_id, employee_id, punched_at);
SELECT core.enable_tenant_rls('hr', 'attendance_punch');

INSERT INTO core.role_permission (id, tenant_id, role_id, permission, created_by, created_at)
SELECT gen_random_uuid(), r.tenant_id, r.id, 'hr.expenses:approve', '00000000-0000-0000-0000-000000000000', now()
  FROM core.role r
 WHERE r.code IN ('hr_manager', 'general_manager')
   AND NOT EXISTS (SELECT 1 FROM core.role_permission x WHERE x.tenant_id = r.tenant_id AND x.role_id = r.id AND x.permission = 'hr.expenses:approve');
