ALTER TABLE store.products
    DROP CONSTRAINT IF EXISTS products_publication_status_check;

ALTER TABLE store.products
    DROP CONSTRAINT IF EXISTS ck_products_publication_status;

ALTER TABLE store.products
    ADD CONSTRAINT ck_products_publication_status
    CHECK (publication_status IN ('DRAFT','PUBLISHED','SUSPENDED','ARCHIVED'));

ALTER TABLE store.products
    ADD COLUMN IF NOT EXISTS status_changed_at timestamptz,
    ADD COLUMN IF NOT EXISTS status_changed_by uuid,
    ADD COLUMN IF NOT EXISTS status_reason varchar(500);

UPDATE store.products
SET status_changed_at = COALESCE(published_at, updated_at, created_at)
WHERE status_changed_at IS NULL;

UPDATE store.products
SET status_changed_by = COALESCE(published_by, created_by)
WHERE status_changed_by IS NULL;

ALTER TABLE store.products
    ALTER COLUMN status_changed_at SET DEFAULT now(),
    ALTER COLUMN status_changed_at SET NOT NULL;

DO $$ BEGIN
    ALTER TABLE store.products
        ADD CONSTRAINT products_status_changed_by_fkey
        FOREIGN KEY (status_changed_by) REFERENCES store.user_accounts(id) ON DELETE RESTRICT;
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

CREATE INDEX IF NOT EXISTS ix_products_lifecycle_queue
    ON store.products(publication_status, status_changed_at DESC);
