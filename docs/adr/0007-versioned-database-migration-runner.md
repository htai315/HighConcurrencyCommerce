# ADR 0007 — Dùng migrator có version, checksum và advisory lock

- Trạng thái: Accepted
- Ngày: 2026-11-05
- Task: T40

## Bối cảnh

PostgreSQL chỉ chạy `/docker-entrypoint-initdb.d` khi tạo volume lần đầu. Vì vậy volume cũ có thể chạy API mới trên schema cũ; tình huống thực tế đã làm T38/T39 trả HTTP 500 do thiếu cột `vouchers.name` và bảng `order_return_requests`.

## Quyết định

1. Mọi migration sản phẩm được nhúng vào Infrastructure assembly và có version ba chữ số.
2. `public.schema_migrations` lưu version, SHA-256 checksum, thời gian chạy, phiên bản ứng dụng và chế độ `APPLIED`/`BASELINE`.
3. CLI `--migrate-database` là writer schema duy nhất. Toàn bộ lượt chạy giữ cùng một PostgreSQL session advisory lock.
4. Compose chạy service one-shot `migrator`; API chỉ được khởi động khi migrator exit `0`.
5. API mặc định chạy validation fail-fast: thiếu history, thiếu migration, checksum drift hoặc database có version không được binary nhận diện đều từ chối startup.
6. Database legacy đã có `store.roles` nhưng chưa có history chỉ được baseline migration `001`; các migration cộng dồn sau đó vẫn được chạy lại và ghi history.
7. File migration đã được áp dụng là bất biến. Mọi thay đổi schema mới phải dùng version mới, không sửa file cũ.
8. Demo seed `007` không phải migration schema và không được ghi vào `schema_migrations`.

## Hệ quả

- Upgrade volume cũ và cài mới dùng cùng một đường chạy.
- Nhiều replica/migrator không thể áp migration đồng thời.
- Downgrade binary trên database mới hơn bị chặn có chủ đích.
- Rollback database vẫn dùng restore/PITR hoặc migration forward sửa lỗi; migrator không tự chạy down script.
- Production nên dùng credential DDL riêng cho migrator và credential DML hạn chế hơn cho API, dù Compose local dùng chung một credential.

