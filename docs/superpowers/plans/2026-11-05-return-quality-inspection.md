# T41 — Returned Goods Quality Inspection & Quarantine

## Mục tiêu

Ngăn hàng trả chưa kiểm tra quay lại tồn bán, bảo toàn partition tồn kho dưới concurrency và tạo dấu vết kiểm định trước khi hoàn tiền COD.

## Phạm vi

1. Migration 012: phân vùng `quarantined_qty`, `damaged_qty`, trạng thái `INSPECTED`, snapshot phân loại và movement types.
2. EF Core mapping và DTO cho tồn kho/inspection.
3. Staff receive vào quarantine; API chi tiết và `POST /staff/returns/{id}/inspect`.
4. Phân loại RESTOCK/QUARANTINE/DAMAGED/SCRAPPED nguyên tử, idempotent và có audit.
5. Admin refund guard chỉ nhận `INSPECTED`.
6. UI Staff hai bước; UI Admin/Customer và bảng kho hiển thị state/partition mới.
7. Integration tests PostgreSQL cho migration, state transition, bốn disposition, replay/conflict và concurrent inspection.
8. Cập nhật Playwright E2E, API docs, README/TASKS và Graphify.

## Acceptance criteria

- Sau receive: available không đổi, physical/quarantined tăng đúng.
- Tổng disposition mỗi line bằng requested quantity.
- Partition invariant luôn đúng và không có số âm.
- Concurrent replay cùng payload không áp dụng hai lần; payload khác trả 409.
- Refund trước inspection bị từ chối.
- Toàn bộ release gates hiện có vẫn xanh.
