# ADR 0013: Phương thức giao hàng và snapshot phí vận chuyển

- Trạng thái: Accepted
- Ngày: 2026-11-06
- Phạm vi: T48 — Shipping Methods & Fees

## Bối cảnh

Checkout hiện chỉ tính tiền hàng sau Flash Sale và voucher. Hệ thống chưa có khái niệm phương thức giao hàng, phí theo khu vực, thời gian giao dự kiến hoặc bằng chứng bất biến về mức phí đã chốt lúc đặt hàng. Nếu chỉ tham chiếu cấu hình hiện tại, đơn cũ sẽ đổi nghĩa khi Admin sửa tên, giá hoặc thời gian giao hàng.

Ngoài ra, báo giá hiển thị trên giao diện có thể trở nên cũ giữa lúc khách chọn phương thức và lúc checkout ghi đơn. Vì checkout giữ tồn kho, quota Flash Sale và voucher trong một transaction, việc bổ sung phí vận chuyển phải giữ nguyên tính nguyên tử và không tạo thêm thứ tự khóa gây deadlock.

## Quyết định

### 1. Cấu hình phương thức giao hàng

Mỗi phương thức có mã bất biến, tên hiển thị, trạng thái `ACTIVE`/`INACTIVE`, phí cơ bản, ngưỡng miễn phí tùy chọn, ETA tối thiểu/tối đa, thứ tự hiển thị, cờ mặc định và `revision` tăng đơn điệu.

Admin có thể cấu hình phụ phí theo tỉnh/thành đã chuẩn hóa. Một cặp `(shipping_method_id, province_key)` chỉ có một cấu hình. Tiền được lưu dưới dạng `numeric(18,2)` nhưng chỉ chấp nhận giá trị VND nguyên, không âm.

Tại một thời điểm chỉ có tối đa một phương thức mặc định đang hoạt động. Service quản trị chịu trách nhiệm bảo đảm hệ thống luôn còn phương thức dùng được trước khi tắt phương thức mặc định.

### 2. Công thức phí và ngưỡng miễn phí

Giá trị xét miễn phí là tổng tiền hàng đã áp dụng giá niêm yết/Flash Sale nhưng **trước voucher**. Voucher không làm mất quyền miễn phí vận chuyển đã đạt được.

```text
configuredFee = baseFee + activeProvinceSurcharge
shippingFee = 0 nếu freeShippingThreshold != null
                    và merchandiseSubtotal >= freeShippingThreshold
              ngược lại configuredFee

orderTotal = merchandiseSubtotal - voucherDiscount + shippingFee
```

Phí giao hàng không tham gia phân bổ voucher và mặc định không được hoàn trong luồng đổi/trả COD. Một chính sách hoàn phí vận chuyển trong tương lai phải là quyết định riêng, không suy diễn từ tiền hoàn hàng hóa.

### 3. Quote và chống báo giá cũ

API quote trả về `shippingMethodId`, `revision`, số tiền và ETA. Checkout nhận lựa chọn đó nhưng luôn đọc lại cấu hình trong transaction. Nếu phương thức đã ngừng hoạt động, revision thay đổi, tỉnh giao hàng đổi hoặc mức phí client gửi không khớp, checkout từ chối bằng lỗi conflict để khách báo giá lại.

Quote chỉ mang tính tham khảo và không giữ chỗ. Checkout là nguồn chân lý cuối cùng.

### 4. Snapshot trên đơn hàng

Mỗi đơn lưu:

- khóa tham chiếu phương thức nếu còn tồn tại;
- mã và tên phương thức tại thời điểm đặt hàng;
- phí vận chuyển đã chốt;
- ETA tối thiểu/tối đa;
- tỉnh/thành chuẩn hóa dùng để tính phí;
- revision cấu hình đã sử dụng.

Snapshot không bị sửa khi Admin thay đổi cấu hình. Đơn lịch sử được backfill bằng `LEGACY_FREE`, tên `Miễn phí (đơn lịch sử)` và phí `0` để không thay đổi tổng tiền đã ghi nhận.

### 5. Checkout và idempotency

Payload chuẩn hóa checkout có shipping selection và sử dụng phiên bản `checkout-v3`. Cùng idempotency key nhưng khác phương thức, revision hoặc phí là xung đột payload; không được replay như cùng một yêu cầu.

Thứ tự khóa hiện hữu `Customer -> Voucher -> Flash Sale -> SKU -> Inventory` được giữ nguyên. Cấu hình shipping được đọc và kiểm tra trước các mutation quota/tồn kho, không thêm một khóa ghi dùng chung vào chuỗi khóa.

### 6. Ràng buộc database

Database bảo vệ các bất biến cuối cùng:

- phí, ngưỡng và tổng tiền là VND nguyên, không âm;
- ETA hợp lệ và không vượt quá 365 ngày trên cấu hình;
- `total_amount = subtotal_amount - voucher_discount_amount + shipping_fee`;
- revision dương;
- mã phương thức chuẩn hóa và duy nhất;
- snapshot bắt buộc trên mọi đơn, kể cả đơn lịch sử.

## Hệ quả

- Đơn hàng có lịch sử tiền và dịch vụ giao nhận ổn định dù cấu hình thay đổi.
- Checkout cần báo giá lại khi cấu hình bị sửa trong lúc khách đang đặt hàng.
- Báo cáo doanh thu phải phân biệt tiền hàng, voucher và phí vận chuyển; không được coi toàn bộ `total_amount` là doanh thu hàng hóa.
- Việc thêm tích hợp hãng vận chuyển hoặc bảng giá chi tiết hơn có thể mở rộng từ quote engine mà không phá snapshot của đơn cũ.

## Phương án bị loại

- **Chỉ lưu `shipping_method_id`:** đơn cũ đổi nghĩa theo cấu hình mới.
- **Tin phí từ frontend:** cho phép sửa payload và dùng báo giá cũ.
- **Áp voucher trước khi xét miễn phí:** làm hành vi khó hiểu và khiến voucher có thể tăng phí phải trả.
- **Hoàn phí giao hàng theo tỷ lệ đổi/trả:** thiếu bằng chứng dịch vụ giao hàng chưa được thực hiện và làm sai chính sách COD hiện tại.
