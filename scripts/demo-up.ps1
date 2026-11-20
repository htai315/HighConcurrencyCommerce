# scripts/demo-up.ps1
# Dựng trọn bộ hệ thống HighConcurrencyCommerce và seed dữ liệu demo trong một lệnh duy nhất.
param (
    [switch]$Reset = $false
)

$ErrorActionPreference = "Stop"

Write-Host "=== HighConcurrencyCommerce - Khởi động Demo ===" -ForegroundColor Cyan

# 1. Kiểm tra file .env
if (-not (Test-Path ".env")) {
    Write-Host "[+] Tạo file .env từ .env.example..." -ForegroundColor Yellow
    Copy-Item ".env.example" ".env"
    (Get-Content ".env") -replace "replace-with-a-long-random-password", "HccLocal-2026-Only-7f3b9c2d" | Set-Content ".env"
}

# 2. Reset volume nếu được yêu cầu
if ($Reset) {
    Write-Host "[!] Đang xóa volumes và làm mới hoàn toàn cơ sở dữ liệu..." -ForegroundColor Red
    docker compose -f compose.yaml -f compose.demo.yaml down -v --remove-orphans
}

# 3. Khởi động Docker Compose với cấu hình demo
Write-Host "[+] Đang xây dựng và khởi động containers (DB, Mailpit, API, Web)..." -ForegroundColor Yellow
docker compose -f compose.yaml -f compose.demo.yaml up -d --build

# 4. Chờ API và Web sẵn sàng qua Health Check
Write-Host "[+] Đang chờ hệ thống kiểm tra tình trạng sẵn sàng (health check)..." -ForegroundColor Yellow
$maxRetries = 30
$retryCount = 0
$isHealthy = $false

while ($retryCount -lt $maxRetries) {
    Start-Sleep -Seconds 2
    $retryCount++
    try {
        $response = Invoke-RestMethod -Uri "http://localhost:8080/health/ready" -Method Get -TimeoutSec 3 -ErrorAction SilentlyContinue
        if ($response.status -eq "Healthy") {
            $isHealthy = $true
            break
        }
    } catch {
        Write-Host "    Chờ service khởi động ($retryCount/$maxRetries)..." -ForegroundColor DarkGray
    }
}

if (-not $isHealthy) {
    Write-Host "[x] Lỗi: Hệ thống chưa sẵn sàng sau 60 giây. Vui lòng kiểm tra log bằng 'docker compose logs'." -ForegroundColor Red
    exit 1
}

# 5. Service migrator đã hoàn tất schema trước khi API được phép khởi động.
# Chủ động chạy demo seed (idempotent) để dữ liệu luôn sẵn sàng kể cả khi volume đã tồn tại từ trước.
Write-Host "[+] Đang đồng bộ dữ liệu demo (idempotent seed)..." -ForegroundColor Yellow
docker compose -f compose.yaml -f compose.demo.yaml exec -T api dotnet HighConcurrencyCommerce.Api.dll --seed-demo

Write-Host ""
Write-Host "=== HỆ THỐNG ĐÃ SẴN SÀNG ===" -ForegroundColor Green
Write-Host "Storefront & Web Portal : http://localhost:8080" -ForegroundColor White
Write-Host "Mailpit Email Web UI   : http://localhost:8025" -ForegroundColor White
Write-Host "Health Check Endpoint   : http://localhost:8080/health/ready" -ForegroundColor White
Write-Host ""
Write-Host "[!] CẢNH BÁO BẢO MẬT: Môi trường này chỉ dành cho demo/testing nội bộ." -ForegroundColor Yellow
Write-Host "--- TÀI KHOẢN DEMO CÓ SẴN (Mật khẩu: DemoPass123!@#) ---" -ForegroundColor Cyan
Write-Host "1. Quản trị viên (Admin) : admin@badminton.local" -ForegroundColor White
Write-Host "2. Nhân viên vận hành   : staff@badminton.local" -ForegroundColor White
Write-Host "3. Khách hàng thân thiết : customer@badminton.local" -ForegroundColor White
Write-Host ""
Write-Host "Dừng hệ thống: docker compose -f compose.yaml -f compose.demo.yaml down" -ForegroundColor DarkGray
Write-Host "Dừng và xóa toàn bộ dữ liệu: .\scripts\demo-up.ps1 -Reset" -ForegroundColor DarkGray

