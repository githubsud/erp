-- Reports emailed on a schedule (daily, weekly or monthly) to a list of addresses. Each send checks that the schedule owner still
-- holds the report's permission.
CREATE TABLE core.report_schedule (
    id             uuid PRIMARY KEY,
    tenant_id      uuid NOT NULL,
    company_id     uuid NOT NULL,
    report_key     varchar(60) NOT NULL,
    frequency      varchar(20) NOT NULL CHECK (frequency IN ('daily', 'weekly', 'monthly')),
    weekday        int CHECK (weekday BETWEEN 0 AND 6),
    month_day      int CHECK (month_day BETWEEN 1 AND 28),
    recipients     varchar(1000) NOT NULL,
    owner_user_id  uuid NOT NULL,
    is_active      boolean NOT NULL DEFAULT true,
    next_run_on    date NOT NULL,
    last_sent_at   timestamptz,
    last_error     varchar(500),
    created_by     uuid NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_by     uuid,
    updated_at     timestamptz,
    UNIQUE (tenant_id, id),
    CHECK (frequency <> 'weekly' OR weekday IS NOT NULL),
    CHECK (frequency <> 'monthly' OR month_day IS NOT NULL),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, owner_user_id) REFERENCES core.app_user (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('core', 'report_schedule');
