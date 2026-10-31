# T14 — Inventory Adjustment Approval

**Goal:** Staff đề nghị giảm tồn; Admin duyệt/từ chối trước khi thay đổi có hiệu lực, không bao giờ giảm Reserved hoặc Allocated.

**Architecture:** `StaffInventoryController`/`AdminInventoryController` → `IInventoryService` → `PostgresInventoryService` → PostgreSQL `inventory_adjustment_requests`, `inventory_balances`, `inventory_movements`, `audit_logs`. React tích hợp form Staff và approval queue Admin.

## Invariants

1. Request mới luôn `PENDING`, `delta_qty < 0`, chưa tác động balance.
2. Chỉ Admin được quyết định; request chỉ quyết định một lần.
3. Approve khóa request, SKU và balance; kiểm tra `available >= abs(delta)` tại thời điểm commit.
4. Apply giảm đồng thời Physical/Available, giữ nguyên Reserved/Allocated, tăng Revision và ghi đúng một movement `ADJUST`.
5. Balance, request, movement và audit commit hoặc rollback cùng nhau.
6. Reject không tạo movement và không đổi balance.

## Verification

- RBAC Staff/Admin tách biệt.
- Pending không đổi tồn.
- Approve bảo toàn partition invariant và ghi movement/audit.
- Reject terminal, không side effect.
- Thiếu available trả conflict và request vẫn Pending.
- Hai approval song song chỉ apply một lần; stock-in cạnh approval không lost update.

