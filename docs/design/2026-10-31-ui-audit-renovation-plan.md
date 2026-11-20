# Đánh giá và kế hoạch trùng tu giao diện HighCommerce

Ngày: 31/10/2026. Trạng thái: kế hoạch triển khai, chưa thay đổi UI trong lượt đánh giá này.

## 1. Phạm vi và bằng chứng

Đọc toàn bộ danh sách route trong `frontend/src/App.tsx`, 14 page component, shell/header/account navigation, các panel nghiệp vụ và CSS liên quan. 14 component không tương đương 14 đường dẫn nghiệp vụ: AuthError/CatalogError là error boundary, NotFound là wildcard; logout chỉ có action. Giỏ và checkout dùng chung `/cart`; Staff/Admin chứa nhiều màn hình trong tab.

Chạy browser smoke commerce trên Edge ở 1440×1000, 820×1180, 390×844: 6 trang khách hàng, trạng thái rỗng giỏ/đơn/thông báo, 8 tab Staff/Admin, 404/lỗi catalog/403. Cả ba kích thước pass kiểm tra runtime và tràn ngang. Dữ liệu commerce là fixture chặn toàn bộ API, không chứng minh tính đúng của nghiệp vụ backend. Đọc và đối chiếu ảnh tại `frontend/playwright-artifacts/`, đặc biệt cart, product-detail, account-mobile, orders, notifications, staff-inventory, admin-products, admin-reports và admin-flash-sale-mobile.

Auth/storefront dùng script riêng. Các trường hợp bảng hàng trăm dòng, campaign đang chạy, biến thể dài, mạng chậm, zoom 200% và screen reader cần bổ sung khi thực hiện. Không coi smoke pass là chứng nhận accessibility hay đánh giá thẩm mỹ đạt.

Skills: `.github/skills/frontend-design/SKILL.md` và `.github/skills/ui-ux-pro-max/SKILL.md`. Áp dụng phân cấp thị giác, bố cục theo nội dung, trạng thái tương tác, ảnh có kích thước ổn định. Kết quả tìm design-system gợi ý Liquid Glass và serif thời trang không phù hợp cửa hàng cầu lông nên không áp dụng; giữ nhận diện navy–blue hiện tại. Hướng dẫn React chung không thay thế cơ chế Form/action của React Router đang dùng.

## 2. Kết luận thiết kế

Nền màu đã tương đối nhất quán, nhưng cấu trúc trải nghiệm còn giống các form nghiệp vụ được ghép vào storefront. Điểm yếu chính là nhiều khung lồng nhau, padding chưa theo mật độ thông tin, quá nhiều câu giải thích kỹ thuật, thiếu ưu tiên hành động và thanh điều hướng trùng tầng.

Chốt hướng: **cửa hàng cầu lông hiện đại, sản phẩm là trọng tâm; khu vận hành gọn, ưu tiên dữ liệu và hành động**. Hai shell dùng chung token/component, không tạo hai thương hiệu.

### Token và nguyên tắc

- Navy `#102b50`: tiêu đề/nhận diện; blue `#245edb`: hành động; nền `#f6f8fb`; surface `#ffffff`; text phụ `#63748a`; viền `#dfe7f0`. Xanh lá nhạt dùng cho điểm nhấn hoặc còn hàng, không thay primary tùy trang.
- Giữ Plus Jakarta Sans cho nội dung; Space Grotesk chỉ cho display khi phù hợp. Nội dung chính 14–16px; label 13–14px; metadata tối thiểu 12px. Kiểm tra tương phản thực tế, không mặc định mọi token đều đạt trên mọi nền.
- Khung mua sắm tối đa 1320px, vùng form đọc khoảng 640–760px; khoảng cách 8/12/16/24/32/48. Bo control 8–10px, card 12–16px. Chỉ dùng shadow để phân biệt nổi/lớp phủ.
- Giá và số liệu dùng tabular numerals; tên dài xuống dòng đúng, không tràn hoặc cắt mất thông tin phân biệt SKU.
- Một hành động chính mỗi vùng. Icon SVG đồng nhất, bỏ emoji trong điều khiển nghiệp vụ. Motion 150–200ms, tắt khi reduced-motion.
- Không thêm đánh giá sao, giá gạch ngang, số người mua, cam kết giao hàng/chính hãng khi chưa có dữ liệu hoặc chính sách xác nhận.
- Không đổi API, RBAC, kiểm tra tồn kho, CSRF hay idempotency chỉ để đơn giản hóa giao diện.

## 3. Từng thanh và thành phần dùng chung

| Thành phần | Hiện trạng / vấn đề | Hướng cải tiến | Ưu tiên |
|---|---|---|---|
| Header mua sắm | Logo–search–account–cart đã hợp lý; giỏ chưa có số lượng; search có nhiều đường viền | Giữ cấu trúc, đơn giản viền, badge tổng số lượng đúng nguồn giỏ khách/customer; trạng thái tải không hiện số giả | P1 |
| Thanh nav chính | Cửa hàng và Khám phá sản phẩm cùng về trang chủ; nhóm tài khoản có thể tạo thêm tầng nav | Tách rõ trang chủ/danh mục; danh mục thực từ API; liên kết Đơn hàng trong khu tài khoản; tránh liệt kê lại mọi link ở hai tầng | P1 |
| Account dropdown | Có Escape/click ngoài/focus return, là phần nên giữ | Thêm truy cập đơn hàng cho customer, thống nhất icon và vùng bấm; tên dài không đẩy giỏ khỏi màn hình | P1 |
| Header mobile | Logo, search, nav chiếm nhiều chiều cao trước nội dung | Thu gọn nav, luôn thấy search; chỉ sticky phần cần thiết, không cố định cả ba hàng; đo trên 320px và khi mở bàn phím | P1 |
| Điều hướng tài khoản | Thanh ngang xuất hiện bên dưới nav shop, lặp Đơn hàng | Desktop sidebar 200–220px trong account shell; mobile một hàng cuộn hoặc menu chọn mục với mục hiện tại rõ | P1 |
| Header/sidebar vận hành | Staff/Admin đang thấy search sản phẩm, giỏ và footer mua sắm | OperationsShell: sidebar nhóm công việc, topbar tên trang/vai trò/tài khoản và link về shop. Drawer trên mobile | P1 |
| Tabs Staff/Admin | Nhãn dài, đánh số 01–04 dù không phải các bước; admin dùng role tab nhưng cần hoàn chỉnh keyboard semantics | Chuyển điều hướng workspace sang route/query state; refresh/back giữ mục đang xem. Nếu giữ tabs thì đủ tabpanel/aria-controls/arrow keys | P1 |
| Thanh lọc catalog | Sidebar mới tốt trên desktop; trên mobile form dài đứng trước sản phẩm | Nút Bộ lọc + số điều kiện, mở disclosure/drawer; sort và số kết quả luôn thấy; chip điều kiện đang áp dụng, reset rõ | P2 |
| Toolbar bảng | Nút và mô tả nằm rải rác; bảng rộng nhiều cột | Toolbar chung: tiêu đề/count, tìm/lọc nếu API hỗ trợ, hành động chính; header cột, số căn phải; cuộn trong bảng có nhãn | P1 |
| Thanh mua/đặt hàng | CTA nằm sâu trong content trên mobile | Thanh tổng tiền + CTA khi cần, không che nội dung/focus/footer; một handler/state chung với CTA gốc | P1 |
| Footer | Chỉ có nhận diện và slogan | Thêm điều hướng đang có thật; nhóm chính sách/liên hệ chỉ khi có nội dung thật. Operations dùng footer tối giản | P2 |
| Loading/alert/error | Chủ yếu đoạn chữ, vị trí thay đổi giữa trang | Skeleton có kích thước ổn định; lỗi tại vùng thất bại; toast/status cho thao tác thành công; không đẩy layout lớn | P1 |
| Dialog/confirm | Địa chỉ dùng native confirm; các panel xác nhận chưa đồng nhất | ConfirmDialog chung: focus trap, Escape, focus return, tên đối tượng, hậu quả và nút hủy rõ | P1 |

## 4. Đánh giá từng trang khách hàng

### 4.1 Trang chủ và catalog — `/`

Giữ hero cầu lông và card sản phẩm vừa làm. Ảnh, giá và CTA đã rõ hơn; không làm lại lần nữa chỉ để đổi phong cách. Hero hiện còn cao khi người dùng quay lại để tìm hàng; trên mobile filter chiếm nhiều màn hình.

Kế hoạch: thu gọn hero theo viewport, đặt lối vào danh mục rõ; với URL đã lọc/tìm kiếm ưu tiên đưa người dùng tới kết quả. Card duy trì chiều cao và giá/CTA thẳng hàng; kiểm tra ảnh hỏng, ảnh trắng, tỷ lệ vợt dài/áo/giày. Chỉ thêm thương hiệu hoặc thông số khi contract có dữ liệu. Không thêm nút wishlist giả. Trang phân trang quay đúng vùng kết quả.

Nghiệm thu: tìm kiếm từ header và filter không mất điều kiện ngoài ý muốn; mobile chạm lọc rồi trở về kết quả; 0 sản phẩm và loading không nhảy layout.

### 4.2 Chi tiết sản phẩm — `/products/:id`

Ảnh lớn và radio SKU đã có; nhiều lớp card trong card, tiêu đề cách dòng lớn; mô tả nằm chung cột mua khiến hai cột mất cân đối. Nguồn hiện có optionLabel, không đủ để suy ra chắc chắn size/màu/trọng lượng.

Kế hoạch: breadcrumb Cửa hàng / Danh mục / Sản phẩm; gallery khoảng 55%, purchase khoảng 45%; giá và tình trạng ngay dưới tên; selector phiên bản rõ, CTA rộng; mô tả dưới cả hai cột. Bỏ viền khung thừa. Mobile ảnh → tên/giá → phiên bản → CTA. Zoom ảnh là cải tiến UI có thể làm nếu bổ sung dialog truy cập được. Không tự tách optionLabel thành thuộc tính thiếu chuẩn.

Nghiệm thu: đổi SKU đổi đúng giá/tồn; ảnh thumbnail dùng bàn phím; lỗi thêm giỏ nằm sát CTA; hết hàng/offer hết hạn không cho bấm mua.

### 4.3 Giỏ và đặt hàng — `/cart`

Tóm tắt hiện chứa cả kiểm tra, địa chỉ, thanh toán và giải thích kho nên quá dài; một sản phẩm tạo nhiều khoảng trống bên trái. Copy “Kiểm tra trước checkout”, “Đặt hàng và giữ tồn” là ngôn ngữ hệ thống; địa chỉ trong select bị cắt.

Kế hoạch: tách ba vùng trong cùng route: sản phẩm, giao hàng/thanh toán, tổng tiền. Quantity stepper có input nhập trực tiếp; thông báo thiếu hàng ngay dòng. Địa chỉ trình bày thành card đầy đủ và nút đổi. CTA “Đặt hàng”, trạng thái “Đang xử lý…”. Giữ kiểm tra server bắt buộc; nếu gộp nút kiểm tra vào luồng đặt hàng phải giữ logic và test. Chỉ hiển thị COD hoạt động; PayOS đưa khỏi lựa chọn chính khi chưa mở. Không tự thêm phí vận chuyển bằng 0 nếu backend chưa định nghĩa.

Thành công: trạng thái tiếng Việt, mã đơn và CTA theo dõi đơn; không render enum thô. Guest có lời nhắc giỏ sẽ gộp sau đăng nhập. Lỗi merge phải có hướng xử lý mà không xóa giỏ khách.

### 4.4 Đăng nhập — `/login`

Visual hiện khá thống nhất với thương hiệu; vẫn có nhiều chức năng disabled: ghi nhớ, Google/Facebook; quên mật khẩu mở thông báo không thực hiện được.

Kế hoạch: giữ split layout; tập trung email/mật khẩu/CTA, bỏ vùng social và checkbox chưa hỗ trợ khỏi luồng chính; thông tin khôi phục chỉ dẫn đúng khả năng hiện có, không thêm link hỗ trợ giả. Giữ password toggle, autoComplete, lỗi rõ; khi đến từ giỏ có câu “Đăng nhập để tiếp tục đặt hàng”.

### 4.5 Đăng ký — `/register`

Có checklist mật khẩu và xác nhận khớp, cần giữ. Phone disabled là một trường không thể dùng, làm form dài; nội dung đồng ý hiện là consent, chưa phải bộ điều khoản có trang riêng.

Kế hoạch: bỏ input phone disabled, nhắc bổ sung sau đăng ký bằng một câu nhỏ; checklist gọn không giấu điều kiện; lỗi cạnh trường và summary nếu nhiều lỗi. Không ghi “đồng ý điều khoản” với link rỗng. Tính năng phone lúc đăng ký là thay đổi contract riêng nếu sau này cần.

### 4.6 Hồ sơ và địa chỉ — `/account`

Ảnh mobile cho thấy trang rất dài vì hồ sơ, form sửa và form thêm cùng hiển thị. Nút thao tác địa chỉ hiện có kích thước nhỏ khoảng 34px và native confirm.

Kế hoạch: desktop account sidebar + nội dung; hồ sơ một khối gọn, địa chỉ là danh sách card. “Thêm địa chỉ” mở form riêng; sửa từng địa chỉ, chỉ một editor hoạt động. Desktop có thể dùng dialog; mobile dùng sheet/toàn màn hình có focus management. Địa chỉ mặc định có nhãn nhẹ; xóa nằm nhóm phụ, dialog nêu rõ địa chỉ. Giữ 409 ADDRESS_IN_USE và owner-only.

### 4.7 Bảo mật — `/account/security`

Có hướng dẫn và form riêng; khối navy nhấn mạnh quá nhiều so với nhiệm vụ đổi mật khẩu.

Kế hoạch: form chiều rộng vừa phải, hướng dẫn compact cạnh/dưới form; chuẩn hóa checklist như đăng ký; báo lỗi đúng current/new password khi mã lỗi cho phép. Nêu rõ đăng nhập lại sau đổi. Không thêm thiết bị đăng nhập/2FA khi chưa có chức năng.

### 4.8 Đơn hàng — `/orders`

Thẻ đơn hiển thị bốn nhóm trạng thái cùng trọng số, thiếu ảnh sản phẩm; form hủy vàng luôn mở chiếm nhiều chú ý. Có totalCount nhưng màn hình chưa cung cấp phân trang rõ dù API trả page/pageSize.

Kế hoạch: tiêu đề “Đơn hàng của tôi”; card có mã/ngày/trạng thái chính, sản phẩm/tổng tiền và nút chi tiết. Timeline chỉ dùng trạng thái/mốc thật, không suy diễn ngày giao. Reservation chuyển vào chi tiết nếu cần. Hủy chỉ mở form sau bấm, xác nhận trước gửi. Lọc trạng thái phải rõ lọc trang hiện tại hay toàn bộ; ưu tiên API pagination trước khi gắn tổng count lên bộ lọc cục bộ.

### 4.9 Thông báo — `/notifications`

Card quá rộng cho một thông báo ngắn; emoji và badge chiếm diện tích, nhãn “Cập nhật đơn hàng” dùng cho mọi loại đã đọc có thể không đúng nội dung.

Kế hoạch: dạng inbox gọn, SVG theo loại, chấm chưa đọc kèm text truy cập được, ngày theo nhóm; hành động đọc nhẹ ở bên phải. Tất cả/chưa đọc nếu chỉ dữ liệu đang tải phải thể hiện đúng phạm vi. Link tới đơn chỉ khi payload có ID đáng tin cậy; “đọc tất cả” cần contract riêng, chưa mặc định thêm.

### 4.10 Các màn hình lỗi — AuthError, CatalogError, NotFound

Đã có CTA thử lại/về shop và correlation ID. AuthError dựa pathname cart, các lỗi nghiệp vụ khác còn có thể bị mô tả như lỗi phiên.

Kế hoạch: ErrorState dùng chung nhưng copy theo ngữ cảnh route + mã HTTP: chưa đăng nhập, không đủ quyền, không tìm thấy, kết nối lỗi, filter không hợp lệ. Giữ thông tin hỗ trợ nhỏ có thể sao chép. 404 có tìm sản phẩm/về shop, không auto redirect mất ngữ cảnh. Không coi mọi 403 là đăng xuất.

## 5. Từng màn hình vận hành

### Staff — sản phẩm và media

Hiện tạo draft + danh sách + editor SKU/media đặt chung trang dài. Chuyển thành list/detail: danh sách có thumbnail, tên, trạng thái; chọn mở editor; tạo mới là hành động riêng. Editor có Nội dung / Phiên bản / Hình ảnh. Gallery quản trị có preview, alt, thứ tự, lỗi URL. Không vẽ upload kéo-thả khi chưa có endpoint upload. Published vẫn chỉ đọc đúng quyền.

### Staff — kho

Hai form lớn nằm trước bảng tồn, che mất công việc tra cứu. Đưa bảng tồn lên đầu với tìm SKU (phạm vi dữ liệu rõ), cảnh báo tồn thấp. “Nhập kho”, “Đề nghị giảm”, “Lịch sử” mở drawer theo SKU. Mã phiếu vẫn ổn định qua retry; hiển thị dạng mã tham chiếu có copy, không yêu cầu nhân viên hiểu idempotency. Không đổi mã khi lỗi hoặc tự tạo lệnh mới khi đóng/mở drawer.

### Staff — lịch sử dịch chuyển

Panel hiện thuộc kho, không phải route riêng. Đưa thời gian/loại/số lượng/người thực hiện lên trước; source/reference vào chi tiết; phân biệt tăng/giảm bằng dấu và chữ, không chỉ màu. Loading/empty/error độc lập với bảng tồn.

### Staff — COD chờ xác nhận

Hiện là hàng đợi card và CTA xác nhận. Cải tiến bảng/card compact: mã đơn, khách, tổng tiền, thời hạn, trạng thái. Chi tiết nhận hàng mở theo đơn; CTA xác nhận cần thấy đơn đang xử lý, chống bấm lặp giữ nguyên. Màu cảnh báo hạn chỉ tính từ deadline thật.

### Staff — giao hàng / Fulfillment

Panel lồng trong vận hành, có các nút theo trạng thái. Chia nhóm Chờ đóng gói / Chờ bàn giao / Đang giao / Giao thất bại theo API thật. Trình bày bước kế tiếp rõ; biên nhận COD, lý do thất bại, bằng chứng hoàn kho mở đúng ngữ cảnh. Không giấu yêu cầu chứng từ để làm form ngắn. Mobile card tóm tắt và trang/khối chi tiết.

### Staff — tác vụ

Hiện chỉ các metric dạng article, chưa tạo lối đi trực tiếp tới công việc. Cho mỗi số liệu dẫn đến danh sách đúng mục; nêu thời điểm cập nhật; nhóm công việc cần xử lý và thông tin theo dõi. Không thêm biểu đồ trang trí hoặc số liệu giả.

### Admin — giá và công bố

List/detail là nền tốt nhưng tiêu đề/copy dài và bảng lặp viền. Thêm ảnh xem trước, giá căn phải, kiểm tra nhập tiền rõ. Checklist publish dựa dữ liệu thực: draft, có SKU active, SKU active có giá hợp lệ. Hiển thị lý do chưa thể công bố gần CTA; xác nhận công bố có tên sản phẩm và tóm tắt. Không bắt ảnh thành invariant mới nếu backend không yêu cầu.

### Admin — duyệt giảm tồn

Tổ chức theo trạng thái yêu cầu, số lượng và lý do; mở chi tiết thấy SKU, người đề nghị, tồn khả dụng và ảnh hưởng dự kiến. Hai nút Duyệt/Từ chối khác mức nhấn, xác nhận theo yêu cầu. Số dư dự kiến là tham khảo; kết quả server quyết định khi concurrent.

### Admin — báo cáo

Hiện bốn KPI, bảng tồn thấp và audit cùng trang; khoảng ngày hiển thị ISO, chưa có thanh chọn kỳ trực quan. Tách Báo cáo doanh thu / Tồn thấp / Nhật ký. KPI căn thẳng, dùng định dạng ngày Việt Nam; chart chỉ dùng days/paymentMethods có thật và có bảng thay thế. Kiểm tra hỗ trợ from/to của API trước khi thêm picker.

### Admin — nhật ký thao tác

Người dùng đang phải nhập action chính xác; bảng rỗng chỉ có header. Dùng dropdown nhãn Việt map action chuẩn, giữ ô tra cứu nâng cao nếu cần; empty state rõ; detail hiển thị actor/entity/correlation và before/after chỉ khi contract cung cấp. Không tự gộp mất các bản ghi audit.

### Admin — Flash Sale

Mobile có nút “Đóng form” chen cạnh mô tả; CTA xanh lá khác chuẩn; copy “bên trái” sai khi xếp dọc. Một số label cần liên kết id/htmlFor, không chỉ đặt cạnh input.

Kế hoạch: list chiến dịch với trạng thái/thời gian; create/edit form theo nhóm Tên và lịch → Ưu đãi SKU → Kiểm tra và lên lịch. Chỉ dùng stepper nếu thực sự chia bước. Đồng nhất primary blue; thời gian có timezone; SKU tìm kiếm, giá/quota/giới hạn rõ; xác nhận hủy có lý do và ảnh hưởng. Mobile hành động thành hàng riêng, không ép mô tả cạnh button. Giữ điều kiện draft, schedule/cancel và giới hạn quota từ server.

## 6. Cấu trúc đích

```text
StorefrontShell
  Header mua sắm → nội dung catalog/detail/cart → footer
  AccountLayout: navigation + hồ sơ / đơn / thông báo / bảo mật
AuthLayout
  nhận diện cầu lông + form đăng nhập / đăng ký
OperationsShell
  Sidebar công việc + topbar + toolbar + list/detail/form
```

Component chung: Button, FormField, StatusBadge, EmptyState, ErrorState, LoadingState, ConfirmDialog, Drawer, DataTable, PageHeading, Price, ProductImage. Chỉ trừu tượng hóa khi có ít nhất hai nơi dùng hoặc ranh giới trách nhiệm rõ. Giữ semantic HTML và React Router data APIs.

Dọn CSS theo từng phạm vi: token → primitive → shell → page. Dự án hiện có index.css, design-v2.css, storefront-premium.css, commerce-premium.css, catalog-showroom.css và auth styles; không thêm một lớp theme phủ toàn app nữa. Di chuyển từng nhóm và bỏ rule cũ sau screenshot/test đối chiếu; không xóa hàng loạt trước khi biết selector còn dùng.

## 7. Thứ tự triển khai đã đề xuất

| Đợt | Công việc cụ thể | Phụ thuộc | Điều kiện xong |
|---|---|---|---|
| UI-01 | Token, button/field/badge/error/dialog, nền tảng CSS và copy | Không | Snapshot các trang đại diện không hồi quy; focus/reduced motion đạt |
| UI-02 | Header/footer/account navigation và OperationsShell | UI-01 | Điều hướng đúng role, mobile drawer keyboard được; refresh/back giữ mục |
| UI-03 | Chi tiết sản phẩm và cart/checkout | UI-01, UI-02 | SKU/giá/tồn đúng; guest merge; thiếu địa chỉ; checkout success/failure/retry |
| UI-04 | Orders, account, notifications, security | UI-01, UI-02 | Form ngắn hơn, state đầy đủ; address owner/default và cancel flow giữ nguyên |
| UI-05 | Staff product/media, kho, ledger, COD, giao hàng, tasks | UI-01, UI-02 | Nhập kho idempotent; đúng quyền; bảng nhiều dòng và drawer mobile usable |
| UI-06 | Admin pricing/publish, adjustment, reports/audit, flash sale | UI-05 patterns | Publish/approve invariants giữ nguyên; form timezone/quota, error rõ |
| UI-07 | Tinh chỉnh auth, catalog mobile, các error page; tổng kiểm | Các đợt trên | Bộ screenshot và test toàn tuyến; không lỗi layout/focus/content |

Ưu tiên thực thi: UI-01 và UI-02 phạm vi tối thiểu để UI-03 có thể hoàn thành sớm. Không để refactor nền tảng kéo dài trước khi người dùng thấy cải thiện luồng mua hàng. Mỗi đợt giao được một phần hoàn chỉnh, không hứa lịch trước khi đo khối lượng.

## 8. Nghiệm thu bắt buộc cho các đợt sửa code

1. Desktop 1440, tablet 820, mobile 390 và 320px; zoom 200%; không tràn ngang toàn trang. Bảng cuộn trong container có hướng dẫn và keyboard access.
2. Tên dài, 0/1/nhiều dữ liệu, ảnh hỏng, hết hàng, giá lớn, nhiều SKU/địa chỉ; không chụp màn hình khi ảnh lazy chưa tải rồi kết luận ảnh hỏng.
3. Tab/Shift+Tab/Escape; focus sau mở/đóng drawer/dialog; label liên kết input; lỗi có mô tả; status không chỉ dựa màu; sticky không che control.
4. Loading, retry, 401/403/404/409/500 và mất mạng; thao tác submit không bị gửi lặp. UI không báo thành công trước khi server xác nhận.
5. Chạy unit tests liên quan, lint/build; auth/storefront/commerce browser suites. Bổ sung fixture lớn và ca tương tác mới; smoke hiện tại chưa cover hết mutation nghiệp vụ.
6. Nếu sửa code: chạy `python -m graphify update .` từ root sau lần sửa cuối trước handoff. Chỉ sửa tài liệu kế hoạch thì không cần rebuild đồ thị code.
7. Lưu screenshot trước/sau cùng viewport và dữ liệu; so nội dung thực thay vì chỉ đếm test pass.

## 9. Ranh giới chức năng

Có thể làm trong FE: layout, copy, semantic/accessibility, drawer/dialog, grouping dữ liệu đã có, responsive, image fallback, trạng thái và link tới route đang tồn tại.

Cần kiểm tra contract hoặc hạng mục riêng: brand/spec filters, đánh giá/wishlist, reset mật khẩu/social login/remember-me, upload ảnh, bulk actions, notification deep link/mark-all, server-side filter/pagination mới, phí vận chuyển, PayOS. Không đưa control giả vào bản UI giao người dùng.

Đây là kế hoạch đã chốt hướng thiết kế để triển khai tiếp; không phải xác nhận rằng mọi đề xuất đã được code hoặc backend đã hỗ trợ.

## 10. Tiến độ triển khai — đợt nền tảng và trang khách hàng

Đã triển khai:

- AppShell tách header/footer vận hành khỏi header mua sắm, khu tài khoản có sidebar desktop và navigation cuộn ngang mobile.
- Footer mua sắm có liên kết sản phẩm/đơn/tài khoản; dropdown tài khoản có đơn hàng.
- CSS dùng tiếp commerce-premium.css hiện có: chuẩn hóa spacing, trạng thái, số liệu, form địa chỉ, bảng vận hành, trang bảo mật và thông báo.
- StatusBadge dùng lại được; checkout success dịch trạng thái thay enum thô.
- Chi tiết sản phẩm có breadcrumb, giảm khung lồng nhau, mô tả riêng dưới gallery và panel mua.
- Giỏ có preview đầy đủ địa chỉ đã chọn; CTA và hướng dẫn tiếng Việt; PayOS chưa hoạt động không còn là lựa chọn disabled trong form.
- Form thêm địa chỉ và yêu cầu hủy đơn mở theo thao tác bằng disclosure HTML có keyboard support. Không đổi API hoặc quy tắc hủy/xóa.
- Auth bỏ phone disabled/social/remember-me chưa hoạt động; giữ checklist password, confirm password và hành vi xác thực.

Kiểm chứng: 51/51 frontend tests, build, lint pass; auth/storefront/commerce browser suites pass desktop/tablet/mobile; Graphify update hoàn tất. Commerce dùng fixture, không phải bằng chứng kiểm thử backend. Đã xem ảnh sau sửa cho product-detail, cart-mobile, account-mobile, admin-products.

Còn lại trong kế hoạch: primitive dialog/drawer dùng chung, cart badge đồng bộ, bộ lọc catalog mobile, Operations sidebar và lưu tab trong URL, chia sâu các màn hình kho/giao hàng/Flash Sale, timeline và pagination đơn, báo cáo/audit, các tình huống accessibility/320px/zoom 200% mở rộng. Đợt này là bước triển khai nền, không đánh dấu toàn bộ UI-01…UI-07 hoàn tất.

### Đợt tiếp theo — điều hướng vận hành và form kho

- Sidebar desktop Staff/Admin; tablet/mobile chuyển thành nhóm điều hướng phía trên. Bỏ số thứ tự trang trí.
- `useWorkspaceSection` lưu mục vào query `section`; có thể mở URL trực tiếp, refresh và dùng history trình duyệt. Giá trị không hợp lệ quay về products. Admin duyệt giảm tồn tải dữ liệu cả khi mở URL trực tiếp.
- Tab Admin có roving tabindex, Arrow/Home/End, aria-controls và tabpanel liên kết.
- Nhập kho/đề nghị giảm tồn dùng disclosure; các field vẫn mounted, đóng/mở không đổi sourceId. Nút Nhập ở dòng SKU mở đúng form và focus số lượng.
- Flash Sale: toolbar responsive, label liên kết input tên/ngày, CTA theo màu thương hiệu, bỏ copy phụ thuộc vị trí “bên trái”.
- Browser smoke bổ sung reload từng mục Staff/Admin và giữ sourceId khi đóng/mở form. Unit tests cập nhật để chờ điều hướng/tải chi tiết bất đồng bộ và mở disclosure trước thao tác.
- Build/lint sạch, 51/51 frontend tests pass, Graphify cập nhật. Phần drawer/dialog, bộ lọc lớn, báo cáo và timeline vẫn chưa hoàn tất.

### Đợt đơn hàng và báo cáo

- Đơn hàng có timeline dựa trên timestamp server, không suy diễn ngày giao; phân trang URL gửi page thực tới API, remount danh sách theo trang để không giữ đơn trang trước. Test chuyển trang xác nhận dữ liệu cũ biến mất.
- Báo cáo có form khoảng ngày, kiểm tra thứ tự ngày, dùng API from/to; bảng chi tiết doanh thu theo ngày mở theo nhu cầu.
- Nhật ký có gợi ý action, empty state, phân trang server; trang sau dùng bộ lọc đã áp dụng, không dùng nội dung chưa submit.
- Build/lint pass. Sau lượt toàn suite có 50/51 pass và một selector doanh thu trùng với bảng chi tiết mới, đã sửa selector chỉ vào KPI; chạy lại 5/5 test báo cáo/đơn (gồm test pagination mới) pass. Graphify cập nhật sau lần sửa cuối.
- Chi tiết before/after nhật ký, dialog chung và các hạng mục còn lại chưa triển khai trong đợt này.

### Chi tiết nhật ký

- Bổ sung AuditDetail mở tại từng dòng: dữ liệu trước/sau, mã bản ghi, correlation. JSON được định dạng dễ đọc; null có giải thích, chuỗi không phải JSON giữ nguyên và render dạng text an toàn.
- Vùng nhật ký cuộn có tên và nhận focus bàn phím; trạng thái tải gắn aria-busy. Lỗi tải báo cáo ban đầu có nút thử lại.
- Test báo cáo kiểm tra mở chi tiết, null, JSON và mã đối chiếu: 2/2 pass; build/lint pass. Graphify cập nhật. Dialog chung vẫn là việc tiếp theo.

### Hộp thoại xác nhận

- Thêm ConfirmDialog dùng native dialog, tên/mô tả truy cập được, focus nút Quay lại khi mở, vòng Tab/Shift+Tab và trả focus khi đóng; chặn đóng khi đang gửi.
- Áp dụng hủy đơn: nhập lý do → xem mã đơn và hậu quả → xác nhận mới gọi API. Quay lại không gửi mutation. Không thay đổi quyền hủy hoặc contract backend.
- Unit suite 52/52 pass; thêm kiểm tra không gọi API trước xác nhận và focus return. Browser smoke bổ sung dialog visible, focus ban đầu, Shift+Tab và Escape trên các viewport. jsdom shim chỉ dùng trong tests vì không triển khai showModal.

### Hoàn thiện đồng bộ các trang — 31/10/2026

- Header hiển thị tổng số lượng giỏ khách/khách hàng, cập nhật sau mutation và checkout; ẩn số lượng khi API lỗi thay vì báo số giả. Giữ revision guard tránh response cũ ghi đè kết quả mới.
- Danh mục có bộ lọc đóng/mở trên mobile và chip gỡ từng điều kiện đang áp dụng.
- Xóa địa chỉ, công bố sản phẩm, duyệt/từ chối giảm tồn và hủy Flash Sale dùng ConfirmDialog chung. Hủy Flash Sale giữ ô lý do, hỗ trợ bàn phím và giải thích ảnh hưởng bằng ngôn ngữ người dùng.
- Staff/Admin tìm sản phẩm theo tên/SKU; kho tìm theo tên, SKU, phiên bản, có thông báo khi không tìm thấy. Fulfillment lọc theo trạng thái giao hàng.
- Thông báo dùng icon SVG, lọc chưa đọc trong danh sách hiện tại, trạng thái rỗng và đã đọc rõ ràng.
- Flash Sale có nhãn liên kết input SKU/giá/quota/giới hạn, hiển thị múi giờ máy đang nhập ngày. Không đổi contract thời gian UTC.
- Kiểm tra responsive 1440/820/390/320px; sửa CTA công bố tràn ngang và giữ badge không xuống dòng, bảng quản trị cuộn bên trong khung. Browser smoke bao phủ trang khách hàng, trạng thái rỗng, 8 mục Staff/Admin, lỗi 403/404/catalog, tìm kiếm và dialog.
- Xác minh: 52/52 tests (17 files), build và lint đạt; smoke auth/catalog/commerce trên Edge headless. Auth/catalog có truy cập local API; commerce dùng fixtures, không thay thế kiểm thử tích hợp nghiệp vụ backend.
- Đã chạy `python -m graphify update .`. Chưa xác minh mức zoom trình duyệt 200% thực hoặc thiết bị iOS/Safari; các kết quả responsive trên là viewport CSS trong Edge. Không coi đợt này là audit WCAG toàn diện.
