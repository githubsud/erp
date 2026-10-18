-- Order lines may be entered in an alternative unit; quantity and unit_price stay in the item's base unit.
ALTER TABLE pur.purchase_order_line ADD COLUMN uom_id uuid;
ALTER TABLE pur.purchase_order_line ADD COLUMN uom_factor numeric(19,6) NOT NULL DEFAULT 1 CHECK (uom_factor > 0);
ALTER TABLE pur.purchase_order_line ADD COLUMN uom_quantity numeric(19,6);
ALTER TABLE pur.purchase_order_line ADD COLUMN uom_price numeric(19,6);
UPDATE pur.purchase_order_line SET uom_quantity = quantity, uom_price = unit_price;
ALTER TABLE pur.purchase_order_line ALTER COLUMN uom_quantity SET NOT NULL;
ALTER TABLE pur.purchase_order_line ALTER COLUMN uom_price SET NOT NULL;
ALTER TABLE pur.purchase_order_line ADD FOREIGN KEY (tenant_id, uom_id) REFERENCES inv.uom (tenant_id, id) DEFERRABLE INITIALLY DEFERRED;
