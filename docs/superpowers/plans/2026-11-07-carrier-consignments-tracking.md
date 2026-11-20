# T49 — Carrier Consignments & Internal Shipment Tracking

Ngày lập: 07/11/2026  
Trạng thái: **Hoàn tất — T49.1–T49.8**

Kết quả 07/11/2026: T49.1–T49.8 đã hoàn tất backend, ba React workspace, tài liệu và live-stack acceptance. Customer timeline owner-only dùng cùng 404 cho cross-owner/missing/no-consignment; safe DTO loại actor ID, payload hash, external ID và storage key. Order list/detail có summary nullable cho legacy. Manual event gửi notification exactly-once bằng event key băm ổn định trong cùng transaction. Admin quản lý carrier theo revision; Staff thao tác consignment/label/handover/event; Customer xem timeline lazy-load và chỉ nhận external HTTPS link. Full backend đạt **375/375**; frontend **134/134**, lint/build sạch. Playwright Flow 15 cùng regression Flow 3, 7, 8, 14 pass trên Docker demo thật.

## 1. Mục tiêu

T48 đã giải quyết phương thức, phí và ETA tại checkout nhưng fulfillment mới chỉ có trạng thái nội bộ. T49 bổ sung lớp vận hành vận đơn độc lập để:

1. Admin quản trị danh mục đơn vị vận chuyển dùng trong cửa hàng.
2. Staff gắn một vận đơn vào shipment bán hàng hoặc shipment đổi sản phẩm, cấp mã vận đơn và nhãn bất biến.
3. Staff ghi nhận timeline lấy hàng/đang trung chuyển/đang giao/giao thất bại/hoàn hàng theo command có idempotency.
4. Customer xem mã vận đơn, đơn vị vận chuyển và timeline owner-only ngay trên Order Center.
5. Không thay đổi các invariant kho, Payment COD, Voucher, Flash Sale, Return và Exchange đã được chứng minh.
6. Tạo seam rõ ràng để sau này cắm adapter GHN/GHTK/Viettel Post mà không sửa lịch sử đơn hoặc tin webhook chưa xác minh.

T49 V1 là **provider-neutral và vận hành nội bộ**. Không gọi API hãng vận chuyển, không mua nhãn và không nhận webhook thật khi chưa có sandbox credential cùng contract chính thức.

## 2. Quyết định kiến trúc

### 2.1. Không thay thế state machine fulfillment

Các aggregate hiện hữu tiếp tục là nguồn chân lý nghiệp vụ:

- `store.shipments`: shipment của SalesOrder.
- `store.exchange_shipments`: shipment giao sản phẩm thay thế.
- `store.delivery_attempts` và `store.exchange_delivery_attempts`: kết quả từng lần giao.
- `SalesOrder`, `Payment`, `StockReservation`, Voucher và Flash Sale chỉ được thay đổi qua service fulfillment hiện hữu.

Tracking carrier là bằng chứng vận hành và timeline. Sự kiện carrier `DELIVERED` **không tự động**:

- chuyển Order thành `COMPLETED`;
- chuyển Payment thành `COD_COLLECTED`;
- tạo PaymentTransaction;
- consume Voucher;
- tạo/đảo Inventory Movement.

Với COD, Staff vẫn phải dùng command `delivery-result` hiện hữu, nhập đúng số tiền và mã biên nhận. Quyết định này ngăn webhook giả hoặc event đến sai thứ tự làm sai tiền/tồn.

### 2.2. Một consignment chung cho hai loại shipment

Không nhân đôi tracking event cho shipment bán hàng và shipment đổi hàng. Tạo `carrier_consignments` với đúng một trong hai FK:

```text
(sales_shipment_id IS NOT NULL) XOR (exchange_shipment_id IS NOT NULL)
```

Mỗi shipment chỉ có tối đa một consignment trong V1. Nếu sau này cần split shipment/multi-package sẽ mở task riêng; không phá contract này bằng cách nhét nhiều tracking number vào một chuỗi.

### 2.3. Trạng thái vận đơn

```text
DRAFT
  -> LABEL_READY
  -> HANDED_OVER
  -> IN_TRANSIT
  -> OUT_FOR_DELIVERY
  -> DELIVERED

OUT_FOR_DELIVERY -> DELIVERY_FAILED -> OUT_FOR_DELIVERY
DELIVERY_FAILED  -> RETURNING -> RETURNED

DRAFT|LABEL_READY -> CANCELLED
```

Quy tắc:

- Tạo consignment ở `DRAFT` khi shipment đang `PENDING_PACKING` hoặc `PACKED`.
- Có thể cấp tracking number ngay khi tạo; tracking number bắt buộc trước `HANDED_OVER`.
- Nhãn hợp lệ chuyển `DRAFT -> LABEL_READY`; phương thức manual không bắt buộc file nhãn và có thể handover trực tiếp nếu đã có tracking number.
- `HANDED_OVER` chỉ hợp lệ khi shipment nghiệp vụ được bàn giao cùng transaction hoặc đã ở `SHIPPING` do replay an toàn.
- `DELIVERED`/`DELIVERY_FAILED` trên tracking không thay thế DeliveryAttempt nghiệp vụ.
- `CANCELLED` chỉ trước khi hàng rời kho.
- `RETURNED` chỉ là bằng chứng kiện về điểm nhận; hoàn kho vẫn cần command có evidence hiện hữu.

### 2.4. Thứ tự khóa

Shipment bán hàng:

```text
SalesOrder -> Shipment -> CarrierConsignment -> CarrierTrackingEvent
```

Shipment đổi hàng:

```text
SalesOrder -> OrderReturnRequest -> ExchangeAllocation
-> ExchangeShipment -> CarrierConsignment -> CarrierTrackingEvent
```

Khi handover còn đụng SKU/Inventory, giữ nguyên thứ tự fulfillment hiện hữu và chỉ khóa consignment sau shipment, trước khi append event. Không service nào được khóa consignment rồi quay lại Order/Shipment.

Carrier master data không khóa commerce aggregate. Service chỉ đọc provider `ACTIVE` trước khi khóa Order/Shipment và revalidate dưới consignment transaction khi cần.

## 3. Phạm vi

### 3.1. Có trong T49

- Carrier master data do Admin quản trị: code, tên, hotline, tracking URL template, trạng thái và revision.
- Consignment dùng chung cho Sales Shipment và Exchange Shipment.
- Mã vận đơn duy nhất theo carrier.
- Upload nhãn PDF/PNG/JPEG nội bộ, kiểm tra signature/size và key bất biến.
- Timeline event append-only có idempotency key.
- Staff workspace tạo vận đơn, upload/tải nhãn, handover và ghi event thủ công.
- Customer tracking owner-only trong Order Center.
- Notification có event key ổn định cho các mốc quan trọng.
- Audit Log cho carrier config, tạo/hủy consignment, thay nhãn và manual tracking event.
- Integration, concurrency, frontend và Playwright Flow 15.

### 3.2. Không có trong T49

- Gọi API GHN/GHTK/Viettel Post.
- Webhook public chưa có chữ ký/fixture chính thức.
- Mua vận đơn, tính cước thực tế hoặc đối soát carrier invoice.
- Split shipment, multi-package hoặc đổi carrier sau handover.
- GPS/map real-time.
- Tự động hoàn tất COD từ tracking event.
- Tự động hoàn phí shipping khi giao thất bại.
- Tracking cho PayOS; PayOS tiếp tục deferred theo ADR 0003.

## 4. ADR và migration `019_carrier_consignments_tracking.sql`

T49.1 tạo ADR 0014 ghi nhận ranh giới tracking/fulfillment và migration version 019.

### 4.1. `store.carriers`

- `id uuid PK`
- `code varchar(32) NOT NULL UNIQUE`, uppercase, `^[A-Z0-9_-]{2,32}$`
- `name varchar(120) NOT NULL`
- `hotline varchar(32) NULL`
- `tracking_url_template varchar(500) NULL`; nếu có phải HTTPS và chứa `{trackingNumber}`
- `carrier_status varchar(16) NOT NULL` — `ACTIVE|INACTIVE`
- `revision bigint NOT NULL DEFAULT 1`
- `created_by`, `updated_by` FK UserAccount
- `created_at`, `updated_at`

Không hard-delete carrier đã được dùng. `INACTIVE` chặn consignment mới nhưng lịch sử vẫn đọc được.

### 4.2. `store.carrier_consignments`

- `id uuid PK`
- `sales_shipment_id uuid NULL UNIQUE` FK `shipments` `ON DELETE RESTRICT`
- `exchange_shipment_id uuid NULL UNIQUE` FK `exchange_shipments` `ON DELETE RESTRICT`
- `carrier_id uuid NOT NULL` FK `carriers` `ON DELETE RESTRICT`
- `tracking_number varchar(100) NOT NULL`
- `consignment_status varchar(24) NOT NULL DEFAULT 'DRAFT'`
- `service_code varchar(64) NULL`
- `recipient_name_snapshot varchar(200) NOT NULL`
- `recipient_phone_snapshot varchar(32) NOT NULL`
- `destination_snapshot text NOT NULL`
- `shipping_method_code_snapshot varchar(32) NULL`
- `shipping_method_name_snapshot varchar(120) NULL`
- `label_file_key varchar(300) NULL`
- `label_content_type varchar(80) NULL`
- `label_size_bytes bigint NULL`
- `label_sha256 char(64) NULL`
- `revision bigint NOT NULL DEFAULT 1`
- `handed_over_at`, `delivered_at`, `cancelled_at` nullable
- `created_by`, `updated_by`, timestamps

Constraints/indexes:

- XOR đúng một shipment FK.
- Unique `(carrier_id, upper(tracking_number))`.
- Tracking number trim, 3–100 ký tự, chỉ chữ/số/`._:/-`.
- File metadata hoặc cùng NULL, hoặc đầy đủ và hợp lệ.
- Partial indexes cho queue theo status và loại shipment.
- Revision dương, timestamps phù hợp trạng thái.

Không copy tracking number vào SalesOrder. Order detail chiếu qua consignment; snapshot người nhận nằm trên consignment để bằng chứng nhãn không đổi nếu tài khoản/địa chỉ thay đổi.

### 4.3. `store.carrier_tracking_events`

- `id uuid PK`
- `consignment_id uuid NOT NULL` FK `ON DELETE RESTRICT`
- `event_key varchar(180) NOT NULL`
- `event_type varchar(32) NOT NULL`
- `event_status varchar(24) NOT NULL`
- `location varchar(200) NULL`
- `description varchar(500) NULL`
- `occurred_at timestamptz NOT NULL`
- `recorded_at timestamptz NOT NULL DEFAULT now()`
- `source_type varchar(16) NOT NULL` — `STAFF|SYSTEM`; dành `PROVIDER` cho task adapter sau
- `actor_id uuid NULL` FK UserAccount
- `external_event_id varchar(180) NULL`
- `payload_hash char(64) NOT NULL`

Constraints/indexes:

- Unique `(consignment_id, event_key)` bảo vệ replay.
- Partial unique `(consignment_id, external_event_id)` khi external ID có giá trị.
- Timeline `(consignment_id, occurred_at, id)`.
- `source_type=STAFF` bắt buộc actor; `SYSTEM` có thể không có actor.
- Event append-only; không update/delete qua application.

`payload_hash` được tính từ canonical event payload. Replay cùng key/cùng hash trả event cũ; cùng key/khác hash trả `409 TRACKING_IDEMPOTENCY_CONFLICT`.

### 4.4. Migration/backfill

- Shipment lịch sử không bắt buộc consignment; không tạo mã vận đơn giả.
- Migration chạy lại không đổi dữ liệu.
- Fixture legacy chứng minh shipment cũ vẫn đọc/fulfill bình thường.
- Đăng ký migration 019 trong `EmbeddedDatabaseMigrations` và embed vào Infrastructure image theo migration runner hiện tại.

## 5. Application contracts

Module `Application/CarrierTracking`:

- `CarrierSummaryDto`, `CarrierDetailDto`
- `CreateCarrierRequest`, `UpdateCarrierRequest`, `ChangeCarrierStatusRequest`
- `CarrierConsignmentDto`, `CarrierTrackingEventDto`
- `CreateSalesConsignmentRequest`, `CreateExchangeConsignmentRequest`
- `RecordTrackingEventRequest`
- `CarrierLabelDto`
- `ICarrierAdminService`
- `ICarrierConsignmentService`
- `ICarrierLabelStorage`
- `CarrierTrackingFailure`

Runtime validation:

- Không nhận actor/customer ID từ body.
- `occurredAt` không quá 5 phút trong tương lai và không trước thời điểm tạo shipment.
- Event type/status dùng allow-list, không nhận chuỗi tự do làm transition.
- Description/nơi chốn có giới hạn độ dài và trim.
- Expected revision bắt buộc cho mutation consignment/carrier.

## 6. API contract

### 6.1. Admin carrier master

Base: `/api/v1/admin/carriers`, policy `Admin`.

| Method | Route | Mục đích |
|---|---|---|
| GET | `/` | Danh sách, lọc status/search |
| GET | `/{id}` | Chi tiết carrier |
| POST | `/` | Tạo carrier INACTIVE |
| PATCH | `/{id}` | Sửa metadata theo expectedRevision |
| POST | `/{id}/activate` | Cho phép tạo vận đơn mới |
| POST | `/{id}/deactivate` | Chặn vận đơn mới, không phá lịch sử |

### 6.2. Staff consignments

Base: `/api/v1/staff/carrier-consignments`, chỉ policy `Staff`; Admin không được vận hành thay Staff theo separation of duties hiện tại.

| Method | Route | Mục đích |
|---|---|---|
| GET | `/queue` | Shipment đủ điều kiện và consignment hiện tại |
| POST | `/sales-shipments/{shipmentId}` | Tạo consignment cho đơn bán |
| POST | `/exchange-shipments/{shipmentId}` | Tạo consignment cho kiện đổi |
| GET | `/{id}` | Chi tiết + timeline |
| POST | `/{id}/label` | Upload nhãn multipart |
| GET | `/{id}/label` | Tải nhãn có authorize/no-store |
| POST | `/{id}/handover` | Bàn giao có expectedRevision |
| POST | `/{id}/events` | Append manual tracking event |
| POST | `/{id}/cancel` | Hủy trước handover |

Endpoint handover phải tích hợp service fulfillment hiện hữu thay vì tạo một transaction cạnh tranh. Một command thực hiện atomically:

1. khóa Order/Shipment;
2. revalidate consignment/tracking;
3. thực hiện stock-out và DeliveryAttempt như hiện tại;
4. chuyển consignment `HANDED_OVER`;
5. append event `HANDED_OVER`;
6. Audit + Notification;
7. commit.

API handover cũ được giữ tương thích trong một giai đoạn nhưng gọi chung orchestrator. Khi shipment đã có consignment, handover cũ phải yêu cầu consignment hợp lệ; shipment legacy không consignment vẫn chạy contract cũ.

### 6.3. Customer tracking

- Mở rộng `GET /api/v1/customer/orders` trả tracking summary nullable.
- `GET /api/v1/customer/orders/{orderId}/tracking` trả consignment + timeline owner-only.
- Cross-owner và ID không tồn tại cùng trả `404 TRACKING_NOT_FOUND`.
- Response `Cache-Control: no-store`.
- Không trả internal actor ID, payload hash, label file key hay raw audit data.

## 7. Transition và mapping với fulfillment

| Tracking event | Consignment | Điều kiện shipment nghiệp vụ | Tác động commerce |
|---|---|---|---|
| `LABEL_CREATED` | `LABEL_READY` | `PENDING_PACKING|PACKED` | Không |
| `HANDED_OVER` | `HANDED_OVER` | command chuyển `PACKED -> SHIPPING` | Stock-out/attempt theo fulfillment hiện tại |
| `PICKED_UP` | `IN_TRANSIT` | Shipment `SHIPPING` | Không |
| `OUT_FOR_DELIVERY` | `OUT_FOR_DELIVERY` | Shipment `SHIPPING` | Không |
| `DELIVERY_FAILED` | `DELIVERY_FAILED` | Shipment `SHIPPING|DELIVERY_FAILED` | Không tự kết thúc attempt |
| `REDELIVERY_STARTED` | `OUT_FOR_DELIVERY` | command retry hiện hữu đã thành công | Không thêm stock movement |
| `DELIVERED` | `DELIVERED` | Chỉ append sau fulfillment delivered hoặc cùng transaction trusted command | COD vẫn cần amount/reference |
| `RETURNING` | `RETURNING` | Shipment delivery failed | Không hoàn kho |
| `RETURNED` | `RETURNED` | Command return-stock đã có evidence hoặc cùng transaction | Hoàn kho theo fulfillment hiện tại |

Manual UI không được ghi `DELIVERED` trước khi Staff hoàn tất command COD. Service có thể tự append event hệ thống sau fulfillment success để timeline và commerce luôn đồng bộ.

## 8. Label storage và bảo mật

- Chấp nhận PDF, PNG, JPEG; xác minh magic bytes, không tin extension/content-type client.
- Giới hạn 5 MB/file; tên storage UUID bất biến, không dùng tracking number làm path.
- Tính SHA-256 trong lúc stream.
- Lưu ngoài web root qua `ICarrierLabelStorage`; Docker dùng persistent volume riêng hoặc chung media root với prefix cô lập.
- Download qua authenticated endpoint, `Content-Disposition: attachment`, `X-Content-Type-Options: nosniff`, `Cache-Control: private, no-store`.
- Label cũ chỉ được thay trước handover; file thay thế được đưa vào durable cleanup queue, không xóa trước DB commit.
- Không log recipient phone/address hoặc URL nhãn.

## 9. Concurrency và idempotency

Các race bắt buộc kiểm chứng trên PostgreSQL thật:

1. Hai Staff tạo consignment cho cùng shipment: một `201`, một replay/`409`, không `500`.
2. Hai shipment dùng cùng carrier + tracking number: unique violation thành `409 TRACKING_NUMBER_CONFLICT`.
3. Cùng event key/cùng payload: replay không append lần hai.
4. Cùng event key/khác payload: `409 TRACKING_IDEMPOTENCY_CONFLICT`.
5. Event out-of-order cạnh tranh: chỉ transition hợp lệ commit.
6. Handover cũ ↔ handover qua consignment: một stock-out, một DeliveryAttempt, một event.
7. Carrier deactivate ↔ create consignment: hoặc tạo hoàn chỉnh với carrier active trước commit, hoặc bị chặn; không vận đơn nửa vời.
8. Fulfillment delivered ↔ manual tracking event: không deadlock và không double notification.
9. Label upload ↔ handover: handover thấy toàn bộ label metadata hoặc chặn; không đọc file orphan.
10. Return-stock/retry ↔ tracking return/retry: giữ thứ tự Order -> Shipment -> Consignment.

## 10. Frontend

### 10.1. Admin Dashboard

Tab `Đơn vị vận chuyển`:

- list/search/filter active;
- form tạo/sửa carrier;
- activate/deactivate với ConfirmDialog;
- hiển thị revision và stale reload;
- không hiển thị secret/webhook khi V1 chưa có adapter.

### 10.2. Staff Dashboard

Trong Fulfillment workspace:

- queue `Chưa có vận đơn`, `Có nhãn`, `Đã bàn giao`, `Đang giao`, `Cần xử lý`;
- chọn carrier, nhập tracking number/service code;
- upload/tải nhãn;
- một CTA handover duy nhất thực hiện cả fulfillment + consignment;
- form event có allow-list theo trạng thái hiện tại;
- timeline hiển thị thời điểm xảy ra và thời điểm ghi nhận;
- retry/return-stock tái sử dụng command hiện hữu.

### 10.3. Customer Order Center

- Card đơn hiển thị carrier + tracking number khi tồn tại.
- Link tracking external chỉ render từ template HTTPS đã sanitize, mở `noopener noreferrer`.
- Timeline có nhãn tiếng Việt, địa điểm/mô tả đã sanitize.
- Empty state rõ ràng cho đơn legacy hoặc chưa cấp vận đơn.
- Mobile timeline một cột, keyboard/focus/accessibility đầy đủ.

## 11. Error contract

| HTTP | Code | Ý nghĩa |
|---|---|---|
| 400 | `TRACKING_INVALID_ARGUMENT` | Tracking/event/file không hợp lệ |
| 400 | `TRACKING_INVALID_TRANSITION` | Event không đúng state machine |
| 401/403 | chuẩn auth | Chưa đăng nhập/sai role |
| 404 | `CARRIER_NOT_FOUND` | Không có carrier |
| 404 | `TRACKING_NOT_FOUND` | Không có hoặc không thuộc owner |
| 409 | `CARRIER_CODE_CONFLICT` | Trùng code |
| 409 | `CARRIER_STALE` | Revision carrier cũ |
| 409 | `CARRIER_INACTIVE` | Carrier không nhận vận đơn mới |
| 409 | `CONSIGNMENT_ALREADY_EXISTS` | Shipment đã có consignment |
| 409 | `CONSIGNMENT_STALE` | Revision consignment cũ |
| 409 | `TRACKING_NUMBER_CONFLICT` | Trùng tracking number trong carrier |
| 409 | `TRACKING_IDEMPOTENCY_CONFLICT` | Cùng event key nhưng khác payload |
| 409 | `SHIPMENT_STATE_CONFLICT` | Shipment nghiệp vụ không phù hợp |
| 413 | `LABEL_TOO_LARGE` | Nhãn vượt 5 MB |
| 415 | `LABEL_MEDIA_TYPE_UNSUPPORTED` | Signature file không hợp lệ |

ProblemDetails phải có `code`, `correlationId`; không trả exception/SQL/path vật lý.

## 12. Kiểm thử

### 12.1. Backend integration

- Migration fresh/reapply/legacy và DB constraints.
- RBAC Guest/Customer/Staff/Admin cho từng API.
- Carrier CRUD, revision, deactivate guard, Audit Log.
- Consignment cho sales/exchange, snapshot và unique tracking.
- Label validation/storage rollback/cleanup.
- Owner isolation Customer.
- Idempotency và toàn bộ race mục 9.
- Regression fulfillment stock-out, COD exact amount, retry, return-stock.
- Regression exchange shipment/quarantine.
- Notification exactly-once.

### 12.2. Frontend

- Runtime parser từ chối malformed contracts.
- Admin CRUD/status/stale.
- Staff create/upload/handover/event timeline.
- Customer tracking/empty/cross-owner error UI.
- Accessibility dialog, form error, disabled/busy state.

### 12.3. Playwright Flow 15

1. Admin tạo và activate carrier nội bộ.
2. Customer tạo đơn COD với shipping method T48.
3. Staff xác nhận COD, pack, tạo consignment, upload nhãn và handover.
4. Customer thấy tracking number và timeline handover.
5. Staff ghi pickup, transit, out-for-delivery.
6. Staff hoàn tất giao COD đúng tổng; system append delivered event đúng một lần.
7. Customer thấy Order completed và tracking delivered.
8. Nhánh thứ hai kiểm tra failed -> retry -> delivered hoặc returned, không double stock movement.

## 13. Chia task triển khai

| Bước | Hạng mục | Acceptance |
|---|---|---|
| T49.1 | ADR 0014 + migration 019 — **Hoàn tất** | Fresh/reapply/legacy/schema constraints pass |
| T49.2 | EF models + contracts + storage abstraction — **Hoàn tất** | Mapping/build và label validation tests pass; full BE 361/361 |
| T49.3 | Admin carrier API — **Hoàn tất** | RBAC/revision/audit/concurrency pass; full BE 366/366 |
| T49.4 | Staff consignment + event API — **Hoàn tất** | State machine/idempotency/unique tracking pass; full BE 373/373 |
| T49.5 | Fulfillment + Exchange integration — **Hoàn tất** | Một stock-out/attempt/event, legacy↔carrier replay, lifecycle sync; full BE 375/375 |
| T49.6 | Customer tracking API + notifications — **Hoàn tất** | Owner-only, safe projection, legacy null, notification exactly-once; full BE 375/375 |
| T49.7 | Admin/Staff/Customer React UI — **Hoàn tất** | Carrier registry, consignment operations, Customer timeline; FE 134/134, lint/build sạch |
| T49.8 | Playwright Flow 15 + docs/release gate — **Hoàn tất** | Flow 15 và regression 3/7/8/14 pass trên Docker; full BE/FE/lint/build/Compose xanh |

## 14. Release gate

Chỉ đóng T49 khi:

- Full backend suite pass trên PostgreSQL thật.
- Full frontend tests, oxlint và production build pass.
- Migration 019 pass database sạch, legacy upgrade và reapply.
- Unique tracking/event idempotency/concurrency không lọt HTTP 500.
- Handover race không double stock-out, DeliveryAttempt hoặc notification.
- Carrier event không thể tự thu COD hoặc hoàn tất Order.
- Sales shipment và Exchange shipment đều có regression tests.
- Existing Playwright Flow 3, 7, 8 và 14 không regression.
- Playwright Flow 15 pass trên Docker demo stack.
- API docs, README, TASKS, ADR 0014 và Graphify được cập nhật.

## 15. Rủi ro và điểm dừng

- Không mở endpoint webhook public chỉ để “chuẩn bị trước”; chỉ thêm khi có provider + signature fixture thật.
- Không hợp nhất `ShipmentStatus` và `ConsignmentStatus`; hai state machine có trách nhiệm khác nhau.
- Không để manual tracking event trở thành đường vòng bypass COD amount/reference.
- Không sửa Order snapshot T48 khi tạo vận đơn.
- Nếu label storage không bảo đảm atomic DB/file cleanup, dừng ở metadata-only trước handover thay vì chấp nhận orphan không kiểm soát.

## 16. Thứ tự bắt đầu

1. T49.1: ADR 0014, migration 019, migration tests.
2. T49.2: entity mapping, contracts, label storage interface.
3. T49.3–T49.4: API carrier/consignment/event.
4. T49.5–T49.6: tích hợp fulfillment/exchange/customer.
5. T49.7: ba workspace React.
6. T49.8: Flow 15, regression, docs và Graphify.
