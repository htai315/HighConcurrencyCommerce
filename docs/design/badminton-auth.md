# Concept HighCommerce — chuyên cầu lông

Chủ dự án xác định cửa hàng bán vợt, áo/trang phục, hộp cầu và phụ kiện cầu lông. Giữ tên HighCommerce hiện tại; định vị này thay cho nội dung cửa hàng công nghệ trước đây.

## Hướng hình ảnh

- Cobalt sân đấu `#164ce7`, mực chữ `#142442`, nền sáng `#f2f5fb`, trắng `#ffffff`, đường viền `#d7dfed`, điểm nhấn dây quấn vợt `#d4fa70`.
- Manrope cho heading, DM Sans cho form/nội dung, fallback sans-serif. Heading đậm gọn, thân bài thoáng; nhãn và checklist không viết hoa toàn bộ.
- Desktop: panel câu chuyện/thể thao trái, form phải có giới hạn chiều rộng 440px. Mobile: panel thể thao gọn phía trên, form một cột và nút bấm lớn.

```text
Desktop: [ Thương hiệu + lời dẫn ] [ Trở về cửa hàng        ]
         [ Vợt, cầu và sân vẽ SVG] [ Login/Register + form   ]
         [ Nhóm sản phẩm        ] [ Nút chính + chuyển trang]

Mobile:  [ Thương hiệu + visual gọn ]
         [ Form + checklist + CTA  ]
```

Điểm nhận diện là vợt có lưới, cán quấn và quả cầu trên hình sân, vẽ bằng SVG nội bộ; không tải ảnh từ bên thứ ba. Không đưa số liệu khách hàng, ưu đãi, chứng nhận hay đăng nhập mạng xã hội chưa có vào giao diện. Giữ hero làm điểm nhấn; form dùng bề mặt phẳng, bố cục rõ và ít trang trí.

## Trải nghiệm mật khẩu

Register nhập hai lần; điều kiện 12–128 ký tự, chữ A–Z, a–z, số 0–9 và một ký tự ngoài chữ/số/khoảng trắng (ví dụ @). Checklist đổi trạng thái theo từng ký tự, có chữ/biểu tượng ngoài màu sắc. Confirmation phản hồi khớp/chưa khớp qua aria-live; mỗi ô có nút hiện/ẩn riêng. Action chặn request sai trước khi gọi API, backend xác minh độc lập. New password khi đổi mật khẩu/CLI bootstrap dùng cùng chính sách; login vẫn xác minh mật khẩu tài khoản cũ bình thường.

## Kiểm tra giao diện

`npm run test:auth-browser` dùng Edge headless qua Playwright, mặc định FE 5173/API 5291 đang chạy. Chụp login/register ở 1440×1000 và 390×844 vào `frontend/playwright-artifacts/` (gitignored), kiểm tra không tràn ngang, không pageerror, nút hiện/ẩn và việc mật khẩu yếu/confirmation sai không gửi registration request. Script không tạo tài khoản thực. Có thể đổi URL bằng `AUTH_SMOKE_URL`.

Phạm vi: thiết kế lại auth và đồng bộ màu/copy nền tảng với cầu lông. Các trang quản trị và nghiệp vụ chưa triển khai vẫn theo roadmap; browser smoke không thay thế E2E toàn bộ đơn hàng.
