<#
.SYNOPSIS
    Bật Postgres test trong Docker cho kiểm thử giao diện (tests/ui/e2e.py).

.DESCRIPTION
    Tạo container threads_toxic_test (cùng major version với Supabase), nạp db/schema.sql rồi
    db/migrations theo thứ tự. Container tự xóa khi dừng: docker stop threads-test-db.
    `dotnet test` không cần script này: test tích hợp tự dựng container riêng.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\start_test_db.ps1
#>
param(
    [int]$Port = 55432,
    [string]$Name = 'threads-test-db'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$DbDir = Join-Path $RepoRoot 'db'

& docker run -d --rm --name $Name -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=threads_toxic_test `
    -p "${Port}:5432" -v "${DbDir}:/db:ro" postgres:17-alpine | Out-Null
if ($LASTEXITCODE -ne 0) { throw "docker run lỗi (Docker Desktop đã bật chưa? Container '$Name' đã chạy sẵn?)" }

# Qua TCP: lúc khởi tạo, image chỉ mở unix socket rồi khởi động lại; TCP sẵn sàng mới là xong.
for ($i = 0; $i -lt 60; $i++) {
    & docker exec $Name pg_isready -h 127.0.0.1 -U postgres -d threads_toxic_test | Out-Null
    if ($LASTEXITCODE -eq 0) { break }
    Start-Sleep -Seconds 1
}
if ($LASTEXITCODE -ne 0) { throw "Postgres trong container không sẵn sàng sau 60 giây" }

$scripts = @('schema.sql') + (Get-ChildItem (Join-Path $DbDir 'migrations') -Filter '*.sql' |
    Sort-Object Name | ForEach-Object { "migrations/$($_.Name)" })
foreach ($script in $scripts) {
    & docker exec $Name psql -q -U postgres -d threads_toxic_test -v ON_ERROR_STOP=1 -c 'SET client_min_messages = warning' -f "/db/$script"
    if ($LASTEXITCODE -ne 0) { throw "Lỗi khi chạy $script" }
}

Write-Host "Postgres test sẵn sàng ở localhost:$Port. Dùng cho e2e:" -ForegroundColor Green
Write-Host "  `$env:ConnectionStrings__Default = 'Host=localhost;Port=$Port;Database=threads_toxic_test;Username=postgres;Password=postgres'"
Write-Host "  `$env:THREADS_TEST_DSN = 'host=localhost port=$Port dbname=threads_toxic_test user=postgres password=postgres'"
Write-Host "Dừng và xóa: docker stop $Name"
