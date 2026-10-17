-- HR & payroll (Qatar): employees, documents, contracts, leave, payroll runs, gratuity accrual, WPS files.
-- Personal identifiers (QID, passport, IBAN) are stored encrypted (AES-GCM, application side) with a keyed
-- hash ("blind index") where equality search or uniqueness is needed.

CREATE SCHEMA IF NOT EXISTS hr;

CREATE TABLE hr.settings (
    id                          uuid PRIMARY KEY,
    tenant_id                   uuid NOT NULL,
    company_id                  uuid NOT NULL,
    employer_eid                varchar(40),
    payer_eid                   varchar(40),
    payer_qid                   varchar(20),
    payer_bank_short_name       varchar(10),
    payer_iban                  varchar(34),
    sif_format_version          varchar(10)   NOT NULL DEFAULT '1',
    gratuity_days_per_year      numeric(9,4)  NOT NULL DEFAULT 21,
    gratuity_divisor            numeric(9,4)  NOT NULL DEFAULT 30,
    gratuity_min_service_months int           NOT NULL DEFAULT 12,
    daily_rate_divisor          numeric(9,4)  NOT NULL DEFAULT 30,
    hours_per_day               numeric(9,4)  NOT NULL DEFAULT 8,
    overtime_normal_rate        numeric(9,4)  NOT NULL DEFAULT 1.25,
    overtime_night_rate         numeric(9,4)  NOT NULL DEFAULT 1.5,
    overtime_rest_day_rate      numeric(9,4)  NOT NULL DEFAULT 1.5,
    overtime_holiday_rate       numeric(9,4)  NOT NULL DEFAULT 1.5,
    deduction_cap_percent       numeric(9,4)  NOT NULL DEFAULT 50,
    minimum_basic_wage          numeric(19,4) NOT NULL DEFAULT 1000,
    minimum_housing_allowance   numeric(19,4) NOT NULL DEFAULT 500,
    minimum_food_allowance      numeric(19,4) NOT NULL DEFAULT 300,
    wps_deposit_days            int           NOT NULL DEFAULT 7,
    created_by                  uuid NOT NULL,
    created_at                  timestamptz NOT NULL DEFAULT now(),
    updated_by                  uuid,
    updated_at                  timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('hr', 'settings');

CREATE TABLE hr.department (
    id         uuid PRIMARY KEY,
    tenant_id  uuid NOT NULL,
    company_id uuid NOT NULL,
    code       varchar(30)  NOT NULL,
    name_en    varchar(200) NOT NULL,
    name_ar    varchar(200) NOT NULL,
    is_active  boolean NOT NULL DEFAULT true,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_by uuid,
    updated_at timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, code)
);
SELECT core.enable_tenant_rls('hr', 'department');

CREATE TABLE hr.pay_component (
    id                uuid PRIMARY KEY,
    tenant_id         uuid NOT NULL,
    company_id        uuid NOT NULL,
    code              varchar(30)  NOT NULL,
    name_en           varchar(200) NOT NULL,
    name_ar           varchar(200) NOT NULL,
    kind              varchar(20)  NOT NULL CHECK (kind IN ('earning', 'deduction')),
    wps_field         varchar(20)  NOT NULL CHECK (wps_field IN ('basic', 'housing', 'food', 'transport', 'overtime', 'extra_income', 'deduction')),
    is_fixed          boolean NOT NULL,
    prorate_by_days   boolean NOT NULL DEFAULT true,
    in_gratuity_basis boolean NOT NULL DEFAULT false,
    posting_role      varchar(60) NOT NULL,
    is_system         boolean NOT NULL DEFAULT false,
    is_active         boolean NOT NULL DEFAULT true,
    created_by        uuid NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_by        uuid,
    updated_at        timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, code),
    CHECK ((kind = 'deduction') = (wps_field = 'deduction'))
);
SELECT core.enable_tenant_rls('hr', 'pay_component');

CREATE TABLE hr.employee (
    id                         uuid PRIMARY KEY,
    tenant_id                  uuid NOT NULL,
    company_id                 uuid NOT NULL,
    emp_no                     varchar(30)  NOT NULL,
    name_en                    varchar(200) NOT NULL,
    name_ar                    varchar(200) NOT NULL,
    nationality                char(2)      NOT NULL,
    gender                     varchar(10)  NOT NULL CHECK (gender IN ('male', 'female')),
    date_of_birth              date,
    joining_date               date NOT NULL,
    status                     varchar(20)  NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'terminated')),
    termination_date           date,
    department_id              uuid,
    branch_id                  uuid,
    cost_center_id             uuid,
    job_title_en               varchar(200),
    job_title_ar               varchar(200),
    mobile                     varchar(30),
    email                      varchar(200),
    qid_encrypted              text,
    qid_hash                   char(64),
    visa_id                    varchar(30),
    passport_no_encrypted      text,
    bank_short_name            varchar(10),
    iban_encrypted             text,
    in_wps                     boolean NOT NULL DEFAULT true,
    social_insurance_scheme    varchar(20) NOT NULL DEFAULT 'none' CHECK (social_insurance_scheme IN ('none', 'grsia', 'gcc_home')),
    opening_gratuity_liability numeric(19,4) NOT NULL DEFAULT 0,
    created_by                 uuid NOT NULL,
    created_at                 timestamptz NOT NULL DEFAULT now(),
    updated_by                 uuid,
    updated_at                 timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, emp_no),
    UNIQUE (tenant_id, qid_hash),
    CHECK (termination_date IS NULL OR termination_date >= joining_date),
    CHECK ((status = 'terminated') = (termination_date IS NOT NULL)),
    FOREIGN KEY (tenant_id, department_id) REFERENCES hr.department (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, branch_id) REFERENCES core.branch (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, cost_center_id) REFERENCES fin.cost_center (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('hr', 'employee');

CREATE TABLE hr.employee_document (
    id          uuid PRIMARY KEY,
    tenant_id   uuid NOT NULL,
    employee_id uuid NOT NULL,
    doc_type    varchar(30) NOT NULL CHECK (doc_type IN ('qid', 'passport', 'residence_permit', 'work_permit', 'health_card', 'driving_licence', 'professional_licence', 'other')),
    doc_no      varchar(60),
    issue_date  date,
    expiry_date date,
    notes       varchar(500),
    created_by  uuid NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  uuid,
    updated_at  timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, employee_id) REFERENCES hr.employee (tenant_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_employee_document_expiry ON hr.employee_document (tenant_id, expiry_date);
SELECT core.enable_tenant_rls('hr', 'employee_document');

CREATE TABLE hr.company_document (
    id                  uuid PRIMARY KEY,
    tenant_id           uuid NOT NULL,
    company_id          uuid NOT NULL,
    doc_type            varchar(40) NOT NULL CHECK (doc_type IN ('commercial_registration', 'commercial_license', 'municipality_license',
                            'establishment_card', 'chamber_certificate', 'tax_card', 'civil_defence', 'lease', 'insurance', 'vehicle_registration', 'other')),
    name_en             varchar(200) NOT NULL,
    name_ar             varchar(200) NOT NULL,
    doc_no              varchar(60),
    issue_date          date,
    expiry_date         date,
    responsible_user_id uuid,
    notes               varchar(500),
    created_by          uuid NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          uuid,
    updated_at          timestamptz,
    UNIQUE (tenant_id, id)
);
CREATE INDEX ix_company_document_expiry ON hr.company_document (tenant_id, expiry_date);
SELECT core.enable_tenant_rls('hr', 'company_document');

CREATE TABLE hr.employment_contract (
    id                     uuid PRIMARY KEY,
    tenant_id              uuid NOT NULL,
    employee_id            uuid NOT NULL,
    start_date             date NOT NULL,
    end_date               date,
    contract_type          varchar(20) NOT NULL CHECK (contract_type IN ('limited', 'unlimited')),
    probation_months       int NOT NULL DEFAULT 0 CHECK (probation_months BETWEEN 0 AND 6),
    accommodation_provided boolean NOT NULL DEFAULT false,
    food_provided          boolean NOT NULL DEFAULT false,
    gratuity_days_per_year numeric(9,4),
    gratuity_basis         varchar(30) NOT NULL DEFAULT 'basic' CHECK (gratuity_basis IN ('basic', 'basic_plus_fixed_allowances')),
    annual_leave_days      int,
    status                 varchar(10) NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'ended')),
    created_by             uuid NOT NULL,
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_by             uuid,
    updated_at             timestamptz,
    UNIQUE (tenant_id, id),
    CHECK (end_date IS NULL OR end_date >= start_date),
    FOREIGN KEY (tenant_id, employee_id) REFERENCES hr.employee (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE UNIQUE INDEX ux_contract_one_active ON hr.employment_contract (tenant_id, employee_id) WHERE status = 'active';
SELECT core.enable_tenant_rls('hr', 'employment_contract');

CREATE TABLE hr.contract_component (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    contract_id      uuid NOT NULL,
    pay_component_id uuid NOT NULL,
    amount           numeric(19,4) NOT NULL CHECK (amount >= 0),
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    updated_at       timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, contract_id, pay_component_id),
    FOREIGN KEY (tenant_id, contract_id) REFERENCES hr.employment_contract (tenant_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, pay_component_id) REFERENCES hr.pay_component (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('hr', 'contract_component');

CREATE TABLE hr.leave_type (
    id                      uuid PRIMARY KEY,
    tenant_id               uuid NOT NULL,
    company_id              uuid NOT NULL,
    code                    varchar(30)  NOT NULL,
    name_en                 varchar(200) NOT NULL,
    name_ar                 varchar(200) NOT NULL,
    is_paid                 boolean NOT NULL,
    affects_service         boolean NOT NULL DEFAULT false,
    deducts_annual_balance  boolean NOT NULL DEFAULT false,
    is_system               boolean NOT NULL DEFAULT false,
    created_by              uuid NOT NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_by              uuid,
    updated_at              timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, code)
);
SELECT core.enable_tenant_rls('hr', 'leave_type');

CREATE TABLE hr.leave_request (
    id            uuid PRIMARY KEY,
    tenant_id     uuid NOT NULL,
    employee_id   uuid NOT NULL,
    leave_type_id uuid NOT NULL,
    from_date     date NOT NULL,
    to_date       date NOT NULL,
    days          numeric(9,2) NOT NULL CHECK (days > 0),
    status        varchar(20) NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'approved', 'rejected', 'cancelled')),
    reason        varchar(500),
    decided_by    uuid,
    decided_at    timestamptz,
    created_by    uuid NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_by    uuid,
    updated_at    timestamptz,
    UNIQUE (tenant_id, id),
    CHECK (to_date >= from_date),
    FOREIGN KEY (tenant_id, employee_id) REFERENCES hr.employee (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, leave_type_id) REFERENCES hr.leave_type (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_leave_request_employee ON hr.leave_request (tenant_id, employee_id, from_date);
SELECT core.enable_tenant_rls('hr', 'leave_request');

CREATE TABLE hr.payroll_input (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    employee_id      uuid NOT NULL,
    year             int NOT NULL,
    month            int NOT NULL CHECK (month BETWEEN 1 AND 12),
    pay_component_id uuid NOT NULL,
    overtime_type    varchar(20) CHECK (overtime_type IN ('normal', 'night', 'rest_day', 'holiday')),
    hours            numeric(19,6),
    amount           numeric(19,4),
    notes            varchar(500),
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    updated_at       timestamptz,
    UNIQUE (tenant_id, id),
    CHECK ((hours IS NULL) <> (amount IS NULL)),
    CHECK ((overtime_type IS NULL) = (hours IS NULL)),
    CHECK (COALESCE(hours, amount) > 0),
    FOREIGN KEY (tenant_id, employee_id) REFERENCES hr.employee (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, pay_component_id) REFERENCES hr.pay_component (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_payroll_input_period ON hr.payroll_input (tenant_id, year, month);
SELECT core.enable_tenant_rls('hr', 'payroll_input');

CREATE TABLE hr.payroll_run (
    id                       uuid PRIMARY KEY,
    tenant_id                uuid NOT NULL,
    company_id               uuid NOT NULL,
    run_no                   varchar(40) NOT NULL,
    year                     int NOT NULL,
    month                    int NOT NULL CHECK (month BETWEEN 1 AND 12),
    period_start             date NOT NULL,
    period_end               date NOT NULL,
    due_date                 date NOT NULL,
    wps_deadline             date NOT NULL,
    status                   varchar(20) NOT NULL CHECK (status IN ('draft', 'calculated', 'approved', 'posted', 'paid', 'reversed')),
    employee_count           int NOT NULL DEFAULT 0,
    total_gross              numeric(19,4) NOT NULL DEFAULT 0,
    total_deductions         numeric(19,4) NOT NULL DEFAULT 0,
    total_net                numeric(19,4) NOT NULL DEFAULT 0,
    total_gratuity_accrual   numeric(19,4) NOT NULL DEFAULT 0,
    calculated_by            uuid,
    calculated_at            timestamptz,
    approved_by              uuid,
    approved_at              timestamptz,
    posted_at                timestamptz,
    paid_at                  timestamptz,
    payment_date             date,
    payment_account_id       uuid,
    journal_entry_id         uuid,
    gratuity_journal_entry_id uuid,
    payment_journal_entry_id uuid,
    created_by               uuid NOT NULL,
    created_at               timestamptz NOT NULL DEFAULT now(),
    updated_by               uuid,
    updated_at               timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, run_no),
    FOREIGN KEY (tenant_id, journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, gratuity_journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, payment_journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
-- One live payroll run per company and month (a reversed run can be redone).
CREATE UNIQUE INDEX ux_payroll_run_period ON hr.payroll_run (tenant_id, company_id, year, month) WHERE status <> 'reversed';
SELECT core.enable_tenant_rls('hr', 'payroll_run');

CREATE TABLE hr.payslip (
    id                 uuid PRIMARY KEY,
    tenant_id          uuid NOT NULL,
    payroll_run_id     uuid NOT NULL,
    employee_id        uuid NOT NULL,
    emp_no             varchar(30)  NOT NULL,
    name_en            varchar(200) NOT NULL,
    name_ar            varchar(200) NOT NULL,
    cost_center_id     uuid,
    qid_encrypted      text,
    visa_id            varchar(30),
    bank_short_name    varchar(10),
    iban_encrypted     text,
    in_wps             boolean NOT NULL,
    paid_days          numeric(9,2) NOT NULL,
    unpaid_days        numeric(9,2) NOT NULL,
    basic_salary       numeric(19,4) NOT NULL,
    overtime_hours     numeric(19,6) NOT NULL DEFAULT 0,
    gross              numeric(19,4) NOT NULL,
    deductions         numeric(19,4) NOT NULL,
    net                numeric(19,4) NOT NULL CHECK (net >= 0),
    warnings           text,
    created_by         uuid NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_by         uuid,
    updated_at         timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, payroll_run_id, employee_id),
    CHECK (net = gross - deductions),
    FOREIGN KEY (tenant_id, payroll_run_id) REFERENCES hr.payroll_run (tenant_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, employee_id) REFERENCES hr.employee (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('hr', 'payslip');

CREATE TABLE hr.payslip_line (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    payslip_id       uuid NOT NULL,
    line_no          int NOT NULL,
    pay_component_id uuid NOT NULL,
    code             varchar(30)  NOT NULL,
    name_en          varchar(200) NOT NULL,
    name_ar          varchar(200) NOT NULL,
    kind             varchar(20)  NOT NULL CHECK (kind IN ('earning', 'deduction')),
    wps_field        varchar(20)  NOT NULL,
    quantity         numeric(19,6),
    rate             numeric(19,6),
    amount           numeric(19,4) NOT NULL CHECK (amount >= 0),
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    updated_at       timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, payslip_id, line_no),
    FOREIGN KEY (tenant_id, payslip_id) REFERENCES hr.payslip (tenant_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('hr', 'payslip_line');

CREATE TABLE hr.gratuity_accrual (
    id                 uuid PRIMARY KEY,
    tenant_id          uuid NOT NULL,
    employee_id        uuid NOT NULL,
    payroll_run_id     uuid NOT NULL,
    period_end         date NOT NULL,
    basis_wage         numeric(19,4) NOT NULL,
    days_per_year      numeric(9,4)  NOT NULL,
    service_days       int NOT NULL,
    liability          numeric(19,4) NOT NULL,
    previous_liability numeric(19,4) NOT NULL,
    delta              numeric(19,4) NOT NULL,
    status             varchar(10) NOT NULL DEFAULT 'posted' CHECK (status IN ('posted', 'reversed')),
    created_by         uuid NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_by         uuid,
    updated_at         timestamptz,
    UNIQUE (tenant_id, id),
    CHECK (delta = liability - previous_liability),
    FOREIGN KEY (tenant_id, employee_id) REFERENCES hr.employee (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, payroll_run_id) REFERENCES hr.payroll_run (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE UNIQUE INDEX ux_gratuity_accrual_period ON hr.gratuity_accrual (tenant_id, employee_id, period_end) WHERE status = 'posted';
SELECT core.enable_tenant_rls('hr', 'gratuity_accrual');

CREATE TABLE hr.wps_file (
    id                    uuid PRIMARY KEY,
    tenant_id             uuid NOT NULL,
    payroll_run_id        uuid NOT NULL,
    format_version        varchar(10)  NOT NULL,
    payer_bank_short_name varchar(10)  NOT NULL,
    file_name             varchar(200) NOT NULL,
    content               text NOT NULL,
    sha256                char(64) NOT NULL,
    total_salaries        numeric(19,4) NOT NULL,
    record_count          int NOT NULL,
    status                varchar(20) NOT NULL DEFAULT 'generated' CHECK (status IN ('generated', 'submitted', 'accepted', 'rejected')),
    created_by            uuid NOT NULL,
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_by            uuid,
    updated_at            timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, payroll_run_id) REFERENCES hr.payroll_run (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('hr', 'wps_file');
