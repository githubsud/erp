-- Employees sign in to self-service with their employee number: users get an optional username, email becomes optional,
-- and a temporary password must be changed at first sign-in.
ALTER TABLE core.app_user ALTER COLUMN email DROP NOT NULL;
ALTER TABLE core.app_user ADD COLUMN username varchar(60) CHECK (username = lower(username));
ALTER TABLE core.app_user ADD COLUMN must_change_password boolean NOT NULL DEFAULT false;
ALTER TABLE core.app_user ADD CONSTRAINT app_user_identifier_check CHECK (email IS NOT NULL OR username IS NOT NULL);
ALTER TABLE core.app_user ADD CONSTRAINT app_user_username_key UNIQUE (tenant_id, username);

-- The self-service role for existing companies (new companies get it at provisioning).
INSERT INTO core.role (id, tenant_id, code, name_en, name_ar, is_system, created_by, created_at)
SELECT gen_random_uuid(), t.id, 'employee', 'Employee (self-service)', 'موظف (الخدمة الذاتية)', true, '00000000-0000-0000-0000-000000000000', now()
  FROM core.tenant t
 WHERE NOT EXISTS (SELECT 1 FROM core.role r WHERE r.tenant_id = t.id AND r.code = 'employee');

INSERT INTO core.role_permission (id, tenant_id, role_id, permission, created_by, created_at)
SELECT gen_random_uuid(), r.tenant_id, r.id, 'ess.self:use', '00000000-0000-0000-0000-000000000000', now()
  FROM core.role r
 WHERE r.code = 'employee'
   AND NOT EXISTS (SELECT 1 FROM core.role_permission x WHERE x.tenant_id = r.tenant_id AND x.role_id = r.id AND x.permission = 'ess.self:use');
