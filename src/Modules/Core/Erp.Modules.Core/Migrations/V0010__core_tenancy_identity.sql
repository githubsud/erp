-- Tenants, companies, branches, users and roles.

-- Platform table: no tenant_id; read at login to resolve the tenant code.
CREATE TABLE core.tenant (
    id             uuid PRIMARY KEY,
    code           varchar(40)  NOT NULL UNIQUE CHECK (code ~ '^[a-z0-9][a-z0-9-]{2,39}$'),
    name_en        varchar(200) NOT NULL,
    name_ar        varchar(200) NOT NULL,
    status         varchar(20)  NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'suspended', 'closed')),
    base_currency  char(3)      NOT NULL DEFAULT 'QAR',
    default_locale varchar(10)  NOT NULL DEFAULT 'ar-QA',
    time_zone      varchar(50)  NOT NULL DEFAULT 'Asia/Qatar',
    created_at     timestamptz  NOT NULL DEFAULT now(),
    updated_at     timestamptz
);

CREATE TABLE core.company (
    id                    uuid PRIMARY KEY,
    tenant_id             uuid NOT NULL REFERENCES core.tenant (id) DEFERRABLE INITIALLY DEFERRED,
    name_en               varchar(200) NOT NULL,
    name_ar               varchar(200) NOT NULL,
    cr_no                 varchar(40),
    establishment_id      varchar(40),
    tin                   varchar(40),
    foreign_ownership_pct numeric(5,2) NOT NULL DEFAULT 0 CHECK (foreign_ownership_pct BETWEEN 0 AND 100),
    address_en            varchar(500),
    address_ar            varchar(500),
    phone                 varchar(40),
    email                 varchar(200),
    base_currency         char(3) NOT NULL DEFAULT 'QAR',
    created_by            uuid NOT NULL,
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_by            uuid,
    updated_at            timestamptz,
    UNIQUE (tenant_id, id)
);
SELECT core.enable_tenant_rls('core', 'company');

CREATE TABLE core.branch (
    id         uuid PRIMARY KEY,
    tenant_id  uuid NOT NULL,
    company_id uuid NOT NULL,
    code       varchar(20)  NOT NULL,
    name_en    varchar(200) NOT NULL,
    name_ar    varchar(200) NOT NULL,
    is_active  boolean NOT NULL DEFAULT true,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_by uuid,
    updated_at timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, code),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('core', 'branch');

CREATE TABLE core.app_user (
    id                 uuid PRIMARY KEY,
    tenant_id          uuid NOT NULL,
    email              varchar(200) NOT NULL CHECK (email = lower(email)),
    display_name_en    varchar(200) NOT NULL,
    display_name_ar    varchar(200) NOT NULL,
    password_hash      varchar(500) NOT NULL,
    preferred_language varchar(5)   NOT NULL DEFAULT 'ar' CHECK (preferred_language IN ('ar', 'en')),
    is_active          boolean NOT NULL DEFAULT true,
    default_company_id uuid,
    failed_login_count int NOT NULL DEFAULT 0,
    lockout_until      timestamptz,
    last_login_at      timestamptz,
    created_by         uuid NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_by         uuid,
    updated_at         timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, email),
    FOREIGN KEY (tenant_id, default_company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('core', 'app_user');

CREATE TABLE core.role (
    id         uuid PRIMARY KEY,
    tenant_id  uuid NOT NULL,
    code       varchar(60)  NOT NULL,
    name_en    varchar(200) NOT NULL,
    name_ar    varchar(200) NOT NULL,
    is_system  boolean NOT NULL DEFAULT false,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_by uuid,
    updated_at timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, code)
);
SELECT core.enable_tenant_rls('core', 'role');

CREATE TABLE core.role_permission (
    id         uuid PRIMARY KEY,
    tenant_id  uuid NOT NULL,
    role_id    uuid NOT NULL,
    permission varchar(100) NOT NULL,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_by uuid,
    updated_at timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, role_id, permission),
    FOREIGN KEY (tenant_id, role_id) REFERENCES core.role (tenant_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('core', 'role_permission');

CREATE TABLE core.user_role (
    id         uuid PRIMARY KEY,
    tenant_id  uuid NOT NULL,
    user_id    uuid NOT NULL,
    role_id    uuid NOT NULL,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_by uuid,
    updated_at timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, user_id, role_id),
    FOREIGN KEY (tenant_id, user_id) REFERENCES core.app_user (tenant_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, role_id) REFERENCES core.role (tenant_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('core', 'user_role');
