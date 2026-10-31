# Inventory Adjustment Approval API — Task T14

Base URL: `/api/v1`. JSON dùng camelCase. Mọi mutation bắt buộc CSRF header `X-CSRF-TOKEN`; response thuộc `/staff` và `/admin` có `Cache-Control: no-store`.

## Nghiệp vụ

- Staff chỉ tạo đề nghị giảm tồn; thao tác này không thay đổi `inventory_balances`.
- Request nhận `quantity > 0`, database lưu `delta_qty = -quantity`.
- Admin có thể `APPROVE` hoặc `REJECT` đúng một lần. Người tạo không được tự duyệt.
- `REJECT` chuyển request sang `REJECTED`, không tạo movement và không thay đổi tồn.
- `APPROVE` kiểm tra lại tồn khả dụng tại thời điểm duyệt. Nếu đủ, transaction giảm cùng lượng ở `physical_qty` và `available_qty`, giữ nguyên `reserved_qty`/`allocated_qty`, tăng `revision`, tạo movement `ADJUST`, ghi audit và kết thúc ở `APPLIED`.
- Request, SKU và balance được khóa hàng khi quyết định; hai Admin duyệt đồng thời chỉ một request thành công.

## Endpoint

| Method/path | Quyền | Kết quả |
|---|---|---|
| `POST /staff/inventory/adjustment-requests` | Staff | Tạo request `PENDING`, trả `201` |
| `GET /admin/inventory/adjustment-requests` | Admin | Danh sách request, Pending đứng trước |
| `POST /admin/inventory/adjustment-requests/{id}/decision` | Admin | Duyệt hoặc từ chối, trả `200` |

### Tạo đề nghị

```json
{
  "productSkuId": "30000000-0000-0000-0000-000000000001",
  "quantity": 4,
  "reason": "Hàng hỏng sau kiểm kê"
}
```

Response chứa `deltaQty: -4`, `reviewStatus: "PENDING"`, thông tin SKU, sản phẩm và người đề nghị.

### Quyết định

```json
{ "decision": "APPROVE" }
```

`decision` chỉ nhận `APPROVE` hoặc `REJECT`. Khi approve thành công, response chứa request `APPLIED`, balance sau điều chỉnh và `movementId`. Khi reject, balance và movementId là `null`.

## Mã lỗi

| HTTP/code | Ý nghĩa |
|---|---|
| `400 VALIDATION_ERROR` | Body sai định dạng/range |
| `400 INVALID_ARGUMENT` | Dữ liệu nghiệp vụ không hợp lệ |
| `401` | Chưa đăng nhập |
| `403` | Sai role Staff/Admin theo endpoint |
| `404 NOT_FOUND` | SKU hoặc request không tồn tại |
| `409 INSUFFICIENT_AVAILABLE` | Tồn khả dụng không đủ lúc tạo hoặc lúc duyệt |
| `409 ALREADY_DECIDED` | Request không còn Pending |
| `409 SELF_REVIEW` | Người tạo cố tự duyệt |

