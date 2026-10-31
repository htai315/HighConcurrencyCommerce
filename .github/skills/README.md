# Project Skills

Các skill trong thư mục này được lưu trực tiếp cùng project để GitHub Copilot/agent có thể sử dụng mà không phụ thuộc vào `D:\AI-Agent-Skills`.

## Frontend skills

| Skill | Dùng khi |
|---|---|
| `core-typescript` | Viết, review hoặc refactor TypeScript; type safety và strictness |
| `react` | Component, hooks, state, context, Suspense và React conventions |
| `react-doctor` | Kiểm tra sức khỏe React sau khi hoàn thành feature hoặc sửa bug |
| `architecture-and-design` | Quyết định layer, SOLID, clean architecture và ranh giới module frontend |
| `feature-sliced-design` | Sắp xếp code theo app/pages/features/entities/shared và kiểm soát imports |
| `frontend-design` | Thiết kế giao diện có định hướng thị giác rõ ràng, tránh UI khuôn mẫu |
| `ui-ux-pro-max` | UX, responsive, typography, màu sắc, accessibility và design intelligence |
| `vercel-composition-patterns` | Thiết kế React component API và composition patterns |
| `vercel-react-best-practices` | Hiệu năng React, tránh waterfall và tối ưu bundle/rendering |
| `web-design-guidelines` | Audit UI, accessibility và web interface guidelines |

## Engineering workflow skills

| Skill | Dùng khi |
|---|---|
| `systematic-debugging` | Điều tra root cause trước khi sửa lỗi |
| `test-driven-development` | Phát triển feature/bugfix theo Red–Green–Refactor |
| `verification-before-completion` | Chạy bằng chứng build/test/lint trước khi tuyên bố hoàn tất |
| `writing-plans` | Lập kế hoạch chi tiết cho thay đổi nhiều bước |

## Nguyên tắc sử dụng

- Đọc toàn bộ `SKILL.md` của skill được chọn trước khi thực hiện công việc.
- Nếu `SKILL.md` trỏ tới `references/`, `scripts/` hoặc file hỗ trợ, dùng bản nằm ngay trong cùng thư mục skill này.
- Chỉ nạp các reference liên quan đến tác vụ; không cần đọc toàn bộ mọi skill cho mỗi thay đổi.
- Với React/TypeScript thông thường, ưu tiên `core-typescript` + `react`; thêm skill chuyên biệt khi tác vụ thực sự cần.
- Với thay đổi UI mới, kết hợp `frontend-design` hoặc `ui-ux-pro-max` với các rule React.
- Với refactor cấu trúc, dùng `architecture-and-design` và/hoặc `feature-sliced-design`.
- Trước khi bàn giao thay đổi, áp dụng `verification-before-completion`.

Mỗi thư mục skill là bản sao đầy đủ, bao gồm `SKILL.md` và mọi asset/reference/script đi kèm tại thời điểm import.
