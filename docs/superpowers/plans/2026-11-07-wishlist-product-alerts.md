# T51 — Wishlist Product Alerts

Ngày lập: 07/11/2026. Trạng thái: **Hoàn tất — T51.1–T51.8**.

Phụ thuộc đã hoàn tất: T13/T14 Inventory, T27 Notifications, T33 Email delivery, T35 Release Gate, T37 Wishlist, T39 Returns, T46 Product Lifecycle, T50 Search & Discovery.

PayOS tiếp tục deferred theo ADR 0003. T51 không phụ thuộc nhà cung cấp thanh toán hoặc dịch vụ push bên ngoài.

## 1. Mục tiêu

Cho phép Customer đang lưu một sản phẩm trong Wishlist đăng ký hai loại cảnh báo:

1. `BACK_IN_STOCK`: sản phẩm chuyển từ không mua được sang có ít nhất một SKU `ACTIVE` với `available_qty > 0`.
2. `PRICE_DROP`: regular price thấp nhất của các SKU `ACTIVE` giảm so với lần quan sát gần nhất.

Mỗi cảnh báo luôn tạo notification in-app. Customer có thể bật thêm email nếu email tài khoản đã được xác minh. Hệ thống phải không bỏ sót thay đổi đã commit, không gửi lặp do worker retry và không giữ transaction nghiệp vụ trong lúc gọi SMTP.

## 2. Phạm vi và ngoài phạm vi

### Trong T51

- Bật/tắt từng loại cảnh báo trên Wishlist và Product Detail.
- Snapshot trạng thái ngay lúc bật; không gửi cảnh báo giả ngay khi đăng ký.
- Theo dõi thay đổi tồn kho, regular price, SKU status và Product lifecycle từ một dirty queue bền vững.
- Notification in-app có đường dẫn nội bộ tới Product Detail.
- Email tùy chọn qua commerce email outbox riêng, có lease/retry/backoff.
- Owner isolation, CSRF, optimistic concurrency, idempotency và rate/cap bảo vệ hệ thống.
- Integration, component và Playwright E2E trên PostgreSQL/Docker thật.

### Ngoài T51

- Web Push, SMS, mobile push, realtime WebSocket/SSE.
- Theo dõi Flash Sale price. Flash Sale có thời gian/quota riêng; trộn vào regular-price alert sẽ tạo cảnh báo hết hiệu lực hoặc không còn quota.
- Ngưỡng giá do Customer nhập, lịch sử/biểu đồ giá, daily digest và recommendation cá nhân hóa.
- Alert cho sản phẩm không nằm trong Wishlist, Guest wishlist hoặc nhiều wishlist đặt tên.
- Cam kết exactly-once với SMTP. Delivery email là at-least-once; business event và outbox record là exactly-once theo khóa logic.

## 3. Quyết định nghiệp vụ

1. Alert settings thuộc đúng `(customer_id, product_id)` của `customer_wishlist_items`. Bỏ sản phẩm khỏi Wishlist đồng thời xóa settings; không giữ subscription ẩn.
2. In-app là kênh bắt buộc khi một loại alert được bật. `emailEnabled` chỉ là bản sao bổ sung, không thể bật nếu cả hai loại alert đều tắt.
3. Email chỉ bật được khi `email_verified_at IS NOT NULL`. Google-only account đã verified hợp lệ. Nếu email bị mất trạng thái hợp lệ trước lúc phát, event in-app vẫn tạo và email bị bỏ qua an toàn.
4. Bật alert snapshot trạng thái hiện tại dưới transaction. Nếu sản phẩm đang còn hàng, không phát `BACK_IN_STOCK`; chỉ phát sau một chu kỳ `true -> false -> true`.
5. Availability cấp Product là:
   - Product `PUBLISHED`;
   - có ít nhất một SKU `ACTIVE`;
   - tổng điều kiện tồn là tồn tại SKU `ACTIVE` có `inventory_balances.available_qty > 0`.
   Không dùng `physical_qty`, `reserved_qty` hoặc tồn của SKU `INACTIVE`.
6. Product `SUSPENDED`, `ARCHIVED`, `DRAFT` hoặc không có SKU `ACTIVE` được quan sát là unavailable. Resume/publish trở lại với tồn khả dụng tạo một transition hợp lệ.
7. Giá quan sát là `MIN(regular_price)` của SKU `ACTIVE` khi Product đang `PUBLISHED`. Không dùng giá Flash Sale, voucher, cart snapshot hoặc giá đơn cũ.
8. `PRICE_DROP` phát khi `newMinPrice < lastObservedMinPrice`. Sau mỗi lần đánh giá committed, `lastObservedMinPrice` cập nhật thành giá hiện tại; một lần tăng giá rồi giảm lại được coi là transition mới.
9. Nhiều thay đổi bị coalesce trước khi worker chạy tạo tối đa một cảnh báo cho trạng thái cuối. Điều này có chủ đích; T51 không phải sổ cái giá/tồn.
10. Tắt rồi bật lại một alert tạo baseline mới và không replay các biến động xảy ra lúc tắt.
11. Product unavailable không lộ title/price mới qua Wishlist API. Notification chỉ được tạo khi Product public trở lại; nội dung lấy snapshot public tại thời điểm phát.
12. Mỗi Customer vẫn tối đa 500 Wishlist items như T37. T51 không tạo thêm cap subscription độc lập.

## 4. ADR và lock order

Tạo `docs/adr/0016-wishlist-product-alert-outbox.md` trước khi code. ADR chốt:

- PostgreSQL transactional dirty queue thay cho in-memory event/broker.
- Commerce email outbox tách khỏi `auth_email_outbox`; tuyệt đối không giả action token để gửi email thương mại.
- Trigger DB chỉ đánh dấu Product “dirty”, không chứa nghiệp vụ dựng nội dung hay gửi email.
- SMTP at-least-once và business notification/outbox exactly-once theo `event_key`.

Lock order:

- Customer thay settings: `UserAccount -> CustomerWishlistItem`.
- Worker đánh giá: claim queue trong transaction ngắn; transaction đánh giá chỉ khóa `CustomerWishlistItem` theo `(product_id, customer_id)` và không khóa Customer/Product/SKU/Inventory.
- Product/Inventory writers giữ lock order hiện tại và chỉ upsert dirty queue bằng trigger trong cùng transaction.
- Email worker claim bằng `FOR UPDATE SKIP LOCKED`, commit lease trước khi SMTP và persist từng item sau khi gửi.

Worker không được giữ Product/SKU/Inventory lock trong lúc quét subscriber, tránh đảo lock với Checkout/Inventory/Product Management.

## 5. Migration 021 và mô hình dữ liệu

Tạo migration additive, idempotent `database/021_wishlist_product_alerts.sql`.

### 5.1 Mở rộng Wishlist item

Thêm vào `store.customer_wishlist_items`:

- `back_in_stock_enabled boolean NOT NULL DEFAULT false`
- `price_drop_enabled boolean NOT NULL DEFAULT false`
- `email_enabled boolean NOT NULL DEFAULT false`
- `last_observed_in_stock boolean NULL`
- `last_observed_min_price numeric(18,2) NULL`
- `alert_sequence bigint NOT NULL DEFAULT 0`
- `alert_revision integer NOT NULL DEFAULT 0`
- `last_evaluated_at timestamptz NULL`
- `last_notified_at timestamptz NULL`
- `updated_at timestamptz NOT NULL DEFAULT now()`

Constraints:

- `email_enabled = false OR back_in_stock_enabled OR price_drop_enabled`.
- observed price `NULL OR >= 0`.
- sequence/revision không âm.

Index subscriber scan:

```sql
CREATE INDEX ... ON store.customer_wishlist_items(product_id, customer_id)
WHERE back_in_stock_enabled OR price_drop_enabled;
```

Migration không tự bật alert cho wishlist cũ và không phát event khi backfill.

### 5.2 Dirty queue

`store.product_alert_refresh_queue`:

- `product_id` PK/FK Product.
- `reason_mask integer NOT NULL` cho `INVENTORY=1`, `PRICE=2`, `LIFECYCLE=4`.
- `event_version bigint NOT NULL`, tăng mỗi lần upsert.
- `queued_at`, `locked_at`, `locked_by`, `attempt_count`, `next_attempt_at`, `last_error_code`.

Trigger transactionally upsert queue khi các giá trị liên quan thực sự đổi:

- `inventory_balances.available_qty`.
- `product_skus.regular_price`, `sku_status`, `product_id`.
- `products.publication_status`.

Trigger phải xử lý `INSERT/UPDATE/DELETE`, `OLD/NEW product_id`, chỉ dùng SQL tham số nội bộ và không query Customer.

Worker claim `(product_id, event_version)`. Sau đánh giá chỉ xóa queue row khi version vẫn bằng version đã claim. Nếu writer tăng version trong lúc xử lý, delete không khớp và Product được đánh giá lại; không mất wake-up.

### 5.3 Commerce email outbox

`store.commerce_email_outbox`:

- ID, recipient user ID, recipient email snapshot.
- `event_key`, `template_key`, subject/payload JSON đã sanitize.
- `created_at`, `next_attempt_at`, `attempt_count`.
- lease `locked_at/locked_by`.
- terminal `sent_at/discarded_at`, `last_error_code varchar(64)`.
- unique `(recipient_id, event_key)`.

Payload chỉ chứa product ID, public title, old/new price nếu cần và internal action path. Không chứa token bí mật hoặc dữ liệu tồn kho nội bộ.

### 5.4 Notification action URL

Mở rộng `in_app_notifications` với `action_url varchar(500) NULL`. Chỉ cho phép internal relative path bắt đầu bằng `/` và không bắt đầu `//`; service không nhận URL ngoài từ client. DTO cũ tương thích bằng field nullable.

## 6. Backend contracts và API

Policy `Customer`, prefix hiện tại `/api/v1/wishlist`. GET private `no-store`; PUT bắt buộc CSRF.

| Endpoint | Kết quả | Ghi chú |
|---|---|---|
| `GET /wishlist/items/{productId}/alerts` | 200 | Settings owner-only; 404 nếu item không thuộc Customer |
| `PUT /wishlist/items/{productId}/alerts` | 200 | Ghi toàn bộ settings, yêu cầu `expectedRevision` |

Request:

```json
{
  "backInStockEnabled": true,
  "priceDropEnabled": true,
  "emailEnabled": false,
  "expectedRevision": 3
}
```

Response:

```json
{
  "productId": "uuid",
  "backInStockEnabled": true,
  "priceDropEnabled": true,
  "emailEnabled": false,
  "currentAvailability": "OUT_OF_STOCK",
  "currentMinPrice": 1890000,
  "revision": 4,
  "updatedAt": "2026-11-07T10:00:00Z"
}
```

`WishlistItemDto` chứa cùng alert summary để trang `/wishlist` không N+1. `WishlistSummaryDto` bổ sung collection compact settings cho Product Detail/shared context; tối đa 500 items theo cap hiện tại.

Mã lỗi:

- `400 VALIDATION_ERROR`, `EMAIL_CHANNEL_REQUIRES_VERIFIED_EMAIL`.
- `401/403` chuẩn auth/RBAC.
- `404 WISHLIST_ITEM_NOT_FOUND` không phân biệt item của người khác.
- `409 STALE_WISHLIST_ALERT_SETTINGS` kèm current revision để UI reload.

PUT cùng payload/revision đã được commit không được âm thầm ghi lần hai. Client reload settings khi stale; không tự retry mutation với revision cũ.

## 7. Pipeline đánh giá và phát cảnh báo

Tạo `ProductAlertEvaluationWorker`:

1. Claim tối đa N Product rows đến hạn bằng short transaction + `FOR UPDATE SKIP LOCKED`; đặt lease ID rồi commit.
2. Ngoài transaction claim, đọc một snapshot public của Product: publication status, title, min active regular price và in-stock aggregate.
3. Xử lý subscriber theo batch ổn định `customer_id ASC`. Mỗi batch mở transaction ngắn và khóa các Wishlist rows mục tiêu `FOR UPDATE`.
4. Revalidate settings dưới khóa, so sánh snapshot với observed state.
5. Với mỗi transition:
   - tăng `alert_sequence`;
   - tạo `event_key = WISHLIST:{type}:{customerId}:{productId}:{sequence}`;
   - insert notification `ON CONFLICT DO NOTHING` với `action_url=/products/{id}`;
   - nếu email bật và còn verified, insert commerce outbox cùng transaction, cùng event key.
6. Cập nhật observed state/evaluated time trong cùng transaction.
7. Khi mọi subscriber hoàn tất, xóa queue theo `(product_id, claimed_event_version)`. Version mới hoặc lỗi giữ row để retry.

Yêu cầu worker:

- Lease expiry/recovery, exponential backoff + jitter, lỗi code sanitize <= 64.
- Cancellation/shutdown an toàn; không nuốt `OperationCanceledException` của host.
- Không gửi SMTP và không gọi mạng.
- Poison Product sau số lần lỗi không bị xóa mất; chuyển dead-letter/discarded state có log/health metric rõ ràng hoặc giữ retry với backoff hữu hạn theo ADR.
- Hai worker đồng thời claim batch rời nhau; cùng Product không phát duplicate.

Tạo `CommerceEmailDeliveryWorker` theo mô hình đã kiểm chứng ở auth worker:

- short claim transaction;
- SMTP ngoài transaction với timeout từng item;
- persist outcome và release lease từng email;
- tối đa 5 lần, backoff+jitter;
- mask recipient PII trong log;
- không ghi `Exception.Message` vào DB.

Có thể trích low-level SMTP transport dùng chung, nhưng không đổi contract/action-token semantics của auth recovery trong T51.

## 8. Frontend UX

### Wishlist

- Mỗi public card có control “Thông báo khi có hàng” và “Thông báo khi giảm giá”.
- Email là checkbox phụ “Gửi thêm qua email”, kèm trạng thái email đã xác minh.
- Product unavailable chỉ cho tắt settings hoặc bỏ lưu; không cho bật alert mới.
- Pending theo product ID; disable double submit; stale 409 reload và thông báo rõ.

### Product Detail

- Nếu Customer chưa lưu: CTA “Lưu vào yêu thích để nhận thông báo”, không tự bật alert ngầm.
- Nếu đã lưu: popover/dialog dùng cùng component settings.
- Guest chuyển login bằng safe `returnTo`; Staff/Admin không thấy controls Customer.

### Notification Center

- Hiển thị icon/type `WISHLIST_BACK_IN_STOCK` và `WISHLIST_PRICE_DROP`.
- Notification có action link nội bộ an toàn. Parser từ chối absolute/protocol-relative URL.
- Badge/unread dùng cơ chế hiện tại; sau T51 vẫn chưa cần realtime polling.

Accessibility:

- Fieldset/legend cho nhóm kênh; label rõ, focus visible.
- Dialog trap focus, Escape và trả focus trigger.
- Success/error dùng live region; không chỉ truyền đạt trạng thái bằng màu.

## 9. Test matrix bắt buộc

### Migration/persistence

1. Fresh, legacy upgrade, reapply và concurrent migration đều pass; wishlist cũ giữ nguyên và alert mặc định off.
2. Constraints/index/trigger tồn tại; trigger chỉ enqueue khi relevant value đổi.
3. Queue version tăng và reason mask hợp nhất; thay đổi xảy ra trong transaction rollback không để queue row.

### API/RBAC

4. Guest 401, Staff/Admin 403, Customer owner-only; CSRF bắt buộc.
5. Cross-owner GET/PUT trả 404; item không tồn tại 404.
6. Email chưa verified bị chặn; verified password/Google account được bật.
7. Concurrent PUT cùng revision: đúng một thắng, request còn lại 409; không lost update.
8. Xóa Wishlist cạnh tranh update/worker không 500 và cho kết quả tuần tự hợp lệ.

### Transition correctness

9. Opt-in không gửi ngay. Out-of-stock -> stock-in gửi đúng một alert.
10. Stock-in nhiều SKU/concurrent requests coalesce; chỉ một business notification/customer/transition.
11. Customer cancel, COD expiry, delivery return, customer return, exchange release và inventory adjustment làm tăng available đều đánh queue; chỉ transition false->true mới gửi.
12. Checkout/reservation làm available về 0 cập nhật baseline nhưng không gửi; lần restock kế tiếp gửi.
13. Price 2.000.000 -> 1.800.000 gửi; replay/same price/increase không gửi; 1.800.000 -> 1.700.000 gửi transition mới.
14. Giá Flash Sale/voucher không kích hoạt price alert.
15. SKU inactive, suspend/archive và resume đúng public predicate, không lộ unpublished Product.
16. Tắt alert trước worker evaluation không gửi. Tắt/bật lại reset baseline.

### Worker/outbox/concurrency

17. Hai evaluator claim disjoint; expired lease recovery; crash/retry không duplicate notification/outbox.
18. Queue version thay đổi trong lúc evaluate không bị xóa; Product được evaluate lại.
19. SMTP chậm không giữ DB transaction; persist per item; timeout/backoff/discard và error code <=64.
20. SMTP nhận rồi worker crash có thể gửi lại email nhưng chỉ một in-app/outbox business event; tài liệu nêu rõ at-least-once.
21. Worker shutdown release/recover lease; test fixture tắt hosted workers mặc định để suite deterministic.

### Frontend/E2E

22. Runtime parser, CSRF, stale reload, role visibility, verified-email guard và account-switch stale response.
23. Wishlist/PDP cùng settings state; keyboard/dialog/focus/mobile; safe internal notification link.
24. Playwright Flow 17 trên Docker thật:
   - Customer lưu Product hết hàng và bật hai alerts + email.
   - Staff stock-in; Customer nhận đúng một in-app notification và Mailpit nhận email.
   - Admin giảm regular price; Customer nhận price-drop notification/email với giá đúng.
   - Retry worker/reload không tạo bản ghi lặp.
   - Customer tắt alerts; thay đổi tiếp theo không phát.

Regression tối thiểu: Flow 1 auth/email, Flow 5 wishlist, Flow 8 inventory/adjustment liên quan, Flow 11 revision, Flow 12 lifecycle, Flow 16 search visibility; backend full suite, frontend full suite, lint/build/Compose và Graphify.

## 10. Chia task triển khai

| Task | Công việc | Điều kiện hoàn tất |
|---|---|---|
| T51.1 | ADR 0016 + migration 021 + EF mapping/fixture/Compose | **Hoàn tất 07/11/2026** — fresh/legacy/reapply/concurrent, trigger reason/version, rollback và constraints pass; full backend 416/416 |
| T51.2 | Alert contracts, API, service settings, RBAC/CSRF/no-store | **Hoàn tất 07/11/2026** — owner-only GET/PUT, verified email, lifecycle guard, baseline, replay idempotent và revision conflict; full backend 421/421 |
| T51.3 | Dirty queue triggers + ProductAlertEvaluationWorker | **Hoàn tất 07/11/2026** — short lease/SKIP LOCKED, subscriber batches, transition tồn/giá, atomic notification/outbox, version-safe complete, lease recovery/backoff; full backend 426/426 |
| T51.4 | Commerce email outbox/sender/worker | **Hoàn tất 07/11/2026** — short claim/SKIP LOCKED, SMTP ngoài transaction, timeout 30 giây từng item, persist outcome/release lease từng email, retry/backoff tối đa 5 lần, recipient/payload/internal URL revalidation và PII masking; 5 delivery tests, full backend 431/431 |
| T51.5 | Wishlist/PDP/Notifications frontend | **Hoàn tất 07/11/2026** — shared alert summaries không N+1, dialog responsive/accessibility/focus-return, verified-email guard, stale revision reload, Wishlist/PDP integration và safe internal notification CTA; frontend 149/149, backend 431/431 |
| T51.6 | Backend/FE hardening và fault injection | **Hoàn tất 07/11/2026** — concurrent remove/update, delete-after-snapshot, multi-SKU coalesce, reservation/release, price/lifecycle/Flash Sale/disable transitions, evaluator+SMTP cancellation lease release, verified-email UI và account-switch stale response; backend 441/441, frontend 151/151 |
| T51.7 | Playwright Flow 17 + regression | **Hoàn tất 08/11/2026** — Flow 17 chạy thật trên Docker/PostgreSQL/Mailpit: Customer bật cả hai cảnh báo cho sản phẩm hết hàng, Staff stock-in phát đúng một in-app + email, Admin giảm regular price phát đúng giá, reload/retry không nhân bản và opt-out chặn thay đổi sau. Regression Flow 1, 5, 8, 11, 12, 16 xanh; Flow 8 được cô lập khỏi thứ tự Catalog bằng fixture Product hai SKU cố định; Playwright discovery 19 tests/17 specs |
| T51.8 | API docs, operations, release gate, README/TASKS/Graphify | **Hoàn tất 08/11/2026** — bổ sung runbook giám sát backlog/lease/retry và phục hồi an toàn; đồng bộ API, deployment, E2E guide, release checklist; CI bắt buộc discover Flow 17 và chạy đủ Flows 1–17; harden timing assertions của Catalog router; release gate thực tế đạt backend 441/441, frontend 151/151, Playwright discovery 19 tests/17 specs, lint/build và hai cấu hình Compose sạch |

Thứ tự: T51.1 -> T51.2 -> T51.3 -> T51.4 -> T51.5 -> T51.6 -> T51.7 -> T51.8.

Không mở UI/email trước khi queue transition và idempotency tests của T51.3 xanh.

## 11. Files dự kiến

- `database/021_wishlist_product_alerts.sql`
- `docs/adr/0016-wishlist-product-alert-outbox.md`
- `Application/Wishlist/WishlistAlertContracts.cs`
- `Infrastructure/Wishlist/PostgresWishlistAlertService.cs`
- `Infrastructure/Wishlist/ProductAlertEvaluationWorker.cs`
- `Infrastructure/Notifications/CommerceEmailDeliveryWorker.cs`
- Persistence models/mapping cho refresh queue và commerce email outbox
- `Api/Controllers/WishlistController.cs`, exception handler và `Program.cs`
- Integration tests `WishlistProductAlertMigrationTests.cs`, `WishlistProductAlertTests.cs`, worker tests
- `frontend/src/features/wishlist/*`, `NotificationsPage.tsx`, Wishlist/PDP shared settings UI
- `frontend/src/test/wishlist-alerts.test.tsx`
- `frontend/e2e/17-wishlist-product-alerts.spec.ts`
- `docs/api/wishlist-alerts.md`, operations docs, README và TASKS

## 12. Release gate

Chỉ đóng T51 khi:

- Mọi mutation kho/giá/lifecycle trong source hiện tại được chứng minh đi qua trigger queue hoặc có test chỉ ra không ảnh hưởng availability/price.
- Không có SMTP/network call trong transaction nghiệp vụ hoặc transaction worker claim.
- Không duplicate in-app/outbox dưới retry, concurrency và fault injection.
- Không mất queue wake-up khi mutation xảy ra trong lúc worker evaluate.
- Owner isolation, lifecycle visibility và email verification guard đều có HTTP-level tests.
- Flow 17 và regression đã chạy trên Docker thật.
- Full backend/frontend/lint/build/Compose pass; Graphify cập nhật sau code cuối cùng.

## 13. Rủi ro và điểm dừng

- Nếu DB trigger làm migration/lock contention không đạt, dừng T51.3 để viết ADR thay thế bằng SaveChanges interceptor + explicit raw-SQL producers; không duy trì hai nguồn event song song.
- Nếu một Product có lượng subscriber lớn làm worker vượt lease, bổ sung cursor/continuation bền vững theo `customer_id`; không tăng lease vô hạn.
- Nếu email marketing cần consent pháp lý riêng, giữ email channel tắt mặc định và không triển khai gửi production trước khi policy/opt-out được chốt.
- Nếu production chứng minh PostgreSQL queue không đạt throughput, đo trước rồi mới ADR broker; T51 không thêm RabbitMQ/Kafka theo suy đoán.
