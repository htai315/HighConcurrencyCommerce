# T26 — Giao lại và hoàn kho exactly-once

Ngày hoàn tất: 30/10/2026.

## Mục tiêu

Mở rộng success path T25 bằng nhánh giao thất bại có kiểm soát: tối đa hai lần giao lại và hoàn hàng thực nhận về kho đúng một lần, kể cả khi nhiều Staff xử lý đồng thời.

## Quyết định triển khai

1. Attempt đầu tiên có `attemptNo=1`; mỗi retry tăng `redeliveryCount`, tạo attempt 2 hoặc 3.
2. Kết quả `FAILED` bắt buộc có lý do và tuyệt đối không nhận amount/reference COD.
3. Retry và return-stock cùng khóa Order rồi Shipment. Trạng thái nguồn `DELIVERY_FAILED` bảo đảm chỉ một nhánh commit.
4. Return-stock khóa SKU/Balance theo UUID, tăng Physical/Available và ghi `RETURN_IN`; không đổi Reservation/Lines khỏi `CONSUMED`.
5. Cờ `returnStockCompleted` và movement identity là guard chống hoàn kho lặp.
6. COD chưa thu chuyển `VOID`; Order đóng `CANCELLED/DELIVERY_FAILED_RETURNED`.

## Bằng chứng

- Backend: 105/105 integration tests pass; ba test T26 bao phủ giới hạn retry, double-return concurrent và retry-vs-return race.
- Frontend: 43/43 tests pass; test UI bao phủ failed → retry → failed → return-stock và payload chứng từ.
- .NET build 0 warning/error; React build và lint pass.
