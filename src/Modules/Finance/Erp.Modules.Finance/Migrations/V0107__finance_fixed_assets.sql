-- Fixed assets: categories (with their accounts), assets, monthly straight-line depreciation runs and disposals.
INSERT INTO fin.account (id, tenant_id, company_id, parent_id, code, name_en, name_ar, account_type, normal_balance, is_control, is_postable, is_active,
                         created_by, created_at)
SELECT gen_random_uuid(), p.tenant_id, p.company_id, p.id, '4230', 'Gain or Loss on Disposal of Assets', 'أرباح أو خسائر بيع الأصول', 'income', 'credit',
       false, true, true, '00000000-0000-0000-0000-000000000000', now()
  FROM fin.account p
 WHERE p.code = '4' AND p.parent_id IS NULL
   AND NOT EXISTS (SELECT 1 FROM fin.account a WHERE a.tenant_id = p.tenant_id AND a.company_id = p.company_id AND a.code = '4230');

CREATE TABLE fin.asset_category (
    id                          uuid PRIMARY KEY,
    tenant_id                   uuid NOT NULL,
    company_id                  uuid NOT NULL,
    code                        varchar(20) NOT NULL,
    name_en                     varchar(200) NOT NULL,
    name_ar                     varchar(200) NOT NULL,
    useful_life_months          int NOT NULL CHECK (useful_life_months > 0),
    asset_account_id            uuid NOT NULL,
    accumulated_account_id      uuid NOT NULL,
    expense_account_id          uuid NOT NULL,
    is_active                   boolean NOT NULL DEFAULT true,
    created_by                  uuid NOT NULL,
    created_at                  timestamptz NOT NULL DEFAULT now(),
    updated_by                  uuid,
    updated_at                  timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, code),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, asset_account_id) REFERENCES fin.account (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, accumulated_account_id) REFERENCES fin.account (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, expense_account_id) REFERENCES fin.account (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('fin', 'asset_category');

CREATE TABLE fin.fixed_asset (
    id                       uuid PRIMARY KEY,
    tenant_id                uuid NOT NULL,
    company_id               uuid NOT NULL,
    asset_no                 varchar(40) NOT NULL,
    name_en                  varchar(200) NOT NULL,
    name_ar                  varchar(200) NOT NULL,
    category_id              uuid NOT NULL,
    status                   varchar(20) NOT NULL CHECK (status IN ('draft', 'active', 'fully_depreciated', 'disposed')),
    acquisition_date         date NOT NULL,
    depreciation_start       date NOT NULL,
    cost                     numeric(19,4) NOT NULL CHECK (cost > 0),
    salvage_value            numeric(19,4) NOT NULL DEFAULT 0 CHECK (salvage_value >= 0),
    useful_life_months       int NOT NULL CHECK (useful_life_months > 0),
    accumulated_depreciation numeric(19,4) NOT NULL DEFAULT 0,
    opening_depreciation     numeric(19,4) NOT NULL DEFAULT 0,
    cost_center_id           uuid,
    location                 varchar(200),
    serial_no                varchar(100),
    capitalization_entry_id  uuid,
    disposal_date            date,
    disposal_proceeds        numeric(19,4),
    disposal_entry_id        uuid,
    notes                    varchar(1000),
    created_by               uuid NOT NULL,
    created_at               timestamptz NOT NULL DEFAULT now(),
    updated_by               uuid,
    updated_at               timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, asset_no),
    CHECK (salvage_value < cost),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, category_id) REFERENCES fin.asset_category (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, cost_center_id) REFERENCES fin.cost_center (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('fin', 'fixed_asset');

CREATE TABLE fin.depreciation_run (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    company_id       uuid NOT NULL,
    run_no           varchar(40) NOT NULL,
    year             int NOT NULL,
    month            int NOT NULL CHECK (month BETWEEN 1 AND 12),
    posting_date     date NOT NULL,
    total            numeric(19,4) NOT NULL,
    status           varchar(20) NOT NULL CHECK (status IN ('posted', 'reversed')),
    journal_entry_id uuid,
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    updated_at       timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, run_no),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE UNIQUE INDEX ux_depreciation_run_month ON fin.depreciation_run (tenant_id, company_id, year, month) WHERE status = 'posted';
SELECT core.enable_tenant_rls('fin', 'depreciation_run');

CREATE TABLE fin.depreciation_line (
    id                  uuid PRIMARY KEY,
    tenant_id           uuid NOT NULL,
    depreciation_run_id uuid NOT NULL,
    fixed_asset_id      uuid NOT NULL,
    amount              numeric(19,4) NOT NULL CHECK (amount > 0),
    created_by          uuid NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          uuid,
    updated_at          timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, depreciation_run_id) REFERENCES fin.depreciation_run (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, fixed_asset_id) REFERENCES fin.fixed_asset (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('fin', 'depreciation_line');

INSERT INTO core.role_permission (id, tenant_id, role_id, permission, created_by, created_at)
SELECT gen_random_uuid(), r.tenant_id, r.id, p.permission, '00000000-0000-0000-0000-000000000000', now()
  FROM core.role r
  JOIN (VALUES ('accountant', 'fin.assets:manage'), ('accountant', 'fin.assets:read'), ('general_manager', 'fin.assets:read'), ('auditor', 'fin.assets:read'))
       AS p(role, permission) ON p.role = r.code
 WHERE NOT EXISTS (SELECT 1 FROM core.role_permission x WHERE x.tenant_id = r.tenant_id AND x.role_id = r.id AND x.permission = p.permission);
