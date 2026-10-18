-- Alternative units per item (a pallet of 40 bags, a box of 1.44 m²): how many base units one of them holds.
CREATE TABLE inv.item_unit (
    id         uuid PRIMARY KEY,
    tenant_id  uuid NOT NULL,
    item_id    uuid NOT NULL,
    uom_id     uuid NOT NULL,
    factor     numeric(19,6) NOT NULL CHECK (factor > 0),
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_by uuid,
    updated_at timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, item_id, uom_id),
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, uom_id) REFERENCES inv.uom (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('inv', 'item_unit');
