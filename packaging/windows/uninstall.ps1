$ErrorActionPreference = 'SilentlyContinue'
$installDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$startMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\DUSMP Launcher'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'DUSMP Launcher.lnk'

Unregister-ScheduledTask -TaskName 'DUSMP Updater' -Confirm:$false
Remove-Item $startMenuDir -Recurse -Force
Remove-Item $desktopShortcut -Force
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DusmpLauncher' -Recurse -Force
Remove-Item $installDir -Recurse -Force
