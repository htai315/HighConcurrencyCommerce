# Wishlist API Specification (T37)

Tài liệu đặc tả API Danh sách yêu thích (Wishlist) cho khách hàng mua sắm tại HighCommerce.

---

## 1. Tổng quan & Quy tắc nghiệp vụ

- **Phân quyền (RBAC):** Chỉ tài khoản có vai trò `CUSTOMER` mới có quyền truy cập và thao tác trên Wishlist. Khách vãng lai (Guest) hay tài khoản vận hành (`STAFF`, `ADMIN`) khi gọi API sẽ bị từ chối với mã lỗi `401 Unauthorized` hoặc `403 Forbidden`.
- **Bảo vệ CSRF:** Mọi thao tác làm thay đổi dữ liệu (`PUT`, `DELETE`) bắt buộc phải gửi header `X-CSRF-TOKEN` hợp lệ.
- **Giới hạn số lượng:** Mỗi khách hàng được lưu tối đa **500 sản phẩm**. Khi đạt giới hạn 500, cố gắng thêm sản phẩm mới (chưa có trong danh sách) sẽ trả về lỗi `409 Conflict` với mã lỗi `WISHLIST_LIMIT_REACHED`.
- **Tính lũy đẳng (Idempotency):**
  - `PUT /api/v1/wishlist/items/{productId}`: Nếu sản phẩm đã được lưu trước đó, request trả về ngay `204 No Content` (no-op, không tăng số lượng, không bị chặn bởi giới hạn 500).
  - `DELETE /api/v1/wishlist/items/{productId}`: Nếu sản phẩm không có trong danh sách, request trả về `204 No Content` bình thường.
- **Tính hợp lệ khi thêm:** Để được thêm vào wishlist lần đầu, sản phẩm phải đang ở trạng thái `PUBLISHED` và có ít nhất 1 SKU `ACTIVE`. Sản phẩm không tồn tại hoặc không còn công khai đều trả về `404 Not Found` với mã `PRODUCT_NOT_FOUND`, tránh làm lộ metadata nội bộ.
- **Trạng thái sẵn sàng (Availability):**
  - `AVAILABLE`: Sản phẩm đang mở bán (`PUBLISHED`) và có ít nhất 1 SKU còn hàng tồn khả dụng (`available_qty > 0`).
  - `OUT_OF_STOCK`: Sản phẩm đang mở bán nhưng tất cả SKU đều hết hàng.
  - `PRODUCT_UNAVAILABLE`: Sản phẩm đã bị gỡ bán, chuyển về bản nháp hoặc xóa. Sản phẩm hiển thị dạng placeholder, vẫn chiếm 1 vị trí trong danh sách yêu thích và cho phép khách hàng nhấn "Bỏ lưu" để giải phóng vị trí.
- **Thêm vào giỏ hàng:** Khách hàng có thể chọn SKU và thêm vào giỏ hàng từ trang danh sách yêu thích; thao tác này **không** tự động xóa sản phẩm khỏi wishlist.

---

## 2. Danh sách Endpoint

### 2.1. Lấy trang danh sách yêu thích (Paginated Wishlist)

```http
GET /api/v1/wishlist?page={page}&pageSize={pageSize}
```

- **Query Parameters:**
  - `page` (tùy chọn, mặc định: 1): Số trang (tối thiểu 1).
  - `pageSize` (tùy chọn, mặc định: 12): Kích thước trang (1 - 50).
- **Authentication:** Yêu cầu Customer session cookie.
- **Mã phản hồi thành công:** `200 OK`
- **Response Body:**

```json
{
  "items": [
    {
      "productId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "addedAt": "2026-11-04T12:00:00Z",
      "availability": "AVAILABLE",
      "product": {
        "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
        "title": "Vợt Cầu Lông Yonex Astrox 88D Pro",
        "category": {
          "id": "b36e4c74-b4ed-59d1-bbb5-4b63f2fdce07",
          "name": "Vợt cầu lông",
          "slug": "vot-cau-long"
        },
        "minPrice": 3800000.00,
        "inStock": true,
        "imageUrl": "images/ax88d.jpg",
        "averageRating": 4.90,
        "reviewCount": 12
      }
    }
  ],
  "page": 1,
  "pageSize": 12,
  "totalCount": 1
}
```

---

### 2.2. Lấy tóm tắt danh sách yêu thích (Wishlist Summary)

Dùng cho Header badge và cache ID phía client để tối ưu hiển thị trạng thái nút trái tim trên catalog.

```http
GET /api/v1/wishlist/summary
```

- **Authentication:** Yêu cầu Customer session cookie.
- **Mã phản hồi thành công:** `200 OK`
- **Response Body:**

```json
{
  "productIds": [
    "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "9c82b991-3829-4d22-b5e1-872f26038102"
  ],
  "totalCount": 2
}
```

---

### 2.3. Thêm sản phẩm vào danh sách yêu thích

```http
PUT /api/v1/wishlist/items/{productId}
```

- **Headers:**
  - `X-CSRF-TOKEN`: Token bảo vệ CSRF.
- **Authentication:** Yêu cầu Customer session cookie.
- **Mã phản hồi:**
  - `204 No Content`: Thêm thành công hoặc sản phẩm đã có sẵn trong danh sách.
  - `404 Not Found`: Mã `PRODUCT_NOT_FOUND` nếu sản phẩm không tồn tại hoặc không ở trạng thái mở bán.
  - `409 Conflict`: Mã `WISHLIST_LIMIT_REACHED` nếu danh sách đã đạt tối đa 500 sản phẩm.
  - `401 Unauthorized` / `403 Forbidden`: Chưa đăng nhập hoặc không phải Customer.

---

### 2.4. Bỏ sản phẩm khỏi danh sách yêu thích

```http
DELETE /api/v1/wishlist/items/{productId}
```

- **Headers:**
  - `X-CSRF-TOKEN`: Token bảo vệ CSRF.
- **Authentication:** Yêu cầu Customer session cookie.
- **Mã phản hồi:**
  - `204 No Content`: Bỏ lưu thành công hoặc sản phẩm vốn không có trong danh sách.
  - `401 Unauthorized` / `403 Forbidden`: Chưa đăng nhập hoặc không phải Customer.

