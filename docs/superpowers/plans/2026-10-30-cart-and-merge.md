# T15 — Guest cart, Customer cart và merge

## Mục tiêu

Hoàn thiện M04 xuyên suốt React → API → PostgreSQL mà không tạo reservation sớm: Guest mua sắm được trước đăng nhập, Customer có giỏ owner-only, merge an toàn và validate lại dữ liệu server trước checkout T17.

## Thiết kế đã thực hiện

1. Application contract `CartDto`, set item, merge result và validation result.
2. `PostgresCartService` dùng customer row làm serialization boundary cho mọi mutation; dữ liệu sản phẩm/giá luôn lấy từ database.
3. Customer controller gồm đủ 5 endpoint M04 và exception RFC 7807.
4. Guest cart versioned trong `localStorage`, runtime validation chống dữ liệu local malformed.
5. Trang Product Detail thêm SKU vào local/server cart; trang Cart hỗ trợ xem, đổi quantity, xóa, tổng tiền và validate.
6. Login Customer merge Guest cart; lỗi merge giữ local để retry, thành công mới clear.
7. Integration tests bao phủ RBAC, CRUD/ownership, SKU validation, merge stale item, inventory no-side-effect và concurrent merge.

## Invariant chuyển tiếp sang T17

- Cart chỉ là ý định mua, không bảo đảm tồn.
- Checkout phải tự đọc lại giá và khóa Inventory theo thứ tự thống nhất; không được tin `subtotal`, `lineTotal` hoặc `availabilityCode` từ Cart response.
- `validate` không thay thế transaction checkout.
