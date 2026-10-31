# T17 — Multi-SKU checkout transaction và snapshots

## Mục tiêu

Hiện thực checkout core nhiều SKU theo ADR 0002: không oversell, không giữ một phần, không tin dữ liệu giá từ client và retry không tạo Order trùng.

## Phần đã thực hiện

1. Application DTO/service contract cho explicit checkout lines.
2. Customer-only controller, CSRF, Idempotency-Key validation và RFC 7807 errors.
3. Canonical SHA-256 payload và replay `SUCCEEDED`/`REJECTED`.
4. Customer/SKU/Inventory row locks theo thứ tự ổn định.
5. Savepoint giữ rejection record nhưng rollback toàn bộ commerce side effects.
6. Snapshot địa chỉ, title, SKU code/option và giá tại commit.
7. Order, Items, Payment, Reservation/Lines, InventoryBalance và movement `RESERVE` trong một transaction.
8. COD 24 giờ và PayOS 15 phút; không external call trong transaction.
9. React Cart checkout: địa chỉ, COD/PayOS, idempotency key bền qua ambiguous retry và màn hình kết quả.
10. Integration tests cho RBAC, snapshot, AON rollback, replay/conflict, concurrent same-key và last-unit race.

## Chuyển tiếp

T18 dùng Order/Payment/Reservation đã tạo để triển khai COD pending limit, confirm, expiry và release exactly once. T19–T20 mới được gọi PayOS.
