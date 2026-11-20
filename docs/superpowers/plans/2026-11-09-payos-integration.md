# T19–T22 — Tích hợp PayOS an toàn, đối soát được và không làm sai lệch tồn kho

Ngày lập: 09/11/2026. Trạng thái: **T19 đang thực hiện — create/query/cancel đã kiểm chứng; chưa bật PayOS trong production**.

Phụ thuộc đã hoàn tất: Checkout/idempotency T16–T18, Flash Sale T23–T24, fulfillment T25–T26, Customer Order Center T31, Voucher T38, Returns T39 và release gate T35. Thiết kế phải tuân thủ [ADR 0002](../../adr/0002-checkout-idempotency-and-cod-void.md), [ADR 0003](../../adr/0003-defer-payos-complete-cod-first.md), [ADR 0004](../../adr/0004-enable-cod-for-flash-sale-while-payos-deferred.md) và [ADR 0005](../../adr/0005-voucher-allocation-and-release.md).

## 1. Kết luận kiến trúc

PayOS được triển khai theo bốn task đã có trên roadmap:

1. **T19 — Provider contract gate:** kiểm chứng bằng credential kênh test và fixture thật; chốt ADR mở lại PayOS.
2. **T20 — Payment attempts + webhook:** tạo link ngoài transaction DB, webhook có chữ ký và xử lý lũy đẳng.
3. **T21 — Payment/expiry/cancel race:** một state machine thanh toán dùng chung, khóa có thứ tự, không oversell hoặc nhả giữ hàng khi trạng thái tiền còn mơ hồ.
4. **T22 — Reconciliation + manual refund:** xử lý tiền đến muộn, lệch số tiền, giao dịch trùng và quy trình hoàn tiền có chứng từ.

`PayOs:Enabled` mặc định là `false`. Không bỏ lỗi `PAYOS_DEFERRED`, không bật lựa chọn trên frontend và không cho production tạo link cho tới khi toàn bộ acceptance gate của T19–T21 đạt.

## 2. Nguyên tắc không được vi phạm

1. Không gọi PayOS trong transaction PostgreSQL.
2. Không retry mù lệnh tạo payment link sau timeout/5xx/429. Kết quả mơ hồ phải được phân giải bằng API tra cứu với `provider_order_code` ổn định.
3. Return URL chỉ phục vụ UX; không được dùng query string từ trình duyệt để xác nhận đã thu tiền.
4. Chỉ webhook đã xác minh chữ ký hoặc kết quả query trực tiếp từ PayOS mới có quyền chuyển Payment sang `SUCCESS`.
5. Một khoản thu thật chỉ tạo tối đa một `payment_transaction` có hiệu lực nghiệp vụ.
6. Hết hạn/hủy chỉ nhả Inventory, Voucher và Flash Sale khi đã chứng minh giao dịch chưa được thanh toán. Không đoán trạng thái khi PayOS đang mất kết nối.
7. Tiền đến sau khi Order đã `EXPIRED/CANCELLED` không hồi sinh Order và không giữ lại tồn kho; chuyển `LATE_PAID` và mở reconciliation case.
8. Secret `ClientId`, `ApiKey`, `ChecksumKey` không vào Git, frontend, log, audit payload hoặc ProblemDetails.
9. Tắt feature flag chỉ chặn checkout/attempt mới; webhook, status query và reconciliation cho Order cũ vẫn phải hoạt động.

## 3. Contract chính thức cần kiểm chứng ở T19

Nguồn chuẩn là tài liệu PayOS chính thức:

- API payment request: <https://payos.vn/docs/api/>
- Xác minh signature: <https://payos.vn/docs/tich-hop-webhook/kiem-tra-du-lieu-voi-signature/>
- Webhook acknowledgment: <https://payos.vn/docs/du-lieu-tra-ve/webhook/>
- Return URL: <https://payos.vn/docs/du-lieu-tra-ve/return-url/>
- .NET SDK: <https://payos.vn/docs/sdks/back-end/net/>
- Hướng dẫn môi trường test: <https://payos.vn/docs/moi-truong-test/>

T19 phải xác minh và lưu fixture đã loại secret cho các điểm sau:

- Base URL, credential của kênh test và khả năng tương thích của SDK chính thức với .NET 10.
- Giới hạn/kiểu dữ liệu của `orderCode`; cách sinh duy nhất và khả năng tra cứu lại.
- Ràng buộc `description`, `amount`, `returnUrl`, `cancelUrl`, `expiredAt` và timezone/Unix timestamp.
- Chuỗi canonical và HMAC-SHA256 của request create-link.
- Cách xác minh signature response/webhook, bao gồm null, array và thứ tự key.
- Payload thật cho `PENDING`, `PAID`, `CANCELLED`, callback lặp, callback sai chữ ký và amount mismatch.
- Hành vi khi timeout trước/sau lúc PayOS đã tạo link, 429, 5xx và query-at-deadline.
- Quy tắc webhook retry và điều kiện PayOS coi HTTP 2xx là ACK thành công.
- Khả năng cancel payment request và kết quả khi cancel cạnh tranh với thanh toán.

### Deliverables T19

- `docs/adr/0017-enable-payos-with-verified-provider-contract.md` ở trạng thái Accepted sau khi có bằng chứng.
- `docs/provider-fixtures/payos/README.md` mô tả ngày kiểm chứng, môi trường và hash fixture; JSON phải được sanitize.
- Contract tests chạy trên fixture thật, không tự bịa payload/signature.
- Quyết định dùng official SDK phiên bản pin chính xác sau spike; mọi call vẫn đi qua interface nội bộ `IPayOsGateway` để test và thay phiên bản an toàn.
- Nếu chưa có đủ ba secret kênh test (`ClientId`, `ApiKey`, `ChecksumKey`) thì T19 dừng ở `BLOCKED_BY_PROVIDER_CREDENTIALS`; T20 không được bắt đầu bằng mock contract giả.

Tiến độ 09/11/2026: đã có đủ credential ngoài repository; create/query/cancel, invalid request signature và SDK `payOS 2.1.0` trên .NET 10 đã được kiểm chứng. Bằng chứng sanitize nằm tại [`docs/provider-fixtures/payos`](../../provider-fixtures/payos/README.md). T19 còn chờ webhook/payment-success/ACK-retry/timeout/race fixtures trước khi ADR 0017 chuyển từ Proposed sang Accepted.

## 4. Cấu hình và bảo mật

Application options dự kiến:

```text
PayOs__Enabled=false
PayOs__ClientId=<secret>
PayOs__ApiKey=<secret>
PayOs__ChecksumKey=<secret>
PayOs__ReturnUrl=https://shop.example.com/payment/payos/return
PayOs__CancelUrl=https://shop.example.com/payment/payos/return?cancel=1
PayOs__RequestTimeoutSeconds=10
PayOs__CheckoutUrlAllowedHosts=pay.payos.vn
```

- Dùng environment/secret store; `.env.example` chỉ có placeholder.
- Fail-fast khi `Enabled=true` mà thiếu credential, URL không phải HTTPS ở Production hoặc host allow-list rỗng.
- Không trả raw provider response về browser. API chỉ trả DTO an toàn gồm URL đã kiểm tra, thời hạn, attempt/status.
- Webhook là endpoint anonymous duy nhất được miễn CSRF trong module; vẫn có body-size limit, rate limit, signature verification và structured logging không chứa secret/raw PII.
- Header secret chỉ được thêm trong adapter server-side; `HttpClient` có timeout hữu hạn và không bật automatic retry cho POST create-link.

## 5. Database hardening — migration dự kiến `022_payos_integration_hardening.sql`

Migration cuối cùng chỉ chốt sau T19, nhưng tối thiểu cần:

1. Sequence hoặc allocator DB cho `provider_order_code` trong miền PayOS đã kiểm chứng; không sinh từ timestamp ngẫu nhiên trên nhiều instance.
2. Unique partial index cho một active attempt trên mỗi Payment (`CREATED`/`LINK_READY`) để hai request đồng thời không tạo hai link.
3. Unique partial index `(provider, provider_transaction_id)` khi transaction ID khác null để chặn ghi tiền hai lần.
4. Unique callback identity theo provider + payload hash/order code, hỗ trợ insert-on-conflict và replay an toàn.
5. Bổ sung metadata attempt tối thiểu sau khi contract được xác minh: provider payment-link ID, safe error code, thời điểm query gần nhất và revision/concurrency token nếu cần.
6. Không lưu raw header credential. Callback payload chỉ lưu projection cần đối soát; nếu cần raw payload vì audit thì phải có retention/redaction rõ ràng.
7. Migration idempotent, mount vào base Compose, embedded migration và Integration Test fixture giống các migration hiện hành.

Không thêm trạng thái `UNKNOWN` chỉ vì tên gọi thuận tiện trước khi kiểm chứng. Theo ADR 0002, kết quả create-link mơ hồ có thể giữ attempt ở `CREATED`; contract service phải biểu diễn rõ `NeedsProviderQuery` mà không biến timeout thành `FAILED`.

## 6. Backend design

### 6.1 Application contracts

Tạo module `Application/Payments`:

- `IPayOsGateway`: create, get, cancel và verify webhook; không lộ type của SDK ra Application/API.
- `IPayOsPaymentService`: tạo/replay attempt, lấy trạng thái owner-only và hủy payment pending.
- `IPayOsWebhookService`: persist/verify/process callback.
- `IPayOsReconciliationService`: danh sách case, resolve/manual-refund theo Admin.
- DTO chỉ dùng số tiền integer VND ở provider boundary; chuyển đổi có guard từ `decimal` và từ chối fraction/overflow.
- Failure code ổn định: `PAYOS_DISABLED`, `PAYMENT_NOT_FOUND`, `PAYMENT_NOT_PENDING`, `PAYMENT_ATTEMPT_LIMIT_REACHED`, `PAYMENT_PROVIDER_UNAVAILABLE`, `PAYMENT_STATE_UNRESOLVED`, `PAYMENT_AMOUNT_MISMATCH`, `PAYMENT_LINK_INVALID`.

### 6.2 Tạo payment attempt

Endpoint đề xuất:

```http
POST /api/v1/customer/orders/{orderId}/payos-attempts
GET  /api/v1/customer/orders/{orderId}/payment
POST /api/v1/customer/orders/{orderId}/payos-cancel
```

Flow tạo link:

1. Transaction ngắn: owner-only; khóa `SalesOrder -> Payment -> PaymentAttempt`; kiểm tra Order `PENDING_PAYMENT`, Payment `PENDING`, chưa quá deadline, dưới ba business attempts; allocate `provider_order_code`; insert attempt `CREATED`; commit.
2. Gọi PayOS đúng một lần ngoài transaction.
3. Transaction ngắn: khóa lại cùng thứ tự; nếu response hợp lệ và URL thuộc allow-list thì lưu `LINK_READY` + checkout URL + provider payment-link ID.
4. Provider rejection xác định được mới chuyển `FAILED`. Timeout/429/5xx/connection reset giữ trạng thái cần query; response API cho client là `202/409 PAYMENT_STATE_UNRESOLVED`, không tạo attempt mới.
5. Retry của client lấy lại attempt active và query theo `provider_order_code`; không POST create-link lần hai.

API mutation bắt buộc Customer policy + CSRF. Replay attempt đang `LINK_READY` trả cùng link nếu chưa hết hạn; không tính thành attempt mới.

### 6.3 Webhook ingress và xử lý

Endpoint:

```http
POST /api/v1/integrations/payos/webhook
```

Pipeline:

1. Đọc body có giới hạn; parse strict; tính payload hash.
2. Xác minh signature trước mọi side effect kinh doanh.
3. Persist durable callback event bằng transaction ngắn. Chỉ trả 2xx sau khi event hợp lệ đã được ghi bền vững hoặc xác định duplicate.
4. Process event lũy đẳng theo lock order tại mục 7.
5. Revalidate `orderCode`, payment-link ID, số tiền chính xác, transaction reference và Payment/Order hiện tại.
6. Callback chữ ký sai không thay đổi Order/Payment. Callback hợp lệ nhưng không map được được lưu `REJECTED` an toàn hoặc mở reconciliation tùy nguyên nhân; không log secret.

Nếu cần ACK nhanh, ingress ghi inbox bền vững rồi worker xử lý. Không trả 2xx trước durable insert vì crash sẽ làm mất sự kiện.

### 6.4 Return page

Route frontend `/payment/payos/return` chỉ đọc query để hiển thị trạng thái ban đầu, sau đó gọi `GET .../payment` từ backend. Các giá trị `status`, `cancel`, `orderCode` trên URL không trực tiếp đổi database. Trang poll có backoff hữu hạn và dừng ở success/expired/cancelled/reconciliation.

## 7. State machine và lock order T21

Mọi payment transition dùng chung một orchestrator; webhook, query worker, customer cancel và expiry worker không tự viết trạng thái riêng.

Lock order toàn cục cho transition sau checkout:

1. `SalesOrder FOR UPDATE`.
2. `Payment FOR UPDATE`.
3. `PaymentAttempt FOR UPDATE` theo attempt number/ID ổn định.
4. `Voucher FOR UPDATE` nếu có redemption.
5. `FlashSaleCampaign/Offer FOR UPDATE` theo UUID tăng dần nếu có allocation.
6. `ProductSku FOR UPDATE` theo UUID tăng dần.
7. `InventoryBalance FOR UPDATE` theo SKU UUID tăng dần.
8. Reservation/allocation/transaction rows liên quan.

Đây là lock order cho lifecycle của Order hiện hữu; không trộn lại lock order checkout `Customer -> Voucher -> Product/Flash Sale -> SKU -> Inventory`.

### Payment được xác minh trước deadline

Trong một transaction:

- Chèn `payment_transaction` verified theo unique provider transaction ID.
- `Payment PENDING -> SUCCESS`, lưu `paid_at`.
- Attempt -> `SUCCESS`.
- `Order PENDING_PAYMENT -> CONFIRMED`.
- Chuyển reservation `HELD -> ALLOCATED` và `ReservedQty -> AllocatedQty` exactly once.
- Chuyển Flash Sale allocation theo lifecycle đã chốt; Voucher vẫn tuân thủ lifecycle T38 và chỉ consume ở mốc nghiệp vụ đã định, không tự viết quy tắc mới.
- Ghi notification/audit/outbox cùng transaction.

Nên trích xuất transition dùng chung từ logic COD confirmation thay vì sao chép, nhưng phải giữ khác biệt: PayOS đã thu tiền, COD chưa thu tiền.

### Expiry

1. Worker claim Order quá deadline bằng lease/batch ngắn.
2. Query PayOS ngoài transaction cho mọi attempt unresolved.
3. Nếu provider xác nhận `PAID`, áp transition success.
4. Nếu provider xác nhận unpaid/cancelled và deadline đã qua, guarded transition sang `Order EXPIRED`, `Payment VOID`, release reservation/voucher/flash-sale exactly once.
5. Nếu query thất bại/mơ hồ, **không release stock**; giữ trạng thái pending, ghi metric và mở/reuse reconciliation case để retry có giới hạn/điều tra. An toàn tiền ưu tiên hơn giải phóng tồn sớm.

### Customer cancel

- Chỉ áp dụng Order PayOS chưa verified paid.
- Gọi provider cancel/query ngoài transaction.
- Chỉ commit `CANCELLED + VOID + release` sau kết quả authoritative unpaid/cancelled.
- Webhook paid và cancel tranh cùng Order/Payment lock; đúng một guarded transition thắng.

### Late payment

Nếu tiền verified đến sau `VOID`, `EXPIRED` hoặc `CANCELLED`:

- Ghi transaction `RECONCILIATION_REQUIRED` exactly once.
- Payment -> `LATE_PAID`.
- Mở case `LATE_PAID`.
- Không đổi Order về confirmed, không phục hồi reservation/quota và không tự tạo shipment.

## 8. Fulfillment và hoàn tiền

- Fulfillment cho phép Order PayOS chỉ khi `Order=CONFIRMED` và `Payment=SUCCESS`.
- Handover/stock-out dùng invariant hiện tại.
- Delivery của PayOS không yêu cầu nhập số tiền COD và không tạo COD transaction lần hai.
- Return/refund T39 phải nhận biết khoản thu PayOS nhưng V1 không tự gọi payout/refund khi tài liệu contract chưa chứng minh một API refund tương ứng.
- T22 dùng manual refund có evidence, audit và amount cap theo verified transaction. Tích hợp payout tự động là task riêng, cần ADR và provider contract riêng; không dùng API payout như thể đó là refund.

## 9. Frontend

1. API provider/config công khai chỉ trả `payOsEnabled`; không trả credential.
2. Cart hiển thị PayOS chỉ khi flag bật. Khi tắt vẫn ghi “Sắp mở” như hiện tại.
3. Checkout PayOS tạo Order idempotent trước; sau đó gọi attempt endpoint và redirect tới checkout URL an toàn do backend trả.
4. Không tạo lại Order khi user reload/return/retry. Giữ checkout idempotency key đến khi backend xác nhận Order.
5. Orders page hiển thị trạng thái Payment, thời hạn, nút “Thanh toán lại” khi còn attempt quota và nút hủy khi đủ điều kiện.
6. Return page có trạng thái: đang xác minh, đã thanh toán, chưa hoàn tất, đã hết hạn/hủy, cần hỗ trợ đối soát; có retry status cục bộ và link về Order.
7. Admin Dashboard có queue reconciliation, chi tiết callback/transaction đã sanitize, action nhận xử lý và resolve/manual-refund có confirm dialog.
8. Accessibility, mobile layout, disabled/pending state và duplicate-click guard bắt buộc.

## 10. Test matrix bắt buộc

### Unit/contract

- Canonical signature từ fixture chính thức; sai một byte/key/order phải fail.
- Parser create/query/webhook cho mọi trạng thái đã quan sát.
- Amount integer VND, overflow/fraction guard.
- Checkout URL allow-list và HTTPS guard.
- State-machine transition table; terminal state không bị viết ngược.

### Backend integration với PostgreSQL thật

- RBAC, owner isolation, CSRF; webhook anonymous nhưng signature bắt buộc.
- Concurrent create attempt: một active attempt/link, không 500.
- Create-link timeout sau khi provider đã tạo: retry query tìm lại link, không POST lần hai.
- Invalid signature, malformed body, unknown order code và amount mismatch không xác nhận Order.
- 5 và 100 duplicate callbacks: đúng một transaction/transition.
- Webhook paid cạnh tranh customer cancel và expiry: đúng một kết quả hợp lệ, không deadlock/oversell/double release.
- Query-at-deadline paid/unpaid/unavailable.
- Late-paid sau VOID mở đúng một case, không hồi sinh Order.
- Voucher/Flash Sale/Inventory counters giữ invariant qua success, expiry, cancel và rollback/fault injection.
- Fulfillment PayOS hoàn tất mà không thu COD.
- Migration chạy lặp lại và unique constraints chịu concurrency thật.

### E2E

- Local Docker dùng adapter fixture có chữ ký đã được T19 xác minh, chỉ được đăng ký trong `Testing`; Production không thể khởi động adapter giả.
- Flow: Customer checkout PayOS -> redirect giả lập -> signed webhook -> return page -> Order confirmed -> Staff fulfillment -> delivered không COD.
- Flow cancel/expiry và flow late-paid/reconciliation.
- Sandbox smoke dùng secret CI riêng, chạy manual/nightly; không chạy trên PR fork và không ghi payload/secret vào artifact.

### Release gate

- Toàn bộ backend/frontend regression hiện có xanh.
- AC-20b: callback duplicate.
- AC-20c: paid vs cancel/expiry race.
- `docker compose config`, base Production không có secret/fixture adapter/demo enablement.
- Security review cho webhook, SSRF/open redirect, secret scan, log redaction và dependency scan.

## 11. Observability và vận hành

Metrics tối thiểu:

- create/query/cancel latency và count theo kết quả safe code.
- attempts ở `CREATED` quá ngưỡng; Order `PENDING_PAYMENT` quá deadline.
- webhook valid/invalid/duplicate/rejected, amount mismatch.
- late paid và reconciliation cases open theo tuổi.
- provider 429/5xx/timeouts; expiry bị hoãn vì provider unresolved.

Runbook `docs/operations/payos.md` phải có:

- Rotate credential/checksum key và xác minh webhook sau rotate.
- Triage Order pending, callback mất, amount mismatch, late paid, duplicate collection.
- Query/read-only SQL an toàn; cấm sửa trực tiếp counter/status rời rạc.
- Cách disable checkout mới nhưng giữ ingress/reconciliation hoạt động.
- Cách refund thủ công và bằng chứng bắt buộc.

## 12. Triển khai theo lát dọc

### T19 — Contract gate

1. T19.1: Nhận credential kênh test qua secret store; xác minh môi trường.
2. T19.2: Spike official SDK/HTTP contract trên .NET 10.
3. T19.3: Thu fixture create/query/cancel/webhook đã sanitize và contract tests.
4. T19.4: Chạy timeout/duplicate/late/amount mismatch experiments.
5. T19.5: Chốt ADR 0017, schema delta và go/no-go.

### T20 — Attempts và webhook

1. T20.1: Migration 022 + EF mapping/fixture/Compose.
2. T20.2: Options, gateway adapter, safe logging và DI.
3. T20.3: Customer attempt/status APIs.
4. T20.4: Durable webhook inbox + signature/idempotency.
5. T20.5: Cart/checkout/return/orders UI phía Customer.
6. T20.6: Contract/integration tests và docs API.

Tiến độ backend 09/11/2026: đã hoàn tất T20.1–T20.4 foundation và phần API docs. Migration 022, official SDK adapter, attempt/status API và durable webhook inbox đã có integration tests PostgreSQL cho replay, ambiguous timeout, create concurrency, invalid signature, sequential/concurrent duplicate callback và owner isolation. Feature flag vẫn tắt; webhook mới dừng tại `RECEIVED`, chưa áp payment transition trước khi T21 và provider webhook fixture thật hoàn tất.

### T21 — Race và fulfillment

1. T21.1: Shared payment transition orchestrator + global lock order.
2. T21.2: Success allocation + notifications/outbox.
3. T21.3: Deadline query/expiry worker.
4. T21.4: Customer cancel.
5. T21.5: PayOS-aware fulfillment/delivery.
6. T21.6: AC-20b/AC-20c, fault injection và E2E.

Tiến độ backend 09/11/2026: T21.1–T21.5 đã triển khai. Webhook transition, deadline query/cancel, Customer cancel, late-paid reconciliation và PayOS-aware fulfillment có integration tests PostgreSQL thật. AC-20c dùng barrier kiểm chứng cả hai thứ tự thắng giữa cancel và paid webhook. Chưa đánh dấu T21 hoàn tất end-to-end vì còn frontend return/status flow, E2E và fixture provider GET `PAID` cung cấp transaction reference thật; khi provider trả PAID nhưng thiếu contract này worker giữ nguyên hold thay vì release hoặc tự suy diễn transaction.

### T22 — Reconciliation và release

1. T22.1: Late-paid/amount mismatch/duplicate case creation.
2. T22.2: Admin queue, audit và resolve/manual-refund.
3. T22.3: Metrics, alerts và runbook.
4. T22.4: Sandbox smoke + staged rollout.
5. T22.5: Release gate, documentation và Graphify update.

Tiến độ hardening 09/11/2026: T22.1–T22.3 và runtime T22.5 đã triển khai. Migration 024 liên kết mỗi case với đúng transaction/refund; claim có lease takeover 30 phút; mismatch refund giải phóng reservation nguyên tử; duplicate collection mở case riêng nhưng giữ giao dịch gốc; evidence/note được trim validate; UI yêu cầu confirm; .NET Meter và runbook alert threshold đã có. T22.4 vẫn mở cho signed sandbox callback/provider query thật và cấu hình exporter/pager của môi trường triển khai.

## 13. Rollout và rollback

1. **Stage A:** `Enabled=false`, migrations/code deployed; webhook endpoint health-checked.
2. **Stage B:** Testing/test-channel only, internal accounts, no production money.
3. **Stage C:** Production allow-list hoặc tỷ lệ nhỏ, theo dõi pending/reconciliation.
4. **Stage D:** General availability sau thời gian quan sát và đối soát số liệu.

Rollback: đặt `PayOs:Enabled=false` để chặn checkout/attempt mới. Không tắt webhook, status resolution, expiry/reconciliation workers cho các Order đã tạo. Không rollback migration chứa dữ liệu tài chính; dùng forward fix.

## 14. Definition of Done

PayOS chỉ được coi là hoàn tất khi:

- ADR 0017 Accepted dựa trên credential/fixture thật.
- Create/query/cancel/webhook contract tests khớp provider.
- Không provider call trong DB transaction và không blind retry create-link.
- Duplicate/race/late/amount mismatch tests pass trên PostgreSQL thật.
- Inventory/Voucher/Flash Sale/Payment/Order invariants được chứng minh dưới concurrency.
- Fulfillment PayOS không tạo COD collection.
- Reconciliation/manual-refund có owner/RBAC/audit/evidence đầy đủ.
- Secret scan, lint, build, backend/frontend/E2E, Compose và sandbox smoke đạt.
- `PayOs:Enabled` vẫn default false; rollout có metric, runbook và rollback an toàn.

