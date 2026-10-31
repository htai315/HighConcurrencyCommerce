# Account & Addresses API Contract

> Triển khai cho task T11 — Hồ sơ và sổ địa chỉ Customer.
> Quyền: Chỉ `CUSTOMER` đã đăng nhập (`[Authorize(Policy = "Customer")]`).
> Yêu cầu CSRF: Mọi request thay đổi (`POST`, `PATCH`, `DELETE`) bắt buộc có header `X-CSRF-TOKEN`.

---

## 1. GET `/api/v1/account/profile`

Lấy thông tin hồ sơ của Customer hiện tại.

### Response `200 OK`

```json
{
  "id": "e4b2d3d9-693f-4e09-90d2-1c29e6fb9b95",
  "email": "customer@example.com",
  "fullName": "Nguyễn Văn A",
  "phone": "0987654321",
  "role": "CUSTOMER"
}
```

---

## 2. PATCH `/api/v1/account/profile`

Cập nhật họ tên và số điện thoại của Customer hiện tại.

### Request Body

```json
{
  "fullName": "Nguyễn Văn B",
  "phone": "0912345678"
}
```

* `fullName`: string, bắt buộc, 1–160 ký tự sau khi trim.
* `phone`: string nullable, tối đa 16 ký tự; nếu có phải là định dạng số điện thoại hợp lệ (8–15 chữ số, cho phép dấu `+` ở đầu).

### Response `200 OK`

Trả về đối tượng hồ sơ sau khi cập nhật.

### Lỗi
* `400 Bad Request`: Tên rỗng hoặc số điện thoại không hợp lệ (`code: VALIDATION_ERROR`).
* `401 Unauthorized`: Chưa đăng nhập.
* `403 Forbidden`: Tài khoản không phải Customer.

---

## 3. GET `/api/v1/account/addresses`

Lấy danh sách địa chỉ giao hàng của Customer hiện tại.

### Response `200 OK`

Sắp xếp: `isDefault DESC, createdAt DESC`.

```json
[
  {
    "id": "7fa85f64-5717-4562-b3fc-2c963f66afa6",
    "recipientName": "Nguyễn Văn B",
    "phone": "0912345678",
    "addressLine": "Số 123 Đường Cầu Lông",
    "ward": "Phường Bến Nghé",
    "province": "Thành phố Hồ Chí Minh",
    "isDefault": true,
    "createdAt": "2026-10-30T10:00:00Z"
  }
]
```

---

## 4. GET `/api/v1/account/addresses/{id}`

Xem chi tiết một địa chỉ giao hàng thuộc về Customer hiện tại.

### Response `200 OK`

```json
{
  "id": "7fa85f64-5717-4562-b3fc-2c963f66afa6",
  "recipientName": "Nguyễn Văn B",
  "phone": "0912345678",
  "addressLine": "Số 123 Đường Cầu Lông",
  "ward": "Phường Bến Nghé",
  "province": "Thành phố Hồ Chí Minh",
  "isDefault": true,
  "createdAt": "2026-10-30T10:00:00Z"
}
```

### Lỗi
* `404 Not Found`: Không tìm thấy địa chỉ hoặc địa chỉ thuộc tài khoản khác (Owner-only).

---

## 5. POST `/api/v1/account/addresses`

Tạo địa chỉ giao hàng mới.

### Request Body

```json
{
  "recipientName": "Nguyễn Văn B",
  "phone": "0912345678",
  "addressLine": "Số 123 Đường Cầu Lông",
  "ward": "Phường Bến Nghé",
  "province": "Thành phố Hồ Chí Minh",
  "isDefault": true
}
```

* `recipientName`: string, 1–160 ký tự.
* `phone`: string, 8–16 ký tự, định dạng 8–15 chữ số (cho phép dấu `+` ở đầu).
* `addressLine`: string, 1–500 ký tự.
* `ward`: string nullable, tối đa 160 ký tự.
* `province`: string, 1–160 ký tự.
* `isDefault`: boolean. Nếu `true` hoặc nếu đây là địa chỉ đầu tiên của khách, địa chỉ cũ sẽ được tự động bỏ cờ mặc định trong cùng transaction.

### Response `201 Created`

Headers: `Location: /api/v1/account/addresses/{id}`
Body: Đối tượng địa chỉ vừa tạo.

---

## 6. PATCH `/api/v1/account/addresses/{id}`

Sửa một địa chỉ giao hàng thuộc về Customer hiện tại.

### Request Body

```json
{
  "recipientName": "Nguyễn Văn C",
  "phone": "0988888888",
  "addressLine": "Số 456 Đường Vợt Cầu",
  "ward": "Phường Đa Kao",
  "province": "Thành phố Hồ Chí Minh",
  "isDefault": true
}
```

* Các trường tương tự tạo mới.
* Nếu `isDefault` chuyển sang `true`, hệ thống unset địa chỉ mặc định cũ trong cùng transaction.

### Response `200 OK`

Body: Đối tượng địa chỉ sau khi sửa.

### Lỗi
* `404 Not Found`: Không tìm thấy địa chỉ hoặc địa chỉ thuộc tài khoản khác (Owner-only).

---

## 7. DELETE `/api/v1/account/addresses/{id}`

Xóa một địa chỉ giao hàng thuộc về Customer hiện tại.

### Response `204 No Content`

### Lỗi
* `404 Not Found`: Không tìm thấy hoặc không thuộc Customer hiện tại.
* `409 Conflict`: Địa chỉ đã được liên kết với đơn hàng (`code: ADDRESS_IN_USE`).
