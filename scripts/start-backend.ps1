param([ValidateSet('local','mysql')][string]$Profile = 'local', [string]$JavaHome, [string]$Maven)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$localDir = Join-Path $projectRoot '.local'
New-Item -ItemType Directory -Force -Path $localDir | Out-Null
if (!$JavaHome) {
  if ($env:JAVA_HOME -and (Test-Path (Join-Path $env:JAVA_HOME 'bin\javac.exe'))) { $JavaHome = $env:JAVA_HOME }
  elseif (Test-Path 'C:\Program Files\Java\jdk-21\bin\java.exe') { $JavaHome = 'C:\Program Files\Java\jdk-21' }
  else { throw 'Java 21 is required. Pass -JavaHome <JDK path>.' }
}
$env:JAVA_HOME = $JavaHome
if (!$Maven) {
  $mavenCommand = Get-Command mvn -ErrorAction SilentlyContinue
  if ($mavenCommand) { $Maven = $mavenCommand.Source }
  elseif (Test-Path (Join-Path $env:USERPROFILE 'tools\apache-maven-3.9.9\bin\mvn.cmd')) { $Maven = Join-Path $env:USERPROFILE 'tools\apache-maven-3.9.9\bin\mvn.cmd' }
  else { throw 'Maven 3.9+ is required. Pass -Maven <mvn.cmd path>.' }
}
$tokenPath = Join-Path $localDir 'access-token.txt'
if (!(Test-Path $tokenPath)) { [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant() | Set-Content -LiteralPath $tokenPath -Encoding utf8NoBOM }
$env:ASSISTANT_API_TOKEN = (Get-Content -LiteralPath $tokenPath -Raw).Trim()
$env:SPRING_PROFILES_ACTIVE = $Profile
if ($Profile -eq 'mysql' -and !(Test-Path Env:DB_PASSWORD)) {
  $mysqlConfigPath = Join-Path $localDir 'mysql-connection.json'
  if (!(Test-Path $mysqlConfigPath)) { throw 'Set DB_PASSWORD or run scripts/start-mysql.ps1 first.' }
  $mysqlConfig = Get-Content -LiteralPath $mysqlConfigPath -Raw | ConvertFrom-Json
  $env:DB_PASSWORD = $mysqlConfig.password
  $env:DB_USER = $mysqlConfig.user
  $env:DB_URL = "jdbc:mysql://127.0.0.1:$($mysqlConfig.port)/personal_assistant?useUnicode=true&characterEncoding=utf8&serverTimezone=UTC"
}
$connection = @{ url = 'http://127.0.0.1:8080'; token = $env:ASSISTANT_API_TOKEN }
$connection | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $localDir 'connection.json') -Encoding utf8NoBOM
Write-Host 'Access token saved in .local/access-token.txt. Do not share it.'
Write-Host "Starting backend ($Profile) at http://127.0.0.1:8080. Press Ctrl+C to stop."
Push-Location (Join-Path $projectRoot 'backend')
try { & $Maven "-Dmaven.repo.local=$localDir\m2" -B -ntp spring-boot:run; if ($LASTEXITCODE -ne 0) { throw 'Backend exited with an error.' } }
finally { Pop-Location }
