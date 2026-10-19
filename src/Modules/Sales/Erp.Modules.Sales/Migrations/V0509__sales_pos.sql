-- Point of sale: registers, cashier shifts, sales (with split payments) and refunds. Sales made offline are synced later and are
-- idempotent by the client-generated id.
CREATE TABLE sal.pos_register (
    id                      uuid PRIMARY KEY,
    tenant_id               uuid NOT NULL,
    company_id              uuid NOT NULL,
    code                    varchar(20) NOT NULL,
    name_en                 varchar(200) NOT NULL,
    name_ar                 varchar(200) NOT NULL,
    warehouse_id            uuid NOT NULL,
    cash_account_id         uuid NOT NULL,
    card_account_id         uuid NOT NULL,
    is_active               boolean NOT NULL DEFAULT true,
    created_by              uuid NOT NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_by              uuid,
    updated_at              timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, code),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, cash_account_id) REFERENCES fin.account (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, card_account_id) REFERENCES fin.account (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'pos_register');

CREATE TABLE sal.pos_shift (
    id                uuid PRIMARY KEY,
    tenant_id         uuid NOT NULL,
    register_id       uuid NOT NULL,
    shift_no          varchar(40) NOT NULL,
    cashier_user_id   uuid NOT NULL,
    opened_at         timestamptz NOT NULL,
    opening_float     numeric(19,4) NOT NULL CHECK (opening_float >= 0),
    status            varchar(20) NOT NULL CHECK (status IN ('open', 'closed')),
    closed_at         timestamptz,
    business_date     date NOT NULL,
    expected_cash     numeric(19,4),
    counted_cash      numeric(19,4),
    difference        numeric(19,4),
    closing_entry_id  uuid,
    notes             varchar(500),
    created_by        uuid NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_by        uuid,
    updated_at        timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, shift_no),
    FOREIGN KEY (tenant_id, register_id) REFERENCES sal.pos_register (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE UNIQUE INDEX ux_pos_shift_open ON sal.pos_shift (tenant_id, register_id) WHERE status = 'open';
SELECT core.enable_tenant_rls('sal', 'pos_shift');

CREATE TABLE sal.pos_sale (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    shift_id         uuid NOT NULL,
    receipt_no       varchar(40) NOT NULL,
    client_id        uuid NOT NULL,
    sold_at          timestamptz NOT NULL,
    customer_name    varchar(200),
    customer_phone   varchar(40),
    total            numeric(19,4) NOT NULL,
    discount_total   numeric(19,4) NOT NULL DEFAULT 0,
    cash_amount      numeric(19,4) NOT NULL DEFAULT 0,
    card_amount      numeric(19,4) NOT NULL DEFAULT 0,
    change_given     numeric(19,4) NOT NULL DEFAULT 0,
    cost             numeric(19,4) NOT NULL DEFAULT 0,
    refund_of_id     uuid,
    journal_entry_id uuid,
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    updated_at       timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, receipt_no),
    UNIQUE (tenant_id, client_id),
    FOREIGN KEY (tenant_id, shift_id) REFERENCES sal.pos_shift (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, refund_of_id) REFERENCES sal.pos_sale (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'pos_sale');

CREATE TABLE sal.pos_sale_line (
    id                uuid PRIMARY KEY,
    tenant_id         uuid NOT NULL,
    pos_sale_id       uuid NOT NULL,
    line_no           int NOT NULL,
    item_id           uuid NOT NULL,
    quantity          numeric(19,6) NOT NULL,
    unit_price        numeric(19,6) NOT NULL CHECK (unit_price >= 0),
    discount_percent  numeric(19,6) NOT NULL DEFAULT 0,
    amount            numeric(19,4) NOT NULL,
    cost              numeric(19,4) NOT NULL DEFAULT 0,
    refund_of_line_id uuid,
    created_by        uuid NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_by        uuid,
    updated_at        timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, pos_sale_id) REFERENCES sal.pos_sale (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('sal', 'pos_sale_line');

INSERT INTO core.role_permission (id, tenant_id, role_id, permission, created_by, created_at)
SELECT gen_random_uuid(), r.tenant_id, r.id, p.permission, '00000000-0000-0000-0000-000000000000', now()
  FROM core.role r
  JOIN (VALUES ('salesperson', 'sal.pos:sell'), ('storekeeper', 'sal.pos:sell'), ('general_manager', 'sal.pos:manage'), ('accountant', 'sal.pos:manage'))
       AS p(role, permission) ON p.role = r.code
 WHERE NOT EXISTS (SELECT 1 FROM core.role_permission x WHERE x.tenant_id = r.tenant_id AND x.role_id = r.id AND x.permission = p.permission);
