$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.UI.Notifications.ToastNotifier, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.UI.Notifications.NotificationSetting, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
$appId = 'Shiji.PersonalAssistant'
while ($null -ne ($line = [Console]::ReadLine())) {
    try {
        $request = $line | ConvertFrom-Json
        if (!(Test-Path -LiteralPath ('HKCU:\Software\Classes\AppUserModelId\' + $appId))) { throw 'Shiji notification identity is not registered. Run scripts/register-notifications.ps1.' }
        $notifier = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId)
        if ($request.command -ne 'initialize') {
            # Reflection propagates WinRT getter failures; PowerShell property access can silently return null.
            $setting = $notifier.GetType().GetProperty('Setting').GetValue($notifier, $null)
            if ($setting.ToString() -ne 'Enabled') { throw ('Windows notification setting: ' + $setting) }
        }
        switch ($request.command) {
            'ping' { $result = @{ ok = $true; setting = 'Enabled' } }
            { $_ -in 'show', 'initialize' } {
                # All user content enters XML as text, never as PowerShell source or XML markup.
                $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
                $xml.LoadXml('<toast scenario="reminder"><visual><binding template="ToastGeneric"><text></text><text></text></binding></visual><actions><action activationType="system" arguments="dismiss" content="Dismiss"/></actions></toast>')
                $nodes = $xml.GetElementsByTagName('text')
                $null = $nodes.Item(0).AppendChild($xml.CreateTextNode([string]$request.title))
                $null = $nodes.Item(1).AppendChild($xml.CreateTextNode([string]$request.message))
                $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
                $toast.Tag = ([string]$request.id).Replace('-', '').Substring(0, 16)
                $toast.Group = 'shiji-reminders'
                $toast.ExpirationTime = [DateTimeOffset]::Now.AddMinutes(5)
                $notifier.Show($toast)
                $result = @{ ok = $true; acceptedAt = [DateTimeOffset]::UtcNow.ToString('o') }
            }
            'history' {
                $items = @([Windows.UI.Notifications.ToastNotificationManager]::History.GetHistory($appId) | ForEach-Object { @{ tag = $_.Tag; xml = $_.Content.GetXml() } })
                $result = @{ ok = $true; items = $items }
            }
            default { throw 'Unknown notification command.' }
        }
        [Console]::WriteLine(($result | ConvertTo-Json -Compress -Depth 5))
    } catch {
        [Console]::WriteLine((@{ ok = $false; error = $_.Exception.Message } | ConvertTo-Json -Compress))
    }
}
