-- Goods on trust: an item that belongs to a partner shop (a supplier) and is sold as its agent for a commission agreed in advance.
-- The shop is linked by Purchasing (V0413 adds the foreign key once pur.supplier exists). Goods on trust are held at zero cost.
ALTER TABLE inv.item ADD COLUMN consignor_id uuid;
ALTER TABLE inv.item ADD COLUMN consignment_commission_type varchar(10)
    CHECK (consignment_commission_type IN ('percent', 'fixed'));
ALTER TABLE inv.item ADD COLUMN consignment_commission numeric(19,6) CHECK (consignment_commission >= 0);
ALTER TABLE inv.item ADD CONSTRAINT ck_item_consignment_terms CHECK (
    (consignor_id IS NULL AND consignment_commission_type IS NULL AND consignment_commission IS NULL)
    OR (consignor_id IS NOT NULL AND consignment_commission_type IS NOT NULL AND consignment_commission IS NOT NULL
        AND (consignment_commission_type = 'fixed' OR consignment_commission <= 100)));
CREATE INDEX ix_item_consignor ON inv.item (tenant_id, consignor_id) WHERE consignor_id IS NOT NULL;
