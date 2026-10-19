-- CRM: leads, opportunities (pipeline) and follow-up activities; sales commission rates per salesperson.
CREATE TABLE sal.lead (
    id            uuid PRIMARY KEY,
    tenant_id     uuid NOT NULL,
    company_id    uuid NOT NULL,
    lead_no       varchar(40) NOT NULL,
    company_name  varchar(200) NOT NULL,
    contact_name  varchar(200),
    phone         varchar(40),
    email         varchar(200),
    source        varchar(20) NOT NULL CHECK (source IN ('referral', 'website', 'walk_in', 'phone', 'exhibition', 'social_media', 'other')),
    status        varchar(20) NOT NULL CHECK (status IN ('new', 'contacted', 'qualified', 'lost', 'converted')),
    owner_user_id uuid,
    customer_id   uuid,
    notes         varchar(2000),
    lost_reason   varchar(500),
    created_by    uuid NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_by    uuid,
    updated_at    timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, lead_no),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, customer_id) REFERENCES sal.customer (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'lead');

CREATE TABLE sal.opportunity (
    id                  uuid PRIMARY KEY,
    tenant_id           uuid NOT NULL,
    company_id          uuid NOT NULL,
    opportunity_no      varchar(40) NOT NULL,
    title               varchar(300) NOT NULL,
    lead_id             uuid,
    customer_id         uuid,
    stage               varchar(20) NOT NULL CHECK (stage IN ('prospecting', 'qualification', 'proposal', 'negotiation', 'won', 'lost')),
    expected_value      numeric(19,4) NOT NULL DEFAULT 0 CHECK (expected_value >= 0),
    probability         int NOT NULL DEFAULT 10 CHECK (probability BETWEEN 0 AND 100),
    expected_close_date date,
    owner_user_id       uuid,
    quotation_id        uuid,
    lost_reason         varchar(500),
    closed_at           date,
    notes               varchar(2000),
    created_by          uuid NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          uuid,
    updated_at          timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, opportunity_no),
    CHECK (lead_id IS NOT NULL OR customer_id IS NOT NULL),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, lead_id) REFERENCES sal.lead (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, customer_id) REFERENCES sal.customer (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, quotation_id) REFERENCES sal.quotation (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'opportunity');

CREATE TABLE sal.crm_activity (
    id             uuid PRIMARY KEY,
    tenant_id      uuid NOT NULL,
    company_id     uuid NOT NULL,
    lead_id        uuid,
    opportunity_id uuid,
    customer_id    uuid,
    kind           varchar(20) NOT NULL CHECK (kind IN ('call', 'meeting', 'email', 'whatsapp', 'visit', 'task')),
    subject        varchar(300) NOT NULL,
    due_date       date NOT NULL,
    done           boolean NOT NULL DEFAULT false,
    done_at        timestamptz,
    owner_user_id  uuid,
    notes          varchar(2000),
    created_by     uuid NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_by     uuid,
    updated_at     timestamptz,
    UNIQUE (tenant_id, id),
    CHECK (lead_id IS NOT NULL OR opportunity_id IS NOT NULL OR customer_id IS NOT NULL),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, lead_id) REFERENCES sal.lead (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, opportunity_id) REFERENCES sal.opportunity (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, customer_id) REFERENCES sal.customer (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'crm_activity');

CREATE TABLE sal.commission_rate (
    id         uuid PRIMARY KEY,
    tenant_id  uuid NOT NULL,
    company_id uuid NOT NULL,
    user_id    uuid NOT NULL,
    basis      varchar(20) NOT NULL CHECK (basis IN ('revenue', 'gross_margin', 'collections')),
    percent    numeric(19,6) NOT NULL CHECK (percent >= 0 AND percent <= 100),
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_by uuid,
    updated_at timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, user_id),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'commission_rate');

INSERT INTO core.role_permission (id, tenant_id, role_id, permission, created_by, created_at)
SELECT gen_random_uuid(), r.tenant_id, r.id, p.permission, '00000000-0000-0000-0000-000000000000', now()
  FROM core.role r
  JOIN (VALUES ('salesperson', 'sal.crm:use'), ('general_manager', 'sal.crm:use'), ('general_manager', 'sal.commissions:manage'),
               ('accountant', 'sal.commissions:manage')) AS p(role, permission) ON p.role = r.code
 WHERE NOT EXISTS (SELECT 1 FROM core.role_permission x WHERE x.tenant_id = r.tenant_id AND x.role_id = r.id AND x.permission = p.permission);
