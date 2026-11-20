# Deployment, health và observability

Tài liệu vận hành cho T30. `compose.yaml` là môi trường smoke/local production-like trên một máy, không phải kiến trúc HA production.

## Artifact và topology

- `HighConcurrencyCommerce.Api/Dockerfile`: build/publish .NET 10, runtime non-root, chỉ lắng nghe cổng 8080.
- `frontend/Dockerfile`: build React bằng `npm ci`, phục vụ static assets qua Nginx.
- Nginx giữ FE/API cùng origin và proxy `/api/*` tới API.
- PostgreSQL không publish cổng ra host trong Compose.
- API filesystem read-only; chỉ volume Data Protection keys và `/tmp` được ghi.
- Database data và Data Protection keys dùng named volumes riêng.

PostgreSQL 18 đổi volume chính thức sang `/var/lib/postgresql`; Compose dùng đúng mount này thay vì đường dẫn cũ `/var/lib/postgresql/data` ([official image documentation](https://github.com/docker-library/docs/blob/master/postgres/README.md#pgdata)).

Compose dùng service one-shot `migrator` chạy CLI `--migrate-database` trước API. API phụ thuộc `service_completed_successfully` và tự validation fail-fast lần nữa trước khi nhận traffic. Cơ chế này hoạt động cho cả database trống lẫn volume đã tồn tại; không còn phụ thuộc `/docker-entrypoint-initdb.d`.

## Chạy smoke stack local

```powershell
Copy-Item .env.example .env
# Sửa POSTGRES_PASSWORD trong .env thành chuỗi ngẫu nhiên dài.
docker compose config
docker compose build
docker compose up -d
docker compose ps
Invoke-RestMethod http://localhost:8080/healthz
Invoke-RestMethod http://localhost:8080/api/v1/catalog/categories
```

Compose đặt `Security__RequireHttps=false` chỉ để smoke trên `http://localhost`. Không sao chép lựa chọn này sang Internet. Xóa stack nhưng giữ dữ liệu bằng `docker compose down`; `docker compose down --volumes` xóa database và keys, chỉ dùng khi chủ động muốn reset môi trường local.

## Health contract

| Endpoint | Ý nghĩa | Dùng cho |
|---|---|---|
| `GET /health/live` | Process/middleware đang phục vụ | Liveness; restart khi fail liên tục |
| `GET /health/ready` | API kết nối được PostgreSQL | Readiness; ngừng nhận traffic khi fail |
| `GET /healthz` trên Nginx | Static web server đang sống | Web container health |

Hai endpoint API cho phép anonymous, trả JSON tối giản, `Cache-Control: no-store`, không trả connection string hoặc exception detail. Readiness không thay thế synthetic checkout hoặc monitoring business invariant.

## Correlation và structured logging

- Mọi response API có `X-Correlation-ID`.
- Request ID hợp lệ từ trusted edge được giữ lại; chỉ nhận 1–64 ký tự chữ/số/`._-`, giá trị khác được thay bằng GUID 32 hex.
- Problem Details dùng cùng correlation ID.
- Console log dùng JSON UTC và scope `CorrelationId`; Activity có TraceId/SpanId/ParentId.
- Nginx tạo `$request_id` ở edge. Log collector phải parse JSON theo từng dòng và redaction ở sink; không log cookie, CSRF token, password hoặc connection string.

## TLS và reverse proxy production

Production mặc định `Security:RequireHttps=true`; session/CSRF cookie dùng `Secure` và prefix `__Host-`. TLS phải kết thúc tại ingress/reverse proxy, chỉ proxy đó được truy cập API.

Khai báo chính xác IP proxy tin cậy để ASP.NET chấp nhận `X-Forwarded-For`/`X-Forwarded-Proto`:

```text
ReverseProxy__KnownProxies__0=10.0.0.10
AllowedHosts=shop.example.com
Security__RequireHttps=true
```

Không cấu hình `KnownProxies` thành toàn Internet và không expose trực tiếp cổng API. Nếu IP proxy thay đổi động, dùng ingress/network policy ổn định trước khi deploy; không tắt HTTPS để chữa redirect loop.

## Secrets và cấu hình bắt buộc

| Cấu hình | Nguồn production | Ghi chú |
|---|---|---|
| `ConnectionStrings__StoreDb` | Secret manager/injected secret | Không commit, không log |
| `DataProtection__KeysPath` | Persistent encrypted volume | Dùng chung giữa replica; backup có kiểm soát |
| `AllowedHosts` | Deployment config | Domain chính xác, không để `*` |
| `ReverseProxy__KnownProxies` | Deployment config | Chỉ IP ingress/proxy |
| `Security__RequireHttps` | `true` | Chỉ local smoke mới được `false` |
| PostgreSQL credential | Secret manager/managed identity | Role app không cần CREATEDB/superuser |

Startup fail-fast nếu thiếu `ConnectionStrings:StoreDb`. Data Protection keys phải bền vững qua restart để token antiforgery không vô hiệu ngoài ý muốn. Thay secret database cần rollout có kiểm chứng readiness; không ghi secret vào image hoặc `.env` được commit.

## Migration/release order

Schema dùng numbered SQL và history `public.schema_migrations`. Quy trình release production:

1. Backup và kiểm tra khả năng đọc archive/PITR.
2. Review migration mới trên bản sao dữ liệu, đặc biệt lock duration và backward compatibility.
3. Chạy artifact API ở chế độ one-shot: `dotnet HighConcurrencyCommerce.Api.dll --migrate-database` bằng credential có quyền DDL.
4. Chỉ rollout API khi migrator exit `0`; startup validation sẽ chặn thiếu migration, checksum drift hoặc downgrade binary.
5. Chạy `/health/ready`, smoke read-only rồi mở mutation/traffic.
6. Deploy FE; theo dõi error rate, latency, readiness và correlation logs.

Trong Compose, `docker compose up -d --build` tự thực hiện đúng thứ tự `db -> migrator -> api -> web`. Có thể xem kết quả bằng:

```powershell
docker compose ps -a
docker compose logs migrator
docker compose exec db psql -U $env:POSTGRES_USER -d $env:POSTGRES_DB -c "SELECT * FROM public.schema_migrations ORDER BY version"
```

Migration đã được ghi history là bất biến; sửa file cũ sẽ làm checksum guard từ chối startup. Mọi thay đổi schema mới phải có script số mới, forward/recovery notes và integration test. Rollback application chỉ khi schema còn backward-compatible; rollback database dùng restore/PITR hoặc migration forward sửa lỗi, không tự động chạy down script. Xem [ADR 0007](../adr/0007-versioned-database-migration-runner.md).

## Graceful shutdown và scale

API có shutdown timeout 30 giây. Orchestrator nên ngừng route traffic khi readiness fail, chờ tối thiểu 30 giây trước khi kill. Rate limiter hiện in-memory theo instance; khi scale ngang, giới hạn là per-replica. `CodExpiryWorker` có database locking/exactly-once guard nhưng cần theo dõi contention. Chưa tuyên bố HA/RPS/p95 cho đến khi có benchmark riêng.

`ProductAlertEvaluationWorker` và `CommerceEmailDeliveryWorker` dùng short lease PostgreSQL, hỗ trợ nhiều replica bằng `FOR UPDATE SKIP LOCKED`. Khi shutdown, worker cố giải phóng lease trong tối đa 5 giây; lease quá 5 phút được replica khác reclaim. Theo dõi backlog, retry/discard và xử lý sự cố theo [Wishlist Product Alerts runbook](wishlist-product-alerts.md); không sửa queue/outbox thủ công trong quy trình rollout.

