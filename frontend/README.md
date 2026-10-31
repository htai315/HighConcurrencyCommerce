# HighCommerce Frontend

Frontend React độc lập của High-Concurrency E-Commerce Platform.

Giao diện auth theo concept shop cầu lông: cobalt, trắng, điểm nhấn xanh chanh; SVG vợt/cầu nội bộ, responsive desktop/mobile. Đăng ký nhập mật khẩu hai lần, checklist 12–128 ký tự/chữ hoa/chữ thường/số/ký tự đặc biệt, nút hiện/ẩn và phản hồi khớp. Backend xác minh độc lập. Chi tiết tại [concept](../docs/design/badminton-auth.md).

## Công nghệ

- React 19
- TypeScript
- Vite
- React Router

## Chạy local

```powershell
cd D:\Personal_Project\frontend
npm ci
npm run dev
```

Frontend chạy tại `http://localhost:5173`. Vite proxy mọi request `/api/*` sang backend ASP.NET Core tại `http://localhost:5291`.

## Scripts

```powershell
npm run dev
npm run build
npm run lint
npm run preview
npm test
npm run test:watch
npm run test:auth-browser
```

Browser smoke cần Microsoft Edge, FE và API đang chạy; chụp ảnh và kiểm tra form ở desktop/mobile, không tạo account. Ảnh nằm trong `playwright-artifacts/` (gitignored). Đặt `AUTH_SMOKE_URL` nếu FE khác cổng 5173.

## Cấu trúc

```text
src/
├── components/layout/   # Shell và navigation dùng chung
├── config/              # Biến môi trường đã chuẩn hóa
├── features/            # Code theo miền nghiệp vụ
├── lib/api/             # HTTP client và error contract
├── pages/               # Route-level pages
├── App.tsx              # Route composition
└── main.tsx             # React bootstrap
```

## Trạng thái

Catalog `/` và chi tiết `/products/:id` đọc API C# thật. Search, category, khoảng giá, sort và page được lưu trên URL; React Router loader truyền `request.signal` xuống fetch. Response được validate từ `unknown` trước khi render. Có trạng thái tải/rỗng/lỗi/thử lại; không fallback sang dữ liệu mẫu khi API lỗi.

Giá hiện là regular price, không có Flash Sale; ảnh là placeholder đến khi có media URL contract. Cart, Orders, Staff/Admin vẫn là khung nội dung; chưa có checkout thật.

Auth đã chạy thật: `/login`, `/register`, `/account/security`; header lấy session qua `/auth/me`, logout dùng POST + CSRF. Cookie HttpOnly, không lưu token/role trong localStorage. `/orders` chỉ Customer, `/staff` chỉ Staff, `/admin` chỉ Admin; backend vẫn kiểm tra quyền độc lập. Mutation lấy request token mới từ `/auth/csrf`; auth loaders/actions xử lý lỗi và giới hạn returnTo trong origin hiện tại.

Chạy backend ở terminal riêng:

```powershell
cd D:\Personal_Project\HighConcurrencyCommerce
dotnet run --project .\HighConcurrencyCommerce.Api -- --migrate-auth
dotnet run --project .\HighConcurrencyCommerce.Api --launch-profile http
```

Vite mặc định gọi cùng origin qua `/api/v1`; không cần CORS cho local proxy. Có thể đặt `VITE_API_BASE_URL` trong `.env.local` theo `.env.example`. Deploy production cần reverse proxy `/api` về API và SPA fallback cho `/products/:id`; Vite dev proxy không phải cấu hình production. `npm run preview` dùng để xem build, không thay thế deployment server.

Tests kiểm tra HTTP client, catalog/detail, filter/paging/URL, loading/empty/error/retry; auth form, CSRF, guard/redirect, logout, password change và returnTo ngoài origin. MSW chỉ dùng trong tests; API thật vẫn được dùng khi chạy dev/build. Xem [contract catalog](../docs/api/catalog.md), [contract auth và bootstrap Admin](../docs/api/auth.md), [database/test setup](../database/README.md) và [task tiếp theo](../docs/TASKS.md).
