# Flash Sale hardening — T23/T24 independent review remediation

Ngày hoàn tất: 30/10/2026.

## Phạm vi

- Lưu bền lifecycle `SCHEDULED -> ACTIVE -> ENDED` bằng hosted worker; checkout vẫn đánh giá hiệu lực bằng clock trong lúc worker chưa poll.
- Chuẩn hóa lock order Campaign → Offer → SKU → InventoryBalance giữa checkout, cancel, COD confirm/expiry và return stock.
- Đồng bộ campaign cancel với checkout bằng cùng row lock.
- Mang `flashSaleOfferId` xuyên suốt banner → product → guest/customer cart → login merge → checkout.
- Lưu offer trên `cart_items` qua migration 003 và luôn định giá lại phía server.
- Sửa quota progress theo `(quotaTotal - remainingQuota) / quotaTotal`.
- Chốt COD cho Flash Sale tại ADR 0004 trong thời gian PayOS deferred.

## Acceptance evidence

- Worker ghi audit đúng cho ACTIVE và ENDED.
- Scheduled campaign có thể checkout đúng trong khoảng hiệu lực dù trạng thái lưu chưa được worker cập nhật.
- Checkout chờ transaction cancel rồi từ chối campaign CANCELLED, không stale-read.
- Checkout và COD confirmation đồng thời không deadlock/500.
- Cart lưu offer, dùng giá sale và báo `FLASH_SALE_NOT_AVAILABLE` khi campaign bị hủy.
- Frontend integration test bao phủ banner → product → cart → checkout và xác nhận offer ID/giá sale.
