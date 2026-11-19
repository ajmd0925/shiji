param(
    [string]$Version = '0.3.11',
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$Maven = 'mvn.cmd',
    [string]$RuntimeArchive,
    [switch]$DownloadRuntime
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$') { throw 'Version must be a semantic version.' }
if (!$JavaHome -or !(Test-Path -LiteralPath (Join-Path $JavaHome 'bin\javac.exe'))) { throw 'Build requires a Java 21 JDK. Set JAVA_HOME or pass -JavaHome.' }
$env:JAVA_HOME = $JavaHome
$env:PATH = (Join-Path $JavaHome 'bin') + ';' + $env:PATH
$output = Join-Path $root 'release'
$name = "shiji-$Version-windows-x64"
$stage = Join-Path $output $name
if (Test-Path -LiteralPath $stage) { throw "Output already exists: $stage. Choose another version or archive the previous output." }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$runtimeMetadata = $null
if (!$RuntimeArchive) {
    if (!$DownloadRuntime) { throw 'Pass -RuntimeArchive <official Temurin 21 JRE ZIP> or -DownloadRuntime (about 50 MB, explicit network download).' }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Write-Host 'Downloading official Eclipse Temurin 21 Windows x64 JRE metadata and runtime...'
    $asset = @(Invoke-RestMethod 'https://api.adoptium.net/v3/assets/latest/21/hotspot?architecture=x64&image_type=jre&os=windows&vendor=eclipse')[0]
    $package = $asset.binary.package
    if ($package.link -notlike 'https://github.com/adoptium/temurin21-binaries/*' -or $package.checksum -notmatch '^[a-fA-F0-9]{64}$') { throw 'Unexpected runtime source/checksum.' }
    $RuntimeArchive = Join-Path $output $package.name
    if ((Test-Path -LiteralPath $RuntimeArchive) -and (Get-FileHash -LiteralPath $RuntimeArchive -Algorithm SHA256).Hash -ne $package.checksum) {
        Move-Item -LiteralPath $RuntimeArchive -Destination ($RuntimeArchive + '.partial') -Force
    }
    if (!(Test-Path -LiteralPath $RuntimeArchive)) {
        $partial = $RuntimeArchive + '.partial'
        $offset = if (Test-Path -LiteralPath $partial) { (Get-Item -LiteralPath $partial).Length } else { 0L }
        if ($offset -gt $package.size) { throw 'Partial runtime download is too large; move it aside before retrying.' }
        while ($offset -lt $package.size) {
            $end = [Math]::Min($offset + 4194303, $package.size - 1)
            $chunk = $partial + '.chunk'
            $downloaded = $false
            for ($attempt=0; $attempt -lt 4; $attempt++) {
                try {
                    $response = Invoke-WebRequest -UseBasicParsing -Uri $package.link -Headers @{Range="bytes=$offset-$end"} -OutFile $chunk -PassThru -TimeoutSec 120
                    if ($response.StatusCode -eq 200 -and $offset -eq 0 -and (Get-Item -LiteralPath $chunk).Length -eq $package.size) { $end = $package.size - 1 }
                    elseif ($response.StatusCode -ne 206 -or [string]$response.Headers['Content-Range'] -ne "bytes $offset-$end/$($package.size)") { throw 'Unexpected HTTP range response.' }
                    if ((Get-Item -LiteralPath $chunk).Length -ne ($end-$offset+1)) { throw 'Incomplete runtime chunk.' }
                    $downloaded = $true; break
                } catch { if ($attempt -eq 3) { throw }; Start-Sleep -Seconds 2 }
            }
            if (!$downloaded) { throw 'Runtime download interrupted; rerun to resume.' }
            $destination = [IO.File]::Open($partial, 'Append', 'Write', 'None')
            try {
                $source = [IO.File]::OpenRead($chunk)
                try { $source.CopyTo($destination) } finally { $source.Dispose() }
            } finally { $destination.Dispose() }
            Remove-Item -LiteralPath $chunk
            $offset = $end+1
            Write-Host "Runtime download: $offset / $($package.size) bytes."
        }
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $package.checksum) { throw 'Runtime SHA256 mismatch. Do not use this download.' }
        Move-Item -LiteralPath $partial -Destination $RuntimeArchive
    }
    if ((Get-FileHash -LiteralPath $RuntimeArchive -Algorithm SHA256).Hash -ne $package.checksum) { throw 'Runtime SHA256 mismatch. Do not use this download.' }
    $runtimeMetadata = @{ vendor='Eclipse Temurin'; source=$package.link; sha256=$package.checksum; version=$asset.version.semver }
} else {
    # Supplying an archive is for offline builders; always record its digest in the manifest.
    $RuntimeArchive = (Resolve-Path -LiteralPath $RuntimeArchive).Path
    $runtimeMetadata = @{ vendor='User-supplied Java 21 runtime'; sha256=(Get-FileHash -LiteralPath $RuntimeArchive -Algorithm SHA256).Hash.ToLowerInvariant() }
}
Push-Location $root
try {
    Push-Location frontend
    try {
        & npm.cmd ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency installation failed.' }
        & npm.cmd run build
        if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
    } finally { Pop-Location }
    $cache = Join-Path $root '.local\m2'
    # Never inherit a developer's external test database into a release build.
    $savedTest = @{}
    foreach ($key in @('TEST_DB_URL','TEST_DB_USER','TEST_DB_PASSWORD')) { $savedTest[$key]=[Environment]::GetEnvironmentVariable($key); [Environment]::SetEnvironmentVariable($key,$null) }
    try {
        & $Maven "-Dmaven.repo.local=$cache" -f backend/pom.xml -B -ntp clean package -Pdesktop
        if ($LASTEXITCODE -ne 0) { throw 'Backend tests or packaging failed.' }
    } finally { foreach ($key in $savedTest.Keys) { [Environment]::SetEnvironmentVariable($key,$savedTest[$key]) } }
    New-Item -ItemType Directory -Force -Path (Join-Path $stage 'app'), (Join-Path $stage 'scripts'), (Join-Path $stage 'docs') | Out-Null
    Copy-Item -LiteralPath 'backend/target/personal-assistant-0.1.0.jar' -Destination (Join-Path $stage 'app/shiji.jar')
    & (Join-Path $root 'desktop/build-pet.ps1') -OutputDirectory (Join-Path $stage 'desktop')
    foreach ($entry in @('Start.cmd','Stop.cmd','Check.cmd','OpenBrowser.cmd','LICENSE')) { Copy-Item -LiteralPath $entry -Destination $stage }
    foreach ($entry in @('desktop-common.ps1','start-desktop.ps1','stop-desktop.ps1','check-environment.ps1','register-notifications.ps1','windows-shortcut.cs')) { Copy-Item -LiteralPath (Join-Path 'scripts' $entry) -Destination (Join-Path $stage 'scripts') }
    Copy-Item -LiteralPath 'backend/src/main/resources/windows-notifications.ps1' -Destination (Join-Path $stage 'scripts/windows-notifications.ps1')
    Copy-Item -LiteralPath 'docs/distribution.md' -Destination (Join-Path $stage 'docs/distribution.md')
    Copy-Item -LiteralPath 'docs/portable-readme.txt' -Destination (Join-Path $stage 'README.txt')
    # Windows PowerShell 5.1 needs a BOM for UTF-8 script literals (including Chinese).
    Get-ChildItem -LiteralPath (Join-Path $stage 'scripts') -Filter '*.ps1' | ForEach-Object {
        $scriptText = [IO.File]::ReadAllText($_.FullName, [Text.Encoding]::UTF8)
        [IO.File]::WriteAllText($_.FullName, $scriptText, (New-Object Text.UTF8Encoding($true)))
    }
    $unpack = Join-Path $stage '_runtime-unpack'
    Expand-Archive -LiteralPath $RuntimeArchive -DestinationPath $unpack
    $javaFiles = @(Get-ChildItem -LiteralPath $unpack -Filter java.exe -Recurse | Where-Object { $_.Directory.Name -eq 'bin' })
    if ($javaFiles.Count -ne 1) { throw 'Runtime archive must contain exactly one bin/java.exe.' }
    Move-Item -LiteralPath $javaFiles[0].Directory.Parent.FullName -Destination (Join-Path $stage 'runtime')
    # This is the known staging extraction directory, never a user data directory.
    $resolvedUnpack = [IO.Path]::GetFullPath($unpack)
    if ($resolvedUnpack -ne [IO.Path]::GetFullPath((Join-Path $stage '_runtime-unpack'))) { throw 'Invalid staging path.' }
    Remove-Item -LiteralPath $resolvedUnpack -Recurse
    $runtimeVersion = & (Join-Path $stage 'runtime/bin/java.exe') --version | Out-String
    if ($runtimeVersion -notmatch '(?m)^openjdk 21[.\s\-]') { throw 'The runtime must be Java 21.' }
    $manifest = @{ version=$Version; architecture='windows-x64'; defaultDatabase='H2 local file'; runtime=$runtimeMetadata; jarSha256=(Get-FileHash -LiteralPath (Join-Path $stage 'app/shiji.jar')).Hash.ToLowerInvariant(); petSha256=(Get-FileHash -LiteralPath (Join-Path $stage 'desktop/Shiji.Pet.exe')).Hash.ToLowerInvariant() }
    [IO.File]::WriteAllText((Join-Path $stage 'manifest.json'), ($manifest | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
    # Explicit staging allowlist above excludes .local, credentials, source caches and developer data.
    $zip = Join-Path $output ($name + '.zip')
    Compress-Archive -LiteralPath $stage -DestinationPath $zip
    $digest = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($zip + '.sha256'), "$digest  $name.zip`n", (New-Object Text.UTF8Encoding($false)))
    Write-Host "Portable package: $zip"
} finally { Pop-Location }
