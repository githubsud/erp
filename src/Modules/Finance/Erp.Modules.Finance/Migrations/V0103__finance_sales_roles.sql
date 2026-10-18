-- Posting rules for the Sales module (receivables, revenue, returns, post-dated cheques) in every existing company.
INSERT INTO fin.posting_rule (id, tenant_id, company_id, account_role, role_key, account_id, created_by, created_at)
SELECT gen_random_uuid(), a.tenant_id, a.company_id, r.role, NULL, a.id, '00000000-0000-0000-0000-000000000000', now()
  FROM (VALUES ('sal.receivable', '1130'), ('sal.revenue', '4110'), ('sal.service_revenue', '4120'), ('sal.returns', '4130'),
               ('sal.pdc_receivable', '1140'))
       AS r(role, code)
  JOIN fin.account a ON a.code = r.code
 WHERE NOT EXISTS (SELECT 1 FROM fin.posting_rule p
                    WHERE p.tenant_id = a.tenant_id AND p.company_id = a.company_id AND p.account_role = r.role AND p.role_key IS NULL);
