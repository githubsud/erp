GRANT USAGE ON SCHEMA inv TO erp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA inv TO erp_app;

-- The stock ledger is insert-only for the application.
REVOKE UPDATE, DELETE ON inv.stock_movement FROM erp_app;
