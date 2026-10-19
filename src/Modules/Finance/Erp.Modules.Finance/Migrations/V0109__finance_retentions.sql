-- Retentions held back by customers on progress invoices (contracting), released when the work is handed over.
INSERT INTO fin.account (id, tenant_id, company_id, parent_id, code, name_en, name_ar, account_type, normal_balance, is_control, is_postable, is_active,
                         created_by, created_at)
SELECT gen_random_uuid(), p.tenant_id, p.company_id, p.id, '1145', 'Retentions Receivable', 'محتجزات مستحقة القبض', 'asset', 'debit', false, true, true,
       '00000000-0000-0000-0000-000000000000', now()
  FROM fin.account p
 WHERE p.code = '11'
   AND NOT EXISTS (SELECT 1 FROM fin.account a WHERE a.tenant_id = p.tenant_id AND a.company_id = p.company_id AND a.code = '1145');

INSERT INTO fin.posting_rule (id, tenant_id, company_id, account_role, role_key, account_id, created_by, created_at)
SELECT gen_random_uuid(), a.tenant_id, a.company_id, 'sal.retention_receivable', NULL, a.id, '00000000-0000-0000-0000-000000000000', now()
  FROM fin.account a
 WHERE a.code = '1145'
   AND NOT EXISTS (SELECT 1 FROM fin.posting_rule p WHERE p.tenant_id = a.tenant_id AND p.company_id = a.company_id
                                                     AND p.account_role = 'sal.retention_receivable' AND p.role_key IS NULL);
