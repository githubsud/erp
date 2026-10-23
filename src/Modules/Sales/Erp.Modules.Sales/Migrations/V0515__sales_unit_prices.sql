-- Price lists can price an item per unit (a garage's own box price); a line without a unit is the item's base unit.
ALTER TABLE sal.price_list_item
    ADD COLUMN uom_id uuid,
    ADD FOREIGN KEY (tenant_id, uom_id) REFERENCES inv.uom (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    DROP CONSTRAINT price_list_item_tenant_id_price_list_id_item_id_key;
CREATE UNIQUE INDEX ux_price_list_item_unit
    ON sal.price_list_item (tenant_id, price_list_id, item_id, coalesce(uom_id, '00000000-0000-0000-0000-000000000000'::uuid));

-- Till lines sold by the box. quantity and unit_price stay in base units (stock, refunds and reports count pieces);
-- these keep what the cashier rang up: the unit, its size, how many and at what price.
ALTER TABLE sal.pos_sale_line
    ADD COLUMN uom_id       uuid,
    ADD COLUMN uom_factor   numeric(19,6) NOT NULL DEFAULT 1 CHECK (uom_factor > 0),
    ADD COLUMN uom_quantity numeric(19,6),
    ADD COLUMN uom_price    numeric(19,6),
    ADD FOREIGN KEY (tenant_id, uom_id) REFERENCES inv.uom (tenant_id, id) DEFERRABLE INITIALLY DEFERRED;
