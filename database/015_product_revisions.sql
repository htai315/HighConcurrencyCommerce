ALTER TABLE store.products
    ADD COLUMN IF NOT EXISTS content_version bigint NOT NULL DEFAULT 1;

DO $$ BEGIN
    ALTER TABLE store.products ADD CONSTRAINT ck_products_content_version CHECK (content_version > 0);
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

CREATE TABLE IF NOT EXISTS store.product_revisions (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(), created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
    product_id uuid NOT NULL REFERENCES store.products(id) ON DELETE RESTRICT,
    revision_no integer NOT NULL CHECK (revision_no > 0),
    revision_status text NOT NULL DEFAULT 'DRAFT' CHECK (revision_status IN ('DRAFT','PENDING_REVIEW','APPROVED','REJECTED','CANCELLED')),
    base_content_version bigint NOT NULL CHECK (base_content_version > 0),
    title varchar(240) NOT NULL CHECK (length(btrim(title)) >= 1), description text,
    category_id uuid REFERENCES store.categories(id) ON DELETE RESTRICT, brand_id uuid REFERENCES store.brands(id) ON DELETE RESTRICT,
    created_by uuid NOT NULL REFERENCES store.user_accounts(id) ON DELETE RESTRICT,
    submitted_by uuid REFERENCES store.user_accounts(id) ON DELETE RESTRICT, reviewed_by uuid REFERENCES store.user_accounts(id) ON DELETE RESTRICT,
    submitted_at timestamptz, reviewed_at timestamptz, review_note varchar(1000),
    CONSTRAINT ux_product_revision_number UNIQUE(product_id, revision_no)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_product_one_open_revision ON store.product_revisions(product_id) WHERE revision_status IN ('DRAFT','PENDING_REVIEW');
CREATE INDEX IF NOT EXISTS ix_product_revisions_review_queue ON store.product_revisions(revision_status, submitted_at, id);

CREATE TABLE IF NOT EXISTS store.product_revision_images (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(), created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
    revision_id uuid NOT NULL REFERENCES store.product_revisions(id) ON DELETE CASCADE,
    source_image_id uuid REFERENCES store.product_images(id) ON DELETE SET NULL,
    file_key text NOT NULL CHECK (length(btrim(file_key)) >= 1), image_status text NOT NULL DEFAULT 'ACTIVE' CHECK (image_status IN ('ACTIVE','INACTIVE')),
    alt_text varchar(240), display_order integer NOT NULL DEFAULT 0 CHECK (display_order BETWEEN 0 AND 10000), is_new_upload boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_product_revision_images_order ON store.product_revision_images(revision_id, display_order, id);

CREATE TABLE IF NOT EXISTS store.product_revision_specifications (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(), created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
    revision_id uuid NOT NULL REFERENCES store.product_revisions(id) ON DELETE CASCADE,
    spec_key varchar(80) NOT NULL CHECK (spec_key ~ '^[a-z0-9]+(_[a-z0-9]+)*$'),
    display_name varchar(160) NOT NULL CHECK (length(btrim(display_name)) >= 1), spec_value varchar(500) NOT NULL CHECK (length(btrim(spec_value)) >= 1),
    display_order integer NOT NULL DEFAULT 0 CHECK (display_order BETWEEN 0 AND 10000),
    CONSTRAINT ux_product_revision_specification_key UNIQUE(revision_id, spec_key)
);
CREATE INDEX IF NOT EXISTS ix_product_revision_specs_order ON store.product_revision_specifications(revision_id, display_order, id);
