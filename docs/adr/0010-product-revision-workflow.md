# ADR 0010: Product Revision Workflow

- Trạng thái: Accepted
- Ngày: 06/11/2026
- Task: T47

## Bối cảnh

Catalog hiện đọc trực tiếp từ `products`, `product_images` và `product_specifications`. Sửa các bảng live trước khi Admin duyệt sẽ làm nội dung chưa kiểm soát xuất hiện ngay trên Storefront.

## Quyết định

Staff chỉnh sửa một snapshot revision riêng cho Product đã `PUBLISHED`. Mỗi Product có tối đa một revision mở (`DRAFT` hoặc `PENDING_REVIEW`). Admin approve áp toàn bộ scalar, gallery và specifications trong một transaction rồi tăng `products.content_version`.

Revision giữ `base_content_version`; nếu Product live đã đổi, approve trả `REVISION_STALE`, không tự merge. Thứ tự khóa bắt buộc là `Product -> Revision -> revision children`.

SKU, giá và tồn kho không thuộc revision vì các dữ liệu đó gắn với order, inventory, Flash Sale và reporting. Approve không đổi `publication_status`.

Ảnh mới được lưu qua `IProductMediaStorage` nhưng chỉ tham chiếu trong revision trước approval. Reject/cancel cleanup best-effort ảnh mới; ảnh live bị loại không bị xóa binary ngay vì URL public là immutable và có thể đã được CDN/browser cache.

## Hệ quả

- Catalog không cần join bảng revision và luôn đọc phiên bản đã duyệt.
- Approval là thao tác transaction lớn hơn nhưng nguyên tử và dễ audit.
- Không có merge nhiều nhánh; xung đột version yêu cầu tạo revision mới.
- Orphan binary retention/garbage collection cần quy trình vận hành riêng về sau.
