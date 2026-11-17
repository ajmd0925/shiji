param([ValidateSet('local','mysql')][string]$Profile='mysql', [string]$JavaHome, [string]$Maven)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$localDir=Join-Path $projectRoot '.local'
$targetJar=Join-Path $projectRoot 'backend\target\personal-assistant-0.1.0.jar'
$runningDir=Join-Path $localDir 'running'
$runningJar=Join-Path $runningDir 'backend.jar'
$javaPath=if($JavaHome) { Join-Path $JavaHome 'bin/java.exe' } elseif($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin/java.exe' } else { 'C:\Program Files\Java\jdk-21\bin\java.exe' }
$mavenPath=if($Maven) { $Maven } elseif(Get-Command mvn -ErrorAction SilentlyContinue) { (Get-Command mvn).Source } else { Join-Path $env:USERPROFILE 'tools\apache-maven-3.9.9\bin\mvn.cmd' }
if(!(Test-Path -LiteralPath $javaPath)) { throw 'Java 21 is required.' }
if(!(Test-Path -LiteralPath $mavenPath)) { throw 'Maven 3.9+ is required.' }
New-Item -ItemType Directory -Force -Path $runningDir | Out-Null
# Match the absolute project JAR, never a generic Java process.
$matched=Get-CimInstance Win32_Process -Filter "Name='java.exe'" | Where-Object {
  $_.CommandLine -and ($_.CommandLine.Contains($targetJar) -or $_.CommandLine.Contains($runningJar))
}
foreach($backendProcess in $matched) { Stop-Process -Id $backendProcess.ProcessId }
$env:JAVA_HOME=Split-Path (Split-Path $javaPath -Parent) -Parent
Push-Location $projectRoot
try {
  & $mavenPath "-Dmaven.repo.local=$localDir\m2" -f backend/pom.xml -B -ntp -DskipTests package *> (Join-Path $localDir 'package.log')
  if($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $localDir 'package.log') -Tail 20; throw 'Build failed; sources and database preserved.' }
  Copy-Item -LiteralPath $targetJar -Destination $runningJar -Force
  $connection=Get-Content -LiteralPath (Join-Path $localDir 'connection.json') -Raw | ConvertFrom-Json
  $env:ASSISTANT_API_TOKEN=$connection.token
  $env:SPRING_PROFILES_ACTIVE=$Profile
  if($Profile -eq 'mysql') {
    $database=Get-Content -LiteralPath (Join-Path $localDir 'mysql-connection.json') -Raw | ConvertFrom-Json
    $env:DB_PASSWORD=$database.password
    $env:DB_USER=$database.user
    $env:DB_URL="jdbc:mysql://127.0.0.1:$($database.port)/personal_assistant?useUnicode=true&characterEncoding=utf8&serverTimezone=UTC"
  }
  $started=Start-Process -FilePath $javaPath -ArgumentList @('-jar', "`"$runningJar`"") -WorkingDirectory (Join-Path $projectRoot 'backend') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $localDir 'backend.log') -RedirectStandardError (Join-Path $localDir 'backend-error.log')
  Write-Output "Started backend PID $($started.Id)."
} finally { Pop-Location }
