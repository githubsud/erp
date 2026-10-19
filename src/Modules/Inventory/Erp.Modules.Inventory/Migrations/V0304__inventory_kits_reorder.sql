-- Reorder quantity (how much to buy when stock reaches the reorder level), kit components and assembly orders that build kits
-- from their components (components out at average cost, the kit in at their total value).
ALTER TABLE inv.item ADD COLUMN reorder_quantity numeric(19,6) CHECK (reorder_quantity > 0);

CREATE TABLE inv.kit_component (
    id                 uuid PRIMARY KEY,
    tenant_id          uuid NOT NULL,
    kit_item_id        uuid NOT NULL,
    component_item_id  uuid NOT NULL,
    quantity           numeric(19,6) NOT NULL CHECK (quantity > 0),
    created_by         uuid NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_by         uuid,
    updated_at         timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, kit_item_id, component_item_id),
    CHECK (kit_item_id <> component_item_id),
    FOREIGN KEY (tenant_id, kit_item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, component_item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('inv', 'kit_component');

CREATE TABLE inv.assembly (
    id                uuid PRIMARY KEY,
    tenant_id         uuid NOT NULL,
    company_id        uuid NOT NULL,
    assembly_no       varchar(40) NOT NULL,
    assembly_date     date NOT NULL,
    warehouse_id      uuid NOT NULL,
    kit_item_id       uuid NOT NULL,
    quantity          numeric(19,6) NOT NULL,
    total_value       numeric(19,4) NOT NULL DEFAULT 0,
    status            varchar(20) NOT NULL CHECK (status IN ('posted', 'reversed')),
    journal_entry_id  uuid,
    notes             varchar(500),
    created_by        uuid NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_by        uuid,
    updated_at        timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, assembly_no),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, kit_item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('inv', 'assembly');
