param([Parameter(Mandatory=$true)][string]$Archive, [switch]$BrowserChecks)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$archivePath = (Resolve-Path -LiteralPath $Archive).Path
$testRoot = Join-Path $root ('.local\portable-tests\' + [guid]::NewGuid().ToString() + '\全新安装 📝 with spaces')
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
Expand-Archive -LiteralPath $archivePath -DestinationPath $testRoot
$install = (Get-ChildItem -LiteralPath $testRoot -Directory | Select-Object -First 1).FullName
$nativePS = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$testNode = if ($BrowserChecks) { (Get-Command node.exe -ErrorAction Stop).Source } else { $null }
$envKeys = @('PATH','JAVA_HOME','ASSISTANT_API_TOKEN','SPRING_PROFILES_ACTIVE','SERVER_PORT','DB_URL','DB_PASSWORD','TEST_DB_URL','TEST_DB_USER','TEST_DB_PASSWORD')
$saved = @{}
foreach ($key in $envKeys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key) }
$portProbe = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0)
$portProbe.Start(); $port = $portProbe.LocalEndpoint.Port; $portProbe.Stop()
$script:TestCount = 0
function Assert-Portable($Condition, $Message) {
    if (!$Condition) { throw "FAILED: $Message" }
    $script:TestCount++; Write-Host "PASS: $Message"
}
function Run-Launcher([string]$Script, [string[]]$Arguments=@(), [bool]$ExpectSuccess=$true) {
    $capture = Join-Path $testRoot ([guid]::NewGuid().ToString())
    $launcherArguments = @('-NoLogo','-NoProfile','-ExecutionPolicy','Bypass','-File', ('"' + (Join-Path $install "scripts\$Script") + '"')) + $Arguments
    $child = Start-Process -FilePath $nativePS -ArgumentList $launcherArguments -PassThru -WindowStyle Hidden -RedirectStandardOutput ($capture + '.out') -RedirectStandardError ($capture + '.err')
    # Wait for the launcher itself, never for its intentionally persistent Java descendant.
    if (!$child.WaitForExit(150000)) { throw "$Script timed out; inspect the isolated installation logs." }
    $code = $child.ExitCode
    $output = ''
    foreach ($capturePath in @(($capture + '.out'),($capture + '.err'))) {
        $stream = [IO.File]::Open($capturePath, 'Open', 'Read', 'ReadWrite')
        $reader = New-Object IO.StreamReader($stream)
        try { $output += $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    if ($ExpectSuccess -and $code -ne 0) { throw "$Script failed ($code): $output" }
    if (!$ExpectSuccess -and $code -eq 0) { throw "$Script unexpectedly succeeded: $output" }
    return $output
}
function Get-StatusCode([string]$Path,[string]$Method='GET',[string]$Auth='') {
    $requestHeaders = @{}
    if ($Auth) { $requestHeaders.Authorization = $Auth }
    try { return [int](Invoke-WebRequest -UseBasicParsing -Uri ($script:BaseUrl + $Path) -Method $Method -Headers $requestHeaders -TimeoutSec 10).StatusCode }
    catch { if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }; throw }
}
try {
    $env:PATH = "$env:WINDIR\System32;$env:WINDIR\System32\WindowsPowerShell\v1.0"
    $env:JAVA_HOME = 'X:\intentionally-missing-jdk'
    $env:ASSISTANT_API_TOKEN = 'inherited-token-must-not-be-used'
    $env:SERVER_PORT = '1'
    $env:DB_URL = 'jdbc:mysql://invalid.example:1/do-not-use'
    $env:DB_PASSWORD = 'inherited-password-must-not-be-used'
    Assert-Portable (!(Test-Path -LiteralPath (Join-Path $install '.local'))) 'archive has no user data'
    $manifest = Get-Content -LiteralPath (Join-Path $install 'manifest.json') -Raw | ConvertFrom-Json
    Assert-Portable ((Get-FileHash -LiteralPath (Join-Path $install 'app/shiji.jar')).Hash -eq $manifest.jarSha256) 'application checksum matches manifest'
    Assert-Portable (!(Get-Command node.exe,mvn.cmd,java.exe -ErrorAction SilentlyContinue)) 'system Node, Maven and Java are absent from test PATH'
    Rename-Item -LiteralPath (Join-Path $install 'runtime') -NewName 'runtime-disabled'
    try {
        $diagnosis = Run-Launcher 'check-environment.ps1' @('-AsJson') $false
        Assert-Portable ($diagnosis -match 'Java 21') 'missing runtime is diagnosed'
    }
    finally { Rename-Item -LiteralPath (Join-Path $install 'runtime-disabled') -NewName 'runtime' }
    $null = Run-Launcher 'check-environment.ps1' @('-AsJson')
    Assert-Portable $true 'environment check works with bundled runtime only'
    $null = Run-Launcher 'start-desktop.ps1' @('-NoBrowser','-SkipNotificationSetup','-Port',"$port")
    $connection = Get-Content -LiteralPath (Join-Path $install '.local/connection.json') -Raw | ConvertFrom-Json
    $script:BaseUrl = $connection.url
    $headers = @{Authorization=('Bearer ' + $connection.token)}
    Assert-Portable ($connection.token -match '^[a-f0-9]{64}$') 'first run generates fresh random secret'
    Assert-Portable ((Get-StatusCode '/') -eq 200) 'packaged Vue entry is public'
    $html = (Invoke-WebRequest -UseBasicParsing ($BaseUrl + '/')).Content
    $asset = [regex]::Match($html, 'src="([^"]+\.js)"').Groups[1].Value
    Assert-Portable ($asset -and (Get-StatusCode $asset) -eq 200) 'compiled JavaScript asset loads'
    Assert-Portable ((Get-StatusCode '/api/tasks') -eq 401) 'private API requires token'
    Assert-Portable ((Get-StatusCode '/api/desktop/shutdown' 'POST') -eq 401) 'shutdown requires token'
    $task = Invoke-RestMethod ($BaseUrl + '/api/tasks') -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes('{"title":"全新安装持久化测试","description":"portable test","priority":"NORMAL","completed":false}'))
    Assert-Portable ([bool]$task.id) 'create task in clean local database'
    Assert-Portable (Test-Path -LiteralPath (Join-Path $install '.local/assistant.mv.db')) 'database stays in the original Unicode installation folder'
    $statePath = Join-Path $install '.local/desktop-process.json'
    $firstState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    $null = Run-Launcher 'start-desktop.ps1' @('-NoBrowser','-SkipNotificationSetup')
    $secondState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    Assert-Portable ($firstState.pid -eq $secondState.pid) 'repeated start reuses same process'
    $null = Run-Launcher 'stop-desktop.ps1'
    Assert-Portable (!(Get-Process -Id $firstState.pid -ErrorAction SilentlyContinue)) 'stop waits for graceful exit'
    # Do not let an occupied unrelated port get silently reused or killed.
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,$port)
    $listener.Start()
    try {
        $null = Run-Launcher 'start-desktop.ps1' @('-NoBrowser','-SkipNotificationSetup') $false
        Assert-Portable $true 'occupied port fails without stopping the existing listener'
    } finally { $listener.Stop() }
    $null = Run-Launcher 'start-desktop.ps1' @('-NoBrowser','-SkipNotificationSetup')
    $tasks = @(Invoke-RestMethod ($BaseUrl + '/api/tasks') -Headers $headers)
    Assert-Portable (@($tasks | Where-Object { $_.id -eq $task.id }).Count -eq 1) 'task persists after stop and restart'
    Invoke-RestMethod ($BaseUrl + '/api/tasks/' + $task.id) -Method Delete -Headers $headers | Out-Null
    $notification = Invoke-RestMethod ($BaseUrl + '/api/notifications/status') -Headers $headers
    Assert-Portable ($notification.channel -eq 'SYSTEM') 'portable worker uses system notification channel'
    if ($BrowserChecks) {
        & $testNode (Join-Path $PSScriptRoot 'test-portable-browser.mjs') $install
        if ($LASTEXITCODE -ne 0) { throw 'Portable browser check failed.' }
        Assert-Portable $true 'browser auto-connects and removes launch secret from address bar'
    }
    $null = Run-Launcher 'stop-desktop.ps1'
    Write-Host "Portable smoke test: $script:TestCount checks passed. Isolated installation: $install"
    Write-Host 'Notification banner and first registration on a new Windows account require an interactive desktop.'
} finally {
    if (Test-Path -LiteralPath (Join-Path $install '.local/desktop-process.json')) {
        try { $null = Run-Launcher 'stop-desktop.ps1' } catch { Write-Warning 'Test app could not stop gracefully; inspect its local logs.' }
    }
    foreach ($key in $envKeys) { [Environment]::SetEnvironmentVariable($key,$saved[$key]) }
}
