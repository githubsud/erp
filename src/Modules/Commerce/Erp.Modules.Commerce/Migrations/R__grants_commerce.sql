GRANT USAGE ON SCHEMA commerce TO erp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON commerce.category, commerce.listing, commerce.listing_image TO erp_app;
-- A store is closed, never deleted.
GRANT SELECT, INSERT, UPDATE ON commerce.store TO erp_app;
-- The slug is the directory's key, so a new store address replaces its row.
GRANT SELECT, INSERT, UPDATE, DELETE ON commerce.store_domain TO erp_app;
