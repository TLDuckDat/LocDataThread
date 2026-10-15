# Dùng chung cho tools\supabase_backup.ps1 và tools\supabase_psql.ps1 (dot-source, không chạy trực tiếp):
# đọc .env ở gốc repo và chạy lệnh với biến môi trường PG* trỏ tới Supabase.
# Mật khẩu đi qua PGPASSWORD trong lúc chạy lệnh, không nằm trên dòng lệnh, xong thì trả lại như cũ.

$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Read-DotEnv([string]$Path) {
    $values = @{}
    foreach ($line in [IO.File]::ReadAllLines($Path, $Utf8NoBom)) {
        $trimmed = $line.Trim()
        if ($trimmed -eq '' -or $trimmed.StartsWith('#')) { continue }
        $eq = $trimmed.IndexOf('=')
        if ($eq -lt 1) { continue }
        $value = $trimmed.Substring($eq + 1).Trim()
        if ($value.Length -ge 2 -and (($value.StartsWith('"') -and $value.EndsWith('"')) -or
                                      ($value.StartsWith("'") -and $value.EndsWith("'")))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $values[$trimmed.Substring(0, $eq).Trim()] = $value
    }
    $values
}

function Invoke-WithSupabaseEnv([scriptblock]$Script, [string]$EnvFile = (Join-Path $PSScriptRoot '..\.env')) {
    if (-not (Test-Path $EnvFile)) { throw "Không thấy $EnvFile (chép từ .env.example rồi điền)." }
    $envValues = Read-DotEnv $EnvFile
    foreach ($key in 'SUPABASE_DB_HOST', 'SUPABASE_DB_USER', 'SUPABASE_DB_PASSWORD') {
        if (-not $envValues[$key]) { throw "Thiếu $key trong $EnvFile" }
    }

    $pgEnv = @{
        PGHOST            = $envValues['SUPABASE_DB_HOST']
        PGPORT            = $(if ($envValues['SUPABASE_DB_PORT']) { $envValues['SUPABASE_DB_PORT'] } else { '5432' })
        PGDATABASE        = $(if ($envValues['SUPABASE_DB_NAME']) { $envValues['SUPABASE_DB_NAME'] } else { 'postgres' })
        PGUSER            = $envValues['SUPABASE_DB_USER']
        PGPASSWORD        = $envValues['SUPABASE_DB_PASSWORD']
        PGSSLMODE         = $(if ($envValues['SUPABASE_DB_SSLMODE']) { $envValues['SUPABASE_DB_SSLMODE'] } else { 'require' })
        PGCONNECT_TIMEOUT = '15'
    }
    $saved = @{}
    foreach ($key in $pgEnv.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $pgEnv[$key])
    }
    try { & $Script }
    finally {
        foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    }
}
