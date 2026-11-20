# Catalog Search API — T50

Base path: `/api/v1/catalog`. Các endpoint catalog là public, không nhận hoặc trả dữ liệu tài khoản.

## Product search

```http
GET /api/v1/catalog/products
```

### Query

| Field | Type | Contract |
|---|---|---|
| `q` | string? | Trim, chuẩn hóa chữ thường/không dấu/khoảng trắng, tối đa 100 ký tự sau trim; rỗng thành `null` |
| `categoryId` | UUID? | Category public |
| `brandId` | UUID? | Brand active |
| `minPrice` / `maxPrice` | decimal? | Không âm và `minPrice <= maxPrice`; cùng một SKU active phải thỏa cả hai biên |
| `inStock` | bool? | `true` yêu cầu ít nhất một SKU active có `available_qty > 0`; `false`/không truyền không lọc |
| `minRating` | int? | 1–5; chỉ review `PUBLISHED` chưa soft-delete |
| `sort` | string? | `relevance`, `newest`, `price-asc`, `price-desc`, `rating-desc`, `best-selling` |
| `page` | int | 1–100.000, mặc định 1 |
| `pageSize` | int | 1–48, mặc định 12 |

Nếu không truyền `sort`, hệ thống chọn `relevance` khi có `q`, ngược lại chọn `newest`. `sort=relevance` không có từ khóa trả `400 VALIDATION_ERROR`. Sort luôn qua allow-list phía server, không ánh xạ raw column từ client.

`best-selling` đọc source-of-truth: tổng quantity của order `COMPLETED` có shipment `DELIVERED` trong 90 ngày gần nhất. Không có synchronous popularity counter trên checkout/fulfillment.

### Search relevance

Khi có `q`, backend tìm trên generated normalized columns và dùng `pg_trgm`. Thứ tự ưu tiên:

1. SKU `ACTIVE` exact;
2. SKU `ACTIVE` prefix;
3. title exact;
4. title prefix;
5. title trigram similarity;
6. SKU và option similarity;
7. brand `ACTIVE` similarity;
8. category visible similarity;
9. `createdAt DESC`, rồi `productId ASC` làm tie-break xác định.

Fuzzy threshold tăng cho query ngắn để tránh kết quả nhiễu. Match SKU dùng `EXISTS`, vì vậy một Product có nhiều SKU khớp vẫn chỉ xuất hiện một lần. Product phải `PUBLISHED` và có ít nhất một SKU `ACTIVE`; SKU inactive, brand inactive và category hidden không được đóng góp match/rank. Các dimension không public được trả `null` trong Product summary hiện hữu và không bị lộ qua search.

### Response

```json
{
  "items": [],
  "totalCount": 0,
  "page": 1,
  "pageSize": 12,
  "query": {
    "normalizedText": "vot yonex",
    "effectiveSort": "relevance"
  },
  "facets": {
    "categories": [{ "id": "...", "name": "Vợt cầu lông", "count": 12 }],
    "brands": [{ "id": "...", "name": "Yonex", "count": 8 }],
    "inStockCount": 15,
    "priceMin": 250000,
    "priceMax": 4290000
  }
}
```

`query` được triển khai ở T50.2, relevance đa trường ở T50.3 và `facets` chạy thật từ T50.4.

Facet self-exclusion:

- `categories` bỏ `categoryId`, giữ q/brand/price/stock/rating;
- `brands` bỏ `brandId`, giữ q/category/price/stock/rating;
- `priceMin`, `priceMax`, `inStockCount` bỏ price/stock, giữ q/category/brand/rating;
- chỉ category visible và brand active được group;
- category/brand facet tối đa 50, sort `count DESC`, rồi `name`, `id` xác định;
- price range chỉ đọc SKU `ACTIVE`; stock chỉ dựa trên `available_qty > 0`.

Items, `totalCount` và facets đều dùng chung predicate builder. Rating chỉ tính review `PUBLISHED` có `deleted_at IS NULL`; bestseller chỉ tính order `COMPLETED` có shipment `DELIVERED` trong cửa sổ 90 ngày.

### Errors

- `400 VALIDATION_ERROR`: sai kiểu, range, sort, chiều dài hoặc tổ hợp query.
- Lỗi validation dùng RFC 7807 và có `correlationId`.
- Không phản hồi SQL, query plan hoặc chi tiết extension PostgreSQL.
- Product search đặt PostgreSQL command timeout tối đa 2 giây; timeout/cancellation đi qua error handling chung và DbContext được khôi phục cấu hình sau request.

## Search suggestions

```http
GET /api/v1/catalog/search/suggestions?q={text}&limit={1..10}
```

- Public, không yêu cầu session.
- `q` được trim/normalize không dấu, tối đa 80 ký tự. Dưới 2 ký tự trả `[]` và không chạy fuzzy query.
- `limit` mặc định 8, khoảng 1–10.
- Response có `Cache-Control: public, max-age=30`.
- Fixed-window rate limit riêng theo IP: mặc định 60 request/phút, cấu hình bằng `CatalogSearch:SuggestionPermitLimit`; vượt giới hạn trả `429 RATE_LIMITED` và `Retry-After`.
- Cancellation token từ HTTP được truyền qua cả bốn query PostgreSQL.
- Toàn bộ query suggestion đặt PostgreSQL command timeout tối đa 2 giây.

## Performance gate

T50.8 dùng dataset riêng, deterministic và chỉ chạy trong database loopback disposable tên `hcc_perf_<guid>`; dataset này không thuộc migration hay demo seed. Lệnh chuẩn:

```powershell
dotnet run --project tools/HighConcurrencyCommerce.CatalogBenchmark/HighConcurrencyCommerce.CatalogBenchmark.csproj -c Release
```

Tool tạo 10.000 Product, 30.000 SKU, 20 brand/category cùng stock, rating và lịch sử completed+delivered 90 ngày; sau đó chạy `VACUUM ANALYZE`, `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` và 25 mẫu timing cho exact SKU, title prefix không dấu, fuzzy title, combined facets, suggestion, best-selling và deep page. Năm case search/suggestion fail ngay nếu thiếu GIN index mong đợi hoặc có sequential scan trên `products`/`product_skus`. Timing là baseline tham chiếu, không phải assertion cứng trong integration suite. Kết quả gần nhất: [catalog-search-benchmark-latest.md](../performance/catalog-search-benchmark-latest.md).

Nguồn suggestion:

- `PRODUCT`: Product `PUBLISHED` có SKU active; label là title, secondary là brand/category public.
- `SKU`: chỉ SKU `ACTIVE` thuộc Product public; label là SKU code, secondary là option.
- `BRAND`: brand active có ít nhất một Product public với SKU active.
- `CATEGORY`: category visible có ít nhất một Product public với SKU active.

Thứ tự toàn cục: exact → prefix → contains → fuzzy; trong cùng tier ưu tiên SKU, Product, Brand, Category, sau đó similarity/label/id xác định. Kết quả dedupe theo `(kind, label, productId)` và không vượt `limit`.

Ví dụ:

```json
[
  {
    "kind": "SKU",
    "label": "AX88D-PRO-4U",
    "secondaryText": "4U / G5 Đỏ",
    "productId": "...",
    "queryValue": "AX88D-PRO-4U"
  },
  {
    "kind": "PRODUCT",
    "label": "Vợt Cầu Lông Yonex Astrox 88D Pro",
    "secondaryText": "YONEX Nhật Bản · Vợt Cầu Lông",
    "productId": "...",
    "queryValue": "Vợt Cầu Lông Yonex Astrox 88D Pro"
  }
]
```
