-- Two-factor authentication (TOTP) and rotating refresh tokens.

ALTER TABLE core.app_user
    ADD COLUMN mfa_enabled            boolean NOT NULL DEFAULT false,
    ADD COLUMN mfa_secret_encrypted   text,
    ADD COLUMN mfa_pending_encrypted  text,
    ADD COLUMN mfa_last_step          bigint NOT NULL DEFAULT 0;

-- Only a SHA-256 hash of each refresh token is stored. A rotated (replaced) token that is used again signals
-- theft: every token of that user is then revoked.
CREATE TABLE core.refresh_token (
    id          uuid PRIMARY KEY,
    tenant_id   uuid NOT NULL,
    user_id     uuid NOT NULL,
    token_hash  char(64) NOT NULL,
    company_id  uuid,
    expires_at  timestamptz NOT NULL,
    revoked_at  timestamptz,
    replaced_by uuid,
    created_by  uuid NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  uuid,
    updated_at  timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (token_hash),
    FOREIGN KEY (tenant_id, user_id) REFERENCES core.app_user (tenant_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_refresh_token_user ON core.refresh_token (tenant_id, user_id) WHERE revoked_at IS NULL;
SELECT core.enable_tenant_rls('core', 'refresh_token');
