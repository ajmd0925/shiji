. (Join-Path $PSScriptRoot 'desktop-common.ps1')
$lock = Enter-DesktopLock
try {
    $state = Get-DesktopState
    $process = Get-DesktopProcess $state
    if (!$process) { Stop-DesktopPet; Write-Host 'No running Shiji desktop process belongs to this folder.'; return }
    $connection = Get-Content -LiteralPath (Join-Path $DesktopLocal 'connection.json') -Raw | ConvertFrom-Json
    Invoke-RestMethod ($connection.url + '/api/desktop/shutdown') -Method Post -Headers @{Authorization=('Bearer ' + $connection.token)} -TimeoutSec 5 | Out-Null
    if (!$process.WaitForExit(30000)) { throw 'Graceful shutdown is still pending. See .local/desktop.log. No other process was stopped.' }
    Stop-DesktopPet
    Remove-Item -LiteralPath (Join-Path $DesktopLocal 'desktop-process.json')
    Write-Host 'Shiji stopped. All local data is preserved.'
} finally { $lock.Dispose() }
