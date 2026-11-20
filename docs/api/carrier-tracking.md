# Carrier Consignments & Tracking API

Trạng thái: **T49.1–T49.8 đã hoàn tất**. Backend Admin/Staff, fulfillment orchestration, Customer projection, ba React workspace và Playwright Flow 15 đã được nghiệm thu.

## Quy ước chung

- Base URL: `/api/v1`.
- Tất cả response dùng `Cache-Control: no-store`.
- Mutation yêu cầu đúng session theo policy của route (`ADMIN` hoặc `STAFF`) và header `X-CSRF-TOKEN`.
- `code` được trim, chuyển uppercase và không thể sửa sau khi tạo.
- Carrier mới luôn có trạng thái `INACTIVE`, `revision = 1`.
- Mutation metadata/trạng thái bắt buộc `expectedRevision`; stale write trả `409 CARRIER_STALE`.
- Deactivate chỉ chặn tạo vận đơn mới, không xóa hoặc thay đổi lịch sử vận đơn.

## Admin carrier master

Base route: `/api/v1/admin/carriers`, policy `Admin`.

### Danh sách

`GET /api/v1/admin/carriers?q={query}&status={ACTIVE|INACTIVE}`

- `q` tìm không phân biệt hoa/thường theo `code` hoặc `name` và escape ký tự wildcard.
- `status` không hợp lệ trả `400 VALIDATION_ERROR`.
- Kết quả gồm `consignmentCount` và sắp xếp carrier đang hoạt động trước, sau đó theo tên.

### Chi tiết

`GET /api/v1/admin/carriers/{id}`

Không tìm thấy trả `404 CARRIER_NOT_FOUND`.

### Tạo carrier

`POST /api/v1/admin/carriers`

```json
{
  "code": "GHN",
  "name": "Giao Hàng Nhanh",
  "hotline": "1900636677",
  "trackingUrlTemplate": "https://tracking.example.com/{trackingNumber}"
}
```

- `code`: 2–32 ký tự `A-Z`, `0-9`, `_`, `-` sau chuẩn hóa.
- `name`: 2–120 ký tự.
- `trackingUrlTemplate`: nullable; nếu có phải là HTTPS tuyệt đối và chứa đúng placeholder `{trackingNumber}`.
- Trùng code, kể cả request đồng thời, trả `409 CARRIER_CODE_CONFLICT`, không phát sinh 500.
- Thành công trả `201 Created` và audit `CARRIER_CREATED`.

### Cập nhật metadata

`PATCH /api/v1/admin/carriers/{id}`

```json
{
  "name": "Giao Hàng Nhanh",
  "hotline": "1900636677",
  "trackingUrlTemplate": "https://tracking.example.com/{trackingNumber}",
  "expectedRevision": 1
}
```

Thành công tăng revision và ghi audit `CARRIER_UPDATED`.

### Kích hoạt/ngừng hoạt động

- `POST /api/v1/admin/carriers/{id}/activate`
- `POST /api/v1/admin/carriers/{id}/deactivate`

Body:

```json
{ "expectedRevision": 2 }
```

Transition tới đúng trạng thái hiện tại là idempotent: trả trạng thái hiện tại, không tăng revision và không ghi audit lặp. Transition thực tế tăng revision và ghi `CARRIER_ACTIVATED` hoặc `CARRIER_DEACTIVATED`.

## Mã lỗi

| HTTP | Code | Ý nghĩa |
|---|---|---|
| 400 | `VALIDATION_ERROR` | Model/filter không hợp lệ |
| 400 | `CARRIER_TRACKING_URL_INVALID` | URL không phải HTTPS hoặc thiếu placeholder |
| 401 | — | Chưa đăng nhập |
| 403 | — | Không có role Admin |
| 404 | `CARRIER_NOT_FOUND` | Carrier không tồn tại |
| 409 | `CARRIER_CODE_CONFLICT` | Code đã tồn tại |
| 409 | `CARRIER_STALE` | Revision đã thay đổi |

ProblemDetails luôn kèm `correlationId`.

## Staff consignment

Base route: `/api/v1/staff/carrier-consignments`, chỉ policy `Staff`; Customer và Admin đều nhận `403` theo separation of duties.

### Danh sách carrier khả dụng

`GET /carriers`

Trả danh sách carrier `ACTIVE` tối giản (`id`, `code`, `name`) để Staff chọn khi tạo vận đơn. Endpoint không lộ revision hay metadata quản trị và không yêu cầu frontend gọi chéo API Admin.

### Tạo vận đơn

- `POST /sales-shipments/{shipmentId}`
- `POST /exchange-shipments/{shipmentId}`

```json
{
  "carrierId": "00000000-0000-0000-0000-000000000000",
  "trackingNumber": "GHN123456789",
  "serviceCode": "EXPRESS"
}
```

- Carrier phải `ACTIVE`; create giữ `FOR SHARE` trên carrier để tuần tự hóa an toàn với Admin deactivate nhưng vẫn cho các create khác chạy song song.
- Shipment phải `PENDING_PACKING` hoặc `PACKED`.
- Mỗi shipment chỉ có một consignment; mỗi `(carrier, upper(trackingNumber))` là duy nhất.
- Snapshot người nhận, địa chỉ và shipping method được lấy từ Order tại thời điểm tạo và không đọc lại từ hồ sơ Customer.
- Thành công trả `201`, trạng thái `DRAFT`, revision 1 và event `CONSIGNMENT_CREATED`.

### Tra cứu vận hành

- `GET /queue?status={status}`: danh sách consignment, có thể lọc theo state allow-list.
- `GET /{id}`: metadata, label summary và timeline.

Response không trả storage key, payload hash hoặc actor ID nội bộ.

### Nhãn vận chuyển

- `POST /{id}/label`: multipart gồm `file` và `expectedRevision`.
- `GET /{id}/label`: tải file với `Content-Disposition: attachment`, `nosniff`, `private, no-store`.

PDF/PNG/JPEG được kiểm tra bằng magic bytes, giới hạn 5 MiB và tính SHA-256 khi stream. Nhãn chỉ được gắn/thay ở `DRAFT|LABEL_READY`; mỗi lần thành công tăng revision, chuyển `LABEL_READY`, append `LABEL_CREATED` và ghi audit. File mới được xóa bù nếu DB mutation thất bại.

### Manual tracking event

`POST /{id}/events`

```json
{
  "eventKey": "provider-pickup-001",
  "eventType": "PICKED_UP",
  "location": "Kho Quận 1",
  "description": "Đã lấy hàng",
  "occurredAt": "2026-11-07T08:30:00Z",
  "expectedRevision": 2
}
```

Manual allow-list: `PICKED_UP`, `IN_TRANSIT`, `OUT_FOR_DELIVERY`, `DELIVERY_FAILED`, `RETURNING`. `DELIVERED` không được nhận từ form manual vì phải đi qua fulfillment/COD trusted command.

- Cùng `eventKey` và canonical payload hash: replay trả state hiện tại, không tăng revision.
- Cùng key nhưng payload khác: `409 TRACKING_IDEMPOTENCY_CONFLICT`.
- `occurredAt` không được trước thời điểm tạo shipment hoặc quá 5 phút trong tương lai.
- Transition revalidate cả consignment state và shipment state dưới transaction.

### Hủy trước bàn giao

`POST /{id}/cancel`

```json
{ "expectedRevision": 3 }
```

Chỉ `DRAFT|LABEL_READY` được hủy.

### Handover nguyên tử

`POST /{id}/handover`

```json
{ "expectedRevision": 2 }
```

Lệnh này không chỉ đổi tracking state. Nó gọi đúng fulfillment implementation hiện hữu cho Sales hoặc Exchange và thực hiện trong một transaction:

1. khóa Order/Return aggregate, Shipment rồi Consignment;
2. xác minh consignment `LABEL_READY` và revision;
3. khóa SKU/Inventory theo thứ tự chuẩn;
4. stock-out allocation đúng một lần;
5. tạo DeliveryAttempt số 1 đúng một lần;
6. chuyển Shipment sang `SHIPPING` và Consignment sang `HANDED_OVER`;
7. append system event, audit và notification;
8. commit toàn bộ hoặc rollback toàn bộ.

API legacy `/api/v1/staff/shipments/{id}/handover` và `/api/v1/staff/exchange-shipments/{id}/handover` dùng chung transaction trên. Nếu shipment đã có consignment, API legacy cũng bắt buộc label `LABEL_READY`. Hai API chạy đồng thời đều replay an toàn và không double stock movement/attempt/event. Shipment lịch sử không có consignment vẫn giữ contract cũ.

### Đồng bộ lifecycle từ fulfillment

Các trusted command fulfillment tự append timeline `SYSTEM`:

- handover → `HANDED_OVER`;
- delivery failed → `DELIVERY_FAILED`;
- retry → `REDELIVERY_STARTED`, state `OUT_FOR_DELIVERY`;
- delivered/thu COD hoặc exchange delivered → `DELIVERED`;
- return-stock → `RETURNED`.

Manual form vẫn không được tự ghi `DELIVERED` hoặc thay đổi Payment/COD.

## Customer tracking

Policy: `Customer`; mọi response `no-store`.

### Timeline owner-only

`GET /api/v1/orders/{orderId}/tracking`

- Chỉ trả consignment gắn với Sales Shipment của chính Customer đang đăng nhập.
- Order của Customer khác, ID không tồn tại và đơn chưa có consignment cùng trả `404 TRACKING_NOT_FOUND` để không tạo ownership oracle.
- Timeline sắp xếp theo `occurredAt`, sau đó `id`.
- Projection không chứa `actorId`, `payloadHash`, `externalEventId`, `labelFileKey`, audit data hoặc đường dẫn filesystem.
- External tracking URL chỉ được trả nếu sau khi thay `{trackingNumber}` vẫn là URL HTTPS tuyệt đối.

### Tracking summary trong Order Center

`GET /api/v1/orders` và `GET /api/v1/orders/{orderId}` có thêm trường nullable:

```json
{
  "tracking": {
    "consignmentId": "00000000-0000-0000-0000-000000000000",
    "carrierCode": "GHN",
    "carrierName": "Giao Hàng Nhanh",
    "trackingNumber": "GHN123456789",
    "trackingUrl": "https://tracking.example.com/GHN123456789",
    "consignmentStatus": "IN_TRANSIT"
  }
}
```

Đơn legacy hoặc chưa cấp vận đơn trả `tracking: null`; các trường cũ giữ nguyên.

### Notification manual event

Mỗi manual tracking event thành công tạo một notification `ORDER_TRACKING_UPDATED` trong cùng transaction. Event key notification dùng hash ổn định của `(consignmentId, eventKey)`, nên replay cùng payload không gửi trùng; cùng key khác payload vẫn trả `409 TRACKING_IDEMPOTENCY_CONFLICT`.

### Mã lỗi Staff bổ sung

| HTTP | Code | Ý nghĩa |
|---|---|---|
| 400 | `TRACKING_INVALID_ARGUMENT` | Input, thời gian hoặc queue filter không hợp lệ |
| 400 | `TRACKING_INVALID_TRANSITION` | State machine không cho phép transition |
| 404 | `TRACKING_NOT_FOUND` | Consignment/shipment/label không tồn tại |
| 409 | `CARRIER_INACTIVE` | Carrier đã ngừng nhận vận đơn mới |
| 409 | `CONSIGNMENT_ALREADY_EXISTS` | Shipment đã có consignment |
| 409 | `CONSIGNMENT_STALE` | Revision đã thay đổi |
| 409 | `TRACKING_NUMBER_CONFLICT` | Trùng tracking number trong carrier |
| 409 | `TRACKING_IDEMPOTENCY_CONFLICT` | Replay cùng key nhưng khác payload |
| 409 | `SHIPMENT_STATE_CONFLICT` | Shipment nghiệp vụ không phù hợp |
| 413 | `LABEL_TOO_LARGE` | File vượt 5 MiB |
| 415 | `LABEL_MEDIA_TYPE_UNSUPPORTED` | Magic bytes không được hỗ trợ |
