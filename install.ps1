#Requires -RunAsAdministrator
<#
  Registers WindowsShow as a background task that starts when you sign in, runs with
  administrator rights (so it can hide apps that run as administrator), and restarts if it crashes.

  Install:    powershell -ExecutionPolicy Bypass -File install.ps1
  Uninstall:  powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall
#>
param([switch]$Uninstall)

$taskName = 'WindowsShow'
$exe = Join-Path $PSScriptRoot 'bin\WindowsShow.exe'
$user = "$env:USERDOMAIN\$env:USERNAME"

if ($Uninstall) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "Removed scheduled task '$taskName'."
    return
}

if (-not (Test-Path $exe)) {
    throw "Build first: $exe not found (run build.cmd)."
}

$action    = New-ScheduledTaskAction -Execute $exe -Argument '/tray'
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries `
                 -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew `
                 -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal `
    -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
Write-Host "Installed and started '$taskName' for $user (runs elevated at sign-in)."
