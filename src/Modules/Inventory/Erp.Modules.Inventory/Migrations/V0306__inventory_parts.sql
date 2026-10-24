-- Spare parts: the maker's brand on the item, the other part numbers it is sold under (the car maker's OEM number and
-- replacement brands' numbers) and the cars it fits, so a shop finds a part by any number or by "corolla 2015".
ALTER TABLE inv.item ADD COLUMN brand varchar(80);

CREATE TABLE inv.item_part_number (
    id          uuid PRIMARY KEY,
    tenant_id   uuid NOT NULL,
    item_id     uuid NOT NULL,
    part_no     varchar(60) NOT NULL,
    -- Upper case, letters and digits only: "04152-YZZA1", "04152 yzza1" and "04152YZZA1" are the same number.
    part_key    varchar(60) NOT NULL,
    brand       varchar(80),
    created_by  uuid NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  uuid,
    updated_at  timestamptz,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, item_id, part_key),
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_item_part_number_key ON inv.item_part_number (tenant_id, part_key);
SELECT core.enable_tenant_rls('inv', 'item_part_number');

-- Open-ended years: no "from" fits every earlier model year, no "to" every later one.
CREATE TABLE inv.item_fitment (
    id          uuid PRIMARY KEY,
    tenant_id   uuid NOT NULL,
    item_id     uuid NOT NULL,
    make        varchar(60) NOT NULL,
    model       varchar(60) NOT NULL,
    year_from   int CHECK (year_from BETWEEN 1950 AND 2100),
    year_to     int CHECK (year_to BETWEEN 1950 AND 2100),
    engine      varchar(60),
    created_by  uuid NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  uuid,
    updated_at  timestamptz,
    UNIQUE (tenant_id, id),
    CHECK (year_from IS NULL OR year_to IS NULL OR year_to >= year_from),
    FOREIGN KEY (tenant_id, item_id) REFERENCES inv.item (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_item_fitment_item ON inv.item_fitment (tenant_id, item_id);
SELECT core.enable_tenant_rls('inv', 'item_fitment');
