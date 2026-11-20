# T42 — Direct Product Exchange API

## Business contract

- Customer chooses `resolutionType = EXCHANGE` for an eligible delivered COD order.
- Every selected line provides `targetSkuId`. It must differ from the purchased SKU, belong to the same Product, and remain `ACTIVE` under a `PUBLISHED` Product.
- V1 exchanges directly without collecting or refunding a price difference. Original payment, voucher, and flash-sale quota are not rewritten.
- Admin approval atomically moves target inventory `Available -> Reserved` for seven days.
- Receiving source goods moves them into quarantine while payment stays `COD_COLLECTED`. Inspection moves target inventory `Reserved -> Allocated` and creates a replacement shipment.
- Handover decrements `Allocated` and `Physical` exactly once. A failed parcel returned to the warehouse enters quarantine.

## Customer routes

- `GET /api/v1/orders/{orderId}/return-eligibility` includes `exchangeTargets` per order item.
- `POST /api/v1/orders/{orderId}/returns`

```json
{
  "resolutionType": "EXCHANGE",
  "reasonCode": "WRONG_SIZE",
  "customerNote": "Cần đổi size XL",
  "items": [{ "orderItemId": "uuid", "requestedQuantity": 1, "targetSkuId": "uuid" }]
}
```

Details expose `resolutionType`, line-level `exchange`, allocation state/expiry, and `exchangeShipment`.

## Admin and inspection routes

- `POST /api/v1/admin/returns/{id}/approve` reserves replacement stock.
- `POST /api/v1/staff/returns/{id}/receive` receives source goods into quarantine.
- `POST /api/v1/staff/returns/{id}/inspect` classifies source goods and creates a replacement shipment.
- Manual refund is rejected with `EXCHANGE_REFUND_NOT_ALLOWED` for exchange requests.

## Replacement fulfillment routes (Staff only)

- `GET /api/v1/staff/exchange-shipments`
- `POST /api/v1/staff/exchange-shipments/{id}/pack`
- `POST /api/v1/staff/exchange-shipments/{id}/handover`
- `POST /api/v1/staff/exchange-shipments/{id}/delivery-result` with `DELIVERED` or `FAILED`
- `POST /api/v1/staff/exchange-shipments/{id}/retry` (maximum two re-deliveries)
- `POST /api/v1/staff/exchange-shipments/{id}/return-stock`

All mutations require CSRF. Customer routes enforce ownership; approval requires `ADMIN`; replacement fulfillment requires `STAFF`.

## State and concurrency

`REQUESTED -> APPROVED -> RECEIVED -> EXCHANGE_READY -> EXCHANGE_PACKED -> EXCHANGE_SHIPPING -> EXCHANGED`

Failure branches are `EXCHANGE_EXPIRED` and `EXCHANGE_DELIVERY_FAILED`. Allocation is `HELD -> ALLOCATED -> CONSUMED`, or `HELD -> RELEASED` on expiry.

Lock order is `SalesOrder -> OrderReturnRequest -> Payment (when needed) -> ExchangeAllocation -> ProductSku(sorted) -> InventoryBalance(sorted) -> ExchangeShipment`. PostgreSQL locks and unique movement identities protect approval, handover, expiry, and return-stock under retries.

## Release acceptance (T42.1)

- Concurrent approvals competing for the final target unit produce one success and one `409`, without negative or leaked inventory.
- Inspection and handover replay do not create duplicate shipments or inventory movements.
- Delivery permits one initial attempt plus at most two retries; failed-parcel return-stock is idempotent.
- Expired `HELD` allocations release `Reserved -> Available` exactly once and end in `EXCHANGE_EXPIRED`.
- Payment remains `COD_COLLECTED`; manual refund remains forbidden for direct exchanges.
- Playwright flow 08 verifies the complete Customer purchase/exchange, Admin approval, Staff receive/inspect/fulfill, and Customer `EXCHANGED` timeline against the Docker demo stack without mocked API or database.
