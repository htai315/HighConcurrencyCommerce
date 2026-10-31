# T25 — Kế hoạch và kết quả COD fulfillment

Ngày chốt: 30/10/2026. Trạng thái: hoàn tất.

## Mục tiêu

Hoàn thiện success path của đơn COD sau xác nhận: Staff tiếp nhận, đóng gói, bàn giao/xuất kho, ghi nhận giao thành công và tiền COD thực thu. Luồng phải chống double-submit/race và không hoàn tất đơn nếu chưa có chứng từ tiền đúng số tiền.

## Phạm vi đã thực hiện

1. Application contract và service PostgreSQL cho queue/start/pack/handover/delivery.
2. Staff-only API, CSRF cho mutation và ProblemDetails ổn định.
3. Row locking/state guard, ledger `STOCK_OUT`, audit và PaymentTransaction nguyên tử.
4. React Staff dashboard điều khiển tuần tự bốn bước, nhập mã biên nhận thu COD.
5. Integration tests cho RBAC, inventory invariant, amount mismatch rollback và concurrent double-delivery.
6. Frontend test xuyên suốt bốn bước, CSRF và payload thu tiền.

## Ngoài phạm vi

- Kết quả giao thất bại, lần giao lại và hoàn kho: T26.
- Customer tracking/cancel: task riêng.
- PayOS: deferred theo ADR 0003.

## Bằng chứng

- Backend: 102/102 integration tests pass.
- Frontend: 42/42 tests pass; lint và production build pass.
- Các race giao thành công đồng thời chỉ tạo một PaymentTransaction và một transition cuối.
