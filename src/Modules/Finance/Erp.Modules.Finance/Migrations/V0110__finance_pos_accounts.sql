-- Point of sale: card payments wait in a clearing account until the bank settles them; cash differences at shift close.
INSERT INTO fin.account (id, tenant_id, company_id, parent_id, code, name_en, name_ar, account_type, normal_balance, is_control, is_postable, is_active,
                         subtype, created_by, created_at)
SELECT gen_random_uuid(), p.tenant_id, p.company_id, p.id, '1115', 'Card Payments Clearing', 'حساب وسيط مدفوعات البطاقات', 'asset', 'debit', false, true, true,
       NULL, '00000000-0000-0000-0000-000000000000', now()
  FROM fin.account p
 WHERE p.code = '11'
   AND NOT EXISTS (SELECT 1 FROM fin.account a WHERE a.tenant_id = p.tenant_id AND a.company_id = p.company_id AND a.code = '1115');

INSERT INTO fin.account (id, tenant_id, company_id, parent_id, code, name_en, name_ar, account_type, normal_balance, is_control, is_postable, is_active,
                         created_by, created_at)
SELECT gen_random_uuid(), p.tenant_id, p.company_id, p.id, '5345', 'Cash Over and Short', 'زيادة وعجز النقدية', 'expense', 'debit', false, true, true,
       '00000000-0000-0000-0000-000000000000', now()
  FROM fin.account p
 WHERE p.code = '53'
   AND NOT EXISTS (SELECT 1 FROM fin.account a WHERE a.tenant_id = p.tenant_id AND a.company_id = p.company_id AND a.code = '5345');
