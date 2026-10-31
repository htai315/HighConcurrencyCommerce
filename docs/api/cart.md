# Cart API Contract — Task T15

Base path: `/api/v1/cart`. Toàn bộ API server cart yêu cầu session có role `CUSTOMER`, CSRF cho mutation và `Cache-Control: no-store`.

## Quy tắc nghiệp vụ

- Guest cart chỉ tồn tại trong `localStorage`; server không tạo định danh hoặc cookie giỏ cho Guest.
- Mỗi Customer có tối đa một `shopping_carts`; mỗi SKU có tối đa một `cart_items` trong giỏ.
- Giỏ **không giữ tồn kho** và không thay đổi `inventory_balances`, `inventory_movements` hoặc reservation.
- Server không tin tên, giá hay trạng thái do client gửi. Set/merge có thể nhận `flashSaleOfferId`, nhưng server luôn đọc lại offer, campaign, SKU và giá thật.
- Chỉ SKU `ACTIVE` thuộc Product `PUBLISHED` được thêm/gộp. SKU đã trở nên không hợp lệ vẫn được hiển thị ở giỏ cũ với `SKU_NOT_AVAILABLE` để Customer chủ động xóa.
- Quantity mỗi SKU từ 1–99. Merge cộng quantity với dòng server hiện có và giới hạn tổng ở 99.
- Mọi mutation khóa hàng `user_accounts` của Customer bằng `FOR UPDATE` trước khi tạo/cập nhật cart, tuần tự hóa set/remove/merge và chống lost update khi merge đồng thời.
- `validate` đọc lại trạng thái SKU, giá hiện tại và `available_qty`; kết quả chỉ là snapshot kiểm tra, không phải cam kết giữ hàng.

## Endpoints

### `GET /api/v1/cart`

Trả giỏ hiện tại. Customer chưa có giỏ nhận object rỗng (`id: null`, `items: []`) và GET không tạo dữ liệu.

### `PUT /api/v1/cart/items/{skuId}`

Đặt quantity tuyệt đối cho SKU.

```json
{ "quantity": 2, "flashSaleOfferId": "uuid-or-null" }
```

Trả `CartDto` mới nhất. SKU Draft/Archived/Inactive/không tồn tại trả `404 SKU_NOT_AVAILABLE`.

### `DELETE /api/v1/cart/items/{skuId}`

Xóa SKU owner-only và trả `CartDto` mới nhất. Xóa dòng không tồn tại là idempotent.

### `POST /api/v1/cart/merge`

```json
{
  "items": [
    { "productSkuId": "00000000-0000-0000-0000-000000000000", "quantity": 2, "flashSaleOfferId": null }
  ]
}
```

Tối đa 50 dòng đầu vào. Dòng hợp lệ được cộng vào giỏ server; SKU stale được bỏ qua và trả trong `issues`. `QUANTITY_CAPPED` cho biết tổng đã được giới hạn ở 99.

Frontend gọi endpoint ngay sau login Customer. Chỉ xóa Guest cart local sau khi merge trả thành công; nếu request lỗi, dữ liệu local được giữ để retry.

### `POST /api/v1/cart/validate`

Trả:

```json
{
  "isValid": false,
  "cart": {},
  "issues": [
    { "productSkuId": "...", "code": "INSUFFICIENT_STOCK", "message": "..." }
  ]
}
```

Các issue: `EMPTY_CART`, `SKU_NOT_AVAILABLE`, `INSUFFICIENT_STOCK`, `FLASH_SALE_NOT_AVAILABLE`. Endpoint không ghi database và không giữ tồn.

## Cart item response

Mỗi item chứa dữ liệu hiện tại từ server: `productSkuId`, `productId`, `productTitle`, `skuCode`, `optionLabel`, `unitPrice`, `quantity`, `lineTotal`, `imageUrl`, `availabilityCode`, `flashSaleOfferId`. Khi offer còn hiệu lực, `unitPrice` là giá sale phía server; khi offer hết hiệu lực item mang `FLASH_SALE_NOT_AVAILABLE`. Không trả `physical_qty`, `reserved_qty` hay `allocated_qty`.

## Phân quyền và lỗi

- Guest: `401 Unauthorized`.
- Staff/Admin: `403 Forbidden`.
- Validation model: `400 VALIDATION_ERROR`.
- SKU không bán: `404 SKU_NOT_AVAILABLE`.
- Cart đã đóng: `409 CART_CLOSED`.
- Offer Flash Sale hết hiệu lực/không khớp SKU: `409 FLASH_SALE_NOT_AVAILABLE` khi mutation; validate trả issue cùng mã.
