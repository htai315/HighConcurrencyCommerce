-- T22: explicit lease/ownership fields for the Admin PayOS reconciliation workflow.
ALTER TABLE store.payment_reconciliation_cases
    ADD COLUMN IF NOT EXISTS assigned_to uuid NULL REFERENCES store.user_accounts(id),
    ADD COLUMN IF NOT EXISTS assigned_at timestamptz NULL;

CREATE INDEX IF NOT EXISTS ix_reconciliation_assignee_open
    ON store.payment_reconciliation_cases(assigned_to, updated_at)
    WHERE case_status <> 'RESOLVED';

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_reconciliation_assignment'
          AND conrelid = 'store.payment_reconciliation_cases'::regclass
    ) THEN
        ALTER TABLE store.payment_reconciliation_cases
            ADD CONSTRAINT ck_reconciliation_assignment CHECK (
                (case_status = 'OPEN' AND assigned_to IS NULL AND assigned_at IS NULL)
                OR (case_status = 'IN_REVIEW' AND assigned_to IS NOT NULL AND assigned_at IS NOT NULL AND resolved_at IS NULL)
                OR (case_status = 'RESOLVED' AND resolved_by IS NOT NULL AND resolved_at IS NOT NULL)
            ) NOT VALID;
        -- Keep legacy rows deployable. PostgreSQL still enforces a NOT VALID
        -- constraint for every new or updated row; historical violations can be
        -- repaired explicitly before a later VALIDATE CONSTRAINT migration.
    END IF;
END $$;
