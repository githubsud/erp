-- Daily attendance imported from biometric devices; overtime inputs are generated from it.

CREATE TABLE hr.attendance_day (
    id                uuid PRIMARY KEY,
    tenant_id         uuid NOT NULL,
    employee_id       uuid NOT NULL,
    work_date         date NOT NULL,
    time_in           time NOT NULL,
    time_out          time NOT NULL,
    is_holiday        boolean NOT NULL DEFAULT false,
    worked_hours      numeric(9,2) NOT NULL CHECK (worked_hours >= 0),
    normal_overtime   numeric(9,2) NOT NULL DEFAULT 0,
    night_overtime    numeric(9,2) NOT NULL DEFAULT 0,
    rest_day_overtime numeric(9,2) NOT NULL DEFAULT 0,
    holiday_overtime  numeric(9,2) NOT NULL DEFAULT 0,
    created_by        uuid NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_by        uuid,
    updated_at        timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, employee_id, work_date),
    FOREIGN KEY (tenant_id, employee_id) REFERENCES hr.employee (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('hr', 'attendance_day');

-- Inputs generated from attendance are rebuilt on every import; manual ones are never touched.
ALTER TABLE hr.payroll_input
    ADD COLUMN source varchar(20) NOT NULL DEFAULT 'manual' CHECK (source IN ('manual', 'attendance'));
