-- Approval delegation: an approver hands their approvals to a colleague for a period (leave, travel). Decisions record on whose
-- behalf they were made.
CREATE TABLE core.approval_delegation (
    id                 uuid PRIMARY KEY,
    tenant_id          uuid NOT NULL,
    company_id         uuid NOT NULL,
    delegator_user_id  uuid NOT NULL,
    delegate_user_id   uuid NOT NULL,
    from_date          date NOT NULL,
    to_date            date NOT NULL,
    doc_type           varchar(50),
    reason             varchar(300),
    revoked_at         timestamptz,
    created_by         uuid NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_by         uuid,
    updated_at         timestamptz,
    UNIQUE (tenant_id, id),
    CHECK (to_date >= from_date),
    CHECK (delegator_user_id <> delegate_user_id),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, delegator_user_id) REFERENCES core.app_user (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, delegate_user_id) REFERENCES core.app_user (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_approval_delegation_delegate ON core.approval_delegation (tenant_id, delegate_user_id);
SELECT core.enable_tenant_rls('core', 'approval_delegation');

ALTER TABLE core.approval_step ADD COLUMN on_behalf_of uuid;
