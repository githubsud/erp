-- A till sale can be put on a known customer's account: it is then a posted sales invoice (credit limit, aging and statement apply).
ALTER TABLE sal.pos_sale
    ADD COLUMN customer_id        uuid,
    ADD COLUMN sales_invoice_id   uuid,
    ADD COLUMN on_account_amount  numeric(19,4) NOT NULL DEFAULT 0,
    ADD FOREIGN KEY (tenant_id, customer_id) REFERENCES sal.customer (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    ADD FOREIGN KEY (tenant_id, sales_invoice_id) REFERENCES sal.sales_invoice (tenant_id, id) DEFERRABLE INITIALLY DEFERRED;
