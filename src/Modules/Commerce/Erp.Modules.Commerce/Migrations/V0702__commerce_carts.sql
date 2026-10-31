-- Server-side baskets for anonymous shoppers. The browser keeps a random cart key; only its SHA-256 hash is stored.
CREATE TABLE commerce.cart (
    id          uuid PRIMARY KEY,
    tenant_id   uuid NOT NULL,
    store_id    uuid NOT NULL,
    token_hash  char(64) NOT NULL,
    status      varchar(20) NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'checked_out')),
    expires_at  timestamptz NOT NULL,
    created_by  uuid NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  uuid,
    updated_at  timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, token_hash),
    FOREIGN KEY (tenant_id, store_id) REFERENCES commerce.store (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('commerce', 'cart');

CREATE TABLE commerce.cart_line (
    id          uuid PRIMARY KEY,
    tenant_id   uuid NOT NULL,
    cart_id     uuid NOT NULL,
    listing_id  uuid NOT NULL,
    item_id     uuid NOT NULL,
    uom_id      uuid,
    quantity    numeric(19,6) NOT NULL CHECK (quantity > 0),
    created_by  uuid NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  uuid,
    updated_at  timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE NULLS NOT DISTINCT (tenant_id, cart_id, item_id, uom_id),
    FOREIGN KEY (tenant_id, cart_id) REFERENCES commerce.cart (tenant_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, listing_id) REFERENCES commerce.listing (tenant_id, id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
SELECT core.enable_tenant_rls('commerce', 'cart_line');
