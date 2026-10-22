-- Till limits: prices come from the item list and a cashier's discount is capped. Going over the cap needs the
-- "sal.pos:override" permission or a manager's approval PIN typed on the cashier's device, recorded on the sale.

ALTER TABLE sal.settings
    ADD COLUMN pos_max_discount_percent numeric(19,6) NOT NULL DEFAULT 10 CHECK (pos_max_discount_percent BETWEEN 0 AND 100);

-- A manager's approval PIN (hashed). Five wrong tries lock it for a while.
CREATE TABLE sal.pos_approver (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    user_id          uuid NOT NULL,
    pin_hash         varchar(200) NOT NULL,
    failed_attempts  int NOT NULL DEFAULT 0,
    locked_until     timestamptz,
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    updated_at       timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, user_id)
);
SELECT core.enable_tenant_rls('sal', 'pos_approver');

-- One approval covers one sale of its shift, up to the discount the manager saw.
CREATE TABLE sal.pos_approval (
    id                uuid PRIMARY KEY,
    tenant_id         uuid NOT NULL,
    shift_id          uuid NOT NULL,
    approver_user_id  uuid NOT NULL,
    max_discount      numeric(19,4) NOT NULL CHECK (max_discount >= 0),
    created_by        uuid NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_by        uuid,
    updated_at        timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, shift_id) REFERENCES sal.pos_shift (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'pos_approval');

ALTER TABLE sal.pos_sale
    ADD COLUMN approval_id uuid,
    ADD COLUMN approved_by uuid,
    ADD CONSTRAINT ux_pos_sale_approval UNIQUE (tenant_id, approval_id),
    ADD CONSTRAINT fk_pos_sale_approval FOREIGN KEY (tenant_id, approval_id) REFERENCES sal.pos_approval (tenant_id, id) DEFERRABLE INITIALLY DEFERRED;

INSERT INTO core.role_permission (id, tenant_id, role_id, permission, created_by, created_at)
SELECT gen_random_uuid(), r.tenant_id, r.id, 'sal.pos:override', '00000000-0000-0000-0000-000000000000', now()
  FROM core.role r
 WHERE r.code = 'general_manager'
   AND NOT EXISTS (SELECT 1 FROM core.role_permission x WHERE x.tenant_id = r.tenant_id AND x.role_id = r.id AND x.permission = 'sal.pos:override');
