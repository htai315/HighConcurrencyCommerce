# HighCommerce Frontend Design System — trắng / navy

Ngày cập nhật: 31/10/2026. Thay thế theme graphite/ivory/lime theo yêu cầu của chủ dự án.

## Định hướng

HighCommerce là cửa hàng chuyên cầu lông. Giao diện dùng nền trắng, navy cho tiêu đề và khu vực thương hiệu, xanh dương cho hành động chính, xanh lá nhạt làm điểm nhấn. Các trang nghiệp vụ ưu tiên sự rõ ràng, form dễ dùng và trạng thái có nhãn.

## Nền tảng

- Typography: `Space Grotesk` cho display, `Plus Jakarta Sans` cho nội dung và dữ liệu.
- Brand/action: `#245edb`, navy `#102b50`, accent nhẹ `#d8edab`.
- Surface: `#f6f8fb`, `#ffffff`, `#f1f5fa`; muted `#63748a`, border `#cbd6e3`.
- Control cao 44–48px, bo góc 8–10px; card 16–20px; spacing theo nhịp 8px, bóng đổ nhẹ.
- Button primary xanh dương/chữ trắng; secondary nền trắng/viền rõ.
- Focus state bắt buộc nhìn thấy, hỗ trợ `prefers-reduced-motion` và viewport từ 320px.

## Cấu trúc màn hình

- Storefront: banner cầu lông bo góc, lọc danh mục từ API, catalog responsive.
- Product detail: media lớn bên trái, lựa chọn SKU và hành động mua bên phải.
- Cart/checkout: danh sách và tóm tắt nền trắng; giữ COD, validation và idempotency hiện có.
- Account/order/notification: card trắng, thanh điều hướng tài khoản dùng chung, chi tiết mở rộng và trạng thái có nhãn.
- Bảo mật: card hướng dẫn navy cạnh form đổi mật khẩu; cả hai ô có hiện/ẩn mật khẩu.
- Staff/Admin: tab navy, bảng header xanh nhạt cuộn trong khung; form Flash Sale xếp lại trên mobile.
- Auth: form trắng bên trái, banner navy bên phải; mobile thu gọn banner.
- Lỗi: card giải thích ngắn với CTA trở về/thử lại, hỗ trợ render ngoài shell.

## Tổ chức code

- Header: `SiteHeader.tsx` / `site-header.css`, logo → tìm kiếm → tài khoản → giỏ; hàng điều hướng riêng. Mobile luôn hiện tìm kiếm, tài khoản và giỏ; menu tài khoản hỗ trợ Escape, click ngoài và focus. Tham khảo tổ chức chức năng tại https://shopvnb.com/ và https://lining.com.vn/, không sao chép nội dung thương hiệu. Staff/Admin có lối vào workspace thay liên kết đơn mua hàng.

- `frontend/src/commerce-premium.css`: token chung và các trang nghiệp vụ, import sau CSS cũ.
- `components/layout/storefront-premium.css`: shell và catalog; `auth-premium.css`: auth.
- `PageHeading.tsx`, `AccountNavigation.tsx`: thành phần chung cho các trang khách hàng.
- CSS cũ vẫn cung cấp một số cấu trúc; kiểm tra độ ưu tiên khi sửa selector, không thêm theme thứ ba.
- Không tạo dữ liệu, doanh thu hay cam kết đổi trả giả trong UI thật. PayOS vẫn chưa mở.

## Asset

Hero storefront nằm tại `frontend/src/assets/badminton-hero-v2.png`, được tạo riêng không chứa logo, chữ hoặc nhãn hiệu bên thứ ba. Copy và CTA vẫn là HTML để đảm bảo responsive, accessibility và SEO.

## Kiểm chứng bắt buộc khi sửa UI

```powershell
cd frontend
npm test -- --run
npm run lint
npm run build
npm run test:auth-browser
npm run test:storefront-browser
npm run test:commerce-browser
```

Auth/storefront smoke cần API local ở `http://localhost:5291` và Vite ở `http://127.0.0.1:5173`. Commerce smoke chặn toàn bộ API bằng fixture trong trình duyệt, không ghi database. Cần Edge được cài đặt.

Các script chụp desktop 1440px, tablet 820px, mobile 390px tại `frontend/playwright-artifacts/`; kiểm tra lỗi runtime và tràn ngang. Commerce smoke kiểm tra route khách hàng, trạng thái rỗng, 8 tab Staff/Admin và các trang lỗi, gồm mở chi tiết đơn, sửa địa chỉ, đọc thông báo, mở form chiến dịch. Không thay thế kiểm thử nghiệp vụ hoặc kiểm toán accessibility toàn diện.
