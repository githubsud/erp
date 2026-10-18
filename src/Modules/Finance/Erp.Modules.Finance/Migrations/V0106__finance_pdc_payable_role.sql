-- Post-dated cheques issued to suppliers are held in "Post-dated Cheques Payable" (2120) until they clear the bank.
INSERT INTO fin.posting_rule (id, tenant_id, company_id, account_role, role_key, account_id, created_by, created_at)
SELECT gen_random_uuid(), a.tenant_id, a.company_id, 'pur.pdc_payable', NULL, a.id, '00000000-0000-0000-0000-000000000000', now()
  FROM fin.account a
 WHERE a.code = '2120'
   AND NOT EXISTS (SELECT 1 FROM fin.posting_rule p
                    WHERE p.tenant_id = a.tenant_id AND p.company_id = a.company_id AND p.account_role = 'pur.pdc_payable' AND p.role_key IS NULL);
