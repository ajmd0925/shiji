# Compatible with Windows PowerShell 5.1. No global settings or PATH changes.
$ErrorActionPreference = 'Stop'
$script:DesktopRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$script:DesktopLocal = Join-Path $DesktopRoot '.local'

function Write-DesktopJson($Path, $Value) {
    $temp = "$Path.tmp"
    [IO.File]::WriteAllText($temp, ($Value | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temp -Destination $Path -Force
}

function Get-DesktopFileHash([string]$LiteralPath) {
    $hash = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($LiteralPath)
    try { return [pscustomobject]@{Hash=[BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','').ToLowerInvariant()} }
    finally { $stream.Dispose(); $hash.Dispose() }
}

function Test-DesktopJavaCandidate([string]$Candidate) {
        if (!(Test-Path -LiteralPath $Candidate)) { return $false }
        $info = New-Object Diagnostics.ProcessStartInfo
        $info.FileName = $Candidate; $info.Arguments = '--version'
        $info.UseShellExecute = $false; $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
        $probe = New-Object Diagnostics.Process
        $probe.StartInfo = $info
        try {
            $null = $probe.Start()
            $stdout = $probe.StandardOutput.ReadToEndAsync()
            $stderr = $probe.StandardError.ReadToEndAsync()
            if (!$probe.WaitForExit(10000)) { $probe.Kill(); throw 'Java version check timed out.' }
            $version = $stdout.Result
            if ($probe.ExitCode -eq 0 -and $version -match '(?m)^(?:openjdk|java) 21[.\s\-]') { return $true }
            $script:DesktopJavaError = "Java probe failed: exit=$($probe.ExitCode); stdout=$version; stderr=$($stderr.Result)"
        } catch { $script:DesktopJavaError = $_.Exception.Message }
        finally { $probe.Dispose() }
        return $false
}

function Get-DesktopJava {
    $bundled = Join-Path $DesktopRoot 'runtime\bin\java.exe'
    if ($DesktopRoot -match '[^\x00-\x7f]' -and (Test-Path -LiteralPath $bundled)) {
        $runtimeDir = Join-Path $DesktopRoot 'runtime'
        $runtimeId = (Get-DesktopFileHash -LiteralPath (Join-Path $runtimeDir 'release')).Hash
        $cacheRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Shiji\runtimes'
        $cache = Join-Path $cacheRoot $runtimeId
        $cachedJava = Join-Path $cache 'bin\java.exe'
        if (!(Test-Path -LiteralPath $cachedJava)) {
            New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null
            $temporary = Join-Path $cacheRoot ([guid]::NewGuid().ToString() + '.tmp')
            Copy-Item -LiteralPath $runtimeDir -Destination $temporary -Recurse
            try { Move-Item -LiteralPath $temporary -Destination $cache }
            catch { if (!(Test-Path -LiteralPath $cachedJava)) { throw } }
        }
        if (Test-DesktopJavaCandidate $cachedJava) { return $cachedJava }
    }
    $candidates = @($bundled)
    if ($env:JAVA_HOME) { $candidates += [IO.Path]::Combine($env:JAVA_HOME, 'bin\java.exe') }
    $command = Get-Command java.exe -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (Test-DesktopJavaCandidate $candidate) { return $candidate }
        if (!(Test-Path -LiteralPath $candidate)) { continue }
        try {
            $fileSystem = New-Object -ComObject Scripting.FileSystemObject
            $short = $fileSystem.GetFile($candidate).ShortPath
            if ($short -ne $candidate -and (Test-DesktopJavaCandidate $short)) { return $short }
        } catch {}
        # The Java Windows launcher may use the system ANSI code page to locate java.dll.
        # English Windows can therefore fail in a Chinese path, even though the JVM supports it.
        if ($candidate -eq $bundled -and $script:DesktopJavaError -match 'could not find java.dll') {
            try {
                $runtimeDir = Join-Path $DesktopRoot 'runtime'
                $runtimeId = (Get-DesktopFileHash -LiteralPath (Join-Path $runtimeDir 'release')).Hash
                $cacheRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Shiji\runtimes'
                $cache = Join-Path $cacheRoot $runtimeId
                $cachedJava = Join-Path $cache 'bin\java.exe'
                if (!(Test-Path -LiteralPath $cachedJava)) {
                    New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null
                    $temporary = Join-Path $cacheRoot ([guid]::NewGuid().ToString() + '.tmp')
                    Copy-Item -LiteralPath $runtimeDir -Destination $temporary -Recurse
                    try { Move-Item -LiteralPath $temporary -Destination $cache }
                    catch { if (!(Test-Path -LiteralPath $cachedJava)) { throw } }
                }
                if (Test-DesktopJavaCandidate $cachedJava) { return $cachedJava }
                $short = $fileSystem.GetFile($cachedJava).ShortPath
                if (Test-DesktopJavaCandidate $short) { return $short }
            } catch { $script:DesktopJavaError = $_.Exception.Message }
            $script:DesktopJavaError += ' Try extracting to an ASCII-only folder if your Windows profile also contains unsupported characters.'
        }
    }
    return $null
}

function Get-DesktopLaunchJar([string]$Jar) {
    if ($DesktopRoot -notmatch '[^\x00-\x7f]') { return $Jar }
    # Java's Windows native launcher also narrows its working directory and JAR path.
    # Only immutable program files are cached; the database remains in DesktopLocal.
    $cache = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Shiji\instances\' + (Get-DesktopInstance))
    $appDir = Join-Path $cache 'app'
    New-Item -ItemType Directory -Force -Path $appDir | Out-Null
    $cachedJar = Join-Path $appDir 'shiji.jar'
    if (!(Test-Path -LiteralPath $cachedJar) -or (Get-DesktopFileHash -LiteralPath $cachedJar).Hash -ne (Get-DesktopFileHash -LiteralPath $Jar).Hash) {
        Copy-Item -LiteralPath $Jar -Destination $cachedJar -Force
    }
    return $cachedJar
}

function Enter-DesktopLock {
    New-Item -ItemType Directory -Force -Path $DesktopLocal | Out-Null
    try { return [IO.File]::Open((Join-Path $DesktopLocal 'desktop.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
    catch { throw 'Another Shiji start/stop operation is running. Retry when it finishes.' }
}

function Get-DesktopInstance {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($DesktopRoot.ToLowerInvariant()))).Replace('-','').ToLowerInvariant() }
    finally { $hash.Dispose() }
}

function Get-DesktopProcess($State) {
    if (!$State -or !$State.pid) { return $null }
    $process = Get-Process -Id $State.pid -ErrorAction SilentlyContinue
    if (!$process) { return $null }
    # PID reuse must never cause another application's Java process to be stopped.
    if ($process.StartTime.ToUniversalTime().Ticks.ToString() -ne [string]$State.startedTicks) { return $null }
    $native = Get-CimInstance Win32_Process -Filter "ProcessId=$($State.pid)"
    $jar = Join-Path $DesktopRoot 'app\shiji.jar'
    if (!$native.CommandLine) { return $null }
    if ($State.instance) {
        if ($State.instance -ne (Get-DesktopInstance) -or !$native.CommandLine.Contains("-Dshiji.desktop.instance=$($State.instance)")) { return $null }
    } elseif (!$native.CommandLine.Contains($jar)) { return $null }
    return $process
}

function Get-DesktopState {
    $path = Join-Path $DesktopLocal 'desktop-process.json'
    if (Test-Path -LiteralPath $path) { return Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json }
    return $null
}

function Test-DesktopPort([int]$Port) {
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, $Port)
    try { $listener.Start(); return $true } catch { return $false } finally { $listener.Stop() }
}

function Start-DesktopPet {
    $executable = Join-Path $DesktopRoot 'desktop\Shiji.Pet.exe'
    if (!(Test-Path -LiteralPath $executable)) { throw 'Desktop pet is missing. Use OpenBrowser.cmd for the web panel or download the complete Windows package.' }
    $pet = Start-Process -FilePath $executable -ArgumentList @('--root', ('"' + $DesktopRoot + '"')) -WindowStyle Normal -PassThru
    if ($pet.WaitForExit(1500) -and $pet.ExitCode -ne 0) { throw 'Desktop pet failed to start. See .local/pet-error.log or use OpenBrowser.cmd.' }
}

function Stop-DesktopPet {
    $signal = $null
    try {
        $signal = [Threading.EventWaitHandle]::OpenExisting('Local\Shiji.Pet.Stop.' + (Get-DesktopInstance))
        $null = $signal.Set()
    } catch [Threading.WaitHandleCannotBeOpenedException] {} finally { if ($signal) { $signal.Dispose() } }
}

function Open-DesktopPage($Connection) {
    # A fresh browser tab connects without copying the secret. Do not print this URL in logs.
    Start-Process -FilePath ($Connection.url + '/#shiji-token=' + $Connection.token)
}
