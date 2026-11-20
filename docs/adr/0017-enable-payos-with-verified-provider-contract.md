# ADR 0017 — Mở lại PayOS theo provider contract đã kiểm chứng

Trạng thái: **Proposed — T19 đang thực hiện, chưa được phép bật production**. Ngày lập: 09/11/2026.

## Bối cảnh

ADR 0003 hoãn T19–T22 cho tới khi dự án có credential kênh test và bằng chứng create/query/cancel, signature, webhook, expiry, duplicate, late-paid và amount mismatch. Credential test đã được cung cấp ngoài repository. Phiên probe đầu tiên đã xác minh create/query/cancel và khả năng dùng official SDK trên .NET 10, nhưng chưa có webhook/payment-success fixture thật.

## Bằng chứng hiện có

- Raw API create payment request với canonical HMAC-SHA256 trả `code=00`, `PENDING` và checkout host `pay.payos.vn`.
- Query theo order code trả amount chính xác; cancel và query sau cancel cùng trả `CANCELLED`.
- Response create/query/cancel đều có signature.
- Chữ ký request sai/thiếu bị PayOS từ chối bằng code `20` và không tạo link.
- NuGet `payOS 2.1.0` build sạch trên `net10.0`; query qua SDK thành công khi `MaxRetries=0`.

Chi tiết đã sanitize nằm tại [provider fixtures](../provider-fixtures/payos/README.md).

## Quyết định đề xuất

1. Dùng official SDK `payOS` pin `2.1.0` sau interface nội bộ `IPayOsGateway`.
2. Đặt `MaxRetries=0` cho create-link. SDK có retry mặc định; ứng dụng không được để retry POST tạo link vì kết quả timeout có thể mơ hồ.
3. Mọi provider call chạy ngoài transaction PostgreSQL.
4. Return URL không phải bằng chứng thanh toán. Chỉ webhook đã verify hoặc query trực tiếp mới chuyển Payment sang success.
5. Webhook được persist bền vững và xử lý lũy đẳng trước khi ACK 2xx.
6. Feature flag mặc định tắt; disable chỉ chặn Order/attempt mới, không chặn callback/reconciliation của Order cũ.
7. External result mơ hồ giữ attempt cần query; không chuyển `FAILED` và không nhả inventory.

## Điều kiện chuyển sang Accepted

- Endpoint webhook công khai của dự án được PayOS xác nhận.
- Thu fixture webhook thật cho payment success và verify bằng SDK/checksum key.
- Chứng minh ACK/retry, duplicate callback, amount mismatch và late callback.
- Chứng minh query-at-deadline và cancel-vs-paid race.
- T20/T21 integration tests trên PostgreSQL thật chứng minh exactly-once transaction/allocation và không oversell/double release.
- Security review xác nhận không có credential/raw sensitive payload trong Git, log hoặc browser.

Cho tới lúc đó, `PAYOS_DEFERRED` và `PayOs:Enabled=false` vẫn là hành vi bắt buộc.

