-- Runs after R__grants_core. Tenants are created by provisioning but never deleted by the application.
REVOKE DELETE ON core.tenant FROM erp_app;
