-- Shift rosters: who works which shift on which day, and who is off. Attendance is compared with the roster for lateness,
-- early leaving and absence; a rostered day off is the employee's rest day for overtime.
CREATE TABLE hr.work_shift (
    id             uuid PRIMARY KEY,
    tenant_id      uuid NOT NULL,
    company_id     uuid NOT NULL,
    code           varchar(20) NOT NULL,
    name_en        varchar(200) NOT NULL,
    name_ar        varchar(200) NOT NULL,
    start_time     time NOT NULL,
    end_time       time NOT NULL,
    break_minutes  int NOT NULL DEFAULT 0 CHECK (break_minutes >= 0),
    grace_minutes  int NOT NULL DEFAULT 0 CHECK (grace_minutes >= 0),
    color          varchar(20),
    is_active      boolean NOT NULL DEFAULT true,
    created_by     uuid NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_by     uuid,
    updated_at     timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, code),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('hr', 'work_shift');

-- shift_id NULL = rostered day off.
CREATE TABLE hr.roster_entry (
    id           uuid PRIMARY KEY,
    tenant_id    uuid NOT NULL,
    employee_id  uuid NOT NULL,
    work_date    date NOT NULL,
    shift_id     uuid,
    created_by   uuid NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_by   uuid,
    updated_at   timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, employee_id, work_date),
    FOREIGN KEY (tenant_id, employee_id) REFERENCES hr.employee (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, shift_id) REFERENCES hr.work_shift (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_roster_entry_date ON hr.roster_entry (tenant_id, work_date);
SELECT core.enable_tenant_rls('hr', 'roster_entry');

INSERT INTO core.role_permission (id, tenant_id, role_id, permission, created_by, created_at)
SELECT gen_random_uuid(), r.tenant_id, r.id, 'hr.rosters:manage', '00000000-0000-0000-0000-000000000000', now()
  FROM core.role r
 WHERE r.code IN ('hr_manager', 'payroll_officer')
   AND NOT EXISTS (SELECT 1 FROM core.role_permission x WHERE x.tenant_id = r.tenant_id AND x.role_id = r.id AND x.permission = 'hr.rosters:manage');
