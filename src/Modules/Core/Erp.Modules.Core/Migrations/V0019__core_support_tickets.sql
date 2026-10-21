-- Support requests from customers to the vendor (problems, questions, suggestions, new requirements).
-- Platform tables like the subscription ones: no RLS, because the back-office works across customers. The customer side
-- always filters tenant_id explicitly (SupportService.Mine). Bodies and files stay in the database, so backups carry them.

-- How fast the first answer is promised, per plan (hours from the request).
ALTER TABLE core.plan ADD COLUMN support_response_hours int NOT NULL DEFAULT 24 CHECK (support_response_hours BETWEEN 1 AND 720);
UPDATE core.plan SET support_response_hours = 8 WHERE code = 'ENTERPRISE';
UPDATE core.plan SET support_response_hours = 48 WHERE code = 'PAYROLL';

CREATE SEQUENCE core.support_ticket_no START 1001;

CREATE TABLE core.support_ticket (
    id                    uuid PRIMARY KEY,
    tenant_id             uuid NOT NULL REFERENCES core.tenant (id),
    ticket_no             varchar(20) NOT NULL UNIQUE,
    type                  varchar(20) NOT NULL CHECK (type IN ('problem', 'question', 'suggestion', 'requirement')),
    priority              varchar(10) NOT NULL CHECK (priority IN ('low', 'normal', 'high', 'urgent')),
    status                varchar(20) NOT NULL CHECK (status IN ('new', 'in_progress', 'waiting_customer', 'solved', 'closed')),
    subject               varchar(200) NOT NULL,
    language              varchar(2) NOT NULL CHECK (language IN ('ar', 'en')),
    -- Where it happened, filled in by the app (no business figures).
    page                  varchar(300),
    device                varchar(300),
    requester_id          uuid NOT NULL,
    requester_name        varchar(200) NOT NULL,
    requester_email       varchar(200),
    assigned_admin_id     uuid REFERENCES core.platform_admin (id),
    -- Same request as another ticket: suggestions are counted on the first one.
    duplicate_of_id       uuid REFERENCES core.support_ticket (id),
    response_due_at       timestamptz NOT NULL,
    first_response_at     timestamptz,
    solved_at             timestamptz,
    last_activity_at      timestamptz NOT NULL,
    unread_by_customer    boolean NOT NULL DEFAULT false,
    unread_by_admin       boolean NOT NULL DEFAULT true,
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at            timestamptz,
    CHECK (duplicate_of_id IS NULL OR duplicate_of_id <> id)
);
CREATE INDEX ix_support_ticket_tenant ON core.support_ticket (tenant_id, last_activity_at DESC);
CREATE INDEX ix_support_ticket_status ON core.support_ticket (status, last_activity_at DESC);

CREATE TABLE core.support_message (
    id              uuid PRIMARY KEY,
    ticket_id       uuid NOT NULL REFERENCES core.support_ticket (id),
    author_kind     varchar(10) NOT NULL CHECK (author_kind IN ('customer', 'admin')),
    author_user_id  uuid,
    author_admin_id uuid REFERENCES core.platform_admin (id),
    author_name     varchar(200) NOT NULL,
    body            text NOT NULL,
    -- Notes between administrators; never shown to the customer.
    is_internal     boolean NOT NULL DEFAULT false,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz,
    CHECK (author_kind = 'admin' OR NOT is_internal)
);
CREATE INDEX ix_support_message_ticket ON core.support_message (ticket_id, created_at);

CREATE TABLE core.support_attachment (
    id            uuid PRIMARY KEY,
    ticket_id     uuid NOT NULL REFERENCES core.support_ticket (id),
    message_id    uuid NOT NULL REFERENCES core.support_message (id),
    file_name     varchar(200) NOT NULL,
    content_type  varchar(100) NOT NULL,
    size_bytes    int NOT NULL CHECK (size_bytes > 0),
    content       bytea NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz
);
CREATE INDEX ix_support_attachment_message ON core.support_attachment (message_id);

-- Ready answers the administrators reuse, in both languages.
CREATE TABLE core.support_saved_reply (
    id          uuid PRIMARY KEY,
    title       varchar(100) NOT NULL,
    body_ar     text NOT NULL,
    body_en     text NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz
);

INSERT INTO core.support_saved_reply (id, title, body_ar, body_en) VALUES
    (gen_random_uuid(), 'Received / تم الاستلام',
     'شكراً لتواصلكم. استلمنا طلبكم ونعمل عليه، وسنرد عليكم قريباً.',
     'Thank you for contacting us. We have received your request and are working on it; we will get back to you soon.'),
    (gen_random_uuid(), 'Need details / نحتاج تفاصيل',
     'شكراً لكم. حتى نساعدكم بشكل أسرع، يرجى إرسال صورة من الشاشة والخطوات التي قمتم بها قبل ظهور المشكلة.',
     'Thank you. To help you faster, please send a screenshot and the steps you took before the problem appeared.'),
    (gen_random_uuid(), 'Suggestion noted / تم تسجيل الاقتراح',
     'شكراً على اقتراحكم. أضفناه إلى قائمة التطوير، وسنبلغكم عند توفره.',
     'Thank you for the suggestion. We have added it to our development list and will let you know when it is available.'),
    (gen_random_uuid(), 'Fixed / تم الحل',
     'تم حل المشكلة. يرجى تحديث الصفحة والتجربة مرة أخرى، وإذا استمرت المشكلة ردّوا على هذا الطلب.',
     'The problem is fixed. Please refresh the page and try again; if it continues, reply to this request.');
