-- Checkout needs the warehouse online orders are sent from, and a service item for the delivery fee (so it posts as revenue
-- through the normal sales rules).
ALTER TABLE commerce.store
    ADD COLUMN warehouse_id uuid,
    ADD COLUMN delivery_item_id uuid,
    ADD FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    ADD FOREIGN KEY (tenant_id, delivery_item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED;
