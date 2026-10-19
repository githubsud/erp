-- Budgets per fiscal year: an amount per account (optionally per cost centre) for each month, compared with actuals.
CREATE TABLE fin.budget (
    id             uuid PRIMARY KEY,
    tenant_id      uuid NOT NULL,
    company_id     uuid NOT NULL,
    fiscal_year_id uuid NOT NULL,
    name           varchar(200) NOT NULL,
    status         varchar(20) NOT NULL CHECK (status IN ('draft', 'approved')),
    notes          varchar(1000),
    approved_by    uuid,
    approved_at    timestamptz,
    created_by     uuid NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_by     uuid,
    updated_at     timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, fiscal_year_id, name),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, fiscal_year_id) REFERENCES fin.fiscal_year (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('fin', 'budget');

CREATE TABLE fin.budget_line (
    id             uuid PRIMARY KEY,
    tenant_id      uuid NOT NULL,
    budget_id      uuid NOT NULL,
    account_id     uuid NOT NULL,
    cost_center_id uuid,
    period_no      int NOT NULL CHECK (period_no BETWEEN 1 AND 12),
    amount         numeric(19,4) NOT NULL,
    created_by     uuid NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_by     uuid,
    updated_at     timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, budget_id) REFERENCES fin.budget (tenant_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, account_id) REFERENCES fin.account (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, cost_center_id) REFERENCES fin.cost_center (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE UNIQUE INDEX ux_budget_line ON fin.budget_line (tenant_id, budget_id, account_id, COALESCE(cost_center_id, '00000000-0000-0000-0000-000000000000'), period_no);
SELECT core.enable_tenant_rls('fin', 'budget_line');

INSERT INTO core.role_permission (id, tenant_id, role_id, permission, created_by, created_at)
SELECT gen_random_uuid(), r.tenant_id, r.id, p.permission, '00000000-0000-0000-0000-000000000000', now()
  FROM core.role r
  JOIN (VALUES ('accountant', 'fin.budgets:manage'), ('general_manager', 'fin.budgets:manage')) AS p(role, permission) ON p.role = r.code
 WHERE NOT EXISTS (SELECT 1 FROM core.role_permission x WHERE x.tenant_id = r.tenant_id AND x.role_id = r.id AND x.permission = p.permission);
