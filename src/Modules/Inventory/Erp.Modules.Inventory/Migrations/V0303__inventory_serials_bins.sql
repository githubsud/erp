-- Serial-numbered items (one-unit batches) and bin locations (where an item sits in each warehouse).
ALTER TABLE inv.item ADD COLUMN track_serials boolean NOT NULL DEFAULT false;
ALTER TABLE inv.item ADD CONSTRAINT item_serials_are_batches CHECK (NOT track_serials OR track_batches);

CREATE TABLE inv.item_bin (
    id            uuid PRIMARY KEY,
    tenant_id     uuid NOT NULL,
    item_id       uuid NOT NULL,
    warehouse_id  uuid NOT NULL,
    bin_code      varchar(30) NOT NULL,
    created_by    uuid NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_by    uuid,
    updated_at    timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, item_id, warehouse_id),
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('inv', 'item_bin');
