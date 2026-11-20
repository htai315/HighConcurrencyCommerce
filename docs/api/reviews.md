# Product Reviews & Ratings API Specification

Tài liệu đặc tả các API Đánh giá & Nhận xét sản phẩm (T36) thuộc hệ thống thương mại điện tử HighConcurrencyCommerce.

---

## 1. Tổng quan & Chính sách xác thực (Authentication & RBAC)

| Nhóm API | Đường dẫn cơ sở | Phân quyền (Policy) | Yêu cầu CSRF |
|---|---|---|---|
| **Public Catalog** | `/api/v1/catalog/products/{productId}/reviews` | Anonymous (`AllowAnonymous`) | Không |
| **Customer Reviews** | `/api/v1/products/{productId}/...` & `/api/v1/reviews/...` | `Customer` | Có (POST, PATCH, DELETE) |
| **Moderation** | `/api/v1/moderation/reviews` | `ReviewModerator` (`STAFF` hoặc `ADMIN`) | Có (PATCH) |

- **Session cookie**: Sử dụng cookie `__Host-hcc-session` (production) hoặc `hcc-session` (local/testing).
- **CSRF Token**: Bắt buộc đính kèm header `X-CSRF-TOKEN` cho mọi mutation (`POST`, `PATCH`, `DELETE`). Lấy token qua `GET /api/v1/auth/csrf`.
- **Mã lỗi chuẩn**: Định dạng RFC 7807 ProblemDetails với extension `code` và `correlationId`.

---

## 2. Public Catalog API

### 2.1 Lấy danh sách đánh giá công khai & tổng quan sao
- **Endpoint**: `GET /api/v1/catalog/products/{productId}/reviews`
- **Quyền**: Public / Không cần đăng nhập.
- **Query Parameters**:
  - `page` (int, default: 1, range: 1..100.000): Trang hiện tại.
  - `pageSize` (int, default: 10, range: 1..100): Số lượng review mỗi trang.
  - `sort` (string, default: `newest`, options: `newest`, `highest`, `lowest`): Sắp xếp.
  - `rating` (int, optional, range: 1..5): Lọc số sao trước phân trang. `totalCount` đếm kết quả sau lọc; `summary` giữ thống kê toàn bộ review công khai của sản phẩm.
  - Sản phẩm không tồn tại hoặc không ở trạng thái `PUBLISHED`: `404 PRODUCT_NOT_FOUND`.
  - Visibility, summary, filtered count và items được đọc trong cùng snapshot PostgreSQL `REPEATABLE READ`.
- **Response**: `200 OK`
```json
{
  "items": [
    {
      "id": "e301d0a5-f938-4f51-b844-3ec6b7f3a129",
      "rating": 5,
      "content": "Vợt đập cầu rất thoát tay, lưới căng chuẩn chỉ!",
      "createdAt": "2026-11-04T10:00:00Z",
      "updatedAt": "2026-11-04T10:00:00Z",
      "reviewerDisplayName": "N***n T*",
      "verifiedPurchase": true
    }
  ],
  "summary": {
    "averageRating": 4.67,
    "reviewCount": 3,
    "distribution": {
      "one": 0,
      "two": 0,
      "three": 0,
      "four": 1,
      "five": 2
    }
  },
  "page": 1,
  "pageSize": 10,
  "totalCount": 3
}
```

> [!NOTE]
> Reviewer display name luôn được mask ở server (ví dụ: *"N\*\*\*n T\*"*). Public DTO tuyệt đối không chứa email, số điện thoại hay ID của khách hàng. Chỉ các review `PUBLISHED` và `deleted_at IS NULL` mới xuất hiện công khai.

---

## 3. Customer Reviews API

### 3.1 Kiểm tra điều kiện đánh giá (Review Eligibility)
- **Endpoint**: `GET /api/v1/products/{productId}/review-eligibility`
- **Quyền**: `Customer`
- **Response**: `200 OK`
  - Nếu đủ điều kiện (có đơn hàng `COMPLETED` với shipment `DELIVERED` và chưa có review active):
  ```json
  {
    "eligible": true,
    "reasonCode": null,
    "existingReview": null
  }
  ```
  - Nếu chưa mua hoặc chưa nhận hàng:
  ```json
  {
    "eligible": false,
    "reasonCode": "NO_DELIVERED_ORDER",
    "existingReview": null
  }
  ```
  - Nếu đã đánh giá rồi:
  ```json
  {
    "eligible": false,
    "reasonCode": "ALREADY_REVIEWED",
    "existingReview": {
      "id": "e301d0a5-f938-4f51-b844-3ec6b7f3a129",
      "productId": "9d4f0d2c-8438-4f81-a9e9-74d6b7f3a001",
      "productTitle": "Vợt Yonex Astrox 88D Pro",
      "rating": 5,
      "content": "Vợt đập cầu rất thoát tay, lưới căng chuẩn chỉ!",
      "reviewStatus": "PUBLISHED",
      "createdAt": "2026-11-04T10:00:00Z",
      "updatedAt": "2026-11-04T10:00:00Z",
      "moderatedAt": null,
      "moderationReason": null
    }
  }
  ```

---

### 3.2 Tạo đánh giá mới
- **Endpoint**: `POST /api/v1/products/{productId}/reviews`
- **Quyền**: `Customer`
- **Headers**: `X-CSRF-TOKEN: <token>`
- **Request Body**:
```json
{
  "rating": 5,
  "content": "Vợt đập cầu rất thoát tay, lưới căng chuẩn chỉ!"
}
```
- **Validation**:
  - `rating`: Bắt buộc, số nguyên từ 1 đến 5.
  - `content`: Bắt buộc, từ 10 đến 2.000 ký tự (sau khi trim).
- **Response**: `201 Created`
```json
{
  "id": "e301d0a5-f938-4f51-b844-3ec6b7f3a129",
  "productId": "9d4f0d2c-8438-4f81-a9e9-74d6b7f3a001",
  "productTitle": "Vợt Yonex Astrox 88D Pro",
  "rating": 5,
  "content": "Vợt đập cầu rất thoát tay, lưới căng chuẩn chỉ!",
  "reviewStatus": "PUBLISHED",
  "createdAt": "2026-11-04T10:00:00Z",
  "updatedAt": "2026-11-04T10:00:00Z",
  "moderatedAt": null,
  "moderationReason": null
}
```
- **Lỗi thường gặp**:
  - `400 VALIDATION_ERROR`: Điểm sao hoặc độ dài nội dung không hợp lệ.
  - `404 PRODUCT_NOT_FOUND`: Sản phẩm không tồn tại hoặc chưa xuất bản (`DRAFT`).
  - `409 REVIEW_NOT_ELIGIBLE`: Khách hàng chưa nhận hàng thành công cho sản phẩm này.
  - `409 REVIEW_ALREADY_EXISTS`: Khách hàng đã có đánh giá active cho sản phẩm.

---

### 3.3 Danh sách đánh giá của tôi
- **Endpoint**: `GET /api/v1/reviews/mine`
- **Quyền**: `Customer`
- **Response**: `200 OK` (Mảng `MyProductReviewDto` đã sắp xếp theo `createdAt DESC`).

---

### 3.4 Cập nhật đánh giá
- **Endpoint**: `PATCH /api/v1/reviews/{reviewId}`
- **Quyền**: `Customer` (Chỉ chủ sở hữu review)
- **Headers**: `X-CSRF-TOKEN: <token>`
- **Request Body**:
```json
{
  "rating": 4,
  "content": "Sau 1 tuần đánh quen tay thì thấy đập rất sướng và trợ lực tốt!"
}
```
- **Response**: `200 OK` (`MyProductReviewDto` đã cập nhật).
- **Lỗi**: `404 REVIEW_NOT_FOUND` nếu review không tồn tại, đã bị xóa hoặc thuộc về khách hàng khác.

---

### 3.5 Xóa mềm đánh giá
- **Endpoint**: `DELETE /api/v1/reviews/{reviewId}`
- **Quyền**: `Customer` (Chỉ chủ sở hữu review)
- **Headers**: `X-CSRF-TOKEN: <token>`
- **Response**: `204 No Content`
- **Lưu ý**: Bản ghi được đánh dấu `deleted_at = now()`. Khách hàng được quyền tạo đánh giá mới sau khi xóa.

---

## 4. Review Moderation API

### 4.1 Danh sách hàng đợi kiểm duyệt (Staff & Admin)
- **Endpoint**: `GET /api/v1/moderation/reviews`
- **Quyền**: `ReviewModerator` (`STAFF` hoặc `ADMIN`)
- **Query Parameters**:
  - `status` (`PUBLISHED` | `HIDDEN`): Lọc theo trạng thái.
  - `search` (string): Tìm kiếm theo tên sản phẩm, tên khách hàng, email hoặc nội dung.
  - `page` (int, default: 1), `pageSize` (int, default: 20).
- **Response**: `200 OK`
```json
{
  "items": [
    {
      "id": "e301d0a5-f938-4f51-b844-3ec6b7f3a129",
      "productId": "9d4f0d2c-8438-4f81-a9e9-74d6b7f3a001",
      "productTitle": "Vợt Yonex Astrox 88D Pro",
      "customerId": "a0000000-0000-0000-0000-000000000003",
      "customerName": "Trần Thị Khách",
      "customerEmail": "customer@badminton.local",
      "rating": 1,
      "content": "Chứa nội dung spam...",
      "reviewStatus": "HIDDEN",
      "createdAt": "2026-11-04T10:00:00Z",
      "updatedAt": "2026-11-04T10:05:00Z",
      "moderatedBy": "a0000000-0000-0000-0000-000000000002",
      "moderatorName": "Nguyễn Văn Staff",
      "moderatedAt": "2026-11-04T10:05:00Z",
      "moderationReason": "Vi phạm chính sách: spam quảng cáo",
      "deletedAt": null
    }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 1
}
```

---

### 4.2 Kiểm duyệt ẩn hoặc khôi phục đánh giá
- **Endpoint**: `PATCH /api/v1/moderation/reviews/{reviewId}`
- **Quyền**: `ReviewModerator` (`STAFF` hoặc `ADMIN`)
- **Headers**: `X-CSRF-TOKEN: <token>`
- **Request Body**:
```json
{
  "action": "HIDE",
  "reason": "Chứa từ ngữ xúc phạm hoặc spam quảng cáo."
}
```
hoặc:
```json
{
  "action": "RESTORE",
  "reason": "Đã kiểm tra lại thông tin và xác nhận hợp lệ."
}
```
- **Response**: `200 OK` (`ModerationProductReviewDto`).
- **Audit Log**: Ghi nhận tự động hành động `PRODUCT_REVIEW_HIDDEN` hoặc `PRODUCT_REVIEW_RESTORED` vào bảng `store.audit_logs`.

---

## 5. Bảng mã lỗi (Error Codes)

| HTTP Status | Error Code | Mô tả |
|---|---|---|
| 400 | `VALIDATION_ERROR` | Điểm sao ngoài 1–5, độ dài nhận xét hoặc lý do kiểm duyệt không hợp lệ. |
| 401 | `UNAUTHORIZED` | Chưa đăng nhập session hợp lệ. |
| 403 | `FORBIDDEN` | Sai quyền (ví dụ: Staff gọi tạo review, hoặc Customer gọi moderation). |
| 404 | `PRODUCT_NOT_FOUND` | Sản phẩm không tồn tại hoặc chưa xuất bản. |
| 404 | `REVIEW_NOT_FOUND` | Đánh giá không tồn tại, đã bị xóa hoặc không thuộc quyền sở hữu của Customer. |
| 409 | `REVIEW_NOT_ELIGIBLE` | Khách hàng chưa có đơn hàng giao thành công (`COMPLETED` + `DELIVERED`). |
| 409 | `REVIEW_ALREADY_EXISTS` | Khách hàng đã có một đánh giá đang hoạt động cho sản phẩm này. |
| 409 | `INVALID_REVIEW_STATE` | Thao tác kiểm duyệt không hợp lệ (ví dụ: ẩn review đang ẩn, hoặc khôi phục review đang công khai). |
| 409 | `CONCURRENT_CONFLICT` | Xung đột đồng thời PostgreSQL. |

