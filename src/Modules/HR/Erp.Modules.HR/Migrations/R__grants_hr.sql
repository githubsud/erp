GRANT USAGE ON SCHEMA hr TO erp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA hr TO erp_app;

-- Generated WPS files are evidence of what was sent to the bank; they are never deleted.
REVOKE DELETE ON hr.wps_file, hr.gratuity_accrual, hr.benefit_accrual FROM erp_app;
