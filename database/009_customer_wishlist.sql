-- 009_customer_wishlist.sql: Customer Wishlist (T37)
-- Additive upgrade after 008; safe to reapply without altering existing data.
BEGIN;

CREATE TABLE IF NOT EXISTS store.customer_wishlist_items (
    customer_id uuid NOT NULL REFERENCES store.user_accounts(id),
    product_id uuid NOT NULL REFERENCES store.products(id),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (customer_id, product_id)
);

CREATE INDEX IF NOT EXISTS ix_customer_wishlist_created
    ON store.customer_wishlist_items(customer_id, created_at DESC, product_id);

COMMIT;

