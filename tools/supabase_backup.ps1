<#
.SYNOPSIS
    Sao lưu database Supabase của app về backups/ (gói Free không có backup tự động).

.DESCRIPTION
    Đọc thông tin kết nối từ .env ở gốc repo, pg_dump schema public (cấu trúc + dữ liệu) ra
    backups/supabase_<thời gian>.dump (định dạng custom, đã gitignore). Chỉ đọc, không sửa Supabase.

    Khôi phục vào một database Postgres trống:
        pg_restore --no-owner --no-privileges -d "<chuỗi kết nối>" backups\supabase_<...>.dump

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\supabase_backup.ps1
#>

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'supabase_env.ps1')

$BackupDir = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'backups'
New-Item -ItemType Directory -Force $BackupDir | Out-Null
$Target = Join-Path $BackupDir ("supabase_{0}.dump" -f (Get-Date -Format 'yyyyMMdd_HHmmss'))

# Session pooler thỉnh thoảng cắt kết nối giữa lúc COPY bảng lớn ("SSL connection has been
# closed unexpectedly"); thử lại vài lần thay vì bỏ cả bản sao lưu.
$MaxAttempts = 3
Invoke-WithSupabaseEnv {
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        Write-Host "Sao lưu $env:PGHOST (lần $attempt/$MaxAttempts) ..."
        & pg_dump -w --schema=public --no-owner --no-privileges -Fc -f $Target
        if ($LASTEXITCODE -eq 0) { return }
        Remove-Item $Target -ErrorAction SilentlyContinue
        if ($attempt -lt $MaxAttempts) { Start-Sleep -Seconds 5 }
    }
    throw "pg_dump lỗi sau $MaxAttempts lần thử, mã $LASTEXITCODE"
}

Write-Host ("Xong: {0} ({1:N1} MB)" -f $Target, ((Get-Item $Target).Length / 1MB)) -ForegroundColor Green
