# T42.1 — Direct Exchange Hardening & Release Acceptance

**Ngày:** 06/11/2026  
**Mục tiêu:** đóng toàn bộ khoảng trống giữa T42 happy-path và Definition of Done đã chốt trong kế hoạch T42.

## Phạm vi

1. **PostgreSQL concurrency/invariants**
   - Concurrent Admin approval tranh slot tồn cuối: một thành công, một `409`, không âm/leak tồn.
   - Inspection replay không tạo shipment hoặc allocation lần hai.
   - Handover replay/concurrency chỉ stock-out một lần.
   - Delivery retry giới hạn ba attempts; return-stock lũy đẳng và nhập quarantine đúng một lần.
   - Allocation expiry nhả `Reserved -> Available` và chuyển `EXCHANGE_EXPIRED` đúng một lần.
   - Payment của đơn đổi luôn giữ `COD_COLLECTED`; manual refund bị chặn.

2. **UI contract**
   - Customer phân biệt rõ “Hoàn tiền” và “Đổi SKU”, hiển thị tiến trình đổi hàng.
   - Admin thấy source -> target, tồn giữ/hạn giữ và xác nhận đúng tác động khi duyệt.
   - Staff có queue shipment thay thế với trạng thái/action rõ ràng.
   - Component tests bao phủ selection, Admin detail và Staff replacement fulfillment.

3. **E2E Docker**
   - Thêm `frontend/e2e/08-direct-product-exchange.spec.ts`.
   - Chạy Customer mua/nhận COD -> tạo exchange -> Admin approve -> Staff receive/inspect/pack/handover/deliver -> Customer thấy `EXCHANGED`.

4. **Release gate**
   - Full backend tests, frontend tests, oxlint, production build, Compose validation.
   - Chạy Playwright flow 08 trên demo stack.
   - Cập nhật README/TASKS/API và Graphify sau thay đổi cuối.

## Lock order bắt buộc

`SalesOrder -> OrderReturnRequest -> Payment (nếu cần) -> ExchangeAllocation -> ProductSku(sorted) -> InventoryBalance(sorted) -> ExchangeShipment`.

Không được gọi network bên ngoài khi transaction đang mở. Mọi retry phải không tạo thêm inventory movement, shipment hoặc delivery attempt ngoài contract.

## Definition of Done

- Mọi nhánh cạnh tranh nêu trên có integration test PostgreSQL thật.
- Inventory partition luôn thỏa `Physical = Available + Reserved + Allocated + Quarantined + Damaged`.
- Không có hai replacement shipment, hai stock-out hoặc hai return-in.
- Playwright flow 08 chạy xanh trên Docker, không mock API/DB.
- Tất cả release gates xanh và Graphify đã cập nhật.
