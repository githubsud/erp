-- Checkout (one attempt to buy a cart) and soft stock holds (design §4, §5.2). Phase 2: cash on delivery only. The delivery
-- address is a Qatar address (zone, street, building from the blue plate). The shopper follows the order with a status key whose
-- SHA-256 hash is stored.
CREATE TABLE commerce.checkout (
    id                 uuid PRIMARY KEY,
    tenant_id          uuid NOT NULL,
    company_id         uuid NOT NULL,
    store_id           uuid NOT NULL,
    cart_id            uuid NOT NULL,
    order_no           varchar(40) NOT NULL,
    status             varchar(20) NOT NULL CHECK (status IN ('placed', 'cancelled')),
    payment_method     varchar(20) NOT NULL CHECK (payment_method IN ('cod')),
    customer_name      varchar(200) NOT NULL,
    phone              varchar(40) NOT NULL,
    zone               varchar(10) NOT NULL,
    street             varchar(10) NOT NULL,
    building           varchar(10) NOT NULL,
    unit               varchar(20),
    address_notes      varchar(500),
    latitude           numeric(9,6) CHECK (latitude BETWEEN -90 AND 90),
    longitude          numeric(9,6) CHECK (longitude BETWEEN -180 AND 180),
    subtotal           numeric(19,4) NOT NULL,
    delivery_fee       numeric(19,4) NOT NULL,
    total              numeric(19,4) NOT NULL,
    priced_snapshot    jsonb NOT NULL,
    sales_order_id     uuid NOT NULL,
    status_token_hash  char(64) NOT NULL,
    created_by         uuid NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_by         uuid,
    updated_at         timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, company_id, order_no),
    UNIQUE (tenant_id, cart_id),
    UNIQUE (tenant_id, status_token_hash),
    FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, store_id) REFERENCES commerce.store (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, cart_id) REFERENCES commerce.cart (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, sales_order_id) REFERENCES sal.sales_order (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_checkout_store ON commerce.checkout (tenant_id, store_id, created_at DESC);
SELECT core.enable_tenant_rls('commerce', 'checkout');

-- A hold counts against available stock while it is held and not expired; with cash on delivery it turns into the sales order at
-- once (converted), whose undelivered quantity then counts instead.
CREATE TABLE commerce.reservation (
    id            uuid PRIMARY KEY,
    tenant_id     uuid NOT NULL,
    store_id      uuid NOT NULL,
    cart_id       uuid NOT NULL,
    checkout_id   uuid,
    item_id       uuid NOT NULL,
    warehouse_id  uuid NOT NULL,
    quantity      numeric(19,6) NOT NULL CHECK (quantity > 0),
    status        varchar(20) NOT NULL CHECK (status IN ('held', 'converted', 'released')),
    expires_at    timestamptz NOT NULL,
    created_by    uuid NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_by    uuid,
    updated_at    timestamptz,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, store_id) REFERENCES commerce.store (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, cart_id) REFERENCES commerce.cart (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, checkout_id) REFERENCES commerce.checkout (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES inv.warehouse (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_reservation_held ON commerce.reservation (tenant_id, item_id) WHERE status = 'held';
SELECT core.enable_tenant_rls('commerce', 'reservation');
