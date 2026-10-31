# PostgreSQL backup và recovery runbook

Hai script trong `ops/` là guardrail cho logical backup/restore; production managed PostgreSQL vẫn nên bật automated backup + PITR/WAL archive của nhà cung cấp.

## Backup logical

Password chỉ đặt tạm trong environment của process. Không đưa password vào command history hoặc file script.

```powershell
$env:PGPASSWORD = "<secret-from-vault>"
.\ops\Backup-Postgres.ps1 `
  -HostName db.example.internal -Port 5432 `
  -Database highcommerce -UserName backup_role `
  -OutputDirectory D:\Backups\HighCommerce
Remove-Item Env:PGPASSWORD
```

Script tạo archive custom-format, chạy `pg_restore --list` để kiểm tra khả năng đọc và tạo file `.sha256`. `backup_role` cần quyền đọc cần thiết, không dùng account ứng dụng nếu có thể.

## Restore drill an toàn

Tạo một database **mới và rỗng** trước. Script yêu cầu tên xác nhận trùng tuyệt đối, từ chối database hệ thống và từ chối target đã có schema `store`.

```powershell
$env:PGPASSWORD = "<secret-from-vault>"
.\ops\Restore-Postgres.ps1 `
  -BackupPath D:\Backups\HighCommerce\highcommerce-20261030T120000Z.dump `
  -HostName restore-db.example.internal -Port 5432 `
  -Database highcommerce_restore_20261030 `
  -ExpectedDatabase highcommerce_restore_20261030 `
  -UserName restore_role
Remove-Item Env:PGPASSWORD
```

Sau restore:

1. Chạy kiểm tra row count và FK/unique constraints quan trọng.
2. Khởi động một API cô lập trỏ vào DB restore; `/health/ready` phải Healthy.
3. Smoke catalog, auth test account riêng và báo cáo read-only.
4. Không gửi notification/worker mutation tới dữ liệu production trong restore drill.
5. Ghi thời gian backup, restore, dung lượng và lỗi để đo RPO/RTO thực tế.

RPO/RTO chưa được chủ dự án/SLA chốt. Mốc khởi đầu đề xuất để đo là backup logical hằng ngày, PITR 15 phút và restore drill ít nhất mỗi quý; chỉ công bố cam kết sau khi có số liệu drill thật.

## Khi có sự cố

- Ưu tiên cô lập writer và bảo toàn evidence/log trước khi restore.
- Chọn restore point trước sự kiện lỗi; restore sang database mới, không ghi đè trực tiếp database cũ.
- Đổi connection secret có kiểm soát, xác minh readiness rồi mới chuyển traffic.
- Giữ database cũ read-only trong thời gian điều tra và rollback window.
- Sau phục hồi, đối chiếu Order/Payment/Reservation/Inventory ledger; health check đơn thuần không chứng minh business data nhất quán.

