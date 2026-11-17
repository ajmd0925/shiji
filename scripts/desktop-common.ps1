# Compatible with Windows PowerShell 5.1. No global settings or PATH changes.
$ErrorActionPreference = 'Stop'
$script:DesktopRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$script:DesktopLocal = Join-Path $DesktopRoot '.local'

function Write-DesktopJson($Path, $Value) {
    $temp = "$Path.tmp"
    [IO.File]::WriteAllText($temp, ($Value | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temp -Destination $Path -Force
}

function Get-DesktopJava {
    $candidates = @((Join-Path $DesktopRoot 'runtime\bin\java.exe'))
    if ($env:JAVA_HOME) { $candidates += [IO.Path]::Combine($env:JAVA_HOME, 'bin\java.exe') }
    $command = Get-Command java.exe -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (!(Test-Path -LiteralPath $candidate)) { continue }
        $info = New-Object Diagnostics.ProcessStartInfo
        $info.FileName = $candidate; $info.Arguments = '--version'
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
            if ($probe.ExitCode -eq 0 -and $version -match '(?m)^(?:openjdk|java) 21[.\s\-]') { return $candidate }
            $script:DesktopJavaError = "Java probe failed: exit=$($probe.ExitCode); stdout=$version; stderr=$($stderr.Result)"
        } catch { $script:DesktopJavaError = $_.Exception.Message }
        finally { $probe.Dispose() }
    }
    return $null
}

function Enter-DesktopLock {
    New-Item -ItemType Directory -Force -Path $DesktopLocal | Out-Null
    try { return [IO.File]::Open((Join-Path $DesktopLocal 'desktop.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
    catch { throw 'Another Shiji start/stop operation is running. Retry when it finishes.' }
}

function Get-DesktopProcess($State) {
    if (!$State -or !$State.pid) { return $null }
    $process = Get-Process -Id $State.pid -ErrorAction SilentlyContinue
    if (!$process) { return $null }
    # PID reuse must never cause another application's Java process to be stopped.
    if ($process.StartTime.ToUniversalTime().Ticks.ToString() -ne [string]$State.startedTicks) { return $null }
    $native = Get-CimInstance Win32_Process -Filter "ProcessId=$($State.pid)"
    $jar = Join-Path $DesktopRoot 'app\shiji.jar'
    if (!$native.CommandLine -or !$native.CommandLine.Contains($jar)) { return $null }
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

function Open-DesktopPage($Connection) {
    # A fresh browser tab connects without copying the secret. Do not print this URL in logs.
    Start-Process -FilePath ($Connection.url + '/#shiji-token=' + $Connection.token)
}
