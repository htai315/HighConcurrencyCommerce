# Customer Order Center — implementation record

Ngày hoàn tất: 30/10/2026.

## Phạm vi

- Customer xem lịch sử và chi tiết đơn của chính mình tại `/orders`.
- Hiển thị riêng Order, Payment, Reservation và Shipment status.
- Dùng checkout snapshots cho sản phẩm, giá và địa chỉ.
- Customer hủy trực tiếp đơn COD khi còn chờ Staff xác nhận.
- Owner isolation, CSRF, no-store, idempotent replay và race cancel-vs-confirm được kiểm thử.

## Quyết định nghiệp vụ

Sau khi Staff xác nhận COD, Customer không còn được hủy trực tiếp vì tồn đã chuyển từ Reserved sang Allocated và fulfillment có thể bắt đầu. Các luồng hủy sau xác nhận cần một workflow/approval riêng; không được lách qua endpoint này.

Cancel trước xác nhận là transaction nguyên tử: Reserved → Available, Reservation → RELEASED, Payment → VOID, Order → CANCELLED, đồng thời ghi ledger/audit/evidence/notification. Order là khóa cha dùng chung với Staff confirmation nên hai thao tác cạnh tranh không thể cùng thành công.

## Bằng chứng

- 4 backend integration tests mới: auth/owner/snapshot/no-store; cancel atomically + replay; chặn cancel sau confirm; race cancel-vs-confirm.
- 2 frontend tests mới: render history/detail/state boundaries; cancel gửi CSRF và cập nhật UI.
- Full suite, lint, build và Graphify được chạy trước bàn giao; số cuối được ghi trong `docs/TASKS.md`.

