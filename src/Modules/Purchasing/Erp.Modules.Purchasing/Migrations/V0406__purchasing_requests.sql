-- Purchase requests: staff ask for items, the request goes through approval, and the buyer turns it into a purchase order.
CREATE TABLE pur.purchase_request (
    id                uuid PRIMARY KEY,
    tenant_id         uuid NOT NULL,
    company_id        uuid NOT NULL,
    pr_no             varchar(40) NOT NULL,
    request_date      date NOT NULL,
    required_date     date,
    warehouse_id      uuid NOT NULL,
    status            varchar(20) NOT NULL CHECK (status IN ('draft', 'pending_approval', 'approved', 'ordered', 'cancelled')),
    estimated_total   numeric(19,4) NOT NULL DEFAULT 0,
    notes             varchar(1000),
    requested_by      uuid NOT NULL,
    purchase_order_id uuid,
    created_by        uuid NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_by        uuid,
    updated_at        timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, pr_no),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, purchase_order_id) REFERENCES pur.purchase_order (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'purchase_request');

CREATE TABLE pur.purchase_request_line (
    id                  uuid PRIMARY KEY,
    tenant_id           uuid NOT NULL,
    purchase_request_id uuid NOT NULL,
    line_no             int NOT NULL,
    item_id             uuid NOT NULL,
    description         varchar(500),
    quantity            numeric(19,6) NOT NULL CHECK (quantity > 0),
    estimated_price     numeric(19,6) NOT NULL CHECK (estimated_price >= 0),
    uom_id              uuid,
    created_by          uuid NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          uuid,
    updated_at          timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, purchase_request_id) REFERENCES pur.purchase_request (tenant_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, uom_id) REFERENCES inv.uom (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('pur', 'purchase_request_line');

INSERT INTO core.role_permission (id, tenant_id, role_id, permission, created_by, created_at)
SELECT gen_random_uuid(), r.tenant_id, r.id, 'pur.requests:manage', '00000000-0000-0000-0000-000000000000', now()
  FROM core.role r
 WHERE r.code IN ('purchaser', 'storekeeper', 'general_manager', 'hr_manager', 'accountant')
   AND NOT EXISTS (SELECT 1 FROM core.role_permission x WHERE x.tenant_id = r.tenant_id AND x.role_id = r.id AND x.permission = 'pur.requests:manage');
