<#
.SYNOPSIS
    Đưa app ra internet qua Cloudflare Tunnel, máy này làm máy chủ. Không mở cổng nào trên router.

.DESCRIPTION
    Chạy app trước, trên cùng máy, ở cổng 5000:
        cd src\ThreadsToxic.Web
        dotnet run

    Sau đó chạy script này. Có hai chế độ:
    - Tunnel cố định (có tên miền riêng): đặt CLOUDFLARE_TUNNEL_TOKEN trong .env. Tạo tunnel trên
      Cloudflare Zero Trust > Networks > Tunnels, thêm Public hostname trỏ tới http://localhost:5000.
    - Tunnel tạm (không cần tài khoản): không có token thì dùng địa chỉ *.trycloudflare.com ngẫu nhiên,
      đổi mỗi lần chạy.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\tunnel.ps1
#>
param(
    [int]$Port = 5000
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Find-Cloudflared {
    $cmd = Get-Command cloudflared -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $installed = Join-Path ${env:ProgramFiles(x86)} 'cloudflared\cloudflared.exe'
    if (Test-Path $installed) { return $installed }
    throw "Chưa có cloudflared. Cài bằng: winget install --id Cloudflare.cloudflared"
}

function Read-Token {
    $envFile = Join-Path $RepoRoot '.env'
    if (-not (Test-Path $envFile)) { return '' }
    $line = Get-Content $envFile | Where-Object { $_ -match '^CLOUDFLARE_TUNNEL_TOKEN=' } | Select-Object -First 1
    if (-not $line) { return '' }
    ($line -replace '^CLOUDFLARE_TUNNEL_TOKEN=', '').Trim().Trim('"', "'")
}

function Test-AppListening {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect('127.0.0.1', $Port)
        return $true
    } catch {
        return $false
    } finally {
        $client.Dispose()
    }
}

$exe = Find-Cloudflared
if (-not (Test-AppListening)) {
    throw "Chưa thấy app ở 127.0.0.1:$Port. Chạy app trước: cd src\ThreadsToxic.Web; dotnet run"
}

$token = Read-Token
if ($token) {
    Write-Host "Tunnel cố định (CLOUDFLARE_TUNNEL_TOKEN): đường vào là hostname bạn đã cấu hình trên Cloudflare." -ForegroundColor Green
    $env:TUNNEL_TOKEN = $token
    & $exe tunnel run
} else {
    Write-Host "Tunnel tạm: địa chỉ *.trycloudflare.com in ra bên dưới (đổi mỗi lần chạy)." -ForegroundColor Yellow
    & $exe tunnel --url "http://localhost:$Port"
}
exit $LASTEXITCODE
