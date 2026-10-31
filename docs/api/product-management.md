# Product Management & Media API Contract

> Triển khai cho task T12 — Quản trị sản phẩm Draft/SKU/Giá/Publish và Media ảnh.
> Yêu cầu CSRF: Mọi mutation (`POST`, `PATCH`, `DELETE`) bắt buộc gửi kèm header `X-CSRF-TOKEN`.

---

## 1. Phân quyền

* **Staff (`[Authorize(Policy = "Staff")]`):**
  * `POST /api/v1/staff/products`: Tạo sản phẩm ở trạng thái `DRAFT`.
  * `PATCH /api/v1/staff/products/{id}/draft`: Sửa thông tin sản phẩm Draft (tiêu đề, mô tả, danh mục).
  * `POST /api/v1/staff/products/{id}/skus`: Thêm biến thể SKU cho sản phẩm Draft.
  * `POST /api/v1/staff/products/{id}/images`: Thêm ảnh cho sản phẩm.
  * `DELETE /api/v1/staff/products/{id}/images/{imageId}`: Xóa ảnh của sản phẩm.
* **Admin (`[Authorize(Policy = "Admin")]`):**
  * `PATCH /api/v1/admin/skus/{id}/price`: Đặt hoặc cập nhật giá `regular_price` cho SKU (ghi nhận Audit Log).
  * `POST /api/v1/admin/products/{id}/publish`: Publish sản phẩm đủ điều kiện (ghi nhận Audit Log).

---

## 2. Chi tiết Endpoints Staff

### `POST /api/v1/staff/products`
Tạo sản phẩm Draft mới.

#### Request Body
```json
{
  "title": "Vợt cầu lông Yonex Astrox 88D Pro",
  "description": "Thân vợt cứng, đầm đầu, trợ lực tấn công tối đa.",
  "categoryId": "10000000-0000-0000-0000-000000000001"
}
```
* `title`: string, bắt buộc, 1–240 ký tự.
* `description`: string nullable.
* `categoryId`: UUID nullable.

#### Response `201 Created`
Headers: `Location: /api/v1/staff/products/{id}`
Body:
```json
{
  "id": "20000000-0000-0000-0000-000000000099",
  "title": "Vợt cầu lông Yonex Astrox 88D Pro",
  "description": "Thân vợt cứng, đầm đầu, trợ lực tấn công tối đa.",
  "categoryId": "10000000-0000-0000-0000-000000000001",
  "publicationStatus": "DRAFT",
  "createdAt": "2026-10-30T10:00:00Z"
}
```

---

### `PATCH /api/v1/staff/products/{id}/draft`
Chỉnh sửa thông tin cơ bản của sản phẩm Draft. Không cho phép sửa nếu sản phẩm đã `PUBLISHED` hoặc `ARCHIVED`.

#### Request Body
```json
{
  "title": "Vợt cầu lông Yonex Astrox 88D Pro 2026",
  "description": "Phiên bản nâng cấp mới.",
  "categoryId": "10000000-0000-0000-0000-000000000001"
}
```

#### Response `200 OK`
Trả về thông tin sản phẩm sau khi sửa.
Lỗi `400 Bad Request` nếu sản phẩm không ở trạng thái `DRAFT` (`code: INVALID_STATE`).

---

### `POST /api/v1/staff/products/{id}/skus`
Thêm SKU vào sản phẩm Draft. Giá `regular_price` ban đầu khởi tạo bằng 0 (chờ Admin định giá).

#### Request Body
```json
{
  "skuCode": "AX88D-4UG5",
  "optionLabel": "4U - Cán G5"
}
```
* `skuCode`: string, 1–100 ký tự, duy nhất trong hệ thống.
* `optionLabel`: string, 1–200 ký tự.

#### Response `201 Created`
Body:
```json
{
  "id": "30000000-0000-0000-0000-000000000099",
  "productId": "20000000-0000-0000-0000-000000000099",
  "skuCode": "AX88D-4UG5",
  "optionLabel": "4U - Cán G5",
  "regularPrice": 0,
  "skuStatus": "ACTIVE"
}
```

---

### `POST /api/v1/staff/products/{id}/images`
Thêm hình ảnh cho sản phẩm.

#### Request Body
```json
{
  "fileKey": "https://images.example.com/badminton/astrox-88d.jpg",
  "altText": "Mặt trước vợt Yonex Astrox 88D Pro",
  "displayOrder": 0
}
```
* `fileKey`: string URL hoặc key ảnh, 1–500 ký tự.
* `altText`: string nullable, tối đa 240 ký tự.
* `displayOrder`: integer $\ge 0$.

#### Response `201 Created`
Body:
```json
{
  "id": "40000000-0000-0000-0000-000000000099",
  "productId": "20000000-0000-0000-0000-000000000099",
  "fileKey": "https://images.example.com/badminton/astrox-88d.jpg",
  "altText": "Mặt trước vợt Yonex Astrox 88D Pro",
  "displayOrder": 0,
  "imageStatus": "ACTIVE"
}
```

---

### `DELETE /api/v1/staff/products/{id}/images/{imageId}`
Xóa ảnh khỏi sản phẩm.
Response: `204 No Content`.

---

## 3. Chi tiết Endpoints Admin

### `PATCH /api/v1/admin/skus/{id}/price`
Admin đặt hoặc cập nhật giá `regular_price` cho SKU.
* Ghi log vào `store.audit_logs`:
  * `action`: `"SKU_PRICE_UPDATED"`
  * `entity_type`: `"ProductSku"`
  * `entity_id`: `skuId`
  * `before_data`: `{"regularPrice": 0}`
  * `after_data`: `{"regularPrice": 4200000}`
  * `actor_id`: `adminId`

#### Request Body
```json
{
  "price": 4200000
}
```
* `price`: decimal $> 0$.

#### Response `200 OK`
Body:
```json
{
  "id": "30000000-0000-0000-0000-000000000099",
  "skuCode": "AX88D-4UG5",
  "price": 4200000
}
```

---

### `POST /api/v1/admin/products/{id}/publish`
Admin kiểm duyệt và Publish sản phẩm ra Public Catalog.
* Điều kiện hợp lệ:
  1. `publication_status == 'DRAFT'`
  2. Sản phẩm có ít nhất 1 SKU `ACTIVE`
  3. Tất cả các SKU `ACTIVE` phải có `regular_price > 0`
* Ghi log vào `store.audit_logs`:
  * `action`: `"PRODUCT_PUBLISHED"`
  * `entity_type`: `"Product"`
  * `entity_id`: `productId`
  * `actor_id`: `adminId`

#### Response `200 OK`
Body:
```json
{
  "id": "20000000-0000-0000-0000-000000000099",
  "publicationStatus": "PUBLISHED",
  "publishedAt": "2026-10-30T10:15:00Z"
}
```

#### Lỗi
* `400 Bad Request`: `code: CANNOT_PUBLISH` kèm lý do cụ thể (chưa có SKU, SKU chưa có giá, trạng thái không hợp lệ).

