-- Cash differences counted at the close of a till shift post through a role, like every other business fact.
INSERT INTO fin.posting_rule (id, tenant_id, company_id, account_role, role_key, account_id, created_by, created_at)
SELECT gen_random_uuid(), a.tenant_id, a.company_id, 'sal.cash_over_short', NULL, a.id, '00000000-0000-0000-0000-000000000000', now()
  FROM fin.account a
 WHERE a.code = '5345'
   AND NOT EXISTS (SELECT 1 FROM fin.posting_rule p WHERE p.tenant_id = a.tenant_id AND p.company_id = a.company_id
                                                     AND p.account_role = 'sal.cash_over_short' AND p.role_key IS NULL);
