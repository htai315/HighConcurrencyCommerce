# Implementation Plan — T40: Versioned Database Migration & Deployment Gate

## Mục tiêu

Loại bỏ khả năng API mới chạy trên schema cũ và thay cơ chế init-volume bằng migration pipeline có version, checksum, khóa cụm và fail-fast.

## Invariants

- Tối đa một migrator được thay đổi schema tại một thời điểm.
- Migration thành công mới được ghi history; migration lỗi không được đánh dấu hoàn tất.
- Checksum đã ghi phải khớp byte-for-byte với artifact đang chạy.
- API không phục vụ traffic nếu database thiếu, drift hoặc mới hơn binary.
- Seed demo không thuộc lịch sử schema.

## Hạng mục

| ID | Nội dung | Tiêu chí nghiệm thu |
|---|---|---|
| T40.1 | Manifest embedded + SHA-256 + `schema_migrations` | Database trống áp đủ 001–011 (trừ seed 007) |
| T40.2 | Advisory lock + retry/failure recovery | Hai runner đồng thời không chạy trùng; lỗi có thể sửa và chạy lại |
| T40.3 | Legacy baseline + drift/downgrade guards | Volume cũ nâng cấp được; checksum/unknown version bị chặn |
| T40.4 | CLI `--migrate-database` + startup validation | CLI exit rõ ràng; API fail-fast khi schema chưa sẵn sàng |
| T40.5 | Compose one-shot migrator | API phụ thuộc `service_completed_successfully` |
| T40.6 | CI, operations docs và release gate | Fresh DB, legacy upgrade, replay và E2E đều pass |

## Không thuộc phạm vi

- Tự động rollback/down migration.
- Online DDL cho bảng dữ liệu rất lớn; từng migration mới vẫn phải review lock/time/capacity.
- Quản lý secret/credential ngoài nền tảng triển khai.

