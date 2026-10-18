-- Accounts and posting rules for the Trade bundle (inventory and purchasing) in every existing company.
INSERT INTO fin.account (id, tenant_id, company_id, parent_id, code, name_en, name_ar, account_type, normal_balance, created_by, created_at)
SELECT gen_random_uuid(), p.tenant_id, p.company_id, p.id, a.code, a.name_en, a.name_ar, a.account_type, a.normal_balance,
       '00000000-0000-0000-0000-000000000000', now()
  FROM (VALUES ('1190', '11', 'Landed Cost Clearing', 'حساب وسيط تكاليف الاستيراد', 'asset', 'debit'),
               ('2125', '21', 'Goods Received Not Invoiced', 'بضاعة مستلمة لم تصل فواتيرها', 'liability', 'credit'),
               ('5360', '53', 'Purchased Services and Supplies', 'خدمات ومستلزمات مشتراة', 'expense', 'debit'))
       AS a(code, parent_code, name_en, name_ar, account_type, normal_balance)
  JOIN fin.account p ON p.code = a.parent_code
 WHERE NOT EXISTS (SELECT 1 FROM fin.account x WHERE x.tenant_id = p.tenant_id AND x.company_id = p.company_id AND x.code = a.code);

INSERT INTO fin.posting_rule (id, tenant_id, company_id, account_role, role_key, account_id, created_by, created_at)
SELECT gen_random_uuid(), a.tenant_id, a.company_id, r.role, NULL, a.id, '00000000-0000-0000-0000-000000000000', now()
  FROM (VALUES ('inv.inventory', '1150'), ('inv.cogs', '5110'), ('inv.adjustment', '5120'), ('pur.grni', '2125'),
               ('pur.payable', '2110'), ('pur.wht_payable', '2170'), ('pur.landed_cost_clearing', '1190'), ('pur.expense', '5360'))
       AS r(role, code)
  JOIN fin.account a ON a.code = r.code
 WHERE NOT EXISTS (SELECT 1 FROM fin.posting_rule p
                    WHERE p.tenant_id = a.tenant_id AND p.company_id = a.company_id AND p.account_role = r.role AND p.role_key IS NULL);
