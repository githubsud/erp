-- GRSIA social insurance for Qatari employees (Law No. 1 of 2022): employee and employer contributions on the
-- contribution salary (by default basic + housing), with a monthly ceiling. Rates are settings. ⚠️ Verify the
-- contribution base, caps and rates with GRSIA before go-live.

ALTER TABLE hr.settings
    ADD COLUMN si_employee_rate_percent numeric(9,4)  NOT NULL DEFAULT 7   CHECK (si_employee_rate_percent >= 0),
    ADD COLUMN si_employer_rate_percent numeric(9,4)  NOT NULL DEFAULT 14  CHECK (si_employer_rate_percent >= 0),
    ADD COLUMN si_max_contribution_salary numeric(19,4) NOT NULL DEFAULT 100000 CHECK (si_max_contribution_salary > 0);

ALTER TABLE hr.pay_component
    ADD COLUMN in_social_insurance_basis boolean NOT NULL DEFAULT false;
UPDATE hr.pay_component SET in_social_insurance_basis = true WHERE code IN ('BASIC', 'HOUSING');

ALTER TABLE hr.payslip
    ADD COLUMN employer_social_insurance numeric(19,4) NOT NULL DEFAULT 0;

-- Employee-contribution deduction component for every existing company (new companies get it at provisioning).
INSERT INTO hr.pay_component (id, tenant_id, company_id, code, name_en, name_ar, kind, wps_field, is_fixed, prorate_by_days,
                              in_gratuity_basis, posting_role, is_system, is_active, created_by, created_at)
SELECT gen_random_uuid(), s.tenant_id, s.company_id, 'SI_EMPLOYEE', 'Social insurance (employee share)', 'التأمينات الاجتماعية (حصة الموظف)',
       'deduction', 'deduction', false, false, false, 'hr.social_insurance_payable', true, true, '00000000-0000-0000-0000-000000000000', now()
  FROM hr.settings s
 WHERE NOT EXISTS (SELECT 1 FROM hr.pay_component c WHERE c.tenant_id = s.tenant_id AND c.company_id = s.company_id AND c.code = 'SI_EMPLOYEE');
