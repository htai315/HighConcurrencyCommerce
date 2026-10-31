# T28 — Reporting, Staff tasks và Audit query

Ngày hoàn tất: 30/10/2026.

## Kết quả

1. Chốt revenue là expected amount của Payment hoàn tất có transaction VERIFIED khớp tiền; không đếm Order vừa tạo hoặc transaction dư/sai.
2. Chốt low-stock theo available của SKU sellable, threshold cấu hình mặc định 5.
3. Chốt quyền: Staff-only task queue; Admin-only reports/audit; không cho Admin mặc định kế thừa Staff endpoint.
4. Backend query có date/page guards, paging audit và response no-store.
5. Staff Dashboard có tab tổng hợp sáu nhóm tác vụ.
6. Admin Dashboard có doanh thu, tồn thấp và lọc Audit theo action.

## Bằng chứng

- Backend: 109/109 integration tests pass; ba test mới bao phủ RBAC, revenue exclusion khi số tiền VERIFIED sai, low-stock threshold và audit filtering/paging.
- Frontend: 46/46 tests pass; hai test mới bao phủ Staff task tab và Admin report/audit tab.
- .NET build 0 warning/error; React lint/build pass.
