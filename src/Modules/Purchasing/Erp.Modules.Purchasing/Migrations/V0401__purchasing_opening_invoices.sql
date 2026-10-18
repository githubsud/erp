-- Opening balances: suppliers' unpaid invoices from the previous system, posted against opening balance equity.
ALTER TABLE pur.supplier_invoice ADD COLUMN is_opening boolean NOT NULL DEFAULT false;
