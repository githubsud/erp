GRANT USAGE ON SCHEMA sal TO erp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA sal TO erp_app;

-- Posted sales documents are reversed, never deleted (draft quotations, orders and invoices may be).
REVOKE DELETE ON sal.delivery_note, sal.delivery_note_line, sal.credit_note, sal.credit_note_line,
    sal.customer_receipt, sal.customer_receipt_allocation FROM erp_app;
