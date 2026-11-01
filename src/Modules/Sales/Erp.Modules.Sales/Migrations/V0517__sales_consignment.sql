-- Goods on trust: each sale of a partner shop's goods keeps the shop and our commission (QAR) as they were when sold, so later changes to the
-- agreement never rewrite a statement. The shop's share is the line amount less the commission.
ALTER TABLE sal.sales_invoice_line ADD COLUMN consignor_id uuid;
ALTER TABLE sal.sales_invoice_line ADD COLUMN consignment_commission numeric(19,4) CHECK (consignment_commission >= 0);
ALTER TABLE sal.credit_note_line ADD COLUMN consignor_id uuid;
ALTER TABLE sal.credit_note_line ADD COLUMN consignment_commission numeric(19,4) CHECK (consignment_commission >= 0);
CREATE INDEX ix_sales_invoice_line_consignor ON sal.sales_invoice_line (tenant_id, consignor_id) WHERE consignor_id IS NOT NULL;
CREATE INDEX ix_credit_note_line_consignor ON sal.credit_note_line (tenant_id, consignor_id) WHERE consignor_id IS NOT NULL;
