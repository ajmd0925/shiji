param([ValidateSet('local','mysql')][string]$Profile, [ValidateRange(1024,65535)][int]$Port, [switch]$NoBrowser, [switch]$SkipNotificationSetup)
. (Join-Path $PSScriptRoot 'desktop-common.ps1')
$lock = Enter-DesktopLock
try {
    $jar = Join-Path $DesktopRoot 'app\shiji.jar'
    if (!(Test-Path -LiteralPath $jar)) { throw 'No packaged app found. Download the Windows portable ZIP from Releases, or build with scripts/package-windows.ps1. See docs/distribution.md.' }
    $java = Get-DesktopJava
    if (!$java) { throw 'Java 21 runtime missing. Use the portable ZIP with bundled runtime, or set JAVA_HOME to Java 21.' }
    $settingsPath = Join-Path $DesktopLocal 'desktop.json'
    $settings = if (Test-Path -LiteralPath $settingsPath) { Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{ profile='local'; port=17861 } }
    if ($Profile) { $settings.profile = $Profile }
    if ($Port) { $settings.port = $Port }
    if ($settings.profile -notin @('local','mysql') -or $settings.port -lt 1024 -or $settings.port -gt 65535) { throw 'Invalid .local/desktop.json settings.' }
    $state = Get-DesktopState
    $running = Get-DesktopProcess $state
    if ($running) {
        if ($state.port -ne $settings.port -or $state.profile -ne $settings.profile) { throw 'Shiji is already running with other settings. Use Stop.cmd before changing profile or port.' }
        $connection = Get-Content -LiteralPath (Join-Path $DesktopLocal 'connection.json') -Raw | ConvertFrom-Json
        $status = Invoke-RestMethod ($connection.url + '/api/desktop/status') -Headers @{Authorization=('Bearer ' + $connection.token)} -TimeoutSec 5
        if ($status.application -ne 'shiji') { throw 'Running process identity check failed.' }
        if (!$NoBrowser) { Open-DesktopPage $connection }
        Write-Host "Shiji is already running at $($connection.url)."; return
    }
    if (!(Test-DesktopPort $settings.port)) { throw "Port $($settings.port) is occupied. Run scripts/start-desktop.ps1 -Port <free port>. No existing process was changed." }
    $tokenPath = Join-Path $DesktopLocal 'access-token.txt'
    if (!(Test-Path -LiteralPath $tokenPath)) {
        $bytes = New-Object byte[] 32
        $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
        [IO.File]::WriteAllText($tokenPath, ([BitConverter]::ToString($bytes).Replace('-','').ToLowerInvariant()), (New-Object Text.UTF8Encoding($false)))
    }
    $token = (Get-Content -LiteralPath $tokenPath -Raw).Trim()
    if ($token -notmatch '^[a-f0-9]{64}$') { throw 'Invalid local access token; restore .local/access-token.txt from your backup.' }
    $connection = @{ url="http://127.0.0.1:$($settings.port)"; token=$token }
    if (!$SkipNotificationSetup -and !(Test-Path -LiteralPath (Join-Path $DesktopLocal 'notification-registered'))) {
        $nativePS = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        & $nativePS -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'register-notifications.ps1') -BridgePath (Join-Path $PSScriptRoot 'windows-notifications.ps1') -AppUrl ($connection.url + '/')
        if ($LASTEXITCODE -ne 0) { throw 'Notification registration failed. See Windows notification permissions and docs/distribution.md.' }
        [IO.File]::WriteAllText((Join-Path $DesktopLocal 'notification-registered'), 'registered')
    }
    # Clear inherited developer overrides. The portable app always binds only to loopback.
    $names = @('ASSISTANT_API_TOKEN','SPRING_PROFILES_ACTIVE','SERVER_ADDRESS','SERVER_PORT','DB_URL','DB_USER','DB_PASSWORD','SPRING_DATASOURCE_URL','SPRING_DATASOURCE_USERNAME','SPRING_DATASOURCE_PASSWORD','REMINDER_CHANNEL','REMINDER_WORKER_ENABLED')
    $saved = @{}
    foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process'); [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
    try {
        $env:ASSISTANT_API_TOKEN = $token
        if ($settings.profile -eq 'mysql') {
            $mysqlPath = Join-Path $DesktopLocal 'mysql-connection.json'
            if (!(Test-Path -LiteralPath $mysqlPath)) { throw 'MySQL mode requires .local/mysql-connection.json. See docs/distribution.md. Local mode needs no MySQL installation.' }
            $mysql = Get-Content -LiteralPath $mysqlPath -Raw | ConvertFrom-Json
            $env:DB_USER = $mysql.user; $env:DB_PASSWORD = $mysql.password
            $env:DB_URL = "jdbc:mysql://127.0.0.1:$($mysql.port)/personal_assistant?useUnicode=true&characterEncoding=utf8&serverTimezone=UTC"
        }
        $arguments = @('-jar', ('"' + $jar + '"'), '--server.address=127.0.0.1', "--server.port=$($settings.port)", "--spring.profiles.active=$($settings.profile)", '--assistant.desktop.enabled=true', '--assistant.reminders.channel=SYSTEM', '--assistant.reminders.worker-enabled=true')
        $inputPath = Join-Path $DesktopLocal 'desktop-stdin.txt'
        [IO.File]::WriteAllText($inputPath, '')
        $process = Start-Process -FilePath $java -ArgumentList $arguments -WorkingDirectory $DesktopRoot -WindowStyle Hidden -PassThru -RedirectStandardInput $inputPath -RedirectStandardOutput (Join-Path $DesktopLocal 'desktop.log') -RedirectStandardError (Join-Path $DesktopLocal 'desktop-error.log')
    } finally {
        foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    }
    Write-DesktopJson (Join-Path $DesktopLocal 'desktop-process.json') @{pid=$process.Id; startedTicks=$process.StartTime.ToUniversalTime().Ticks.ToString(); port=$settings.port; profile=$settings.profile}
    Write-DesktopJson $settingsPath $settings
    Write-DesktopJson (Join-Path $DesktopLocal 'connection.json') $connection
    Write-Host 'Starting Shiji. First startup may take up to 90 seconds...'
    $ready = $false
    for ($attempt=0; $attempt -lt 90; $attempt++) {
        $process.Refresh()
        if ($process.HasExited) { throw 'Shiji exited during startup. See .local/desktop.log and desktop-error.log.' }
        try {
            $status = Invoke-RestMethod ($connection.url + '/api/desktop/status') -Headers @{Authorization=('Bearer ' + $token)} -TimeoutSec 1
            if ($status.application -eq 'shiji') { $ready = $true; break }
        } catch {}
        Start-Sleep -Seconds 1
    }
    if (!$ready) { throw 'Startup timed out. See .local/desktop.log; use Stop.cmd before retrying.' }
    if (!$NoBrowser) { Open-DesktopPage $connection }
    Write-Host "Shiji is ready at $($connection.url). Database: $($settings.profile)."
    Write-Host 'Browser and terminal may close. Keep Windows awake and signed in. Stop.cmd closes this app cleanly.'
} finally { $lock.Dispose() }
