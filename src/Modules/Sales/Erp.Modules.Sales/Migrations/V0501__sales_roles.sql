-- Existing companies get the Sales Executive role and the sales permissions on their default roles
-- (new companies receive them at provisioning from SalesPermissionProvider).
INSERT INTO core.role (id, tenant_id, code, name_en, name_ar, is_system, created_by, created_at)
SELECT gen_random_uuid(), t.id, 'salesperson', 'Sales Executive', 'مسؤول المبيعات', true, '00000000-0000-0000-0000-000000000000', now()
  FROM core.tenant t
 WHERE NOT EXISTS (SELECT 1 FROM core.role r WHERE r.tenant_id = t.id AND r.code = 'salesperson');

INSERT INTO core.role_permission (id, tenant_id, role_id, permission, created_by, created_at)
SELECT gen_random_uuid(), r.tenant_id, r.id, p.permission, '00000000-0000-0000-0000-000000000000', now()
  FROM (VALUES ('sal.customers:read', 'salesperson'), ('sal.customers:read', 'storekeeper'), ('sal.customers:read', 'accountant'),
               ('sal.customers:read', 'general_manager'), ('sal.customers:read', 'auditor'),
               ('sal.customers:manage', 'salesperson'), ('sal.customers:manage', 'accountant'),
               ('sal.orders:manage', 'salesperson'),
               ('sal.credit:approve', 'general_manager'),
               ('sal.deliveries:post', 'storekeeper'), ('sal.deliveries:post', 'salesperson'),
               ('sal.invoices:post', 'accountant'), ('sal.invoices:post', 'salesperson'),
               ('sal.receipts:post', 'accountant'),
               ('sal.reports:read', 'accountant'), ('sal.reports:read', 'general_manager'), ('sal.reports:read', 'auditor'),
               ('sal.settings:manage', 'general_manager'), ('sal.settings:manage', 'accountant'),
               ('inv.items:read', 'salesperson'))
       AS p(permission, role_code)
  JOIN core.role r ON r.code = p.role_code
 WHERE NOT EXISTS (SELECT 1 FROM core.role_permission x WHERE x.tenant_id = r.tenant_id AND x.role_id = r.id AND x.permission = p.permission);
