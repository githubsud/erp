GRANT USAGE ON SCHEMA commerce TO erp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON commerce.category, commerce.listing, commerce.listing_image TO erp_app;
-- A store is closed, never deleted; its directory entry follows it.
GRANT SELECT, INSERT, UPDATE ON commerce.store, commerce.store_domain TO erp_app;
