-- Supplier payments by post-dated cheque: held in PDC payable until the cheque clears (or is stopped).
ALTER TABLE pur.supplier_payment ADD COLUMN cheque_no varchar(30);
ALTER TABLE pur.supplier_payment ADD COLUMN cheque_date date;
ALTER TABLE pur.supplier_payment ADD COLUMN pdc_status varchar(20) CHECK (pdc_status IN ('issued', 'cleared'));
ALTER TABLE pur.supplier_payment ADD COLUMN pdc_cleared_date date;
ALTER TABLE pur.supplier_payment ADD CONSTRAINT supplier_payment_pdc_check
    CHECK (pdc_status IS NULL OR (cheque_no IS NOT NULL AND cheque_date IS NOT NULL));
