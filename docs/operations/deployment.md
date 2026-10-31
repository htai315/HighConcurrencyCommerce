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

Compose chỉ khởi tạo `001_create_schema.sql` rồi `002_auth_sessions.sql` khi PostgreSQL volume hoàn toàn mới. API **không tự migrate khi startup**.

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

Hiện schema dùng numbered SQL, chưa có EF migration history. Vì vậy release production phải theo thứ tự:

1. Backup và kiểm tra khả năng đọc archive.
2. Review SQL trên bản sao dữ liệu; ghi SHA-256 artifact trong change ticket.
3. Dừng mutation hoặc dùng maintenance window nếu migration không backward-compatible.
4. Áp script theo số tăng dần bằng `psql -v ON_ERROR_STOP=1`; `001` chỉ cho database mới, `002` có thể chạy lại.
5. Kiểm tra schema/constraints, chạy `/health/ready` và smoke read-only.
6. Deploy API rồi FE; theo dõi error rate, latency, readiness và log correlation.
7. Rollback application chỉ khi schema còn backward-compatible; database recovery dùng restore/PITR, không tự ý chạy SQL đảo chiều.

Mọi thay đổi schema sau T30 phải có script số mới, forward/rollback notes và integration fixture tương ứng. Tuyệt đối không gọi `EnsureCreated`, không auto-migrate trong startup và không chạy baseline lên database đã có schema `store`.

## Graceful shutdown và scale

API có shutdown timeout 30 giây. Orchestrator nên ngừng route traffic khi readiness fail, chờ tối thiểu 30 giây trước khi kill. Rate limiter hiện in-memory theo instance; khi scale ngang, giới hạn là per-replica. `CodExpiryWorker` có database locking/exactly-once guard nhưng cần theo dõi contention. Chưa tuyên bố HA/RPS/p95 cho đến khi có benchmark riêng.

