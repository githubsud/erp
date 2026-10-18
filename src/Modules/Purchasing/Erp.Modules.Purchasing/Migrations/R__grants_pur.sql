GRANT USAGE ON SCHEMA pur TO erp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA pur TO erp_app;

-- Posted purchasing documents are reversed, never deleted (draft invoices and draft order lines may be).
REVOKE DELETE ON pur.goods_receipt, pur.goods_receipt_line, pur.landed_cost_voucher, pur.landed_cost_charge,
    pur.landed_cost_allocation, pur.supplier_payment, pur.supplier_payment_allocation FROM erp_app;
REVOKE DELETE ON pur.debit_note, pur.debit_note_line FROM erp_app;
