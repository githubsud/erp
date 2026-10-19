-- Foreign-currency sales. Documents keep amounts in their currency with an exchange rate to QAR; invoices and receipts also keep
-- QAR (base) amounts so the ledger, aging and settlements stay in QAR.
ALTER TABLE sal.customer ADD COLUMN currency varchar(3) NOT NULL DEFAULT 'QAR';

ALTER TABLE sal.quotation ADD COLUMN currency varchar(3) NOT NULL DEFAULT 'QAR';
ALTER TABLE sal.quotation ADD COLUMN exchange_rate numeric(19,6) NOT NULL DEFAULT 1 CHECK (exchange_rate > 0);
ALTER TABLE sal.sales_order ADD COLUMN currency varchar(3) NOT NULL DEFAULT 'QAR';
ALTER TABLE sal.sales_order ADD COLUMN exchange_rate numeric(19,6) NOT NULL DEFAULT 1 CHECK (exchange_rate > 0);

ALTER TABLE sal.sales_invoice ADD COLUMN currency varchar(3) NOT NULL DEFAULT 'QAR';
ALTER TABLE sal.sales_invoice ADD COLUMN exchange_rate numeric(19,6) NOT NULL DEFAULT 1 CHECK (exchange_rate > 0);
ALTER TABLE sal.sales_invoice ADD COLUMN total_base numeric(19,4);
ALTER TABLE sal.sales_invoice ADD COLUMN paid_base numeric(19,4) NOT NULL DEFAULT 0;
ALTER TABLE sal.sales_invoice ADD COLUMN credited_base numeric(19,4) NOT NULL DEFAULT 0;
UPDATE sal.sales_invoice SET total_base = total, paid_base = paid_amount, credited_base = credited_amount;
ALTER TABLE sal.sales_invoice ALTER COLUMN total_base SET NOT NULL;
ALTER TABLE sal.sales_invoice ALTER COLUMN total_base SET DEFAULT 0;

ALTER TABLE sal.customer_receipt ADD COLUMN currency varchar(3) NOT NULL DEFAULT 'QAR';
ALTER TABLE sal.customer_receipt ADD COLUMN exchange_rate numeric(19,6) NOT NULL DEFAULT 1 CHECK (exchange_rate > 0);
ALTER TABLE sal.customer_receipt ADD COLUMN amount_base numeric(19,4);
ALTER TABLE sal.customer_receipt ADD COLUMN fx_difference numeric(19,4) NOT NULL DEFAULT 0;
UPDATE sal.customer_receipt SET amount_base = amount;
ALTER TABLE sal.customer_receipt ALTER COLUMN amount_base SET NOT NULL;
ALTER TABLE sal.customer_receipt ALTER COLUMN amount_base SET DEFAULT 0;

ALTER TABLE sal.customer_receipt_allocation ADD COLUMN base_cleared numeric(19,4);
UPDATE sal.customer_receipt_allocation SET base_cleared = amount;
ALTER TABLE sal.customer_receipt_allocation ALTER COLUMN base_cleared SET NOT NULL;
ALTER TABLE sal.customer_receipt_allocation ALTER COLUMN base_cleared SET DEFAULT 0;

ALTER TABLE sal.credit_note ADD COLUMN currency varchar(3) NOT NULL DEFAULT 'QAR';
ALTER TABLE sal.credit_note ADD COLUMN exchange_rate numeric(19,6) NOT NULL DEFAULT 1 CHECK (exchange_rate > 0);
ALTER TABLE sal.credit_note ADD COLUMN applied_base numeric(19,4);
UPDATE sal.credit_note SET applied_base = applied_amount;
ALTER TABLE sal.credit_note ALTER COLUMN applied_base SET NOT NULL;
ALTER TABLE sal.credit_note ALTER COLUMN applied_base SET DEFAULT 0;
