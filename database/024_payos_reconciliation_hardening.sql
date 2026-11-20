-- T22 hardening: bind each case/refund to the exact provider transaction.
ALTER TABLE store.payment_reconciliation_cases
    ADD COLUMN IF NOT EXISTS transaction_id uuid NULL
        REFERENCES store.payment_transactions(id);

CREATE UNIQUE INDEX IF NOT EXISTS ux_reconciliation_transaction
    ON store.payment_reconciliation_cases(transaction_id)
    WHERE transaction_id IS NOT NULL;

ALTER TABLE store.manual_refunds
    ADD COLUMN IF NOT EXISTS reconciliation_case_id uuid NULL
        REFERENCES store.payment_reconciliation_cases(id);

CREATE UNIQUE INDEX IF NOT EXISTS ux_manual_refunds_reconciliation_case
    ON store.manual_refunds(reconciliation_case_id)
    WHERE reconciliation_case_id IS NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_manual_refunds_evidence_nonblank'
          AND conrelid = 'store.manual_refunds'::regclass
    ) THEN
        ALTER TABLE store.manual_refunds
            ADD CONSTRAINT ck_manual_refunds_evidence_nonblank
            CHECK (length(btrim(evidence_reference)) >= 3) NOT VALID;
    END IF;
END $$;
