param([switch]$AsJson)
. (Join-Path $PSScriptRoot 'desktop-common.ps1')
$checks = New-Object 'Collections.Generic.List[object]'
function Add-Check($Name, $Ok, $Detail) { $checks.Add([pscustomobject]@{ name=$Name; ok=[bool]$Ok; detail=$Detail }) }
Add-Check 'Windows x64' ([Environment]::OSVersion.Platform -eq 'Win32NT' -and [Environment]::Is64BitOperatingSystem -and $env:PROCESSOR_ARCHITECTURE -ne 'ARM64') 'The release supports Windows 10/11 x64.'
Add-Check 'PowerShell 5.1+' ($PSVersionTable.PSVersion -ge [version]'5.1') $PSVersionTable.PSVersion.ToString()
$java = Get-DesktopJava
Add-Check 'Java 21' ([bool]$java) $(if ($java) { $java } else { 'Use the Windows portable ZIP (bundled runtime), or set JAVA_HOME to Java 21. ' + $script:DesktopJavaError })
Add-Check 'Application package' (Test-Path -LiteralPath (Join-Path $DesktopRoot 'app\shiji.jar')) 'Source ZIP is not the portable package. Build with scripts/package-windows.ps1, or download a Release asset.'
$writable = $false
try {
    New-Item -ItemType Directory -Force -Path $DesktopLocal | Out-Null
    $probe = Join-Path $DesktopLocal ([guid]::NewGuid().ToString() + '.probe')
    [IO.File]::WriteAllText($probe, 'check'); Remove-Item -LiteralPath $probe
    $writable = $true
} catch {}
Add-Check 'Data folder writable' $writable $DesktopLocal
$settingsPath = Join-Path $DesktopLocal 'desktop.json'
$port = 17861
if (Test-Path -LiteralPath $settingsPath) { $port = (Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).port }
$own = Get-DesktopProcess (Get-DesktopState)
Add-Check 'Local port' ($own -or (Test-DesktopPort $port)) "127.0.0.1:$port (an occupied port is never stopped automatically)."
$identity = Test-Path 'HKCU:\Software\Classes\AppUserModelId\Shiji.PersonalAssistant'
$report = [pscustomobject]@{ ready=(@($checks | Where-Object { !$_.ok }).Count -eq 0); checks=@($checks.ToArray()); notificationRegistered=$identity; notificationNote='First Start registers notifications. Windows banner permission and Do Not Disturb need a real desktop check.' }
if ($AsJson) { $report | ConvertTo-Json -Depth 6 }
else {
    foreach ($check in $checks) { $label = if ($check.ok) { '[OK]' } else { '[FAIL]' }; Write-Host "$label $($check.name): $($check.detail)" }
    Write-Host $report.notificationNote
    Write-Host 'Codex plugin is optional and requires Node.js and Codex separately. MySQL mode requires a configured local MySQL.'
}
if (!$report.ready) { exit 1 }
