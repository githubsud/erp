-- Re-applied on every migration run. Runtime roles get least privilege; ledgers and audit are insert-only.
GRANT USAGE ON SCHEMA core TO erp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA core TO erp_app;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA core TO erp_app;

REVOKE UPDATE, DELETE ON core.audit_log FROM erp_app;

GRANT USAGE ON SCHEMA core TO erp_worker;
GRANT EXECUTE ON FUNCTION core.current_tenant_id() TO erp_worker;
GRANT SELECT, UPDATE ON core.outbox_message TO erp_worker;
