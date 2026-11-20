#!/usr/bin/env bash
# scripts/demo-up.sh
# Dựng trọn bộ hệ thống HighConcurrencyCommerce và seed dữ liệu demo trong một lệnh duy nhất.
set -e

echo "=== HighConcurrencyCommerce - Khởi động Demo ==="

# 1. Kiểm tra file .env
if [ ! -f ".env" ]; then
    echo "[+] Tạo file .env từ .env.example..."
    cp .env.example .env
    sed -i 's/replace-with-a-long-random-password/HccLocal-2026-Only-7f3b9c2d/g' .env
fi

# 2. Reset nếu có cờ --reset
if [ "$1" == "--reset" ]; then
    echo "[!] Đang xóa volumes và làm mới hoàn toàn cơ sở dữ liệu..."
    docker compose -f compose.yaml -f compose.demo.yaml down -v --remove-orphans
fi

# 3. Khởi động Docker Compose với cấu hình demo
echo "[+] Đang xây dựng và khởi động containers (DB, Mailpit, API, Web)..."
docker compose -f compose.yaml -f compose.demo.yaml up -d --build

# 4. Chờ health check
echo "[+] Đang chờ hệ thống kiểm tra tình trạng sẵn sàng (health check)..."
READY=0
for i in {1..30}; do
    if curl -s http://localhost:8080/health/ready | grep -q "Healthy"; then
        READY=1
        break
    fi
    echo "    Chờ service khởi động ($i/30)..."
    sleep 2
done

if [ "$READY" -ne 1 ]; then
    echo "[x] Lỗi: Hệ thống chưa sẵn sàng sau 60 giây. Vui lòng kiểm tra 'docker compose logs'."
    exit 1
fi

# 5. Service migrator đã hoàn tất schema trước khi API được phép khởi động.
# Chủ động chạy demo seed (idempotent) để dữ liệu luôn sẵn sàng kể cả khi volume đã tồn tại từ trước.
echo "[+] Đang đồng bộ dữ liệu demo (idempotent seed)..."
docker compose -f compose.yaml -f compose.demo.yaml exec -T api dotnet HighConcurrencyCommerce.Api.dll --seed-demo

echo ""
echo "=== HỆ THỐNG ĐÃ SẴN SÀNG ==="
echo "Storefront & Web Portal : http://localhost:8080"
echo "Mailpit Email Web UI   : http://localhost:8025"
echo "Health Check Endpoint   : http://localhost:8080/health/ready"
echo ""
echo "[!] CẢNH BÁO BẢO MẬT: Môi trường này chỉ dành cho demo/testing nội bộ."
echo "--- TÀI KHOẢN DEMO CÓ SẴN (Mật khẩu: DemoPass123!@#) ---"
echo "1. Quản trị viên (Admin) : admin@badminton.local"
echo "2. Nhân viên vận hành   : staff@badminton.local"
echo "3. Khách hàng thân thiết : customer@badminton.local"
echo ""
exit 0

