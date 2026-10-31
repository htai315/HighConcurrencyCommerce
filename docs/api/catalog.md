# Public Catalog API — đã triển khai ngày 30/10/2026

Base `/api/v1/catalog`, anonymous GET, JSON camelCase. PostgreSQL là nguồn dữ liệu; không cache, không giữ tồn và không áp giá Flash Sale ở đợt này.

## GET /categories

Trả `200` array, chỉ `is_visible=true`, sort `name ASC, id ASC`:

```json
[{"id":"10000000-0000-0000-0000-000000000001","name":"Display","slug":"display"}]
```

## GET /products

| Query | Mặc định / giới hạn |
|---|---|
| `q` | Optional; tối đa 100 ký tự, trim, tìm chuỗi con trong title bằng PostgreSQL ILIKE. `%`, `_`, `\` là ký tự literal; không tìm không dấu. |
| `categoryId` | Optional UUID, chỉ khớp danh mục visible. UUID hợp lệ nhưng không có/ẩn → danh sách rỗng. |
| `minPrice`, `maxPrice` | Optional decimal ≥ 0, tối đa `9999999999999999.99`; min ≤ max. Ít nhất một ACTIVE SKU phải nằm trong toàn khoảng. |
| `sort` | `newest` (created_at DESC), `price-asc`, `price-desc`. Giá sort là giá thấp nhất trong ACTIVE SKU; luôn tie-break id ASC. |
| `page` | 1; từ 1 đến 100000. Trang ngoài kết quả → items rỗng. |
| `pageSize` | 12; từ 1 đến 100. |

```json
{
  "items": [{
    "id": "20000000-0000-0000-0000-000000000001",
    "title": "Alpha Monitor",
    "category": {"id":"10000000-0000-0000-0000-000000000001","name":"Display","slug":"display"},
    "minPrice": 2500000,
    "inStock": true
  }],
  "totalCount": 1,
  "page": 1,
  "pageSize": 12
}
```

Chỉ PUBLISHED product có ít nhất một ACTIVE SKU được trả. Hết hàng vẫn xuất hiện; `inStock=true` nếu có ACTIVE SKU với `available_qty > 0`. Không có InventoryBalance được xem là hết hàng. `minPrice` luôn là giá regular thấp nhất của ACTIVE SKU, kể cả khi SKU khớp filter giá có giá cao hơn.

Category ẩn không tự unpublish product (chưa có rule này trong spec); product đó trả `category: null`, không lộ ID/tên category ẩn. Không serialize entity/FK người tạo, số tồn physical/reserved/allocated hay SKU inactive.

## GET /products/{id}

UUID không tồn tại hoặc product không public → `404`. UUID sai định dạng không khớp route và trả `404`. Thành công:

```json
{
  "id": "20000000-0000-0000-0000-000000000001",
  "title": "Alpha Monitor",
  "description": "Mô tả plain text hoặc null",
  "category": null,
  "skus": [{
    "id": "30000000-0000-0000-0000-000000000001",
    "skuCode": "ALPHA-A",
    "optionLabel": "Tiêu chuẩn",
    "price": 2500000,
    "inStock": false
  }]
}
```

SKU chỉ ACTIVE, sort `sku_code ASC, id ASC`. Description được React render plain text, không chèn HTML. Giá DB/C# là decimal; FE chỉ format để hiển thị, không tính tiền checkout bằng Number.

## Error contract

ASP.NET ProblemDetails, `status`, `title`, `code`, `correlationId`; validation có thêm `errors`. Các field chuẩn `type`, `traceId` có thể có thêm. `correlationId` từ HttpContext.TraceIdentifier để tra request, không phải idempotency key.

- `400 VALIDATION_ERROR`: query sai kiểu, giới hạn hoặc min > max.
- `404 NOT_FOUND`: detail không public/không tồn tại hoặc route không khớp.
- `500 SERVER_ERROR`: lỗi server, title chung, không trả connection string/stack trace. Log phía server dùng cho chẩn đoán.

```json
{"status":400,"title":"Bộ lọc không hợp lệ.","code":"VALIDATION_ERROR","correlationId":"request-id","errors":{"Page":["..."]}}
```

FE kiểm tra response từ `unknown`; sai JSON/schema thành lỗi `INVALID_RESPONSE` nội bộ (status 502), có retry. FE không hiển thị nguyên văn lỗi server. Route loader truyền AbortSignal để hủy request khi chuyển route/bộ lọc.

## Giới hạn và bước tiếp theo

- Chưa có public image URL/storage contract; API chưa trả ảnh, FE hiện placeholder. Hoàn thiện ở task T12 cùng nội dung/media sản phẩm.
- Giá và tồn là tham khảo tại thời điểm đọc; checkout sau này phải kiểm tra lại trong transaction. Hai query count/items không tạo snapshot nhất quán dưới thay đổi đồng thời; sort ổn định trong dữ liệu không đổi, chưa dùng cursor pagination.
- Auth đã có theo [contract riêng](auth.md); catalog vẫn public. Chưa có cart, checkout, payments, Flash Sale pricing, response caching hay benchmark. Không suy ra khả năng chống oversell từ các test catalog.
- Bằng chứng đợt catalog: 25 tests BE với PostgreSQL thật và 12 tests FE với MSW/DOM. Suite hiện có thêm tests auth; số mới nhất ở TASKS. Chưa chạy browser E2E/visual review tự động.
