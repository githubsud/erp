-- Purchase order approval moves to the shared approval rules: each company's old threshold becomes a one-step rule
-- for the General Manager (the threshold column is no longer used).
INSERT INTO core.approval_rule (id, tenant_id, company_id, doc_type, step_no, min_amount, role_code, created_by, created_at)
SELECT gen_random_uuid(), s.tenant_id, s.company_id, 'purchase_order', 1, s.approval_threshold, 'general_manager',
       '00000000-0000-0000-0000-000000000000', now()
  FROM pur.settings s
 WHERE NOT EXISTS (SELECT 1 FROM core.approval_rule r WHERE r.tenant_id = s.tenant_id AND r.company_id = s.company_id AND r.doc_type = 'purchase_order');

-- The approvals screen for existing companies.
INSERT INTO core.role_permission (id, tenant_id, role_id, permission, created_by, created_at)
SELECT gen_random_uuid(), r.tenant_id, r.id, 'core.approvals:manage', '00000000-0000-0000-0000-000000000000', now()
  FROM core.role r
 WHERE r.code = 'general_manager'
   AND NOT EXISTS (SELECT 1 FROM core.role_permission x WHERE x.tenant_id = r.tenant_id AND x.role_id = r.id AND x.permission = 'core.approvals:manage');
