# Kiểm tra chức năng độc lập — 02/11/2026

## Kết luận và phạm vi

Phát hiện 10 vấn đề cần xử lý: 2 mức P1, 7 mức P2 và 1 mức P3. Đây là review tại trạng thái working tree hiện tại, bao gồm các thay đổi chưa commit. Chưa sửa implementation trong đợt này.

- P1: lỗi thao tác nghiệp vụ có thể phát sinh exception không được ánh xạ khi chạy đồng thời.
- P2: sai hành vi mua hàng/giá/hiển thị hoặc không thể hoàn thành thao tác trong một tình huống cụ thể.
- P3: lỗi ở giá trị biên ít gặp.

Đã đọc luồng source và đối chiếu các nhóm: xác thực/password/Google/CSRF/RBAC, hồ sơ/địa chỉ, catalog/sản phẩm/SKU/media, kho/đề nghị giảm kho, giỏ hàng, checkout/COD, hủy đơn/giao vận/hoàn kho, Flash Sale, thông báo, báo cáo/audit. Graphify được dùng để định hướng; kết luận dựa trên source và tái hiện.

Không kết luận hệ thống hết bug chỉ vì test pass. Kiểm tra này không phải kiểm thử tải dài hạn, pentest hay kiểm thử giao diện trên mọi thiết bị. Không thực hiện lại đăng nhập Google thật với tài khoản người dùng; test Google trong suite dùng verifier giả lập. PayOS đang chủ động hoãn theo ADR nên không tính là bug.

## Kiểm chứng nền

| Kiểm tra | Kết quả |
|---|---|
| `dotnet test HighConcurrencyCommerce/HighConcurrencyCommerce.IntegrationTests --no-restore --logger 'console;verbosity=minimal'` | 149 passed, 0 failed, 0 skipped |
| `npm test -- --maxWorkers=2` trong frontend | 62 passed, 18 files |
| `npm run lint` | Thành công |
| `npm run build` | Thành công; cảnh báo chunk JS khoảng 517 kB |

Các phép thử bổ sung sử dụng `CatalogFixture`, tạo và hủy database `hcc_tests_<guid>` riêng. Không nhập/xóa/sửa dữ liệu demo. Harness C# đặt ngoài repository trong `%TEMP%/hcc-audit-20261102`, gọi service thật, EF Core thật, PostgreSQL thật và API qua fixture. Interceptor chỉ dừng tại một điểm để sắp xếp thứ tự chạy, không thay đổi SQL hoặc kết quả nghiệp vụ. Phép thử frontend chạy hàm TypeScript hiện tại sau khi bỏ annotation type; API được giả lập riêng cho tình huống mất phản hồi.

## F01 — P1: Hủy đơn và checkout có thể deadlock

**Source:**

- `HighConcurrencyCommerce/HighConcurrencyCommerce.Infrastructure/Checkout/PostgresCheckoutService.cs:59`: khóa user trước; sau đó khóa SKU ở dòng 173.
- `HighConcurrencyCommerce/HighConcurrencyCommerce.Infrastructure/CustomerOrders/PostgresCustomerOrderService.cs:63`: hủy đơn khóa SKU, sau đó gọi notification ở dòng 123.
- `HighConcurrencyCommerce/HighConcurrencyCommerce.Infrastructure/Notifications/PostgresNotificationService.cs:13`: INSERT notification có FK đến user (`database/001_create_schema.sql:413`).

**Kịch bản:** cùng một khách hủy đơn A và checkout đơn B có cùng SKU. Hủy đơn giữ SKU; checkout giữ user và chờ SKU; INSERT notification của hủy đơn cần khóa FK trên user đang bị checkout khóa. Tạo vòng chờ.

**Bằng chứng:** harness dừng hủy đơn ngay trước INSERT notification, chờ PostgreSQL xác nhận checkout đang chờ lock, rồi tiếp tục hủy đơn. Đặt `deadlock_timeout=50ms` trên connection hủy đơn để tái hiện ổn định việc connection này phát hiện deadlock. Kết quả:

```text
AUDIT_DEADLOCK checkout waiting=True
AUDIT_CANCEL PostgresException SQLSTATE=40P01
AUDIT_CONCURRENT_CHECKOUT success
```

Checkout có retry, nhưng `CancelAsync` không có retry/ánh xạ cho `PostgresException`; `CustomerOrderExceptionHandler` chỉ xử lý `CustomerOrderFailure`. Vì vậy nhánh hủy đơn có thể đi vào lỗi 500. Transaction rollback giữ dữ liệu nguyên vẹn, nhưng thao tác khách yêu cầu thất bại. Các luồng khác ghi notification sau khi khóa kho cần được kiểm tra theo cùng thứ tự khóa; chưa khẳng định đã tái hiện tất cả các luồng đó.

**Hướng sửa:** thống nhất thứ tự khóa xuyên module, tính cả lock phát sinh do FK; cân nhắc kiểu khóa user phù hợp và retry transaction có giới hạn. Không chỉ đổi thông báo lỗi. Thêm regression test checkout đồng thời với hủy/xác nhận/hết hạn đơn.

## F02 — P1: Xóa địa chỉ giữa checkout gây lỗi khóa ngoại

**Source:** `Infrastructure/Account/PostgresAccountService.cs:197` và `Infrastructure/Checkout/PostgresCheckoutService.cs:139` (đường dẫn đều dưới `HighConcurrencyCommerce/HighConcurrencyCommerce.Infrastructure` nếu bỏ tiền tố `Infrastructure/`).

Create/update địa chỉ dùng khóa user, nhưng delete không dùng transaction/khóa user tương ứng. Checkout đọc địa chỉ, sau đó còn phải chờ khóa campaign/SKU trước khi lưu đơn.

**Tái hiện:** dừng checkout sau khi SELECT địa chỉ; gọi `DeleteAddressAsync` thành công; tiếp tục checkout. Kết quả service thật:

```text
AUDIT_ADDRESS DbUpdateException -> PostgresException
SQLSTATE=23503 constraint=sales_orders_shipping_address_id_fkey
```

Checkout không ánh xạ lỗi này thành `ADDRESS_NOT_FOUND`; exception thoát ra nhánh lỗi server. Catch FK trong delete chỉ bảo vệ trường hợp đơn đã lưu trước khi delete, không bảo vệ chiều ngược lại.

**Hướng sửa:** delete tham gia cùng cơ chế tuần tự hóa với checkout/create/update; kiểm tra lại ownership và liên kết đơn dưới khóa. Thêm test cả hai thứ tự: delete thắng và checkout thắng.

## F03 — P2: Gộp giỏ sau đăng nhập có thể cộng trùng khi mất phản hồi

**Source:** `frontend/src/features/auth/auth.api.ts:96`; `HighConcurrencyCommerce/HighConcurrencyCommerce.Infrastructure/Cart/PostgresCartService.cs:68`, cộng số lượng ở dòng 102.

`finishSignIn` giữ local cart khi request lỗi — đúng cho lỗi trước commit, nhưng không đủ cho lỗi sau commit. Merge không có ID thao tác để nhận diện replay. Khi đăng nhập lại, local cart còn nguyên được cộng thêm lần nữa.

**Bằng chứng:**

- API thật: gửi hai lần payload merge cùng SKU, quantity=2 → cart quantity=4.
- Hàm frontend thật với API giả lập commit rồi mất phản hồi: lần đầu server=2/local=2; lần đăng nhập tiếp theo server=4/local trống.

Đây không phải lỗi cộng số lượng của API khi người dùng chủ động thêm hai lần; lỗi là frontend không phân biệt replay của một lần gộp với thao tác mới.

**Hướng sửa:** ID gộp ổn định được lưu bền phía client, backend lưu kết quả/dấu đã xử lý và kiểm tra payload. Chỉ xóa đúng dữ liệu local thuộc lần merge đã xác nhận.

## F04 — P2: Giỏ Guest giữ sai giá và offer khi thêm lại cùng SKU

**Source:** `frontend/src/features/cart/guest-cart.ts:44` (`addGuestItem`).

Nếu SKU đã có, hàm chỉ cộng quantity, không cập nhật `flashSaleOfferId`/`unitPrice`. Khác với nhánh merge của giỏ Customer, có thay offer của dòng hiện hữu.

**Bằng chứng thực thi hàm hiện tại:** thêm SKU giá thường 200.000đ, rồi thêm SKU đó qua offer giá 120.000đ. Kết quả:

```json
{"unitPrice":200000,"flashSaleOfferId":null,"quantity":2}
```

Khi đăng nhập, merge nhận `flashSaleOfferId=null`, nên ưu đãi khách vừa chọn không được áp dụng. Chiều ngược lại cũng giữ offer cũ khi khách thêm sản phẩm từ luồng thường.

**Hướng sửa:** chốt quy tắc thay offer cho một SKU và thực hiện nhất quán Guest/Customer; cập nhật metadata cần thiết hoặc hỏi người dùng nếu không thể tự hợp nhất.

## F05 — P2: Checkout dùng thời gian cũ sau khi chờ xử lý

**Source:** `HighConcurrencyCommerce/HighConcurrencyCommerce.Infrastructure/Checkout/PostgresCheckoutService.cs:89`, `:173`, `:210`.

`now` được lấy trước chuỗi khóa và truyền vào toàn bộ `CreateOrderAsync`. Kiểm tra thời gian Flash Sale dùng giá trị cũ này, không lấy thời gian mới tại bước xác nhận điều kiện sau khóa.

**Bằng chứng:** campaign kết thúc sau 2 giây; checkout bắt đầu trước hạn; dừng tại pha đọc/khóa SKU cho đến sau `ends_at`, rồi tiếp tục validation. Service vẫn trả đơn thành công giá sale 120.000đ:

```text
AUDIT_EXPIRED_SALE accepted=120000.00 afterDeadline=True
```

Không chỉ là commit chậm sau khi đã kiểm tra hợp lệ: trong phép thử, phần validation offer chạy sau hạn nhưng so sánh với timestamp cũ. Worker không cứu được tình huống này vì checkout đã giữ khóa campaign.

**Hướng sửa:** lấy thời gian đánh giá sau khi có đủ khóa, trước kiểm tra trạng thái/quota và tạo reservation. Chốt rõ thời điểm nghiệp vụ được coi là chấp nhận đơn; test chờ khóa đi qua cả starts_at và ends_at.

## F06 — P2: Giá Flash Sale có thể cao hơn giá thường sau khi Admin đổi giá

**Source:** `HighConcurrencyCommerce/HighConcurrencyCommerce.Infrastructure/ProductManagement/PostgresProductManagementService.cs:241`; `.../FlashSale/PostgresFlashSaleService.cs:449`; `docs/api/flash-sale.md:29`.

Publish offer kiểm tra sale nhỏ hơn regular; update giá SKU không bảo vệ điều kiện đó cho offer đã SCHEDULED/ACTIVE. Checkout cũng không kiểm tra lại quan hệ hai giá.

**Bằng chứng API/service thật:**

1. SKU regular=200.000đ; offer ACTIVE sale=120.000đ.
2. `PATCH /api/v1/admin/skus/{id}/price` với `{"price":100000}` → 200.
3. Checkout offer → thành công, total=120.000đ trong khi regular hiện tại=100.000đ.

Vi phạm invariant được ghi trong API contract và gây tình huống mua “khuyến mãi” đắt hơn mua thường.

**Hướng sửa:** chọn rõ chính sách khi đổi giá làm offer không hợp lệ: từ chối, dừng offer hoặc cập nhật có duyệt. Bảo vệ cả publish/đổi giá đồng thời bằng thứ tự khóa thống nhất. Tài liệu đang nói publish khóa thêm SKU/balance nhưng implementation hiện chưa làm bước đó; cần đồng bộ source và contract.

## F07 — P2: Có thể đặt hàng khi cập nhật số lượng vẫn đang chạy

**Source:** `frontend/src/pages/CartPage.tsx:29`, `:60`, `:158`, `:264`.

**Xác định từ source, chưa chạy browser repro riêng:** nút đặt hàng chỉ khóa theo `canCheckout/addressId/checkingOut`, không xét `busySku`. Người dùng tăng số lượng rồi lập tức đặt hàng khi PUT còn pending: request checkout lấy quantity cũ từ React state. Ngược lại, khi checkout pending, các nút đổi/xóa dòng cũng chưa khóa theo `checkingOut`.

Ngoài ra, `busySku` chỉ lưu một ID nhưng các SKU khác vẫn thao tác được. Mỗi response thay toàn bộ `cart`; response cũ đến muộn có thể ghi đè snapshot mới. Database vẫn tuần tự hóa mutation nhưng không bảo vệ thứ tự nhận response trên UI.

**Hướng sửa:** điều phối pending mutation/checkout toàn giỏ, ngăn đặt hàng khi cart chưa ổn định; chống response cũ và đồng bộ lại snapshot sau mutation. Test deferred response, chỉnh hai SKU và bấm checkout khi PUT đang chờ.

## F08 — P2: Trang chi tiết giữ offer cũ khi query thay đổi

**Source:** `frontend/src/pages/ProductDetailPage.tsx:13`, `:47`, `:53`.

**Xác định từ source, chưa chạy browser repro riêng:** component chỉ key theo product.id. Khi chuyển cùng sản phẩm từ URL có `flashSaleOfferId` sang URL không có, effect `return` ngay mà không xóa `flashOffer`. UI vẫn dùng offer cũ để hiển thị giá và thêm giỏ. Đổi từ offer A sang B cũng không bật lại loading; callback request bị hủy có thể cập nhật state của request kế tiếp vì không có guard.

**Hướng sửa:** reset offer/loading theo query, chỉ nhận kết quả của request hiện tại, xóa offer khi không có query. Test điều hướng SPA cùng product ID qua URL sale/thường và chuyển offer nhanh.

## F09 — P2: Thông báo cũ chưa đọc không truy cập được từ UI

**Source:** `frontend/src/features/notifications/notification.api.ts:30`; `frontend/src/pages/NotificationsPage.tsx:17`; `HighConcurrencyCommerce/HighConcurrencyCommerce.Infrastructure/Notifications/PostgresNotificationService.cs:22`.

UI chỉ tải 50 thông báo mới nhất. Checkbox chưa đọc lọc local, không gọi API unreadOnly. Không có phân trang/load-more. `unreadCount` lại tính toàn bộ lịch sử.

**Tái hiện backend bổ sung:** tạo 51 thông báo, lấy 50 mới nhất và đánh dấu đã đọc. Lần lấy tiếp theo vẫn chỉ trả 50 thông báo đã đọc, trong khi unreadCount=1. Trên UI người dùng không thể mở/đánh dấu thông báo thứ 51 dù badge còn báo chưa đọc.

**Hướng sửa:** lọc chưa đọc phía server và bổ sung cursor/phân trang cho toàn bộ lịch sử. Test 51+ notification, đánh dấu đọc rồi chuyển trang/lọc.

## F10 — P3: Bộ lọc báo cáo nhận ngày hợp lệ về cú pháp nhưng trả 500

**Source:** `HighConcurrencyCommerce/HighConcurrencyCommerce.Infrastructure/Reporting/PostgresReportingService.cs:23`, `:82`.

**Bằng chứng HTTP:**

```text
GET /api/v1/admin/reports/revenue?from=9999-12-31&to=9999-12-31
HTTP 500; ArgumentOutOfRangeException tại DateOnly.AddDays(1)
```

Range một ngày vượt qua validation nhưng tính endExclusive làm tràn DateOnly. Nhánh audit dùng phép cộng tương tự; kết luận nhánh này dựa trên source, chưa gửi request riêng.

**Hướng sửa:** giới hạn ngày được hỗ trợ hoặc xử lý inclusive upper bound mà không tràn. Trả lỗi 400 có code thay vì exception.

## Kết quả theo nhóm chức năng

| Nhóm | Đánh giá trong lượt kiểm tra |
|---|---|
| Đăng ký/password/login/logout/session/CSRF/RBAC | Suite hiện tại pass; chưa xác nhận lỗi mới trong logic xác thực chính |
| Google login/link/unlink/challenge | Suite hiện tại pass; không thử Google thật ở lượt này |
| Hồ sơ/sổ địa chỉ | F02 ở giao điểm delete với checkout |
| Catalog/search/filter/gallery | Chưa xác nhận lỗi mới trong reader catalog; F04/F08 ở luồng sản phẩm/offer |
| Draft/SKU/media/pricing/publish | F06 ở giá SKU giao với offer đã publish |
| Nhập kho/ledger/đề nghị giảm/duyệt | Chưa xác nhận lỗi mới ở invariant số dư và phân quyền trong phạm vi đã kiểm tra |
| Guest/customer cart/merge | F03, F04, F07 |
| Checkout/reservation/COD | F01, F02, F05 và giao điểm F06/F07 |
| Hủy đơn/xác nhận/hết hạn | F01 tái hiện trực tiếp ở hủy đơn; cần mở rộng regression qua các nhánh ghi notification khác |
| Đóng gói/giao hàng/thu COD/giao thất bại/hoàn kho | Suite hiện tại pass; chưa có repro lỗi mới riêng của state machine giao vận |
| Flash Sale/campaign/quota | F05, F06, F08 |
| Thông báo | F09 |
| Báo cáo/doanh thu/audit | F10 |
| PayOS | Deferred có chủ đích, không đánh đồng với implementation đã hoàn tất |

## Thứ tự khắc phục đề xuất

1. F01/F02: thống nhất transaction và lock order, kiểm tra cả FK; regression đồng thời có barrier thay vì chỉ Task.WhenAll.
2. F05/F06: bảo vệ thời điểm chấp nhận Flash Sale và quan hệ giá, đồng bộ tài liệu.
3. F03/F04/F07/F08: idempotency của merge và nhất quán state giỏ/offer từ UI tới backend.
4. F09/F10: truy cập đầy đủ thông báo và validation ngày báo cáo.
5. Sau sửa: chạy Graphify theo AGENTS.md, suite BE/FE, lint/build và các repro nêu trên. Mỗi repro phải chuyển sang assertion hành vi đúng, không chỉ assertion hiện trạng lỗi.

Đợt review này chỉ thêm báo cáo Markdown, không sửa source code trong repository, không thay đổi generated Graphify files. Kết quả test nền không bao gồm các kịch bản bổ sung trong scratch harness; vì thế 149/62 test pass không phủ định các bug đã tái hiện.
