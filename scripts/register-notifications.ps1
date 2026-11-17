param([string]$BridgePath, [string]$AppUrl = 'http://127.0.0.1:5173/')
$ErrorActionPreference = 'Stop'
$OutputEncoding = New-Object Text.UTF8Encoding($false)
[Console]::OutputEncoding = $OutputEncoding
Add-Type -Path (Join-Path $PSScriptRoot 'windows-shortcut.cs')
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Shiji.lnk'
[ShijiShortcut]::Create($shortcut, (Join-Path $env:WINDIR 'explorer.exe'), $AppUrl)
# Register only this application's identity. Never change the user's notification preferences.
$identity = 'HKCU:\Software\Classes\AppUserModelId\Shiji.PersonalAssistant'
New-Item -Path $identity -Force | Out-Null
New-ItemProperty -Path $identity -Name DisplayName -Value '拾记 · 个人任务助手' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $identity -Name ShowInSettings -Value 1 -PropertyType DWord -Force | Out-Null
Write-Output 'Registered Shiji.PersonalAssistant for the current Windows user.'
if (!$BridgePath) { $BridgePath = Join-Path $PSScriptRoot '../backend/src/main/resources/windows-notifications.ps1' }
$bridge = Get-Content -LiteralPath $BridgePath -Raw -Encoding UTF8
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($bridge))
# A new unpackaged app becomes queryable by ToastNotifier.Setting after its first toast.
# This visible setup confirmation is sent only by this installer, never by backend startup.
$result = @{ command='initialize'; id=[guid]::NewGuid().ToString(); title='拾记通知已注册'; message='请在 Windows 通知设置中允许拾记横幅。后台运行后即可按计划提醒。' } |
    ConvertTo-Json -Compress | & (Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe') -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand $encoded
$response = $result | ConvertFrom-Json
if (!$response.ok) { throw $response.error }
Write-Output 'Windows accepted the setup notification. Confirm banner permissions in Windows Settings.'
