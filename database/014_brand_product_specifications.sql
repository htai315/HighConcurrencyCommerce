CREATE TABLE IF NOT EXISTS store.brands (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    name varchar(160) NOT NULL CHECK (length(btrim(name)) >= 2),
    slug varchar(180) NOT NULL UNIQUE CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
    country varchar(100),
    website_url varchar(500),
    is_active boolean NOT NULL DEFAULT true
);

ALTER TABLE store.products ADD COLUMN IF NOT EXISTS brand_id uuid;
DO $$ BEGIN
    ALTER TABLE store.products ADD CONSTRAINT products_brand_id_fkey
        FOREIGN KEY (brand_id) REFERENCES store.brands(id) ON DELETE RESTRICT;
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;
CREATE INDEX IF NOT EXISTS ix_products_brand ON store.products(brand_id);

CREATE TABLE IF NOT EXISTS store.product_specifications (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    product_id uuid NOT NULL REFERENCES store.products(id) ON DELETE CASCADE,
    spec_key varchar(80) NOT NULL CHECK (spec_key ~ '^[a-z0-9]+(_[a-z0-9]+)*$'),
    display_name varchar(160) NOT NULL CHECK (length(btrim(display_name)) >= 1),
    spec_value varchar(500) NOT NULL CHECK (length(btrim(spec_value)) >= 1),
    display_order integer NOT NULL DEFAULT 0 CHECK (display_order BETWEEN 0 AND 10000),
    CONSTRAINT ux_product_specification_key UNIQUE(product_id, spec_key)
);
CREATE INDEX IF NOT EXISTS ix_product_specifications_order
    ON store.product_specifications(product_id, display_order, id);
