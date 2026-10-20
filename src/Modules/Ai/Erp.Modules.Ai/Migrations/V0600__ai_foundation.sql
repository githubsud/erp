-- AI assistant. Platform providers (the vendor's free or self-hosted models) and daily usage are platform tables (no RLS);
-- each company's choice (platform provider or its own key) and its daily summaries are tenant tables.
CREATE SCHEMA IF NOT EXISTS ai;

CREATE TABLE ai.provider (
    id            uuid PRIMARY KEY,
    code          varchar(30) NOT NULL UNIQUE,
    name          varchar(100) NOT NULL,
    kind          varchar(30) NOT NULL CHECK (kind IN ('open_ai_compatible', 'anthropic')),
    base_url      varchar(300) NOT NULL,
    model         varchar(100) NOT NULL,
    vision_model  varchar(100),
    api_key       varchar(1000),
    is_private    boolean NOT NULL DEFAULT false,
    is_default    boolean NOT NULL DEFAULT false,
    is_enabled    boolean NOT NULL DEFAULT true,
    daily_limit   int NOT NULL DEFAULT 100 CHECK (daily_limit >= 0),
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz
);
CREATE UNIQUE INDEX ux_ai_provider_default ON ai.provider (is_default) WHERE is_default;

-- Google's free tier keeps what is sent, so it is not private: only figures and placeholders reach it.
INSERT INTO ai.provider (id, code, name, kind, base_url, model, vision_model, is_private, is_default, is_enabled, daily_limit) VALUES
    (gen_random_uuid(), 'gemini', 'Google Gemini (free tier)', 'open_ai_compatible', 'https://generativelanguage.googleapis.com/v1beta/openai/',
     'gemini-flash-latest', 'gemini-flash-latest', false, false, false, 50),
    (gen_random_uuid(), 'local', 'Private model on our server (Ollama)', 'open_ai_compatible', 'http://localhost:11434/v1/',
     'qwen2.5:3b', NULL, true, true, true, 200);

CREATE TABLE ai.usage_day (
    tenant_id      uuid NOT NULL,
    day            date NOT NULL,
    provider       varchar(30) NOT NULL,
    requests       int NOT NULL DEFAULT 0,
    input_tokens   bigint NOT NULL DEFAULT 0,
    output_tokens  bigint NOT NULL DEFAULT 0,
    PRIMARY KEY (tenant_id, day, provider)
);

CREATE TABLE ai.setting (
    id                  uuid PRIMARY KEY,
    tenant_id           uuid NOT NULL,
    company_id          uuid NOT NULL,
    mode                varchar(20) NOT NULL DEFAULT 'platform' CHECK (mode IN ('platform', 'own')),
    provider_code       varchar(30),
    kind                varchar(30) CHECK (kind IN ('open_ai_compatible', 'anthropic')),
    base_url            varchar(300),
    model               varchar(100),
    vision_model        varchar(100),
    api_key             varchar(1000),
    summary_enabled     boolean NOT NULL DEFAULT true,
    summary_hour        int NOT NULL DEFAULT 21 CHECK (summary_hour BETWEEN 0 AND 23),
    summary_language    varchar(2) NOT NULL DEFAULT 'ar' CHECK (summary_language IN ('ar', 'en')),
    summary_recipients  varchar(1000),
    created_by          uuid NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          uuid,
    updated_at          timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id),
    CHECK (mode <> 'own' OR (kind IS NOT NULL AND base_url IS NOT NULL AND model IS NOT NULL)),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('ai', 'setting');

CREATE TABLE ai.summary (
    id            uuid PRIMARY KEY,
    tenant_id     uuid NOT NULL,
    company_id    uuid NOT NULL,
    summary_date  date NOT NULL,
    language      varchar(2) NOT NULL CHECK (language IN ('ar', 'en')),
    text          text NOT NULL,
    facts         text NOT NULL,
    provider      varchar(30) NOT NULL,
    email_queued_at timestamptz,
    emailed_at    timestamptz,
    created_by    uuid NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_by    uuid,
    updated_at    timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, summary_date, language),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('ai', 'summary');

-- The assistant is part of every plan during the pilot; the back-office can take it out of a plan or a customer.
UPDATE core.plan SET modules = modules || ',ai' WHERE NOT ('ai' = ANY (string_to_array(modules, ',')));
UPDATE core.subscription SET modules = modules || ',ai' WHERE NOT ('ai' = ANY (string_to_array(modules, ',')));
