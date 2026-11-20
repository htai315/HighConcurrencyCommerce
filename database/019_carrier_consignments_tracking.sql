BEGIN;

CREATE TABLE IF NOT EXISTS store.carriers (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code varchar(32) NOT NULL UNIQUE,
    name varchar(120) NOT NULL,
    hotline varchar(32),
    tracking_url_template varchar(500),
    carrier_status varchar(16) NOT NULL DEFAULT 'INACTIVE',
    revision bigint NOT NULL DEFAULT 1,
    created_by uuid NOT NULL,
    updated_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT carriers_created_by_fkey FOREIGN KEY (created_by) REFERENCES store.user_accounts(id) ON DELETE RESTRICT,
    CONSTRAINT carriers_updated_by_fkey FOREIGN KEY (updated_by) REFERENCES store.user_accounts(id) ON DELETE RESTRICT,
    CONSTRAINT ck_carriers_code CHECK (code ~ '^[A-Z0-9_-]{2,32}$'),
    CONSTRAINT ck_carriers_name CHECK (btrim(name) <> ''),
    CONSTRAINT ck_carriers_hotline CHECK (hotline IS NULL OR btrim(hotline) <> ''),
    CONSTRAINT ck_carriers_tracking_url CHECK (
        tracking_url_template IS NULL
        OR (tracking_url_template LIKE 'https://%' AND strpos(tracking_url_template, '{trackingNumber}') > 0)
    ),
    CONSTRAINT ck_carriers_status CHECK (carrier_status IN ('ACTIVE', 'INACTIVE')),
    CONSTRAINT ck_carriers_revision CHECK (revision >= 1)
);

CREATE INDEX IF NOT EXISTS ix_carriers_status_name
    ON store.carriers(carrier_status, name, id);

CREATE TABLE IF NOT EXISTS store.carrier_consignments (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    sales_shipment_id uuid UNIQUE,
    exchange_shipment_id uuid UNIQUE,
    carrier_id uuid NOT NULL,
    tracking_number varchar(100) NOT NULL,
    consignment_status varchar(24) NOT NULL DEFAULT 'DRAFT',
    service_code varchar(64),
    recipient_name_snapshot varchar(200) NOT NULL,
    recipient_phone_snapshot varchar(32) NOT NULL,
    destination_snapshot text NOT NULL,
    shipping_method_code_snapshot varchar(32),
    shipping_method_name_snapshot varchar(120),
    label_file_key varchar(300),
    label_content_type varchar(80),
    label_size_bytes bigint,
    label_sha256 char(64),
    revision bigint NOT NULL DEFAULT 1,
    handed_over_at timestamptz,
    delivered_at timestamptz,
    cancelled_at timestamptz,
    created_by uuid NOT NULL,
    updated_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT carrier_consignments_sales_shipment_fkey FOREIGN KEY (sales_shipment_id) REFERENCES store.shipments(id) ON DELETE RESTRICT,
    CONSTRAINT carrier_consignments_exchange_shipment_fkey FOREIGN KEY (exchange_shipment_id) REFERENCES store.exchange_shipments(id) ON DELETE RESTRICT,
    CONSTRAINT carrier_consignments_carrier_fkey FOREIGN KEY (carrier_id) REFERENCES store.carriers(id) ON DELETE RESTRICT,
    CONSTRAINT carrier_consignments_created_by_fkey FOREIGN KEY (created_by) REFERENCES store.user_accounts(id) ON DELETE RESTRICT,
    CONSTRAINT carrier_consignments_updated_by_fkey FOREIGN KEY (updated_by) REFERENCES store.user_accounts(id) ON DELETE RESTRICT,
    CONSTRAINT ck_carrier_consignments_target_xor CHECK (
        (sales_shipment_id IS NOT NULL) <> (exchange_shipment_id IS NOT NULL)
    ),
    CONSTRAINT ck_carrier_consignments_tracking_number CHECK (
        btrim(tracking_number) = tracking_number
        AND length(tracking_number) BETWEEN 3 AND 100
        AND tracking_number ~ '^[A-Za-z0-9._:/-]+$'
    ),
    CONSTRAINT ck_carrier_consignments_status CHECK (consignment_status IN (
        'DRAFT', 'LABEL_READY', 'HANDED_OVER', 'IN_TRANSIT', 'OUT_FOR_DELIVERY',
        'DELIVERY_FAILED', 'DELIVERED', 'RETURNING', 'RETURNED', 'CANCELLED'
    )),
    CONSTRAINT ck_carrier_consignments_service_code CHECK (service_code IS NULL OR btrim(service_code) <> ''),
    CONSTRAINT ck_carrier_consignments_recipient CHECK (
        btrim(recipient_name_snapshot) <> ''
        AND btrim(recipient_phone_snapshot) <> ''
        AND btrim(destination_snapshot) <> ''
    ),
    CONSTRAINT ck_carrier_consignments_shipping_snapshot CHECK (
        (shipping_method_code_snapshot IS NULL) = (shipping_method_name_snapshot IS NULL)
    ),
    CONSTRAINT ck_carrier_consignments_label_metadata CHECK (
        (label_file_key IS NULL AND label_content_type IS NULL AND label_size_bytes IS NULL AND label_sha256 IS NULL)
        OR (
            btrim(label_file_key) <> ''
            AND label_content_type IN ('application/pdf', 'image/png', 'image/jpeg')
            AND label_size_bytes BETWEEN 1 AND 5242880
            AND label_sha256 ~ '^[0-9a-f]{64}$'
        )
    ),
    CONSTRAINT ck_carrier_consignments_revision CHECK (revision >= 1),
    CONSTRAINT ck_carrier_consignments_handover_time CHECK (
        (consignment_status IN ('DRAFT', 'LABEL_READY', 'CANCELLED') AND handed_over_at IS NULL)
        OR (consignment_status IN ('HANDED_OVER', 'IN_TRANSIT', 'OUT_FOR_DELIVERY', 'DELIVERY_FAILED', 'DELIVERED', 'RETURNING', 'RETURNED') AND handed_over_at IS NOT NULL)
    ),
    CONSTRAINT ck_carrier_consignments_delivered_time CHECK (
        (consignment_status = 'DELIVERED' AND delivered_at IS NOT NULL)
        OR (consignment_status <> 'DELIVERED' AND delivered_at IS NULL)
    ),
    CONSTRAINT ck_carrier_consignments_cancelled_time CHECK (
        (consignment_status = 'CANCELLED' AND cancelled_at IS NOT NULL)
        OR (consignment_status <> 'CANCELLED' AND cancelled_at IS NULL)
    ),
    CONSTRAINT ck_carrier_consignments_label_ready CHECK (
        consignment_status <> 'LABEL_READY' OR label_file_key IS NOT NULL
    )
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_carrier_consignments_tracking
    ON store.carrier_consignments(carrier_id, upper(tracking_number));

CREATE INDEX IF NOT EXISTS ix_carrier_consignments_sales_queue
    ON store.carrier_consignments(consignment_status, sales_shipment_id, id)
    WHERE sales_shipment_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_carrier_consignments_exchange_queue
    ON store.carrier_consignments(consignment_status, exchange_shipment_id, id)
    WHERE exchange_shipment_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS store.carrier_tracking_events (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    consignment_id uuid NOT NULL,
    event_key varchar(180) NOT NULL,
    event_type varchar(32) NOT NULL,
    event_status varchar(24) NOT NULL,
    location varchar(200),
    description varchar(500),
    occurred_at timestamptz NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT now(),
    source_type varchar(16) NOT NULL,
    actor_id uuid,
    external_event_id varchar(180),
    payload_hash char(64) NOT NULL,
    CONSTRAINT carrier_tracking_events_consignment_fkey FOREIGN KEY (consignment_id) REFERENCES store.carrier_consignments(id) ON DELETE RESTRICT,
    CONSTRAINT carrier_tracking_events_actor_fkey FOREIGN KEY (actor_id) REFERENCES store.user_accounts(id) ON DELETE RESTRICT,
    CONSTRAINT ux_carrier_tracking_event_key UNIQUE (consignment_id, event_key),
    CONSTRAINT ck_carrier_tracking_events_key CHECK (btrim(event_key) <> ''),
    CONSTRAINT ck_carrier_tracking_events_type CHECK (event_type IN (
        'CONSIGNMENT_CREATED', 'LABEL_CREATED', 'HANDED_OVER', 'PICKED_UP', 'IN_TRANSIT',
        'OUT_FOR_DELIVERY', 'DELIVERY_FAILED', 'REDELIVERY_STARTED', 'DELIVERED',
        'RETURNING', 'RETURNED', 'CANCELLED'
    )),
    CONSTRAINT ck_carrier_tracking_events_status CHECK (event_status IN (
        'DRAFT', 'LABEL_READY', 'HANDED_OVER', 'IN_TRANSIT', 'OUT_FOR_DELIVERY',
        'DELIVERY_FAILED', 'DELIVERED', 'RETURNING', 'RETURNED', 'CANCELLED'
    )),
    CONSTRAINT ck_carrier_tracking_events_location CHECK (location IS NULL OR btrim(location) <> ''),
    CONSTRAINT ck_carrier_tracking_events_description CHECK (description IS NULL OR btrim(description) <> ''),
    CONSTRAINT ck_carrier_tracking_events_source CHECK (source_type IN ('STAFF', 'SYSTEM')),
    CONSTRAINT ck_carrier_tracking_events_actor CHECK (
        (source_type = 'STAFF' AND actor_id IS NOT NULL)
        OR (source_type = 'SYSTEM' AND actor_id IS NULL)
    ),
    CONSTRAINT ck_carrier_tracking_events_external_id CHECK (external_event_id IS NULL OR btrim(external_event_id) <> ''),
    CONSTRAINT ck_carrier_tracking_events_payload_hash CHECK (payload_hash ~ '^[0-9a-f]{64}$')
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_carrier_tracking_external_event
    ON store.carrier_tracking_events(consignment_id, external_event_id)
    WHERE external_event_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_carrier_tracking_timeline
    ON store.carrier_tracking_events(consignment_id, occurred_at, id);

COMMIT;
