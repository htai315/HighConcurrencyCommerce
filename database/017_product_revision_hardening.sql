CREATE TABLE IF NOT EXISTS store.product_media_cleanup_jobs (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    product_id uuid NOT NULL,
    revision_id uuid,
    storage_key varchar(700) NOT NULL,
    public_path text,
    cleanup_reason varchar(40) NOT NULL,
    job_status varchar(20) NOT NULL DEFAULT 'PENDING',
    attempt_count integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    locked_at timestamptz,
    locked_by uuid,
    last_error_code varchar(64),
    completed_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT product_media_cleanup_jobs_product_id_fkey
        FOREIGN KEY (product_id) REFERENCES store.products(id) ON DELETE RESTRICT,
    CONSTRAINT product_media_cleanup_jobs_revision_id_fkey
        FOREIGN KEY (revision_id) REFERENCES store.product_revisions(id) ON DELETE SET NULL,
    CONSTRAINT ck_product_media_cleanup_reason CHECK (cleanup_reason IN (
        'REVISION_IMAGE_DELETED','REVISION_REJECTED','REVISION_CANCELLED','UPLOAD_COMPENSATION')),
    CONSTRAINT ck_product_media_cleanup_status CHECK (job_status IN ('PENDING','PROCESSING','COMPLETED')),
    CONSTRAINT ck_product_media_cleanup_attempt CHECK (attempt_count >= 0)
);

CREATE INDEX IF NOT EXISTS ix_product_media_cleanup_queue
    ON store.product_media_cleanup_jobs(job_status, next_attempt_at, created_at, id);

CREATE INDEX IF NOT EXISTS ix_product_media_cleanup_revision
    ON store.product_media_cleanup_jobs(revision_id, created_at DESC);

CREATE UNIQUE INDEX IF NOT EXISTS ux_product_media_cleanup_active_storage_key
    ON store.product_media_cleanup_jobs(storage_key)
    WHERE job_status IN ('PENDING','PROCESSING');
