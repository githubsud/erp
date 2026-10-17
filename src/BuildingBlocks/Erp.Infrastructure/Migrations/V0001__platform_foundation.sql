-- Platform foundation: helper functions for tenant isolation and immutability, plus infrastructure tables.

CREATE SCHEMA IF NOT EXISTS core;

-- Current tenant from the session (set by the application on every connection). NULL when unset.
CREATE OR REPLACE FUNCTION core.current_tenant_id() RETURNS uuid
LANGUAGE sql STABLE AS $$
    SELECT NULLIF(current_setting('app.tenant_id', true), '')::uuid
$$;

-- Enables and forces row-level security with the standard tenant policy on a tenant-owned table.
CREATE OR REPLACE FUNCTION core.enable_tenant_rls(p_schema text, p_table text) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    EXECUTE format('ALTER TABLE %I.%I ENABLE ROW LEVEL SECURITY', p_schema, p_table);
    EXECUTE format('ALTER TABLE %I.%I FORCE ROW LEVEL SECURITY', p_schema, p_table);
    EXECUTE format('DROP POLICY IF EXISTS tenant_isolation ON %I.%I', p_schema, p_table);
    EXECUTE format(
        'CREATE POLICY tenant_isolation ON %I.%I USING (tenant_id = core.current_tenant_id()) WITH CHECK (tenant_id = core.current_tenant_id())',
        p_schema, p_table);
END $$;

CREATE OR REPLACE FUNCTION core.reject_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Rows of %.% are immutable; post a reversal instead', TG_TABLE_SCHEMA, TG_TABLE_NAME
        USING ERRCODE = 'P0001';
END $$;

-- Ledger-style tables: inserts only.
CREATE OR REPLACE FUNCTION core.make_immutable(p_schema text, p_table text) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    EXECUTE format('DROP TRIGGER IF EXISTS trg_immutable ON %I.%I', p_schema, p_table);
    EXECUTE format('CREATE TRIGGER trg_immutable BEFORE UPDATE OR DELETE ON %I.%I FOR EACH ROW EXECUTE FUNCTION core.reject_change()',
        p_schema, p_table);
END $$;

-- Gapless document numbering --------------------------------------------------------------------
CREATE TABLE core.number_sequence (
    id          uuid PRIMARY KEY,
    tenant_id   uuid NOT NULL,
    company_id  uuid NOT NULL,
    doc_type    varchar(40) NOT NULL,
    branch_id   uuid,
    fiscal_year int NOT NULL,
    prefix      varchar(20) NOT NULL,
    next_value  bigint NOT NULL CHECK (next_value > 0),
    padding     int NOT NULL DEFAULT 6,
    created_by  uuid NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  uuid,
    updated_at  timestamptz,
    UNIQUE (tenant_id, id),
    CONSTRAINT ux_number_sequence_scope UNIQUE NULLS NOT DISTINCT (tenant_id, company_id, doc_type, branch_id, fiscal_year)
);
SELECT core.enable_tenant_rls('core', 'number_sequence');

-- Audit trail (insert-only) ----------------------------------------------------------------------
CREATE TABLE core.audit_log (
    id          uuid PRIMARY KEY,
    tenant_id   uuid NOT NULL,
    entity_type varchar(100) NOT NULL,
    entity_id   uuid NOT NULL,
    action      varchar(20) NOT NULL CHECK (action IN ('create', 'update', 'delete')),
    changes     jsonb NOT NULL,
    user_id     uuid NOT NULL,
    occurred_at timestamptz NOT NULL,
    created_by  uuid NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  uuid,
    updated_at  timestamptz,
    UNIQUE (tenant_id, id)
);
CREATE INDEX ix_audit_log_entity ON core.audit_log (tenant_id, entity_type, entity_id, occurred_at);
SELECT core.enable_tenant_rls('core', 'audit_log');
SELECT core.make_immutable('core', 'audit_log');

-- Transactional outbox ---------------------------------------------------------------------------
CREATE TABLE core.outbox_message (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    type             varchar(100) NOT NULL,
    payload          jsonb NOT NULL,
    available_at     timestamptz NOT NULL DEFAULT now(),
    attempts         int NOT NULL DEFAULT 0,
    last_error       text,
    sent_at          timestamptz,
    dead_lettered_at timestamptz,
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    updated_at       timestamptz,
    UNIQUE (tenant_id, id)
);
CREATE INDEX ix_outbox_pending ON core.outbox_message (available_at) WHERE sent_at IS NULL AND dead_lettered_at IS NULL;
SELECT core.enable_tenant_rls('core', 'outbox_message');
-- The dispatcher role sees all tenants' messages (and nothing else).
CREATE POLICY worker_access ON core.outbox_message TO erp_worker USING (true) WITH CHECK (true);
