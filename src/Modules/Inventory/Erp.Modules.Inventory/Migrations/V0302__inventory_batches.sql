-- Batch and expiry tracking. Cost stays weighted-average per item; batches track quantity per warehouse and expiry.
-- Each stock movement keeps one valuation row; its split over batches is recorded in stock_movement_batch.
ALTER TABLE inv.item ADD COLUMN track_batches boolean NOT NULL DEFAULT false;

CREATE TABLE inv.batch (
    id          uuid PRIMARY KEY,
    tenant_id   uuid NOT NULL,
    company_id  uuid NOT NULL,
    item_id     uuid NOT NULL,
    batch_no    varchar(60) NOT NULL,
    expiry_date date,
    created_by  uuid NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  uuid,
    updated_at  timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, item_id, batch_no),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_batch_expiry ON inv.batch (tenant_id, expiry_date);
SELECT core.enable_tenant_rls('inv', 'batch');

CREATE TABLE inv.batch_stock (
    id           uuid PRIMARY KEY,
    tenant_id    uuid NOT NULL,
    batch_id     uuid NOT NULL,
    warehouse_id uuid NOT NULL,
    quantity     numeric(19,6) NOT NULL DEFAULT 0,
    created_by   uuid NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_by   uuid,
    updated_at   timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, batch_id, warehouse_id),
    FOREIGN KEY (tenant_id, batch_id) REFERENCES inv.batch (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('inv', 'batch_stock');

CREATE TABLE inv.stock_movement_batch (
    id          uuid PRIMARY KEY,
    tenant_id   uuid NOT NULL,
    movement_id uuid NOT NULL,
    batch_id    uuid NOT NULL,
    quantity    numeric(19,6) NOT NULL CHECK (quantity <> 0),
    created_by  uuid NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  uuid,
    updated_at  timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, movement_id) REFERENCES inv.stock_movement (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, batch_id) REFERENCES inv.batch (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_stock_movement_batch_movement ON inv.stock_movement_batch (tenant_id, movement_id);
SELECT core.enable_tenant_rls('inv', 'stock_movement_batch');
SELECT core.make_immutable('inv', 'stock_movement_batch');

-- Issuing an expired batch to a customer needs this permission (write-offs and transfers to quarantine do not).
INSERT INTO core.role_permission (id, tenant_id, role_id, permission, created_by, created_at)
SELECT gen_random_uuid(), r.tenant_id, r.id, 'inv.expired:issue', '00000000-0000-0000-0000-000000000000', now()
  FROM core.role r
 WHERE r.code = 'general_manager'
   AND NOT EXISTS (SELECT 1 FROM core.role_permission x WHERE x.tenant_id = r.tenant_id AND x.role_id = r.id AND x.permission = 'inv.expired:issue');
