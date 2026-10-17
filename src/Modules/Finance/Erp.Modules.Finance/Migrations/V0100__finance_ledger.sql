-- Finance: chart of accounts, fiscal calendar, posting rules and the immutable general ledger.
-- All foreign keys are DEFERRABLE INITIALLY DEFERRED: checked at commit, so insert order inside a
-- posting transaction does not matter.

CREATE SCHEMA IF NOT EXISTS fin;

CREATE TABLE fin.account (
    id                uuid PRIMARY KEY,
    tenant_id         uuid NOT NULL,
    company_id        uuid NOT NULL,
    parent_id         uuid,
    code              varchar(30)  NOT NULL,
    name_en           varchar(200) NOT NULL,
    name_ar           varchar(200) NOT NULL,
    account_type      varchar(20)  NOT NULL CHECK (account_type IN ('asset', 'liability', 'equity', 'income', 'expense')),
    normal_balance    varchar(10)  NOT NULL CHECK (normal_balance IN ('debit', 'credit')),
    is_control        boolean NOT NULL DEFAULT false,
    control_subledger varchar(20) CHECK (control_subledger IN ('customer', 'supplier', 'employee')),
    subtype           varchar(40),
    is_postable       boolean NOT NULL DEFAULT true,
    is_active         boolean NOT NULL DEFAULT true,
    created_by        uuid NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_by        uuid,
    updated_at        timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, code),
    CHECK (NOT is_control OR control_subledger IS NOT NULL),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, parent_id) REFERENCES fin.account (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('fin', 'account');

CREATE TABLE fin.fiscal_year (
    id         uuid PRIMARY KEY,
    tenant_id  uuid NOT NULL,
    company_id uuid NOT NULL,
    name       varchar(40) NOT NULL,
    start_date date NOT NULL,
    end_date   date NOT NULL,
    status     varchar(10) NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'closed')),
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_by uuid,
    updated_at timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, start_date),
    CHECK (end_date > start_date),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('fin', 'fiscal_year');

CREATE TABLE fin.fiscal_period (
    id             uuid PRIMARY KEY,
    tenant_id      uuid NOT NULL,
    company_id     uuid NOT NULL,
    fiscal_year_id uuid NOT NULL,
    period_no      int  NOT NULL CHECK (period_no BETWEEN 1 AND 13),
    start_date     date NOT NULL,
    end_date       date NOT NULL,
    status         varchar(10) NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'closed')),
    created_by     uuid NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_by     uuid,
    updated_at     timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, start_date),
    CHECK (end_date >= start_date),
    FOREIGN KEY (tenant_id, fiscal_year_id) REFERENCES fin.fiscal_year (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('fin', 'fiscal_period');

CREATE TABLE fin.cost_center (
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
    UNIQUE (tenant_id, company_id, code),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('fin', 'cost_center');

CREATE TABLE fin.posting_rule (
    id           uuid PRIMARY KEY,
    tenant_id    uuid NOT NULL,
    company_id   uuid NOT NULL,
    account_role varchar(60) NOT NULL,
    role_key     uuid,
    account_id   uuid NOT NULL,
    created_by   uuid NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_by   uuid,
    updated_at   timestamptz,
    UNIQUE (tenant_id, id),
    CONSTRAINT ux_posting_rule UNIQUE NULLS NOT DISTINCT (tenant_id, company_id, account_role, role_key),
    FOREIGN KEY (tenant_id, account_id) REFERENCES fin.account (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('fin', 'posting_rule');

-- General ledger ---------------------------------------------------------------------------------
CREATE TABLE fin.journal_entry (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    company_id       uuid NOT NULL,
    branch_id        uuid,
    entry_no         varchar(40) NOT NULL,
    posting_date     date NOT NULL,
    fiscal_period_id uuid NOT NULL,
    source_type      varchar(40) NOT NULL,
    source_id        uuid NOT NULL,
    source_no        varchar(40) NOT NULL,
    reversal_of_id   uuid,
    memo_en          varchar(500),
    memo_ar          varchar(500),
    posted_by        uuid NOT NULL,
    posted_at        timestamptz NOT NULL,
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    updated_at       timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, entry_no),
    UNIQUE (tenant_id, reversal_of_id),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, fiscal_period_id) REFERENCES fin.fiscal_period (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, reversal_of_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
-- A source document has at most one original entry (database-level idempotency for posting).
CREATE UNIQUE INDEX ux_journal_entry_source ON fin.journal_entry (tenant_id, source_type, source_id) WHERE reversal_of_id IS NULL;
CREATE INDEX ix_journal_entry_date ON fin.journal_entry (tenant_id, company_id, posting_date);
SELECT core.enable_tenant_rls('fin', 'journal_entry');
SELECT core.make_immutable('fin', 'journal_entry');

CREATE TABLE fin.journal_line (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    journal_entry_id uuid NOT NULL,
    line_no          int  NOT NULL,
    account_id       uuid NOT NULL,
    party_type       varchar(20) CHECK (party_type IN ('customer', 'supplier', 'employee')),
    party_id         uuid,
    cost_center_id   uuid,
    project_id       uuid,
    currency         char(3) NOT NULL,
    fx_rate          numeric(19,8) NOT NULL CHECK (fx_rate > 0),
    debit_txn        numeric(19,4) NOT NULL DEFAULT 0 CHECK (debit_txn >= 0),
    credit_txn       numeric(19,4) NOT NULL DEFAULT 0 CHECK (credit_txn >= 0),
    debit_base       numeric(19,4) NOT NULL DEFAULT 0 CHECK (debit_base >= 0),
    credit_base      numeric(19,4) NOT NULL DEFAULT 0 CHECK (credit_base >= 0),
    description      varchar(500),
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    updated_at       timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, journal_entry_id, line_no),
    CHECK ((debit_base = 0) <> (credit_base = 0)),
    CHECK ((party_type IS NULL) = (party_id IS NULL)),
    FOREIGN KEY (tenant_id, journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, account_id) REFERENCES fin.account (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, cost_center_id) REFERENCES fin.cost_center (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_journal_line_account ON fin.journal_line (tenant_id, account_id);
CREATE INDEX ix_journal_line_party ON fin.journal_line (tenant_id, party_type, party_id) WHERE party_id IS NOT NULL;
SELECT core.enable_tenant_rls('fin', 'journal_line');
SELECT core.make_immutable('fin', 'journal_line');

-- Entries must be balanced and have at least two lines; checked at commit.
CREATE FUNCTION fin.assert_entry_balanced(p_tenant uuid, p_entry uuid) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    v_debit  numeric;
    v_credit numeric;
    v_lines  int;
BEGIN
    SELECT COALESCE(SUM(debit_base), 0), COALESCE(SUM(credit_base), 0), COUNT(*)
      INTO v_debit, v_credit, v_lines
      FROM fin.journal_line
     WHERE tenant_id = p_tenant AND journal_entry_id = p_entry;

    IF v_lines < 2 THEN
        RAISE EXCEPTION 'Journal entry % has fewer than two lines', p_entry USING ERRCODE = 'P0001';
    END IF;
    IF v_debit <> v_credit THEN
        RAISE EXCEPTION 'Journal entry % is not balanced (debit %, credit %)', p_entry, v_debit, v_credit USING ERRCODE = 'P0001';
    END IF;
END $$;

CREATE FUNCTION fin.trg_entry_balanced() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_TABLE_NAME = 'journal_entry' THEN
        PERFORM fin.assert_entry_balanced(NEW.tenant_id, NEW.id);
    ELSE
        PERFORM fin.assert_entry_balanced(NEW.tenant_id, NEW.journal_entry_id);
    END IF;
    RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER trg_journal_entry_balanced AFTER INSERT ON fin.journal_entry
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.trg_entry_balanced();
CREATE CONSTRAINT TRIGGER trg_journal_line_balanced AFTER INSERT ON fin.journal_line
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.trg_entry_balanced();

-- Entries may only be posted into an open period that contains the posting date.
CREATE FUNCTION fin.trg_entry_period_open() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_period fin.fiscal_period%ROWTYPE;
BEGIN
    SELECT * INTO v_period FROM fin.fiscal_period WHERE tenant_id = NEW.tenant_id AND id = NEW.fiscal_period_id;
    IF NOT FOUND OR v_period.status <> 'open' OR NEW.posting_date NOT BETWEEN v_period.start_date AND v_period.end_date THEN
        RAISE EXCEPTION 'Posting date % is not in an open fiscal period', NEW.posting_date USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER trg_journal_entry_period BEFORE INSERT ON fin.journal_entry
    FOR EACH ROW EXECUTE FUNCTION fin.trg_entry_period_open();

-- Manual journal vouchers -------------------------------------------------------------------------
CREATE TABLE fin.journal_voucher (
    id                uuid PRIMARY KEY,
    tenant_id         uuid NOT NULL,
    company_id        uuid NOT NULL,
    branch_id         uuid,
    voucher_no        varchar(40) NOT NULL,
    voucher_date      date NOT NULL,
    memo_en           varchar(500),
    memo_ar           varchar(500),
    status            varchar(10) NOT NULL CHECK (status IN ('posted', 'reversed')),
    journal_entry_id  uuid NOT NULL,
    reversal_entry_id uuid,
    created_by        uuid NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_by        uuid,
    updated_at        timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, voucher_no),
    FOREIGN KEY (tenant_id, journal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, reversal_entry_id) REFERENCES fin.journal_entry (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('fin', 'journal_voucher');
