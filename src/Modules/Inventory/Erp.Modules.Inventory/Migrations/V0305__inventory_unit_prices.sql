-- A box (or any other unit of an item) can have its own sales price, usually less than its pieces, and its own barcode.
ALTER TABLE inv.item_unit
    ADD COLUMN sales_price numeric(19,6) CHECK (sales_price >= 0),
    ADD COLUMN barcode     varchar(60);
CREATE UNIQUE INDEX ux_item_unit_barcode ON inv.item_unit (tenant_id, barcode) WHERE barcode IS NOT NULL;
