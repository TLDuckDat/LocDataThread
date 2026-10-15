<#
.SYNOPSIS
    Chạy psql tới database Supabase bằng thông tin trong .env. Tham số được chuyển thẳng cho psql.

.EXAMPLE
    # Áp dụng một migration
    powershell -ExecutionPolicy Bypass -File tools\supabase_psql.ps1 -v ON_ERROR_STOP=1 -f db\migrations\006_review_incomplete.sql

.EXAMPLE
    # Mở phiên tương tác
    powershell -ExecutionPolicy Bypass -File tools\supabase_psql.ps1
#>

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'supabase_env.ps1')

$psqlArgs = $args
Invoke-WithSupabaseEnv { & psql -X @psqlArgs }
exit $LASTEXITCODE
