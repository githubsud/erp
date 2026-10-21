-- Runs after R__grants_core. Tenants are created by provisioning but never deleted by the application.
REVOKE DELETE ON core.tenant FROM erp_app;

-- Subscription invoice numbers come from a sequence; payments are a record of money received and are never edited.
GRANT USAGE, SELECT ON SEQUENCE core.subscription_invoice_no TO erp_app;
REVOKE DELETE ON core.subscription_payment, core.subscription_invoice, core.subscription, core.platform_admin FROM erp_app;

-- Support requests are kept as a record: closed, never deleted by the application (saved replies can be removed).
GRANT USAGE, SELECT ON SEQUENCE core.support_ticket_no TO erp_app;
REVOKE DELETE ON core.support_ticket, core.support_message, core.support_attachment FROM erp_app;
