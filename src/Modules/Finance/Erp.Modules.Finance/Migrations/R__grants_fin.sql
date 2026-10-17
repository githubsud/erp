GRANT USAGE ON SCHEMA fin TO erp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA fin TO erp_app;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA fin TO erp_app;

-- The ledger is insert-only for the application.
REVOKE UPDATE, DELETE ON fin.journal_entry, fin.journal_line FROM erp_app;
