BEGIN;

-- T50 uses PostgreSQL-native search. Both extensions are trusted PostgreSQL
-- extensions and are installed explicitly so a missing capability fails during
-- migration rather than later on the public catalog path.
CREATE EXTENSION IF NOT EXISTS unaccent WITH SCHEMA public;
CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA public;

-- Generated columns require an immutable expression. public.unaccent(text) is
-- stable because it can accept a configurable dictionary, so this wrapper fixes
-- the project-wide dictionary and exposes the immutable contract we need.
CREATE OR REPLACE FUNCTION store.catalog_search_normalize(value text)
RETURNS text
LANGUAGE sql
IMMUTABLE
PARALLEL SAFE
AS $$
    SELECT btrim(regexp_replace(
        lower(public.unaccent('public.unaccent', coalesce(value, ''))),
        '[[:space:]]+',
        ' ',
        'g'))
$$;

ALTER TABLE store.products
    ADD COLUMN IF NOT EXISTS search_title_norm text
    GENERATED ALWAYS AS (store.catalog_search_normalize(title)) STORED;

ALTER TABLE store.product_skus
    ADD COLUMN IF NOT EXISTS search_sku_norm text
    GENERATED ALWAYS AS (store.catalog_search_normalize(sku_code)) STORED,
    ADD COLUMN IF NOT EXISTS search_option_norm text
    GENERATED ALWAYS AS (store.catalog_search_normalize(option_label)) STORED;

ALTER TABLE store.brands
    ADD COLUMN IF NOT EXISTS search_name_norm text
    GENERATED ALWAYS AS (store.catalog_search_normalize(name)) STORED;

ALTER TABLE store.categories
    ADD COLUMN IF NOT EXISTS search_name_norm text
    GENERATED ALWAYS AS (store.catalog_search_normalize(name)) STORED;

-- Trigram indexes serve accent-insensitive partial/fuzzy matches while partial
-- predicates keep the public discovery indexes focused on visible inventory.
CREATE INDEX IF NOT EXISTS ix_products_search_title_trgm
    ON store.products USING gin (search_title_norm public.gin_trgm_ops)
    WHERE publication_status = 'PUBLISHED';

CREATE INDEX IF NOT EXISTS ix_product_skus_search_sku_trgm
    ON store.product_skus USING gin (search_sku_norm public.gin_trgm_ops)
    WHERE sku_status = 'ACTIVE';

CREATE INDEX IF NOT EXISTS ix_product_skus_search_option_trgm
    ON store.product_skus USING gin (search_option_norm public.gin_trgm_ops)
    WHERE sku_status = 'ACTIVE';

CREATE INDEX IF NOT EXISTS ix_brands_search_name_trgm
    ON store.brands USING gin (search_name_norm public.gin_trgm_ops)
    WHERE is_active = true;

CREATE INDEX IF NOT EXISTS ix_categories_search_name_trgm
    ON store.categories USING gin (search_name_norm public.gin_trgm_ops)
    WHERE is_visible = true;

-- Supporting indexes for deterministic catalog filters, price sorting, and the
-- read-only popularity signal used by later T50 slices.
CREATE INDEX IF NOT EXISTS ix_product_skus_catalog_price
    ON store.product_skus(product_id, sku_status, regular_price, id);

CREATE INDEX IF NOT EXISTS ix_sales_order_items_product_sku_order
    ON store.sales_order_items(product_sku_id, order_id);

CREATE INDEX IF NOT EXISTS ix_sales_orders_completed_recent
    ON store.sales_orders(updated_at DESC, id)
    WHERE order_status = 'COMPLETED';

CREATE INDEX IF NOT EXISTS ix_shipments_delivered_order
    ON store.shipments(order_id)
    WHERE shipment_status = 'DELIVERED';

COMMIT;
