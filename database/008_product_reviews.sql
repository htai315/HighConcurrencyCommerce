-- 008_product_reviews.sql: Product Reviews & Ratings (T36)
-- Additive upgrade after 006; safe to reapply without altering existing data.
BEGIN;

CREATE TABLE IF NOT EXISTS store.product_reviews (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    customer_id uuid NOT NULL REFERENCES store.user_accounts(id),
    product_id uuid NOT NULL REFERENCES store.products(id),
    qualifying_order_item_id uuid NOT NULL REFERENCES store.sales_order_items(id),
    rating smallint NOT NULL CHECK (rating BETWEEN 1 AND 5),
    content varchar(2000) NOT NULL CHECK (char_length(btrim(content)) BETWEEN 10 AND 2000),
    review_status text NOT NULL DEFAULT 'PUBLISHED'
        CHECK (review_status IN ('PUBLISHED', 'HIDDEN')),
    moderated_by uuid REFERENCES store.user_accounts(id),
    moderated_at timestamptz,
    moderation_reason varchar(500),
    deleted_at timestamptz,
    CONSTRAINT ck_product_review_moderation CHECK (
        (review_status = 'PUBLISHED' AND moderated_at IS NULL AND moderation_reason IS NULL)
        OR
        (review_status = 'HIDDEN' AND moderated_by IS NOT NULL
            AND moderated_at IS NOT NULL AND moderation_reason IS NOT NULL)
    )
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_product_reviews_active_customer_product
    ON store.product_reviews(customer_id, product_id)
    WHERE deleted_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_product_reviews_public_product
    ON store.product_reviews(product_id, created_at DESC, id)
    WHERE review_status = 'PUBLISHED' AND deleted_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_product_reviews_moderation
    ON store.product_reviews(review_status, created_at DESC, id)
    WHERE deleted_at IS NULL;

COMMIT;

