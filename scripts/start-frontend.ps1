$ErrorActionPreference = 'Stop'
Push-Location (Join-Path (Split-Path $PSScriptRoot -Parent) 'frontend')
try {
  if (!(Test-Path 'node_modules')) { npm install; if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed.' } }
  npm run dev
} finally { Pop-Location }
