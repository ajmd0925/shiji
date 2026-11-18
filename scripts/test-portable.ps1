param([Parameter(Mandatory=$true)][string]$Archive, [switch]$BrowserChecks, [switch]$PetChecks)
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
    # Diagnose against this isolated test's free port, not an existing user's 17861 service.
    New-Item -ItemType Directory -Force -Path (Join-Path $install '.local') | Out-Null
    [IO.File]::WriteAllText((Join-Path $install '.local/desktop.json'), (@{ profile='local'; port=$port } | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
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
    if ($PetChecks) {
        $petExecutable = Join-Path $install 'desktop/Shiji.Pet.exe'
        Assert-Portable ((Get-FileHash -LiteralPath $petExecutable).Hash -eq $manifest.petSha256) 'desktop pet checksum matches manifest'
        [IO.File]::WriteAllText((Join-Path $install '.local/pet-test-mode'), 'isolated installation only')
        $nativeTest = Start-Process -FilePath $petExecutable -ArgumentList @('--root', ('"' + $install + '"'), '--self-test') -WindowStyle Hidden -PassThru
        if (!$nativeTest.WaitForExit(60000)) { throw 'Native desktop API test timed out.' }
        if ($nativeTest.ExitCode -ne 0) { $nativeError = Get-Content -LiteralPath (Join-Path $install '.local/pet-error.log') -Raw -ErrorAction SilentlyContinue; throw ('Native desktop API test failed: ' + $nativeError) }
        $nativeResult = Get-Content -LiteralPath (Join-Path $install '.local/pet-self-test.json') -Raw | ConvertFrom-Json
        Assert-Portable ($nativeResult.passed -ge 8) 'native desktop API checks pass (notification readiness and actual tested branch reported below)'
        $nativeResult.checks | ForEach-Object { Write-Host ('NATIVE CHECK: ' + $_) }
        $uiTest = Start-Process -FilePath $petExecutable -ArgumentList @('--root', ('"' + $install + '"'), '--ui-test') -PassThru
        if (!$uiTest.WaitForExit(30000)) { throw 'Native UI route test timed out.' }
        $uiError = Join-Path $install '.local/pet-ui-error.log'
        if (Test-Path -LiteralPath $uiError) { throw ('Native UI route test failed: ' + [IO.File]::ReadAllText($uiError)) }
        $uiResult = Get-Content -LiteralPath (Join-Path $install '.local/pet-ui-test.json') -Raw | ConvertFrom-Json
        Assert-Portable ($uiTest.ExitCode -eq 0 -and $uiResult.passed -eq 8) 'compact menu routes, operation windows and dismissal pass native UI checks'
        $uiResult.checks | ForEach-Object { Write-Host ('UI CHECK: ' + $_) }
        $null = Run-Launcher 'start-desktop.ps1' @('-SkipNotificationSetup')
        $instanceHash = [Security.Cryptography.SHA256]::Create()
        try { $instance = [BitConverter]::ToString($instanceHash.ComputeHash([Text.Encoding]::UTF8.GetBytes($install.ToLowerInvariant()))).Replace('-','').ToLowerInvariant() }
        finally { $instanceHash.Dispose() }
        $petShow = $null
        for ($attempt=0; $attempt -lt 30; $attempt++) {
            try { $petShow = [Threading.EventWaitHandle]::OpenExisting('Local\Shiji.Pet.Show.' + $instance); break } catch [Threading.WaitHandleCannotBeOpenedException] { Start-Sleep -Milliseconds 300 }
        }
        Assert-Portable ([bool]$petShow) 'default launcher opens native transparent desktop pet'
        $beforePet = @(Get-CimInstance Win32_Process -Filter "Name='Shiji.Pet.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($install) })
        $null = Run-Launcher 'start-desktop.ps1' @('-SkipNotificationSetup')
        $afterPet = @(Get-CimInstance Win32_Process -Filter "Name='Shiji.Pet.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($install) })
        Assert-Portable ($beforePet.Count -eq 1 -and $afterPet.Count -eq 1 -and $beforePet[0].ProcessId -eq $afterPet[0].ProcessId) 'repeated start shows existing pet without duplicates'
        Start-Sleep -Milliseconds 1300
        $windowReport = Join-Path $install '.local/pet-window-test.json'
        for ($attempt=0; $attempt -lt 30 -and !(Test-Path -LiteralPath $windowReport); $attempt++) { Start-Sleep -Milliseconds 300 }
        if (!(Test-Path -LiteralPath $windowReport)) { throw 'Pet did not report a visible window after repeated start.' }
        $windowState = Get-Content -LiteralPath $windowReport -Raw | ConvertFrom-Json
        Assert-Portable ($windowState.petVisible -and $windowState.menuVisible -and !$windowState.panelVisible -and $windowState.petSize -eq 144 -and $windowState.menuItems -eq 4 -and $windowState.menuWidth -le 210 -and $windowState.menuHeight -le 220) 'repeated start shows compact four-option menu without a dashboard'
        if ($petShow) { $petShow.Dispose() }
        $null = Run-Launcher 'stop-desktop.ps1'
        $petProcess = Get-Process -Id $beforePet[0].ProcessId -ErrorAction SilentlyContinue
        if ($petProcess) { $null = $petProcess.WaitForExit(10000) }
        Assert-Portable (!(Get-Process -Id $beforePet[0].ProcessId -ErrorAction SilentlyContinue)) 'Stop.cmd closes pet and backend together'
    } else { $null = Run-Launcher 'stop-desktop.ps1' }
    Write-Host "Portable smoke test: $script:TestCount checks passed. Isolated installation: $install"
    Write-Host 'Notification banner and first registration on a new Windows account require an interactive desktop.'
} finally {
    if (Test-Path -LiteralPath (Join-Path $install '.local/desktop-process.json')) {
        try { $null = Run-Launcher 'stop-desktop.ps1' } catch { Write-Warning 'Test app could not stop gracefully; inspect its local logs.' }
    }
    foreach ($key in $envKeys) { [Environment]::SetEnvironmentVariable($key,$saved[$key]) }
}
