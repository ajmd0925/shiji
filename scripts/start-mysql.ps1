param([string]$Archive, [int]$Port = 3307)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$localDir = Join-Path $projectRoot '.local'
$toolsDir = Join-Path $localDir 'tools'
$mysqlHome = Get-ChildItem -LiteralPath $toolsDir -Directory -Filter 'mysql-*-winx64' -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
if (!$mysqlHome) {
  if (!$Archive) { throw 'Pass -Archive <official MySQL Windows ZIP path>.' }
  Expand-Archive -LiteralPath $Archive -DestinationPath $toolsDir
  $mysqlHome = Get-ChildItem -LiteralPath $toolsDir -Directory -Filter 'mysql-*-winx64' | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
}
$mysqlServer = Join-Path $mysqlHome 'bin\mysqld.exe'
$mysqlClient = Join-Path $mysqlHome 'bin\mysql.exe'
$dataDir = Join-Path $localDir 'mysql-data'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
# MySQL's Windows command-line parser truncates paths containing non-ASCII characters.
# NTFS short paths refer to the same project folders without moving any data.
$fileSystem = New-Object -ComObject Scripting.FileSystemObject
$mysqlNativeHome = $fileSystem.GetFolder($mysqlHome).ShortPath
$nativeDataDir = $fileSystem.GetFolder($dataDir).ShortPath
$nativeLocalDir = $fileSystem.GetFolder($localDir).ShortPath
$mysqlServer = Join-Path $mysqlNativeHome 'bin\mysqld.exe'
$mysqlClient = Join-Path $mysqlNativeHome 'bin\mysql.exe'
$configPath = Join-Path $localDir 'mysql-connection.json'
if (!(Test-Path $configPath)) {
  $credentials = @{ rootPassword = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24)); password = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24)); port = $Port; user = 'assistant' }
  $credentials | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding utf8NoBOM
}
$credentials = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
if ($credentials.port -ne $Port) { throw 'Existing database uses another port; use the port in .local/mysql-connection.json.' }
$env:MYSQL_PWD = $credentials.password
$oldPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& $mysqlClient '--protocol=TCP' '--host=127.0.0.1' "--port=$Port" '--user=assistant' '--connect-timeout=2' '--skip-column-names' '--execute=SELECT 1' 2>$null | Out-Null
$alreadyRunning = $LASTEXITCODE -eq 0
$ErrorActionPreference = $oldPreference
if ($alreadyRunning) {
  $oldInit = Join-Path $localDir 'mysql-init.sql'
  if (Test-Path $oldInit) { Remove-Item -LiteralPath $oldInit }
  Remove-Item Env:MYSQL_PWD; Write-Host "Project MySQL is already available on 127.0.0.1:$Port"; exit 0
}
if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) { throw 'The selected port is in use; no existing service was changed.' }
if (!(Test-Path (Join-Path $dataDir 'mysql'))) {
  & $mysqlServer --no-defaults --initialize-insecure "--basedir=$mysqlNativeHome" "--datadir=$nativeDataDir" *> (Join-Path $localDir 'mysql-initialize.log')
  if ($LASTEXITCODE -ne 0) { throw 'MySQL initialization failed. See .local/mysql-initialize.log.' }
}
$initPath = Join-Path $localDir 'mysql-init.sql'
@"
ALTER USER 'root'@'localhost' IDENTIFIED BY '$($credentials.rootPassword)';
CREATE DATABASE IF NOT EXISTS personal_assistant CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE DATABASE IF NOT EXISTS personal_assistant_test CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER IF NOT EXISTS 'assistant'@'localhost' IDENTIFIED BY '$($credentials.password)';
GRANT ALL PRIVILEGES ON personal_assistant.* TO 'assistant'@'localhost';
GRANT ALL PRIVILEGES ON personal_assistant_test.* TO 'assistant'@'localhost';
"@ | Set-Content -LiteralPath $initPath -Encoding utf8NoBOM
$nativeInitPath = $fileSystem.GetFile($initPath).ShortPath
$processArgs = @('--no-defaults', "--basedir=`"$mysqlNativeHome`"", "--datadir=`"$nativeDataDir`"", '--bind-address=127.0.0.1', "--port=$Port", '--mysqlx=OFF', "--init-file=`"$nativeInitPath`"", "--log-error=`"$(Join-Path $nativeLocalDir 'mysql-error.log')`"")
$mysqlProcess = Start-Process -FilePath $mysqlServer -ArgumentList $processArgs -WindowStyle Hidden -PassThru
$mysqlProcess.Id | Set-Content -LiteralPath (Join-Path $localDir 'mysql.pid') -Encoding ascii
for ($attempt = 0; $attempt -lt 30; $attempt++) {
  if ($mysqlProcess.HasExited) { throw 'MySQL exited. See .local/mysql-error.log.' }
  $ErrorActionPreference = 'Continue'
  & $mysqlClient '--protocol=TCP' '--host=127.0.0.1' "--port=$Port" '--user=assistant' '--connect-timeout=2' '--skip-column-names' '--execute=SELECT 1' 2>$null | Out-Null
  $ready = $LASTEXITCODE -eq 0
  $ErrorActionPreference = 'Stop'
  if ($ready) { Remove-Item -LiteralPath $initPath; Remove-Item Env:MYSQL_PWD; Write-Host "Project MySQL is ready on 127.0.0.1:$Port (no Windows service installed)."; exit 0 }
  Start-Sleep -Seconds 1
}
Remove-Item Env:MYSQL_PWD
throw 'MySQL did not become ready. See .local/mysql-error.log.'
