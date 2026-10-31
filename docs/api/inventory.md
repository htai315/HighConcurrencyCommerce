# Inventory API Contract (Task T13)

> Quản lý tồn kho và biến động kho dành cho Staff.

## 1. Xem danh sách tồn kho theo SKU

```http
GET /api/v1/staff/inventory
Authorization: Session Cookie (Role: STAFF)
```

### Response 200 OK
```json
[
  {
    "skuId": "30000000-0000-0000-0000-000000000001",
    "skuCode": "AX88D-4UG5",
    "optionLabel": "4U/G5 - Đỏ Ruby",
    "productId": "20000000-0000-0000-0000-000000000001",
    "productTitle": "Vợt Yonex Astrox 88D Pro",
    "physicalQty": 10,
    "availableQty": 0,
    "reservedQty": 5,
    "allocatedQty": 5,
    "skuStatus": "ACTIVE",
    "publicationStatus": "PUBLISHED"
  }
]
```

---

## 2. Xem lịch sử biến động kho theo SKU

```http
GET /api/v1/staff/inventory/{skuId}/movements
Authorization: Session Cookie (Role: STAFF)
```

### Response 200 OK
```json
[
  {
    "id": "40000000-0000-0000-0000-000000000001",
    "productSkuId": "30000000-0000-0000-0000-000000000001",
    "movementType": "STOCK_IN",
    "qtyDelta": 10,
    "sourceType": "PURCHASE_RECEIPT",
    "sourceId": "50000000-0000-0000-0000-000000000001",
    "actorId": "00000000-0000-0000-0000-000000000002",
    "actorName": "Nguyễn Văn Staff",
    "note": "Nhập lô hàng chính hãng từ Yonex Sunrise",
    "createdAt": "2026-10-30T10:00:00Z"
  }
]
```

---

## 3. Nhập tăng tồn kho (Stock-in)

```http
POST /api/v1/staff/inventory/stock-in
Authorization: Session Cookie (Role: STAFF)
X-CSRF-TOKEN: <token>
Content-Type: application/json

{
  "productSkuId": "30000000-0000-0000-0000-000000000001",
  "quantity": 15,
  "sourceType": "PURCHASE_RECEIPT",
  "sourceId": "50000000-0000-0000-0000-000000000001",
  "note": "Nhập thêm đợt 2"
}
```

### Response 200 OK
```json
{
  "balanceId": "60000000-0000-0000-0000-000000000001",
  "productSkuId": "30000000-0000-0000-0000-000000000001",
  "physicalQty": 25,
  "availableQty": 15,
  "reservedQty": 5,
  "allocatedQty": 5,
  "revision": 2,
  "movementId": "40000000-0000-0000-0000-000000000002"
}
```

### Error Responses
- `400 Bad Request`: `VALIDATION_ERROR` (lỗi định dạng dữ liệu đầu vào) hoặc `INVALID_ARGUMENT` (số lượng nhập kho <= 0).
- `401 Unauthorized`: Chưa đăng nhập (Guest).
- `403 Forbidden`: Người dùng không có role `STAFF` (Customer hoặc Admin đều nhận 403; module kho là không gian độc quyền của Staff).
- `404 Not Found`: `NOT_FOUND` (SKU không tồn tại).
- `409 Conflict`: `CONFLICT` (tranh chấp race condition / unique constraint) hoặc `IDEMPOTENCY_CONFLICT` (gửi lại cùng `sourceId` nhưng thay đổi số lượng hoặc ghi chú so với giao dịch trước).

