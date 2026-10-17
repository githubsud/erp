-- Sick leave pay bands (Labour Law art. 82): after the minimum service, the first weeks at full pay, the next weeks
-- at half pay, the rest unpaid — counted per service year. All values are settings.

ALTER TABLE hr.leave_type
    ADD COLUMN pay_rule varchar(20) NOT NULL DEFAULT 'full' CHECK (pay_rule IN ('full', 'unpaid', 'sick_bands'));

UPDATE hr.leave_type SET pay_rule = 'unpaid' WHERE NOT is_paid;
UPDATE hr.leave_type SET pay_rule = 'sick_bands' WHERE code = 'SICK';

ALTER TABLE hr.settings
    ADD COLUMN sick_full_pay_days       int NOT NULL DEFAULT 14 CHECK (sick_full_pay_days >= 0),
    ADD COLUMN sick_half_pay_days       int NOT NULL DEFAULT 28 CHECK (sick_half_pay_days >= 0),
    ADD COLUMN sick_min_service_months  int NOT NULL DEFAULT 3  CHECK (sick_min_service_months >= 0);

ALTER TABLE hr.payslip
    ADD COLUMN half_pay_days numeric(9,2) NOT NULL DEFAULT 0;
