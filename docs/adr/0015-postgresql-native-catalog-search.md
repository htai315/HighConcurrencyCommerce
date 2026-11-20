# ADR 0015: PostgreSQL-native catalog search and discovery

- Status: Accepted
- Date: 2026-11-07
- Owners: Commerce / Storefront
- Related task: T50 — Tìm kiếm & khám phá sản phẩm nâng cao

## Context

Catalog công khai cần tìm được tên sản phẩm, SKU, phân loại, thương hiệu và danh mục khi người dùng gõ không dấu, sai một phần hoặc khác hoa/thường. Kết quả còn phải lọc và sắp xếp ổn định theo giá, độ liên quan, mới nhất và mức phổ biến. Hệ thống hiện vận hành trên PostgreSQL và chưa có nhu cầu vận hành một search cluster riêng.

Các yêu cầu bất biến:

- Chỉ `PUBLISHED` product, `ACTIVE` SKU/brand và category hiển thị mới được xuất hiện công khai.
- Chuẩn hóa tìm kiếm phải nhất quán giữa ghi dữ liệu, truy vấn catalog và autocomplete.
- Phân trang phải có tie-breaker ổn định; không để sản phẩm trùng hoặc mất giữa các trang trong cùng snapshot truy vấn.
- Tín hiệu phổ biến chỉ được suy ra từ dữ liệu đơn hàng/giao hàng đã có, không tạo bộ đếm đồng bộ trên checkout.
- Migration phải chạy được trên database mới, database legacy và chạy lặp an toàn.

## Decision

### 1. Dùng PostgreSQL làm search engine của T50

T50 dùng `unaccent` và `pg_trgm`, không bổ sung Elasticsearch/OpenSearch. Đây là ranh giới phù hợp với quy mô hiện tại, giữ transaction và quyền hiển thị trong một nguồn dữ liệu, đồng thời tránh thêm hạ tầng vận hành.

Nếu đo đạc production cho thấy PostgreSQL không đạt SLO sau khi đã tối ưu query/index, một ADR mới phải đánh giá search engine ngoài. T50 không thiết kế trước cơ chế dual-write.

### 2. Chuẩn hóa bằng generated stored columns

Hàm `store.catalog_search_normalize(text)` cố định từ điển `public.unaccent`, chuyển chữ thường, gom khoảng trắng và được khai báo `IMMUTABLE`. Các cột generated stored là:

- `products.search_title_norm`
- `product_skus.search_sku_norm`
- `product_skus.search_option_norm`
- `brands.search_name_norm`
- `categories.search_name_norm`

Generated columns loại bỏ drift giữa application và database, tự cập nhật khi nguồn thay đổi và có thể đánh GIN trigram index. EF Core ánh xạ chúng dưới dạng computed stored; application không được ghi trực tiếp.

### 3. Index theo phạm vi public

Các trường chuẩn hóa dùng partial GIN trigram index theo lifecycle public. B-tree index bổ trợ lọc/sắp xếp SKU theo giá và tổng hợp mức phổ biến từ đơn đã hoàn thành/giao thành công. Migration cài extension rõ ràng và fail-fast nếu môi trường PostgreSQL không hỗ trợ; application không được âm thầm hạ cấp sang tìm kiếm khác nghĩa.

### 4. Ranking và phân trang

Thứ tự relevance dự kiến ưu tiên exact normalized match, prefix match, rồi trigram similarity trên title/SKU/option/brand/category. Mọi sort thêm tie-breaker `product.id` để ổn định. Giá đại diện của sản phẩm là giá thấp nhất trong các SKU `ACTIVE`.

Popularity là phép đọc/tổng hợp từ fulfillment đã hoàn tất trong cửa sổ thời gian được tài liệu API quy định. Không tăng counter trong checkout, tránh lock contention và sai số khi hủy/đổi trả.

### 5. Public autocomplete

Autocomplete chỉ trả dữ liệu catalog công khai, giới hạn query/page size, áp dụng rate limit và không lộ SKU/product chưa publish. Endpoint dùng cùng normalization và visibility predicate với search chính.

## Consequences

### Positive

- Không thêm service, queue hay cơ chế đồng bộ mới.
- Dữ liệu chuẩn hóa luôn đi cùng dữ liệu nguồn và rollback trong cùng transaction.
- Truy vấn có thể tận dụng trigram index cho tiếng Việt không dấu và partial match.
- Quy tắc lifecycle tập trung trong query và partial index public.

### Trade-offs

- `unaccent` không xử lý mọi lỗi chính tả/ngữ nghĩa như search engine chuyên dụng.
- Generated columns và GIN index tăng chi phí ghi catalog; đây là đường ghi thấp hơn nhiều so với đường đọc.
- Popularity tổng hợp trực tiếp cần đo `EXPLAIN (ANALYZE, BUFFERS)` và có thể cần read model bất đồng bộ trong ADR tương lai nếu dữ liệu tăng mạnh.

## Rejected alternatives

- `ILIKE '%q%'` trên cột gốc: không xử lý dấu nhất quán và không đáp ứng index/ranking.
- Chuẩn hóa chỉ ở React hoặc C#: dễ drift, không bảo vệ các caller khác và không dùng được generated-index contract.
- Elasticsearch/OpenSearch ngay từ đầu: tăng đáng kể chi phí vận hành, đồng bộ và xử lý stale visibility khi chưa có bằng chứng tải yêu cầu.
- Counter bestseller cập nhật đồng bộ trong checkout: tạo hot row, tăng lock contention và khó hoàn tác chính xác qua cancel/return/refund.
