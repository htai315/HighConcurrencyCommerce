# Product Media API

T45 cung cấp upload ảnh sản phẩm do hệ thống quản lý. Bản demo lưu binary trên Docker named volume; application chỉ phụ thuộc `IProductMediaStorage`, vì vậy có thể thay bằng S3/R2 mà không đổi contract nghiệp vụ.

## Quy tắc chung

- Staff-only cho mọi mutation; session cookie và CSRF token vẫn bắt buộc.
- Chỉ sản phẩm `DRAFT` được upload, sửa metadata, đổi thứ tự hoặc xóa ảnh.
- Chấp nhận JPEG, PNG và WebP, tối đa 8 MiB mỗi ảnh.
- Server kiểm tra magic bytes; không tin phần mở rộng hoặc `Content-Type` do client khai báo.
- Tên object là UUID bất biến dưới `products/{productId}/{uuid}.{ext}`; không dùng tên file người dùng.
- `fileKey` trong `product_images` là public path `/api/v1/media/{storageKey}`.
- Upload binary diễn ra ngoài transaction database. Nếu ghi metadata thất bại, object vừa tạo được xóa bù.
- Xóa metadata commit trước, sau đó xóa object theo cơ chế best-effort. File cũ không thể trỏ sang file upload mới vì key là bất biến.
- Publish và mọi Staff mutation cùng khóa hàng cha `store.products FOR UPDATE`, bảo vệ invariant Draft-only.

## Endpoints

### Upload ảnh

`POST /api/v1/staff/products/{productId}/images/upload`

Content type: `multipart/form-data`.

| Field | Kiểu | Bắt buộc | Ghi chú |
|---|---|---|---|
| `file` | binary | Có | JPEG/PNG/WebP, tối đa 8 MiB |
| `altText` | string | Không | Tối đa 240 ký tự |
| `displayOrder` | integer | Có | 0–10.000 |

Thành công: `201 Created`, trả về `ProductImageDto`.

### Sửa metadata

`PATCH /api/v1/staff/products/{productId}/images/{imageId}`

```json
{
  "altText": "Mặt trước vợt cầu lông",
  "displayOrder": 10
}
```

Thành công: `200 OK`.

### Sắp xếp toàn bộ gallery

`PUT /api/v1/staff/products/{productId}/images/order`

```json
{
  "imageIds": ["uuid-anh-chinh", "uuid-anh-phu"]
}
```

Danh sách phải chứa đúng và đủ toàn bộ ảnh hiện tại, không được lặp ID. Server gán `displayOrder` theo vị trí. Thành công: `200 OK`, trả gallery đã sắp xếp.

### Xóa ảnh

`DELETE /api/v1/staff/products/{productId}/images/{imageId}`

Thành công: `204 No Content`. Endpoint JSON cũ `POST .../images` vẫn được giữ để tương thích dữ liệu URL ngoài đã tồn tại.

### Đọc binary công khai

`GET /api/v1/media/{storageKey}`

Không cần đăng nhập. Response hỗ trợ range request và có `Cache-Control: public, max-age=31536000, immutable`, `ETag`, `X-Content-Type-Options: nosniff`. Key không hợp lệ, path traversal hoặc file không tồn tại đều trả `404`.

## Error codes

| HTTP | Code | Trường hợp |
|---|---|---|
| 400 | `MEDIA_EMPTY` | File rỗng |
| 400 | `MEDIA_TOO_LARGE` | Vượt giới hạn cấu hình |
| 400 | `MEDIA_UNSUPPORTED` | MIME/magic bytes không hợp lệ hoặc không khớp |
| 400 | `MEDIA_TYPE_MISMATCH` | MIME khai báo không khớp chữ ký file |
| 400 | `INVALID_STATE` | Sản phẩm không còn là Draft |
| 400 | `VALIDATION_ERROR` | Metadata hoặc danh sách reorder không hợp lệ |
| 401/403 | chuẩn auth | Chưa đăng nhập hoặc không phải Staff |
| 404 | `NOT_FOUND` | Không thấy sản phẩm/ảnh |

## Cấu hình vận hành

```json
{
  "ProductMedia": {
    "RootPath": "product-media",
    "MaxFileBytes": 8388608
  }
}
```

Docker đặt `RootPath=/var/lib/hcc/media` và mount named volume `product-media`. Production có thể đăng ký implementation `IProductMediaStorage` dùng object storage/CDN; không nên dùng filesystem cục bộ nếu API chạy nhiều replica mà không có shared volume.
