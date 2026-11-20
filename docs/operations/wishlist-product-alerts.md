# Wishlist Product Alerts — Operations Runbook

Runbook vận hành pipeline T51 gồm PostgreSQL dirty queue, evaluator tạo notification/outbox và commerce SMTP worker. Mục tiêu là phát hiện backlog sớm, phục hồi an toàn và không phá idempotency.

## Thành phần và delivery contract

- Trigger migration 021 chỉ upsert `store.product_alert_refresh_queue` trong cùng transaction thay đổi tồn kho, regular price, SKU status hoặc Product lifecycle.
- `ProductAlertEvaluationWorker` claim tối đa 10 Product bằng short transaction/`SKIP LOCKED`, xử lý subscriber theo batch 100 và commit observed state + in-app notification + email outbox nguyên tử.
- `CommerceEmailDeliveryWorker` claim tối đa 5 email, gửi SMTP ngoài transaction, timeout 30 giây/item và persist outcome từng item.
- Lease evaluator/email là 5 phút. Shutdown cố release trong 5 giây; lease expiry là fallback.
- Business notification/outbox có unique event identity. SMTP vẫn mang semantic at-least-once ở ranh giới provider accepted nhưng process crash trước khi persist `sent_at`.

## Dashboard/SQL read-only tối thiểu

Chạy bằng database role chỉ đọc. Không xuất `recipient_email` hoặc `payload` vào dashboard/log tập trung.

```sql
-- Queue evaluator: backlog, đến hạn, retry và lease quá hạn.
SELECT
  count(*) AS total,
  count(*) FILTER (WHERE next_attempt_at <= now()) AS due,
  count(*) FILTER (WHERE attempt_count > 0) AS retried,
  count(*) FILTER (WHERE locked_at < now() - interval '5 minutes') AS expired_leases,
  min(queued_at) AS oldest_queued_at
FROM store.product_alert_refresh_queue;

-- Commerce outbox theo terminal state, không đọc PII/payload.
SELECT
  count(*) FILTER (WHERE sent_at IS NULL AND discarded_at IS NULL) AS pending,
  count(*) FILTER (WHERE sent_at IS NULL AND discarded_at IS NULL AND next_attempt_at <= now()) AS due,
  count(*) FILTER (WHERE sent_at IS NOT NULL) AS sent,
  count(*) FILTER (WHERE discarded_at IS NOT NULL) AS discarded,
  count(*) FILTER (WHERE locked_at < now() - interval '5 minutes') AS expired_leases,
  min(created_at) FILTER (WHERE sent_at IS NULL AND discarded_at IS NULL) AS oldest_pending_at
FROM store.commerce_email_outbox;

-- Nhóm lỗi an toàn để phân loại sự cố; không select exception message/recipient.
SELECT 'evaluator' AS component, last_error_code, count(*)
FROM store.product_alert_refresh_queue
WHERE last_error_code IS NOT NULL
GROUP BY last_error_code
UNION ALL
SELECT 'email', last_error_code, count(*)
FROM store.commerce_email_outbox
WHERE last_error_code IS NOT NULL
GROUP BY last_error_code
ORDER BY component, count DESC;
```

Alert vận hành đề xuất ban đầu, cần hiệu chỉnh bằng traffic thật:

- readiness fail hoặc worker exception loop: cảnh báo ngay;
- `expired_leases > 0` kéo dài quá hai chu kỳ polling sau khi database đã healthy;
- evaluator `due > 0` liên tục 10 phút hoặc tuổi queue cũ nhất trên 15 phút;
- email `due > 0` liên tục 10 phút, tuổi pending trên 15 phút hoặc `discarded` tăng;
- cùng `last_error_code` tăng liên tục qua ba lần quan sát.

## Triage

1. Kiểm tra `GET /health/live`, `GET /health/ready`, PostgreSQL latency/connection pool và API restart loop.
2. Lọc structured log theo `ProductAlertEvaluationWorker` hoặc `CommerceEmailDeliveryWorker`; giữ correlation/trace nhưng không sao chép email/payload.
3. Chạy ba query read-only phía trên, ghi lại backlog age, due, retry, expired lease và error-code distribution.
4. Với email, kiểm tra DNS/network/TLS/SMTP credential và provider rate limit. Với evaluator, kiểm tra lock contention và migration 021 đã có trong `public.schema_migrations`.
5. Xác nhận thay đổi nguồn vẫn tăng `event_version`; không thử “chữa” bằng cách gọi SMTP trực tiếp hoặc tạo notification tay.

## Phục hồi an toàn

- Nếu API/worker chết, khởi động lại sau khi PostgreSQL healthy. Row chưa hoàn tất được reclaim khi lease hết hạn; không cần unlock thủ công.
- Nếu SMTP tạm lỗi, sửa connectivity/credential và để `next_attempt_at` + exponential backoff tự retry.
- Nếu backlog tăng do tải, scale API replica có cùng schema/config; `SKIP LOCKED` chia claim. Theo dõi DB contention trước khi tăng replica tiếp.
- Nếu row bị `discarded_at`, giữ nguyên evidence. Phân loại account/email/payload/template/action URL trước; replay chỉ qua repair command/script đã review, giữ nguyên event identity và audit. Không đổi `discarded_at`/`sent_at` trực tiếp trong phiên xử lý sự cố.
- Nếu shutdown không release lease do DB unavailable, chờ tối đa 5 phút sau khi DB phục hồi rồi xác nhận worker khác reclaim.

## Hành động bị cấm

- Không `DELETE` queue/outbox để giảm dashboard backlog.
- Không đặt `sent_at`, `discarded_at`, `locked_at` hoặc `locked_by` bằng SQL ad-hoc.
- Không sửa `event_version`, `alert_sequence`, observed baseline hay tạo lại cùng business event bằng key mới.
- Không đọc/log `recipient_email`, raw `payload`, cookie, CSRF token hoặc SMTP secret ngoài phạm vi incident được cấp quyền.
- Không chạy demo seed hay Mailpit trong production.

## Release và smoke

1. Migrator exit 0; migration 021 checksum khớp history.
2. Backend 441/441, frontend 151/151, lint/build và cả base/demo Compose pass.
3. Chạy Flow 17 trên Docker/PostgreSQL/Mailpit và regression Flow 1, 5, 8, 11, 12, 16 theo [E2E guide](e2e-testing-guide.md).
4. Sau smoke, xác nhận không có expired lease; queue/outbox đến hạn quay về 0 sau worker polling.
5. Production canary: theo dõi backlog/error/discard ít nhất một khoảng rollout đầy đủ trước khi mở toàn bộ traffic.

## Escalation và dữ liệu cần bàn giao

Incident handoff gồm: thời điểm UTC, deployment version, health state, backlog counts/oldest age, error-code distribution, số replica, PostgreSQL/SMTP status và các correlation IDs đại diện. Không đính kèm PII hoặc payload email.
