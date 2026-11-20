# Product Discovery API Specification (T53)

Tài liệu đặc tả các giao diện API khám phá sản phẩm công khai (Anonymous): Gợi ý sản phẩm tương tự (Related Products) và Tải thông tin tóm tắt hàng loạt cho lịch sử đã xem (Recently Viewed Batch Summary).

---

## 1. Tổng quan Nghiệp vụ

- **Tính ẩn danh & Riêng tư**:
  - Toàn bộ endpoint đều cho phép truy cập ẩn danh (`Anonymous`), chỉ đọc (`Read-only`), không yêu cầu session, cookie hoặc CSRF token.
  - Không có server-side tracking, không ghi nhận browsing history trên database, không tạo profile người dùng hoặc sử dụng external recommendation engines.
  - Lịch sử đã xem (`Recently Viewed`) hoàn toàn được quản lý phía Client (localStorage), Server chỉ đóng vai trò hydrate thông tin tóm tắt mới nhất.
- **Quy tắc gợi ý Sản phẩm tương tự (`Related Products`)**:
  - Chỉ gợi ý các sản phẩm cùng danh mục gốc (`underlying category_id`), đang mở bán công khai (`PUBLISHED`), có ít nhất một SKU `ACTIVE`, không bị `SUSPENDED` hoặc `ARCHIVED`.
  - Loại trừ chính sản phẩm nguồn (`candidateId != sourceId`).
  - Thứ tự xếp hạng xác định tuyệt đối (Deterministic ranking tuple):
    1. Sản phẩm có ít nhất một SKU `ACTIVE` còn hàng khả dụng (`available_qty > 0`) đứng trước.
    2. Cùng thương hiệu (`Brand`) đang hoạt động với sản phẩm nguồn đứng trước; nếu sản phẩm nguồn không có thương hiệu hoặc thương hiệu bị ẩn thì bỏ qua tiêu chí này.
    3. Khoảng cách chênh lệch tuyệt đối về giá bán thường nhỏ nhất ($|\min(\text{regular\_price})_{\text{candidate}} - \min(\text{regular\_price})_{\text{source}}|$) tăng dần.
    4. Điểm đánh giá trung bình (`AverageRating`) đã kiểm duyệt giảm dần.
    5. Số lượng đánh giá (`ReviewCount`) đã kiểm duyệt giảm dần.
    6. `Product.Id` tăng dần làm tie-break cuối cùng.
  - Không công bố điểm số hay lý do xếp hạng nội bộ ra response API.
- **Quy tắc Tải tóm tắt hàng loạt (`Batch Product Summaries`)**:
  - Phục vụ hydrate danh sách mã sản phẩm đã xem gần đây (hoặc giỏ hàng/so sánh nhanh).
  - Giữ nguyên vẹn thứ tự các ID được gửi lên trong request.
  - Tự động bỏ qua (`omit`) các sản phẩm không còn công khai (Draft, Suspended, Archived, đã xóa hoặc không tồn tại) mà không tiết lộ lý do cụ thể.
  - Trả về `requestedCount` và `omittedCount` chuẩn xác.

---

## 2. API Endpoints

Base URL: `/api/v1/catalog`

### 2.1. Lấy danh sách sản phẩm tương tự (`GET /api/v1/catalog/products/{id}/related`)

Lấy tối đa danh sách sản phẩm tương tự dựa theo sản phẩm nguồn.

- **Quyền hạn**: Công khai (Anonymous).
- **Cache-Control**: `no-store` (đảm bảo tồn kho và giá cả luôn mới nhất).
- **Path Parameters**:
  - `id` (Guid, required): Mã định danh duy nhất của sản phẩm nguồn.
- **Query Parameters**:
  - `limit` (int, optional, default: 6, range: 1..12): Số lượng sản phẩm tương tự tối đa cần lấy.

- **Response 200 OK**:
```json
{
  "sourceProductId": "b1b01dfc-f836-419b-a78b-d7d8e62d4a11",
  "items": [
    {
      "id": "2c92e921-6b83-4a11-b0db-6e11893c0d89",
      "title": "Vợt cầu lông Yonex Astrox 88D Pro",
      "category": {
        "id": "73d6bb6c-1123-4567-89ab-cdef01234567",
        "name": "Vợt cầu lông",
        "slug": "vot-cau-long"
      },
      "brand": {
        "id": "8a3a0e37-9cb8-4f27-a06f-4318357fbb1a",
        "name": "Yonex",
        "slug": "yonex",
        "country": "Nhật Bản",
        "websiteUrl": null
      },
      "minPrice": 3850000.0,
      "inStock": true,
      "imageUrl": "/media/products/2c92e921/cover.webp",
      "averageRating": 4.8,
      "reviewCount": 24
    }
  ]
}
```

- **Mã lỗi thường gặp**:
  - `400 VALIDATION_ERROR`: Mã sản phẩm không hợp lệ (`Guid.Empty`) hoặc `limit` nằm ngoài khoảng 1..12.
  - `404 PRODUCT_NOT_FOUND`: Sản phẩm nguồn không tồn tại hoặc không ở trạng thái mở bán công khai.
  - `429 TOO_MANY_REQUESTS`: Vượt hạn mức rate-limit Catalog công khai.

---

### 2.2. Tải tóm tắt danh sách sản phẩm hàng loạt (`GET /api/v1/catalog/products/batch`)

Nhận danh sách 1..12 ID sản phẩm và trả về danh sách `ProductSummaryDto` tương ứng theo đúng thứ tự mảng ID gửi lên.

- **Quyền hạn**: Công khai (Anonymous).
- **Cache-Control**: `no-store`.
- **Query Parameters**:
  - `id` (Guid[], required): Danh sách các ID cần lấy. Hỗ trợ dạng lặp query parameter `?id=uuid1&id=uuid2` hoặc client URLSearchParams.
  - Yêu cầu: 1 đến 12 UUID khác `Guid.Empty` và không trùng lặp.

- **Response 200 OK**:
```json
{
  "items": [
    {
      "id": "2c92e921-6b83-4a11-b0db-6e11893c0d89",
      "title": "Vợt cầu lông Yonex Astrox 88D Pro",
      "category": { ... },
      "brand": { ... },
      "minPrice": 3850000.0,
      "inStock": true,
      "imageUrl": "...",
      "averageRating": 4.8,
      "reviewCount": 24
    }
  ],
  "requestedCount": 2,
  "omittedCount": 0
}
```

- **Mã lỗi thường gặp**:
  - `400 VALIDATION_ERROR`: Danh sách ID rỗng, vượt quá 12 ID, chứa ID không hợp lệ hoặc chứa ID trùng lặp.
  - `429 TOO_MANY_REQUESTS`: Vượt hạn mức rate-limit Catalog công khai.

