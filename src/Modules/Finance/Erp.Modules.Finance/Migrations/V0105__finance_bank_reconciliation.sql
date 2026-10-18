-- Bank reconciliation: imported bank statements, their lines, and matches between statement lines and ledger lines.
-- Matches live in their own table so the insert-only ledger is never modified.
CREATE TABLE fin.bank_statement (
    id              uuid PRIMARY KEY,
    tenant_id       uuid NOT NULL,
    company_id      uuid NOT NULL,
    account_id      uuid NOT NULL,
    statement_no    varchar(40) NOT NULL,
    date_from       date NOT NULL,
    date_to         date NOT NULL,
    opening_balance numeric(19,4),
    closing_balance numeric(19,4),
    file_name       varchar(260),
    status          varchar(20) NOT NULL CHECK (status IN ('open', 'reconciled')),
    created_by      uuid NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_by      uuid,
    updated_at      timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, statement_no),
    CHECK (date_to >= date_from),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, account_id) REFERENCES fin.account (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_bank_statement_account ON fin.bank_statement (tenant_id, account_id, date_to);
SELECT core.enable_tenant_rls('fin', 'bank_statement');

CREATE TABLE fin.bank_statement_line (
    id                uuid PRIMARY KEY,
    tenant_id         uuid NOT NULL,
    bank_statement_id uuid NOT NULL,
    line_no           int NOT NULL,
    txn_date          date NOT NULL,
    description       varchar(500),
    reference         varchar(100),
    amount            numeric(19,4) NOT NULL CHECK (amount <> 0),
    status            varchar(20) NOT NULL CHECK (status IN ('unmatched', 'matched', 'ignored')),
    journal_entry_id  uuid,
    created_by        uuid NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_by        uuid,
    updated_at        timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, bank_statement_id, line_no),
    FOREIGN KEY (tenant_id, bank_statement_id) REFERENCES fin.bank_statement (tenant_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('fin', 'bank_statement_line');

CREATE TABLE fin.bank_match (
    id                     uuid PRIMARY KEY,
    tenant_id              uuid NOT NULL,
    bank_statement_line_id uuid NOT NULL,
    journal_line_id        uuid NOT NULL,
    amount                 numeric(19,4) NOT NULL,
    created_by             uuid NOT NULL,
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_by             uuid,
    updated_at             timestamptz,
    UNIQUE (tenant_id, id),
    -- A ledger line is cleared by one statement line at most.
    UNIQUE (tenant_id, journal_line_id),
    FOREIGN KEY (tenant_id, bank_statement_line_id) REFERENCES fin.bank_statement_line (tenant_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, journal_line_id) REFERENCES fin.journal_line (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_bank_match_line ON fin.bank_match (tenant_id, bank_statement_line_id);
SELECT core.enable_tenant_rls('fin', 'bank_match');

-- Accountants of existing companies may reconcile (new companies get it at provisioning).
INSERT INTO core.role_permission (id, tenant_id, role_id, permission, created_by, created_at)
SELECT gen_random_uuid(), r.tenant_id, r.id, 'fin.bank:reconcile', '00000000-0000-0000-0000-000000000000', now()
  FROM core.role r
 WHERE r.code = 'accountant'
   AND NOT EXISTS (SELECT 1 FROM core.role_permission x WHERE x.tenant_id = r.tenant_id AND x.role_id = r.id AND x.permission = 'fin.bank:reconcile');
