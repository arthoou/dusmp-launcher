param(
  [Parameter(Mandatory = $true)]
  [string]$UpdaterExe
)

$exePath = (Resolve-Path $UpdaterExe).Path
$action = New-ScheduledTaskAction -Execute $exePath
$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName "DUSMP Updater" -Action $action -Trigger $trigger -Settings $settings -Description "Keeps DUSMP modpack installations up to date." -Force | Out-Null
