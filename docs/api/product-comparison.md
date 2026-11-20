# Product Comparison API — T52

Trạng thái: **Đã triển khai và harden qua T52.6**. Endpoint có public-visibility, request-order, category, hidden-taxonomy, bounded-query và command-timeout integration tests; UI `/compare` có strict response parser, URL source of truth và localStorage sanitization.

Base URL: `/api/v1/catalog`. API anonymous, read-only, JSON camelCase và không yêu cầu CSRF.

## GET `/products/compare`

Query dùng repeated parameter:

```text
GET /api/v1/catalog/products/compare?id=<product-1>&id=<product-2>
```

### Validation

- Bắt buộc từ 1 đến 4 `id`.
- Mỗi `id` là UUID khác `Guid.Empty`.
- Không chấp nhận ID trùng lặp.
- Các Product public còn lại phải cùng `category_id`.
- Frontend share URL dùng `/compare?ids=id1,id2`; loader chuyển thành repeated `id` khi gọi API. Validator chấp nhận định dạng GUID của .NET/PostgreSQL (không ép RFC version nibble) nhưng từ chối `Guid.Empty`.

### Response

```json
{
  "items": [
    {
      "id": "b0000000-0000-0000-0000-000000000001",
      "title": "Vợt cầu lông Yonex Astrox 88D Pro",
      "description": "...",
      "category": { "id": "...", "name": "Vợt cầu lông", "slug": "vot-cau-long" },
      "brand": { "id": "...", "name": "Yonex", "slug": "yonex", "country": "Japan", "websiteUrl": null },
      "skus": [],
      "images": [],
      "specifications": [],
      "averageRating": 5.0,
      "reviewCount": 1
    }
  ],
  "requestedCount": 2,
  "omittedCount": 1
}
```

`items` tái sử dụng `ProductDetailDto`, giữ đúng thứ tự ID trong request. Product không tồn tại hoặc không còn public bị bỏ khỏi `items` và tăng `omittedCount`; response không tiết lộ Draft/Suspended/Archived hay nguyên nhân bị loại.

Header: `Cache-Control: no-store` vì regular price và availability có thể thay đổi.

### Error contract

- `400 VALIDATION_ERROR`: thiếu ID, quá 4 ID, UUID sai/empty hoặc ID trùng.
- `400 COMPARISON_CATEGORY_MISMATCH`: các Product public còn lại khác danh mục.
- `429`: vượt public Catalog rate limit nếu policy được áp dụng.
- `500 SERVER_ERROR`: lỗi server chung; không trả SQL, stack trace hoặc dữ liệu nội bộ.

ProblemDetails luôn có `status`, `title`, `code`, `correlationId`; validation có thêm `errors`.

## Projection và giới hạn dữ liệu

- Chỉ Product đi qua public predicate của Catalog và có ít nhất một SKU `ACTIVE`.
- Regular price, boolean availability, public brand/category, active SKU, active media, specifications và published rating.
- Không trả creator, lifecycle nội bộ, inventory physical/reserved/allocated, cost hoặc SKU inactive.
- Backend giới hạn bốn ID trước khi query và dùng batch projection; không gọi detail API tuần tự.
- Database command của batch comparison có timeout 2 giây và luôn hoàn nguyên timeout trước đó trên `DbContext`, kể cả khi query bị hủy/lỗi.
- Frontend từ chối response sai count/order, ID lặp/ngoài request, rating ngoài 0–5 hoặc category public bị trộn.
- Metadata ảnh trong localStorage chỉ nhận đường dẫn tương đối/an toàn hoặc URL HTTP(S); protocol-relative URL, backslash và scheme như `javascript:` bị loại trước khi render.

Thiết kế đầy đủ: [kế hoạch T52](../superpowers/plans/2026-11-08-product-comparison.md).

## Demo/E2E fixture

`007_demo_seed.sql` cung cấp hai vợt cùng danh mục (`b000...001`, `b000...002`) với sáu key chuẩn hóa: `weight_class`, `balance`, `shaft_stiffness`, `max_tension`, `material`, `play_style`. Một số giá trị giống nhau và một số khác nhau để kiểm chứng matrix/different-only. Upsert được giới hạn đúng demo IDs và không tạo duplicate khi reseed.

## Frontend route

`/compare?ids=<id1>,<id2>&different=true` dùng URL làm source of truth. Loader canonicalize UUID lower-case, loại duplicate giữ thứ tự đầu tiên, giới hạn bốn và chuyển sang repeated `id` khi gọi API. `different=true` chỉ lọc các dòng matrix có giá trị khác nhau; không thay đổi danh sách Product. Sau response hợp lệ, local comparison state được reconcile theo `items` public thực tế và các ID bị omit được thông báo generic.

Playwright Flow 18 kiểm chứng route này trên Docker thật với localStorage rỗng/context mới, remove-add, category mismatch và Product bị suspend. Product lifecycle nội bộ không xuất hiện trong UI hoặc response public.
