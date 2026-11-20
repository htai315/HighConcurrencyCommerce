# PayOS operations runbook

## Safe disable and rollback

Set `PAYOS_ENABLED=false` to stop new PayOS checkout/attempts. Keep the API, webhook ingress, expiry worker, Customer status endpoint and Admin reconciliation enabled for orders already created. Never roll back financial migrations or delete callback/transaction rows.

## Reconciliation triage

1. Open **Admin → Đối soát PayOS** and claim the case before changing its state.
2. Compare the order number, expected amount and sanitized transaction reference. Never request or store a customer's bank account credentials.
3. `AMOUNT_MISMATCH`: verify the provider dashboard/bank evidence. A false positive may be resolved with a decision note of at least five characters. If money was received and must be returned, use manual refund.
4. `LATE_PAID` or `DUPLICATE_COLLECTION`: the case cannot be closed without a manual-refund evidence reference.
5. Perform the refund outside this application through the approved bank/provider operations channel. Then record the exact evidence reference once. The application does not call a PayOS payout/refund API.
6. A successful mismatch/late-payment refund changes Payment to `REFUNDED`; a duplicate-collection refund keeps the original successful Payment intact. The exact case/transaction is resolved, an immutable Audit Log is written and the Customer is notified.

## Concurrency and recovery

- Two Admins cannot claim the same active lease. The database row lock decides the winner; the other request receives `409 CASE_ALREADY_CLAIMED`. A lease older than 30 minutes can be taken over through the same Claim action and produces `PAYOS_RECONCILIATION_REASSIGNED` audit.
- Evidence references and transaction refunds are unique. Retry after a timeout by reopening the case; do not invent another reference until the original outcome is checked.
- If a request returns `409 CASE_ALREADY_RESOLVED`, reload the detail and compare the recorded evidence.
- Do not update `payments`, `payment_reconciliation_cases`, `manual_refunds` or `payment_transactions` directly. Escalate for a forward migration or audited application command.

## Read-only checks

```sql
SELECT c.id,c.reason,c.case_status,c.created_at,c.assigned_at,
       o.order_number,p.payment_status,p.expected_amount
FROM store.payment_reconciliation_cases c
JOIN store.payments p ON p.id=c.payment_id
JOIN store.sales_orders o ON o.id=p.order_id
WHERE c.case_status <> 'RESOLVED'
ORDER BY c.created_at;
```

```sql
SELECT provider_transaction_id,amount,transaction_status,verified_at
FROM store.payment_transactions
WHERE payment_id = :payment_id
ORDER BY verified_at DESC;
```

## Metrics and alert thresholds

Runtime emits the .NET meter `HighConcurrencyCommerce.PayOs`:

- `payos.reconciliation.opened` tagged by `reason`;
- `payos.reconciliation.claimed` tagged `claimed`/`reassigned`;
- `payos.reconciliation.resolved` and `payos.reconciliation.resolution_age`;
- `payos.reconciliation.refunds` and `payos.reconciliation.refund_amount`.

Configure the deployment collector to alert when an open case is older than 15 minutes, any `LATE_PAID`/`DUPLICATE_COLLECTION` case is older than 5 minutes, reassignments spike above 3 in 15 minutes, or refund/error counters increase without a matching resolved counter. The application emits instruments; pager routing and retention remain deployment-infrastructure responsibilities.

## Credential rotation

Rotate Client ID/API key/checksum key through the deployment secret store. Keep checkout disabled during rotation, deploy the new secrets, verify provider capability and a signed webhook in the test channel, then re-enable gradually. Never put credentials in source, `.env.example`, logs or CI artifacts.

## Rollout stages

1. `Enabled=false`: migrations/runtime deployed; verify queue and webhook health.
2. Testing channel: internal Customer only; verify create → webhook → return → fulfillment and late-paid reconciliation.
3. Limited production allow-list: monitor pending orders, invalid/rejected webhooks and open-case age.
4. General availability only after the provider `PAID` query fixture and signed callback sandbox smoke are accepted under ADR 0017.
