# Customer Orders API

Contract đã triển khai cho trung tâm đơn hàng của Customer. Base path: `/api/v1/orders`.

## Bảo mật và dữ liệu

- Tất cả endpoint yêu cầu session có role `CUSTOMER`; Guest nhận `401`, role khác nhận `403`.
- `customer_id` luôn lấy từ session, không nhận từ request.
- Truy cập đơn của Customer khác trả `404`, không tiết lộ sự tồn tại.
- Response có `Cache-Control: no-store`.
- Tên sản phẩm, SKU, giá, người nhận, số điện thoại và địa chỉ là snapshot tại thời điểm checkout.
- Order, Payment, Reservation và Shipment là các state machine độc lập; client không được suy diễn trạng thái này từ trạng thái kia.

## GET `/api/v1/orders?page=1&pageSize=20`

Trả lịch sử đơn của Customer hiện tại, mới nhất trước. `page` từ 1; `pageSize` từ 1 đến 100.

```json
{
  "items": [
    {
      "id": "uuid",
      "orderNumber": "ORD-20261030-001",
      "orderStatus": "PENDING_COD_CONFIRMATION",
      "paymentMethod": "COD",
      "paymentStatus": "COD_PENDING",
      "reservationStatus": "HELD",
      "totalAmount": 2500000,
      "createdAt": "2026-10-30T02:00:00Z",
      "deadlineAt": "2026-10-31T02:00:00Z",
      "confirmedAt": null,
      "closedReason": null,
      "recipientName": "Nguyễn Văn A",
      "recipientPhone": "0901234567",
      "shippingAddress": "12 Nguyễn Trãi, Quận 1, TP.HCM",
      "shipmentId": null,
      "shipmentStatus": null,
      "shippedAt": null,
      "deliveredAt": null,
      "redeliveryCount": 0,
      "canCancel": true,
      "cancellationReason": null,
      "items": [
        {
          "productSkuId": "uuid",
          "productTitle": "Vợt Yonex Astrox",
          "skuCode": "ASTROX-4U",
          "optionLabel": "4U · Đỏ",
          "quantity": 1,
          "unitPrice": 2500000,
          "lineTotal": 2500000
        }
      ],
      "deliveryAttempts": []
    }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 1
}
```

## GET `/api/v1/orders/{id}`

Trả chi tiết một đơn thuộc Customer hiện tại, cùng shape với phần tử trong danh sách. Đơn không tồn tại hoặc không thuộc quyền sở hữu đều trả `404 NOT_FOUND`.

## POST `/api/v1/orders/{id}/cancel`

Yêu cầu header `X-CSRF-TOKEN`. Body:

```json
{ "reason": "Đặt nhầm phiên bản vợt" }
```

`reason` sau khi trim phải dài 3–500 ký tự. Customer chỉ được hủy trực tiếp đơn COD đang `PENDING_COD_CONFIRMATION`, Payment `COD_PENDING`, Reservation và mọi Reservation Line còn `HELD`.

Trong một transaction, service khóa Order rồi các SKU/Inventory Balance theo thứ tự ổn định và thực hiện:

1. Chuyển `ReservedQty` sang `AvailableQty`, không đổi `PhysicalQty`.
2. Chuyển Reservation và Reservation Lines sang `RELEASED`.
3. Ghi Inventory Movement `RELEASE`.
4. Chuyển Payment sang `VOID`.
5. Chuyển Order sang `CANCELLED`, `closed_reason = CUSTOMER_CANCELLED`.
6. Ghi cancellation evidence, Audit Log và notification owner-only.

Replay trên đơn đã được chính Customer hủy trả lại trạng thái hiện tại, không hoàn kho hoặc ghi side effect lần hai. Race Customer cancel với Staff confirm được tuần tự hóa bằng cùng khóa Order: chỉ một nhánh thắng.

### Mã lỗi

| HTTP | Code | Khi nào |
|---|---|---|
| 400 | `VALIDATION_ERROR` | Body/reason không hợp lệ |
| 401 | — | Chưa đăng nhập |
| 403 | — | Không phải Customer |
| 404 | `NOT_FOUND` | Không có đơn thuộc Customer hiện tại |
| 409 | `ORDER_NOT_CANCELLABLE` | Đơn không còn đủ điều kiện hủy trực tiếp |
| 409 | `ORDER_STATE_INVARIANT` | Reservation hoặc Payment không nhất quán |
| 409 | `INVENTORY_INVARIANT` | Tồn Reserved không đủ để hoàn kho |

