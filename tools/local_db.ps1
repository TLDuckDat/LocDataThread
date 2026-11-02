<#
.SYNOPSIS
    Quản lý Postgres local của ThreadsToxic (Docker, container threads-db, cổng 5433). Thay cho Supabase.

.DESCRIPTION
    up       Bật container và chờ Postgres nhận kết nối
    down     Dừng container (dữ liệu vẫn còn trong volume threads_pgdata)
    migrate  Chạy db/schema.sql rồi db/migrations theo thứ tự tên file (idempotent, chạy lại an toàn)
    check    Kiểm tra toàn vẹn chuỗi F0..F3 (db/checks/chain_integrity.sql), mọi dòng phải bằng 0
    backup   pg_dump schema public ra backups/local_<thời gian>.dump (định dạng custom)
    restore  Nạp dữ liệu từ một file .dump vào database đã migrate nhưng chưa có bình luận
    psql     Mở psql tương tác trong container

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\local_db.ps1 up
    powershell -ExecutionPolicy Bypass -File tools\local_db.ps1 restore backups\supabase_20261029_212657.dump
#>
param(
    [Parameter(Mandatory)]
    [ValidateSet('up', 'down', 'migrate', 'check', 'backup', 'restore', 'psql')]
    [string]$Action,
    [string]$File
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Container = 'threads-db'
$Database = 'threads_toxic'
$DbUser = 'threads'

function Invoke-Docker([string[]]$DockerArgs) {
    & docker @DockerArgs
    if ($LASTEXITCODE -ne 0) { throw "docker $($DockerArgs -join ' ') lỗi (mã $LASTEXITCODE)" }
}

function Wait-Healthy {
    for ($i = 0; $i -lt 60; $i++) {
        $status = (& docker inspect -f '{{.State.Health.Status}}' $Container 2>$null)
        if ($status -eq 'healthy') { return }
        Start-Sleep -Seconds 1
    }
    throw "Postgres trong container $Container không sẵn sàng sau 60 giây (xem: docker logs $Container)"
}

function Invoke-Psql([string[]]$PsqlArgs) {
    # Tắt NOTICE ("đã tồn tại, bỏ qua") của migration idempotent
    Invoke-Docker (@('exec', '-e', 'PGOPTIONS=-c client_min_messages=warning', $Container,
        'psql', '-X', '-v', 'ON_ERROR_STOP=1', '-q', '-U', $DbUser, '-d', $Database) + $PsqlArgs)
}

Set-Location $RepoRoot

switch ($Action) {
    'up' {
        Invoke-Docker @('compose', 'up', '-d', 'db')
        Wait-Healthy
        Write-Host "Postgres sẵn sàng ở 127.0.0.1:5433 (database $Database)." -ForegroundColor Green
    }
    'down' {
        Invoke-Docker @('compose', 'stop', 'db')
    }
    'migrate' {
        Wait-Healthy
        $scripts = @('/db/schema.sql') + (Get-ChildItem (Join-Path $RepoRoot 'db\migrations') -Filter '*.sql' |
            Sort-Object Name | ForEach-Object { "/db/migrations/$($_.Name)" })
        foreach ($script in $scripts) {
            Write-Host "-> $script"
            Invoke-Psql @('-f', $script)
        }
        Write-Host "Xong: $($scripts.Count) file." -ForegroundColor Green
    }
    'check' {
        Wait-Healthy
        Invoke-Psql @('-f', '/db/checks/chain_integrity.sql')
    }
    'backup' {
        Wait-Healthy
        $BackupDir = Join-Path $RepoRoot 'backups'
        New-Item -ItemType Directory -Force $BackupDir | Out-Null
        $Target = Join-Path $BackupDir ("local_{0}.dump" -f (Get-Date -Format 'yyyyMMdd_HHmmss'))
        Invoke-Docker @('exec', $Container, 'pg_dump', '-U', $DbUser, '-d', $Database,
            '--schema=public', '--no-owner', '--no-privileges', '-Fc', '-f', '/tmp/backup.dump')
        Invoke-Docker @('cp', "${Container}:/tmp/backup.dump", $Target)
        Invoke-Docker @('exec', $Container, 'rm', '-f', '/tmp/backup.dump')
        Write-Host ("Xong: {0} ({1:N1} MB)" -f $Target, ((Get-Item $Target).Length / 1MB)) -ForegroundColor Green
    }
    'restore' {
        if (-not $File -or -not (Test-Path $File)) { throw "Thiếu file .dump: restore -File <đường dẫn>" }
        Wait-Healthy
        # Chỉ nạp dữ liệu, vào schema đã migrate (migration giữ schema là nguồn chuẩn của repo).
        # --disable-triggers: trigger chuỗi F không được chạy lại trên dữ liệu đã đúng.
        $existing = (& docker exec $Container psql -At -U $DbUser -d $Database -c 'SELECT COUNT(*) FROM comments')
        if ($existing -ne '0') { throw "Database đã có $existing bình luận. Restore chỉ chạy trên database mới: docker exec $Container psql -U $DbUser -d postgres -c 'DROP DATABASE threads_toxic WITH (FORCE)'; rồi tạo lại và migrate." }
        Invoke-Docker @('cp', (Resolve-Path $File).Path, "${Container}:/tmp/restore.dump")
        Invoke-Docker @('exec', $Container, 'pg_restore', '--data-only', '--disable-triggers', '--no-owner', '--no-privileges',
            '-U', $DbUser, '-d', $Database, '/tmp/restore.dump')
        Invoke-Docker @('exec', $Container, 'rm', '-f', '/tmp/restore.dump')
        Write-Host "Khôi phục xong từ $File." -ForegroundColor Green
    }
    'psql' {
        Wait-Healthy
        & docker exec -it $Container psql -U $DbUser -d $Database
    }
}
