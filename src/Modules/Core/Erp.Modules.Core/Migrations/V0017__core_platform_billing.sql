-- Platform back-office (the ERP vendor, not a tenant): platform administrators, plans, each customer's subscription with the
-- modules it licenses, subscription invoices and the payments received. Platform tables have no tenant_id and no RLS.

ALTER TABLE core.tenant
    ADD COLUMN business_type  varchar(30),
    ADD COLUMN contact_name   varchar(200),
    ADD COLUMN contact_phone  varchar(40),
    ADD COLUMN contact_email  varchar(200),
    ADD COLUMN area           varchar(100),
    ADD COLUMN notes          varchar(1000);

CREATE TABLE core.platform_admin (
    id             uuid PRIMARY KEY,
    email          varchar(200) NOT NULL UNIQUE CHECK (email = lower(email)),
    display_name   varchar(200) NOT NULL,
    password_hash  varchar(500) NOT NULL,
    is_active      boolean NOT NULL DEFAULT true,
    last_login_at  timestamptz,
    failed_login_count int NOT NULL DEFAULT 0,
    lockout_until  timestamptz,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz
);

CREATE TABLE core.plan (
    id             uuid PRIMARY KEY,
    code           varchar(30) NOT NULL UNIQUE,
    name_en        varchar(200) NOT NULL,
    name_ar        varchar(200) NOT NULL,
    monthly_price  numeric(19,4) NOT NULL CHECK (monthly_price >= 0),
    annual_price   numeric(19,4) NOT NULL CHECK (annual_price >= 0),
    modules        varchar(1000) NOT NULL,
    max_users      int,
    max_employees  int,
    is_active      boolean NOT NULL DEFAULT true,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz
);

CREATE TABLE core.subscription (
    id                  uuid PRIMARY KEY,
    tenant_id           uuid NOT NULL UNIQUE REFERENCES core.tenant (id),
    plan_id             uuid NOT NULL REFERENCES core.plan (id),
    modules             varchar(1000) NOT NULL,
    billing_cycle       varchar(10) NOT NULL CHECK (billing_cycle IN ('monthly', 'annual')),
    price               numeric(19,4) NOT NULL CHECK (price >= 0),
    status              varchar(20) NOT NULL CHECK (status IN ('trial', 'active', 'past_due', 'suspended', 'cancelled')),
    start_date          date NOT NULL,
    trial_ends_on       date,
    next_invoice_on     date NOT NULL,
    auto_suspend        boolean NOT NULL DEFAULT true,
    grace_days          int NOT NULL DEFAULT 14 CHECK (grace_days BETWEEN 0 AND 180),
    max_users           int,
    max_employees       int,
    notes               varchar(1000),
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz
);

CREATE TABLE core.subscription_invoice (
    id            uuid PRIMARY KEY,
    tenant_id     uuid NOT NULL REFERENCES core.tenant (id),
    invoice_no    varchar(30) NOT NULL UNIQUE,
    period_start  date NOT NULL,
    period_end    date NOT NULL,
    description   varchar(300) NOT NULL,
    amount        numeric(19,4) NOT NULL CHECK (amount >= 0),
    paid_amount   numeric(19,4) NOT NULL DEFAULT 0 CHECK (paid_amount >= 0),
    issue_date    date NOT NULL,
    due_date      date NOT NULL,
    status        varchar(20) NOT NULL CHECK (status IN ('open', 'paid', 'void')),
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz,
    UNIQUE (tenant_id, period_start, period_end)
);
CREATE INDEX ix_subscription_invoice_tenant ON core.subscription_invoice (tenant_id, due_date);

CREATE TABLE core.subscription_payment (
    id            uuid PRIMARY KEY,
    tenant_id     uuid NOT NULL REFERENCES core.tenant (id),
    invoice_id    uuid REFERENCES core.subscription_invoice (id),
    paid_on       date NOT NULL,
    amount        numeric(19,4) NOT NULL CHECK (amount > 0),
    method        varchar(20) NOT NULL CHECK (method IN ('bank_transfer', 'cash', 'card', 'cheque', 'fawran', 'other')),
    reference     varchar(100),
    recorded_by   uuid NOT NULL REFERENCES core.platform_admin (id),
    notes         varchar(500),
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz
);
CREATE INDEX ix_subscription_payment_tenant ON core.subscription_payment (tenant_id, paid_on);

CREATE SEQUENCE core.subscription_invoice_no START 1001;

-- Default plans (prices can be changed in the back-office). Module codes match ModuleCodes.
INSERT INTO core.plan (id, code, name_en, name_ar, monthly_price, annual_price, modules, max_users, max_employees)
VALUES
    (gen_random_uuid(), 'PAYROLL', 'Payroll', 'الرواتب', 99, 990, 'hr,ess', 3, 10),
    (gen_random_uuid(), 'SHOP', 'Shop', 'المتجر', 199, 1990, 'hr,ess,inventory,sales,pos,tailoring,salon', 5, 15),
    (gen_random_uuid(), 'BUSINESS', 'Business', 'الأعمال', 399, 3990,
     'hr,ess,inventory,purchasing,sales,pos,crm,projects,subcontracts,assets,budgets,tailoring,salon', 15, 50),
    (gen_random_uuid(), 'ENTERPRISE', 'Enterprise', 'المؤسسات', 799, 7990,
     'hr,ess,inventory,purchasing,sales,pos,crm,projects,subcontracts,assets,budgets,tailoring,salon', NULL, NULL);

-- Existing tenants (created before billing) keep everything, as an active Enterprise subscription with no charge.
INSERT INTO core.subscription (id, tenant_id, plan_id, modules, billing_cycle, price, status, start_date, next_invoice_on, auto_suspend)
SELECT gen_random_uuid(), t.id, p.id, p.modules, 'monthly', 0, 'active', current_date, (date_trunc('month', current_date) + interval '1 month')::date, false
  FROM core.tenant t CROSS JOIN core.plan p
 WHERE p.code = 'ENTERPRISE'
   AND NOT EXISTS (SELECT 1 FROM core.subscription s WHERE s.tenant_id = t.id);
