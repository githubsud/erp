-- Employee self-service: the sign-in an employee uses (created by HR).
ALTER TABLE hr.employee ADD COLUMN user_id uuid;
ALTER TABLE hr.employee ADD CONSTRAINT employee_user_key UNIQUE (tenant_id, user_id);
ALTER TABLE hr.employee ADD FOREIGN KEY (tenant_id, user_id) REFERENCES core.app_user (tenant_id, id) DEFERRABLE INITIALLY DEFERRED;
