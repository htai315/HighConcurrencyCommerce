# Product Lifecycle API — T46

Base URL: `/api/v1`  
Authentication: session cookie  
Mutation: bắt buộc `X-CSRF-TOKEN`  
Role: `ADMIN`

## State machine

```text
DRAFT --publish--> PUBLISHED --suspend--> SUSPENDED --resume--> PUBLISHED
                                  |
                                  +--archive--> ARCHIVED
```

`ARCHIVED` là terminal. Product `SUSPENDED`/`ARCHIVED` không xuất hiện trên Catalog và không thể Checkout; Cart/Wishlist lịch sử không bị xóa.

## Suspend

`POST /admin/products/{productId}/suspend`

```json
{ "reason": "Kiểm tra chất lượng lô hàng" }
```

- Chỉ nhận Product `PUBLISHED`.
- `reason`: bắt buộc, 5–500 ký tự.
- Không đổi SKU, giá, tồn kho, quota Flash Sale hoặc đơn hiện hữu.

## Resume

`POST /admin/products/{productId}/resume`

- Chỉ nhận Product `SUSPENDED`.
- Phải có ít nhất một SKU `ACTIVE`; mọi SKU active phải có giá lớn hơn 0.
- Flash Sale offer còn thời hạn có thể xuất hiện lại; quota được giữ nguyên.

## Archive

`POST /admin/products/{productId}/archive`

```json
{ "reason": "Sản phẩm đã ngừng kinh doanh" }
```

- Chỉ nhận Product `SUSPENDED`.
- Bị chặn nếu còn Product Revision `DRAFT`/`PENDING_REVIEW`.
- Bị chặn nếu còn offer thuộc Campaign `ACTIVE`/`SCHEDULED` chưa hết hạn.
- Không hỗ trợ khôi phục trong V1.

## Response

```json
{
  "productId": "b0000000-0000-0000-0000-000000000001",
  "publicationStatus": "SUSPENDED",
  "statusChangedAt": "2026-11-06T08:00:00Z",
  "statusChangedBy": "...",
  "statusReason": "Kiểm tra chất lượng lô hàng"
}
```

## Errors

| HTTP | Code | Ý nghĩa |
|---|---|---|
| 400 | `VALIDATION_ERROR` | Payload/lý do không hợp lệ |
| 401 | — | Chưa đăng nhập |
| 403 | — | Không phải Admin |
| 404 | `PRODUCT_NOT_FOUND` | Product không tồn tại |
| 409 | `PRODUCT_LIFECYCLE_INVALID_TRANSITION` | Transition sai trạng thái |
| 409 | `PRODUCT_RESUME_REQUIREMENTS_NOT_MET` | SKU/giá chưa đủ điều kiện resume |
| 409 | `PRODUCT_ARCHIVE_OPEN_REVISION` | Còn revision mở |
| 409 | `PRODUCT_ARCHIVE_ACTIVE_FLASH_SALE` | Còn Flash Sale hiệu lực |

ProblemDetails luôn có `code` và `correlationId`.

## Concurrency contract

Lock order của Checkout là:

```text
Customer -> Voucher -> Product -> Flash Sale Campaign -> Offer -> SKU -> Inventory
```

Lifecycle và T47 cùng khóa Product. Nếu Checkout lấy Product lock trước, đơn được phép hoàn tất rồi Suspend commit. Nếu Suspend lấy lock trước, Checkout trả 409 và không giữ tồn/quota/voucher. Approval T47 trên Product suspended chỉ cập nhật nội dung, tuyệt đối không resume.
