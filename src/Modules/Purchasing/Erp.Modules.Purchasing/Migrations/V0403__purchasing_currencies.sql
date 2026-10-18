-- Foreign-currency purchasing. Documents keep amounts in the supplier's currency with an exchange rate to QAR; the
-- ledger is posted in QAR. Receipts are costed at the order's rate, invoices at their own rate (the difference is a price
-- variance) and payments at the payment-day rate, the gain or loss going to foreign exchange differences.
ALTER TABLE pur.supplier ADD COLUMN currency char(3) NOT NULL DEFAULT 'QAR';

ALTER TABLE pur.purchase_order ADD COLUMN currency char(3) NOT NULL DEFAULT 'QAR';
ALTER TABLE pur.purchase_order ADD COLUMN exchange_rate numeric(19,6) NOT NULL DEFAULT 1 CHECK (exchange_rate > 0);

ALTER TABLE pur.goods_receipt ADD COLUMN exchange_rate numeric(19,6) NOT NULL DEFAULT 1 CHECK (exchange_rate > 0);

ALTER TABLE pur.supplier_invoice ADD COLUMN currency char(3) NOT NULL DEFAULT 'QAR';
ALTER TABLE pur.supplier_invoice ADD COLUMN exchange_rate numeric(19,6) NOT NULL DEFAULT 1 CHECK (exchange_rate > 0);
ALTER TABLE pur.supplier_invoice ADD COLUMN total_base numeric(19,4);
ALTER TABLE pur.supplier_invoice ADD COLUMN paid_base numeric(19,4) NOT NULL DEFAULT 0;
ALTER TABLE pur.supplier_invoice ADD COLUMN debited_base numeric(19,4) NOT NULL DEFAULT 0;
UPDATE pur.supplier_invoice SET total_base = total, paid_base = paid_amount, debited_base = debited_amount;
ALTER TABLE pur.supplier_invoice ALTER COLUMN total_base SET NOT NULL;

ALTER TABLE pur.supplier_payment ADD COLUMN currency char(3) NOT NULL DEFAULT 'QAR';
ALTER TABLE pur.supplier_payment ADD COLUMN exchange_rate numeric(19,6) NOT NULL DEFAULT 1 CHECK (exchange_rate > 0);
ALTER TABLE pur.supplier_payment ADD COLUMN fx_difference numeric(19,4) NOT NULL DEFAULT 0;

ALTER TABLE pur.supplier_payment_allocation ADD COLUMN base_cleared numeric(19,4);
UPDATE pur.supplier_payment_allocation SET base_cleared = amount;
ALTER TABLE pur.supplier_payment_allocation ALTER COLUMN base_cleared SET NOT NULL;

ALTER TABLE pur.debit_note ADD COLUMN total_base numeric(19,4);
UPDATE pur.debit_note SET total_base = total;
ALTER TABLE pur.debit_note ALTER COLUMN total_base SET NOT NULL;
