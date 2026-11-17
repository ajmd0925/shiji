param([ValidateSet('local','mysql')][string]$Database = 'local', [string]$Maven)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$localDir = Join-Path $projectRoot '.local'
if (Test-Path 'C:\Program Files\Java\jdk-21\bin\java.exe') { $env:JAVA_HOME = 'C:\Program Files\Java\jdk-21' }
if (!$Maven) {
  $cmd = Get-Command mvn -ErrorAction SilentlyContinue
  if ($cmd) { $Maven = $cmd.Source } else { $Maven = Join-Path $env:USERPROFILE 'tools\apache-maven-3.9.9\bin\mvn.cmd' }
}
if ($Database -eq 'mysql') {
  $config = Get-Content -LiteralPath (Join-Path $localDir 'mysql-connection.json') -Raw | ConvertFrom-Json
  $env:TEST_DB_URL = "jdbc:mysql://127.0.0.1:$($config.port)/personal_assistant_test?useUnicode=true&characterEncoding=utf8&serverTimezone=UTC"
  $env:TEST_DB_USER = $config.user
  $env:TEST_DB_PASSWORD = $config.password
} else { Remove-Item Env:TEST_DB_URL,Env:TEST_DB_USER,Env:TEST_DB_PASSWORD -ErrorAction SilentlyContinue }
$log = Join-Path $localDir "backend-test-$Database.log"
Push-Location (Join-Path $projectRoot 'backend')
try {
  & $Maven "-Dmaven.repo.local=$localDir\m2" -B -ntp test *> $log
  $testCode = $LASTEXITCODE
  Get-Content -LiteralPath $log -Tail 20
  if ($testCode -ne 0) { throw "Backend tests failed. See $log" }
} finally { Pop-Location; Remove-Item Env:TEST_DB_URL,Env:TEST_DB_USER,Env:TEST_DB_PASSWORD -ErrorAction SilentlyContinue }
