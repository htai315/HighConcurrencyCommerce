# Wishlist Product Alerts — T51.2–T51.4

## Phạm vi

Customer quản lý cảnh báo cho Product đã nằm trong Wishlist của chính mình. Settings/baseline thuộc T51.2, evaluator thuộc T51.3 và commerce email delivery thuộc T51.4.

- Policy: `Customer`.
- Prefix: `/api/v1/wishlist/items/{productId}/alerts`.
- GET và PUT trả `Cache-Control: no-store`.
- PUT bắt buộc session CSRF token trong `X-CSRF-TOKEN`.
- `customerId` luôn lấy từ authenticated session, không nhận từ route/body.

## Business rules

1. Wishlist item phải thuộc Customer hiện tại. Item không tồn tại hoặc thuộc người khác đều trả `404 WISHLIST_ITEM_NOT_FOUND`.
2. `BACK_IN_STOCK` dùng public Product availability: `PUBLISHED`, có SKU `ACTIVE`, và ít nhất một SKU ACTIVE có `available_qty > 0`.
3. `PRICE_DROP` snapshot regular price thấp nhất của SKU `ACTIVE`; không dùng Flash Sale/voucher.
4. Bật alert lần đầu chỉ ghi baseline, không tạo notification ngay.
5. Email chỉ bật khi có ít nhất một loại alert và `email_verified_at` của account khác null.
6. Product unavailable không cho bật loại alert/kênh email mới, nhưng settings đang có vẫn được tắt.
7. PUT dùng optimistic `expectedRevision`. Payload khác với state hiện tại nhưng revision cũ trả 409 và `currentRevision`.
8. Replay đúng payload đã commit với revision cũ trả state hiện tại, không tăng revision lần hai.
9. Mutation lock theo `UserAccount -> CustomerWishlistItem`; baseline và flags cùng commit.

## GET `/api/v1/wishlist/items/{productId}/alerts`

Response `200 OK`:

```json
{
  "productId": "b0000000-0000-0000-0000-000000000001",
  "backInStockEnabled": true,
  "priceDropEnabled": true,
  "emailEnabled": false,
  "currentAvailability": "OUT_OF_STOCK",
  "currentMinPrice": 1890000,
  "revision": 2,
  "updatedAt": "2026-11-07T12:00:00Z"
}
```

`currentAvailability` là `AVAILABLE`, `OUT_OF_STOCK` hoặc `PRODUCT_UNAVAILABLE`. Khi Product không public, `currentMinPrice` là null và không trả metadata private khác.

## PUT `/api/v1/wishlist/items/{productId}/alerts`

Request:

```json
{
  "backInStockEnabled": true,
  "priceDropEnabled": true,
  "emailEnabled": true,
  "expectedRevision": 0
}
```

Response `200 OK` dùng cùng shape với GET. No-op/replay không tăng revision. Update thành công tăng revision đúng một lần và cập nhật:

- `last_observed_in_stock` khi bật `BACK_IN_STOCK`;
- `last_observed_min_price` khi bật `PRICE_DROP`;
- observed field về null khi loại alert tương ứng bị tắt;
- `last_evaluated_at`, `updated_at`.

## Wishlist list/summary extension

Mỗi `WishlistItemDto` bổ sung:

```json
"alerts": {
  "productId": "uuid",
  "backInStockEnabled": false,
  "priceDropEnabled": false,
  "emailEnabled": false,
  "revision": 0
}
```

`WishlistSummaryDto` bổ sung collection `alerts` compact tương ứng tối đa 500 Product IDs. Frontend T51.5 nạp collection này một lần vào `WishlistContext`; Wishlist/PDP chỉ gọi GET settings chi tiết khi người dùng mở dialog, không gọi N+1 khi render danh sách.

## Error contract

Mọi lỗi dùng RFC 7807 và có `code`, `correlationId`.

| HTTP | Code | Ý nghĩa |
|---|---|---|
| 400 | `VALIDATION_ERROR` | Body/revision không hợp lệ |
| 400 | `INVALID_ALERT_SETTINGS` | Email bật nhưng không có loại alert |
| 400 | `EMAIL_CHANNEL_REQUIRES_VERIFIED_EMAIL` | Account chưa xác minh email |
| 400 | `CSRF_INVALID` | Thiếu/sai CSRF token |
| 401 | chuẩn auth | Chưa đăng nhập |
| 403 | chuẩn auth | Không phải Customer |
| 404 | `WISHLIST_ITEM_NOT_FOUND` | Không tồn tại hoặc không thuộc owner |
| 409 | `WISHLIST_ALERT_PRODUCT_UNAVAILABLE` | Bật alert/kênh mới cho Product không public |
| 409 | `STALE_WISHLIST_ALERT_SETTINGS` | Revision cũ; response thêm `currentRevision` |
| 409 | `WISHLIST_ALERT_REVISION_EXHAUSTED` | Revision đạt giới hạn integer |

Ví dụ stale:

```json
{
  "status": 409,
  "code": "STALE_WISHLIST_ALERT_SETTINGS",
  "currentRevision": 4,
  "correlationId": "..."
}
```

## Evaluation pipeline — T51.3

`ProductAlertEvaluationWorker` claim tối đa 10 Product bằng transaction ngắn và `FOR UPDATE SKIP LOCKED`. Lease hết hạn sau 5 phút có thể được worker khác phục hồi.

Với mỗi Product, worker đọc snapshot public rồi xử lý subscriber theo batch 100, khóa Wishlist rows theo `customer_id` tăng dần. Hai transition được phát:

- `WISHLIST_BACK_IN_STOCK`: observed false chuyển thành public/in-stock true.
- `WISHLIST_PRICE_DROP`: min regular price public mới nhỏ hơn observed price.

Observed state, `alert_sequence`, notification in-app và commerce email outbox cùng commit. Email outbox chỉ được tạo nếu setting email vẫn bật và email account còn verified tại thời điểm phát. Notification có internal action path `/products/{id}`.

Complete xóa queue bằng `(product_id, claimed_event_version, locked_by)`. Nếu version đã tăng trong lúc xử lý, worker chỉ giải phóng lease và giữ row để đánh giá lại. Exception tăng attempt, lưu error type đã sanitize và lên lịch exponential backoff; không xóa queue.

## Commerce email delivery — T51.4

`CommerceEmailDeliveryWorker` claim tối đa 5 outbox rows đến hạn bằng transaction ngắn và `FOR UPDATE SKIP LOCKED`, gắn lease 5 phút rồi commit. SMTP luôn chạy ngoài database transaction với timeout 30 giây cho từng email.

Trước khi gửi, worker revalidate account còn `ACTIVE`, email đã verified và vẫn trùng `recipient_email` snapshot. Chỉ hai template `WISHLIST_BACK_IN_STOCK` và `WISHLIST_PRICE_DROP` được chấp nhận; payload phải có title chuỗi hợp lệ và internal `actionPath` bắt đầu bằng một dấu `/`. Absolute/protocol-relative URL, backslash, whitespace và `FrontendBaseUrl` không phải HTTP(S) tuyệt đối đều bị discard.

Outcome được persist và lease được giải phóng ngay sau từng email, không chờ hết batch. Lỗi tạm thời retry với exponential backoff + jitter, tối đa 5 lần; DB chỉ lưu error code đã sanitize tối đa 64 ký tự, không lưu `Exception.Message`. Lease quá hạn được worker khác phục hồi. Log che địa chỉ người nhận và SMTP giữ delivery semantic at-least-once khi process crash sau khi SMTP nhận nhưng trước khi DB commit.

Khi host cancellation, evaluator và commerce delivery cố giải phóng các lease còn thuộc worker trong một cleanup timeout 5 giây. Nếu database không sẵn sàng, cleanup không chặn shutdown vô hạn và lease expiry 5 phút tiếp tục bảo đảm recovery. SMTP vẫn là at-least-once: cancellation/crash ở ranh giới provider accepted ↔ DB persisted có thể gửi lại email, nhưng business event/in-app/outbox key không bị nhân đôi.

## Frontend integration — T51.5

- Wishlist card và Product Detail dùng chung `WishlistAlertDialog`; Customer chưa lưu Product được hướng dẫn lưu trước, Guest vẫn đi qua safe `returnTo`, Staff/Admin không thấy control Customer.
- Summary flags/revision nằm trong shared Context; update thành công cập nhật đồng thời Wishlist/PDP. `409 STALE_WISHLIST_ALERT_SETTINGS` làm GET lại state mới nhất trước khi người dùng sửa tiếp.
- Session user có `emailVerified`; checkbox email bị khóa sớm khi chưa xác minh, trong khi backend guard vẫn là nguồn quyết định cuối cùng.
- Dialog dùng fieldset/legend, live region, focus trap, Escape và trả focus trigger; mobile có sticky actions.
- Product `PRODUCT_UNAVAILABLE` không thể bật alert mới nhưng vẫn cho tắt settings hiện hữu.

## Bằng chứng T51.2–T51.4

- 5 integration tests: RBAC/owner/CSRF/no-store, baseline/replay, verified email, lifecycle unavailable, concurrent stale revision.
- Wishlist + migration regression: 20/20.
- Full backend: 421/421.
- Frontend compatibility: 145/145; lint và production build sạch.
- 5 evaluator tests: transition kép/idempotency, concurrent disjoint claim, expired lease, version-safe wake-up, verified-email recheck và failure/backoff.
- Full backend sau T51.3: 426/426.
- 5 commerce delivery tests: SMTP ngoài transaction, persist outcome từng item khi SMTP kế tiếp chậm, concurrent disjoint claim/no duplicate, expired lease + sanitized retry và discard payload/recipient không hợp lệ.
- Full backend sau T51.4: 431/431.
- 4 frontend tests mới cho save/CSRF/shared state, stale reload, verified-email feedback/focus và safe notification action URL.
- Full frontend sau T51.5: 149/149; lint 0 warning/error; production build pass.

## Bằng chứng hardening — T51.6

- 10 backend integration tests bổ sung: concurrent remove/update, delete-after-snapshot, multi-SKU coalesce, price increase/replay/new-low, suspend/resume, reservation/release, Flash Sale isolation, disable/re-enable baseline và cancellation lease cleanup của evaluator/email worker.
- 2 frontend tests bổ sung: email chưa verified bị khóa trước mutation và response alert cũ không ghi đè state sau account switch.
- Full backend: 441/441. Full frontend: 151/151. Oxlint và production build sạch.

## E2E và release evidence — T51.7/T51.8

- Playwright Flow 17 chạy thật trên Docker/PostgreSQL/Mailpit: bật đồng thời hai alert + email cho Product hết hàng; stock-in và giảm regular price mỗi loại tạo đúng một in-app notification và một commerce email.
- Reload/retry không nhân đôi; sau khi tắt cả hai alert, lần giảm giá tiếp theo không tạo notification/outbox/email mới.
- Regression Flow 1, 5, 8, 11, 12 và 16: 8/8 pass trong cùng lượt. Flow 8 dùng Product fixture hai SKU cố định để không phụ thuộc thứ tự Catalog.
- Playwright discovery: 19 tests/17 specs. CI kiểm tra Flow 17 được discovery trước khi chạy toàn suite.
- Runbook production: [Wishlist Product Alerts](../operations/wishlist-product-alerts.md). Release checklist cấm xóa queue/outbox hoặc sửa terminal state thủ công để che backlog.
