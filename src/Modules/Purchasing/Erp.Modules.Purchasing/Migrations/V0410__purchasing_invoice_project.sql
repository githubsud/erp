-- Supplier invoices can be charged to a project (contracting): their expense lines carry the project tag in the ledger.
ALTER TABLE pur.supplier_invoice ADD COLUMN project_id uuid;
