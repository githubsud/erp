-- Where a sales order came from (staff, till, online store) and, for online orders, the store's own order number.
ALTER TABLE sal.sales_order
    ADD COLUMN channel varchar(20) NOT NULL DEFAULT 'direct' CHECK (channel IN ('direct', 'pos', 'online')),
    ADD COLUMN external_ref varchar(60);
CREATE UNIQUE INDEX ux_sales_order_external_ref ON sal.sales_order (tenant_id, company_id, channel, external_ref) WHERE external_ref IS NOT NULL;
CREATE INDEX ix_sales_order_channel ON sal.sales_order (tenant_id, company_id, channel, status);
