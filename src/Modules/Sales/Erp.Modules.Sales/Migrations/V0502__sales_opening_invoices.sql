-- Opening balances: customers' unpaid invoices from the previous system, posted against opening balance equity.
ALTER TABLE sal.sales_invoice ADD COLUMN is_opening boolean NOT NULL DEFAULT false;
