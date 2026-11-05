$ErrorActionPreference = 'Stop'

try {
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\DUSMP Launcher'
$startMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\DUSMP Launcher'
$uninstaller = Join-Path $installDir 'uninstall.ps1'

# An upgrade cannot replace an executable while its old process is running.
foreach ($name in @('DusmpLauncher', 'DusmpUpdater')) {
  $installedExe = Join-Path $installDir ($name + '.exe')
  foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
    if ($process.Path -ne $installedExe) { continue }
    if ($name -eq 'DusmpLauncher') {
      if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(10000)) {
        throw 'Close DUSMP Launcher, then run this installer again.'
      }
    } else {
      Stop-Process -Id $process.Id -Force
      $process.WaitForExit()
    }
  }
}

New-Item -ItemType Directory -Force -Path $installDir, $startMenuDir | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'DusmpLauncher.exe') (Join-Path $installDir 'DusmpLauncher.exe') -Force
Copy-Item (Join-Path $PSScriptRoot 'DusmpUpdater.exe') (Join-Path $installDir 'DusmpUpdater.exe') -Force
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $uninstaller -Force

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $startMenuDir 'DUSMP Launcher.lnk'))
$shortcut.TargetPath = Join-Path $installDir 'DusmpLauncher.exe'
$shortcut.WorkingDirectory = $installDir
$shortcut.Description = 'Play DUSMP Minecraft'
$shortcut.IconLocation = (Join-Path $installDir 'DusmpLauncher.exe') + ',0'
$shortcut.Save()

$desktopShortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'DUSMP Launcher.lnk'))
$desktopShortcut.TargetPath = Join-Path $installDir 'DusmpLauncher.exe'
$desktopShortcut.WorkingDirectory = $installDir
$desktopShortcut.Description = 'Play DUSMP Minecraft'
$desktopShortcut.IconLocation = (Join-Path $installDir 'DusmpLauncher.exe') + ',0'
$desktopShortcut.Save()

$uninstallKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DusmpLauncher'
New-Item -Path $uninstallKeyPath -Force | Out-Null
Set-ItemProperty -Path $uninstallKeyPath -Name DisplayName -Value 'DUSMP Launcher'
Set-ItemProperty -Path $uninstallKeyPath -Name DisplayVersion -Value ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $installDir 'DusmpLauncher.exe')).ProductVersion)
Set-ItemProperty -Path $uninstallKeyPath -Name Publisher -Value 'DUSMP'
Set-ItemProperty -Path $uninstallKeyPath -Name InstallLocation -Value $installDir
Set-ItemProperty -Path $uninstallKeyPath -Name DisplayIcon -Value ((Join-Path $installDir 'DusmpLauncher.exe') + ',0')
Set-ItemProperty -Path $uninstallKeyPath -Name UninstallString -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstaller`""

try {
  $taskAction = New-ScheduledTaskAction -Execute (Join-Path $installDir 'DusmpUpdater.exe')
  $taskTrigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
  $taskSettings = New-ScheduledTaskSettingsSet -StartWhenAvailable -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1)
  Register-ScheduledTask -TaskName 'DUSMP Updater' -Action $taskAction -Trigger $taskTrigger -Settings $taskSettings -Description 'Keeps DUSMP modpack installations up to date.' -Force | Out-Null
} catch {
  Write-Warning "The launcher was installed, but its background updater could not be registered: $_"
}

Start-Process (Join-Path $installDir 'DusmpLauncher.exe')
} catch {
  Add-Type -AssemblyName System.Windows.Forms
  [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'DUSMP Launcher installation failed', 'OK', 'Error') | Out-Null
  exit 1
}
