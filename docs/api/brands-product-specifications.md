# Brand & Product Specifications API (T44)

## Quy tắc nghiệp vụ

- Chỉ `ADMIN` quản trị master data thương hiệu; `STAFF` chỉ đọc các thương hiệu đang hoạt động.
- Slug thương hiệu là duy nhất toàn hệ thống. Hai lệnh tạo đồng thời cùng slug cho kết quả đúng một `201`, lệnh còn lại `409 BRAND_SLUG_CONFLICT`.
- Không xóa thương hiệu đang được bất kỳ sản phẩm nào tham chiếu (`409 BRAND_IN_USE`). Có thể chuyển `isActive=false`; sản phẩm đã gắn vẫn giữ snapshot quan hệ.
- Staff chỉ được gắn thương hiệu và thêm/sửa/xóa thông số khi sản phẩm còn `DRAFT`.
- Mutation thông số khóa hàng `products FOR UPDATE`, cùng lock boundary với Publish. Vì vậy Staff mutation không thể chạy xuyên qua Admin Publish.
- `key` thông số dùng định dạng `snake_case`, duy nhất trong một sản phẩm. Catalog sắp theo `displayOrder`, sau đó `key`.

## Admin Brand API

| Method | Route | Kết quả |
|---|---|---|
| `GET` | `/api/v1/admin/brands` | Toàn bộ thương hiệu và `productCount` |
| `POST` | `/api/v1/admin/brands` | Tạo thương hiệu (`201`) |
| `PATCH` | `/api/v1/admin/brands/{id}` | Sửa metadata/trạng thái |
| `DELETE` | `/api/v1/admin/brands/{id}` | Xóa thương hiệu trống (`204`) |

Mutation cần cookie session Admin và header `X-CSRF-TOKEN`.

Payload tạo/sửa:

```json
{
  "name": "Yonex",
  "slug": "yonex",
  "country": "Japan",
  "websiteUrl": "https://www.yonex.com",
  "isActive": true
}
```

## Staff Product API

| Method | Route | Mô tả |
|---|---|---|
| `GET` | `/api/v1/staff/brands` | Danh sách thương hiệu active để gắn Draft |
| `PUT` | `/api/v1/staff/products/{productId}/specifications/{key}` | Upsert thông số Draft |
| `DELETE` | `/api/v1/staff/products/{productId}/specifications/{key}` | Xóa thông số Draft |

`POST /staff/products` và `PATCH /staff/products/{id}/draft` nhận thêm `brandId` tùy chọn. Payload upsert thông số:

```json
{ "displayName": "Trọng lượng", "value": "4U (80–84g)", "displayOrder": 10 }
```

## Public Catalog API

- `GET /api/v1/catalog/brands`: trả các thương hiệu đang active để xây bộ lọc công khai.
- `GET /api/v1/catalog/products?brandId={id}`: lọc sản phẩm Published theo hãng.
- Product summary có `brand`; product detail có `brand` và `specifications`.

## Mã lỗi chính

- `400 INVALID_STATE`: sản phẩm đã Publish/Archived hoặc thương hiệu gắn mới không active.
- `404 BRAND_NOT_FOUND`, `404 NOT_FOUND`.
- `409 BRAND_SLUG_CONFLICT`, `409 BRAND_IN_USE`.
- `400 VALIDATION_ERROR`: slug, URL, key hoặc độ dài không hợp lệ.
