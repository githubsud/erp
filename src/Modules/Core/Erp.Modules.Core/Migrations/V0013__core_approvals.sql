-- Amount-based approval workflow: rules per document type (one row per step), requests with a snapshot of their steps.
CREATE TABLE core.approval_rule (
    id          uuid PRIMARY KEY,
    tenant_id   uuid NOT NULL,
    company_id  uuid NOT NULL,
    doc_type    varchar(40) NOT NULL,
    step_no     int NOT NULL CHECK (step_no BETWEEN 1 AND 9),
    min_amount  numeric(19,4) NOT NULL CHECK (min_amount >= 0),
    role_code   varchar(40) NOT NULL,
    created_by  uuid NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  uuid,
    updated_at  timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, doc_type, step_no),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('core', 'approval_rule');

CREATE TABLE core.approval_request (
    id           uuid PRIMARY KEY,
    tenant_id    uuid NOT NULL,
    company_id   uuid NOT NULL,
    doc_type     varchar(40) NOT NULL,
    doc_id       uuid NOT NULL,
    doc_no       varchar(40) NOT NULL,
    amount       numeric(19,4) NOT NULL,
    summary_en   varchar(300) NOT NULL,
    summary_ar   varchar(300) NOT NULL,
    status       varchar(20) NOT NULL CHECK (status IN ('pending', 'approved', 'rejected', 'withdrawn')),
    current_step int NOT NULL,
    requested_by uuid NOT NULL,
    requested_at timestamptz NOT NULL,
    decided_at   timestamptz,
    created_by   uuid NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_by   uuid,
    updated_at   timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('core', 'approval_request');
CREATE UNIQUE INDEX approval_request_one_pending ON core.approval_request (tenant_id, doc_type, doc_id) WHERE status = 'pending';
CREATE INDEX approval_request_doc ON core.approval_request (tenant_id, doc_type, doc_id);

CREATE TABLE core.approval_step (
    id          uuid PRIMARY KEY,
    tenant_id   uuid NOT NULL,
    request_id  uuid NOT NULL,
    step_no     int NOT NULL,
    role_code   varchar(40) NOT NULL,
    decision    varchar(20) CHECK (decision IN ('approved', 'rejected')),
    decided_by  uuid,
    decided_at  timestamptz,
    comment     varchar(500),
    created_by  uuid NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  uuid,
    updated_at  timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, request_id, step_no),
    FOREIGN KEY (tenant_id, request_id) REFERENCES core.approval_request (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('core', 'approval_step');

-- Segregation of duties: by default nobody approves a document they submitted.
ALTER TABLE core.company ADD COLUMN allow_self_approval boolean NOT NULL DEFAULT false;
