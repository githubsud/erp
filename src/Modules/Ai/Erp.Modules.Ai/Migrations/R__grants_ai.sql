GRANT USAGE ON SCHEMA ai TO erp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ai.setting, ai.summary TO erp_app;
GRANT SELECT, INSERT, UPDATE ON ai.provider, ai.usage_day TO erp_app;
