$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$config = Get-Content -LiteralPath (Join-Path $projectRoot '.local\mysql-connection.json') -Raw | ConvertFrom-Json
$mysqlHome = Get-ChildItem -LiteralPath (Join-Path $projectRoot '.local\tools') -Directory -Filter 'mysql-*-winx64' | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
$fileSystem = New-Object -ComObject Scripting.FileSystemObject
$mysqlAdmin = Join-Path $fileSystem.GetFolder($mysqlHome).ShortPath 'bin\mysqladmin.exe'
$env:MYSQL_PWD = $config.rootPassword
try {
  & $mysqlAdmin '--protocol=TCP' '--host=127.0.0.1' "--port=$($config.port)" '--user=root' '--connect-timeout=3' shutdown
  if ($LASTEXITCODE -ne 0) { throw 'Project MySQL shutdown failed.' }
  Write-Host 'Project MySQL stopped. Database files were preserved.'
} finally { Remove-Item Env:MYSQL_PWD -ErrorAction SilentlyContinue }
