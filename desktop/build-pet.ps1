param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root '.local\pet-build' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework 4.x C# compiler is required to build the desktop pet.' }
$references = @('System.dll','System.Core.dll','System.Net.Http.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$output = Join-Path $OutputDirectory 'Shiji.Pet.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output ('/out:' + $output) ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')) ('/resource:' + (Join-Path $PSScriptRoot 'assets/dragon-girl.png') + ',Shiji.Desktop.DragonGirl.png') @references (Join-Path $PSScriptRoot 'ShijiPet.cs')
if ($LASTEXITCODE -ne 0) { throw 'Desktop pet compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Shiji.Pet.exe.config') -Destination $OutputDirectory -Force
Write-Host "Desktop pet: $output"
