# T44 — Thương hiệu và thông số kỹ thuật sản phẩm

**Ngày:** 06/11/2026

## Mục tiêu

- Admin quản trị danh sách thương hiệu/nhà sản xuất dùng chung.
- Staff gắn thương hiệu và quản lý thông số kỹ thuật khi Product còn `DRAFT`.
- Storefront hỗ trợ lọc thương hiệu và trình bày bảng thông số trên PDP.

## Invariants

- Brand slug duy nhất; race create/update trả `409`, không 500.
- Không xóa Brand đang được Product tham chiếu; có thể deactivate để khỏi xuất hiện trong filter.
- Staff mutation luôn khóa Product trước, chỉ chạy khi `DRAFT`, tuần tự hóa với Admin Publish.
- Specification key duy nhất trong một Product, định dạng `snake_case`; upsert lũy đẳng.
- Brand/spec của sản phẩm `PUBLISHED` chỉ đọc; không thể sửa xuyên qua Publish.
- Create/update/delete Brand và specification mutation có Audit Log phù hợp.

## Definition of Done

- Migration 014 chạy qua versioned migrator và fixture.
- API Admin/Staff/Public, UI Admin/Staff/Storefront hoàn chỉnh.
- Integration tests phủ RBAC, lifecycle, duplicate race, in-use guard và Publish race.
- Frontend tests, Playwright flow 10, lint/build/Compose và Graphify xanh.
