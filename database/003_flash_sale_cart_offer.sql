BEGIN;

ALTER TABLE store.cart_items
    ADD COLUMN IF NOT EXISTS flash_sale_offer_id uuid NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'cart_items_flash_sale_offer_id_fkey'
          AND conrelid = 'store.cart_items'::regclass
    ) THEN
        ALTER TABLE store.cart_items
            ADD CONSTRAINT cart_items_flash_sale_offer_id_fkey
            FOREIGN KEY (flash_sale_offer_id) REFERENCES store.flash_sale_offers(id);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_cart_items_flash_sale_offer_id
    ON store.cart_items(flash_sale_offer_id)
    WHERE flash_sale_offer_id IS NOT NULL;

COMMIT;
