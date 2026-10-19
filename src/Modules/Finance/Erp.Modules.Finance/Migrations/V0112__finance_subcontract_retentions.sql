-- Subcontracting: retention held back from subcontractors until handover, and the tax-clearance retention held until the
-- subcontractor shows a tax clearance certificate (⚠️ VERIFY the rate and scope with a Qatari tax adviser).
INSERT INTO fin.account (id, tenant_id, company_id, parent_id, code, name_en, name_ar, account_type, normal_balance, is_control, is_postable, is_active,
                         created_by, created_at)
SELECT gen_random_uuid(), p.tenant_id, p.company_id, p.id, x.code, x.name_en, x.name_ar, 'liability', 'credit', false, true, true,
       '00000000-0000-0000-0000-000000000000', now()
  FROM fin.account p
 CROSS JOIN (VALUES ('2135', 'Retentions Payable', 'محتجزات مستحقة الدفع'),
                    ('2175', 'Tax Clearance Retentions', 'محتجزات شهادة المخالصة الضريبية')) AS x(code, name_en, name_ar)
 WHERE p.code = '21'
   AND NOT EXISTS (SELECT 1 FROM fin.account a WHERE a.tenant_id = p.tenant_id AND a.company_id = p.company_id AND a.code = x.code);

INSERT INTO fin.posting_rule (id, tenant_id, company_id, account_role, role_key, account_id, created_by, created_at)
SELECT gen_random_uuid(), a.tenant_id, a.company_id, r.role, NULL, a.id, '00000000-0000-0000-0000-000000000000', now()
  FROM fin.account a
  JOIN (VALUES ('2135', 'pur.retention_payable'), ('2175', 'pur.tax_retention_payable')) AS r(code, role) ON r.code = a.code
 WHERE NOT EXISTS (SELECT 1 FROM fin.posting_rule p WHERE p.tenant_id = a.tenant_id AND p.company_id = a.company_id
                                                     AND p.account_role = r.role AND p.role_key IS NULL);
