#Requires -Version 5.1
# Marz Monitor - uninstaller (Settings > Apps). Closes the app the normal way (fans go back to automatic).
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if([Windows.Forms.MessageBox]::Show('Remove Marz Monitor from this account? Your settings in %APPDATA%\MonitorTray are kept.','Marz Monitor','YesNo','Question') -ne 'Yes'){ return }
$h=$null
if([Threading.EventWaitHandle]::TryOpenExisting("Local\MonitorTray.Quit.$sid",[Security.AccessControl.EventWaitHandleRights]::Modify,[ref]$h)){ try{[void]$h.Set()} finally {$h.Dispose()} }
$end=(Get-Date).AddSeconds(20)
while((Get-Process MonitorTray -ErrorAction SilentlyContinue) -and (Get-Date) -lt $end){ Start-Sleep -Milliseconds 400 }
if(Get-Process MonitorTray -ErrorAction SilentlyContinue){ [void][Windows.Forms.MessageBox]::Show('Marz Monitor did not close. Quit it from the tray menu and try again.','Marz Monitor'); exit 1 }
$dir=Join-Path (Join-Path $env:ProgramFiles 'MonitorTray') $sid
$cmd = "& '$env:SystemRoot\System32\schtasks.exe' /Delete /TN 'MonitorTray-$sid' /F; & '$env:SystemRoot\System32\schtasks.exe' /Delete /TN 'MonitorTray-Logon-$sid' /F; " +
       "Remove-Item 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MonitorTray-$sid' -Recurse -Force -ErrorAction SilentlyContinue; " +
       "Start-Sleep 1; Remove-Item -LiteralPath '$dir' -Recurse -Force; `$r=Join-Path `$env:ProgramFiles 'MonitorTray'; if(@(Get-ChildItem `$r -Force | Where-Object Name -ne '_backups').Count -eq 0){ Remove-Item `$r -Recurse -Force }"
$enc=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cmd))
Set-Location $env:TEMP
$p=Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $enc" -Wait -PassThru
foreach($l in @("$([Environment]::GetFolderPath('Programs'))\Monitor Tray.lnk","$([Environment]::GetFolderPath('Desktop'))\Monitor Tray.lnk","$([Environment]::GetFolderPath('Programs'))\Marz Monitor.lnk","$([Environment]::GetFolderPath('Desktop'))\Marz Monitor.lnk")){ Remove-Item $l -ErrorAction SilentlyContinue }
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name MonitorTray -ErrorAction SilentlyContinue
[void][Windows.Forms.MessageBox]::Show($(if(Test-Path $dir){'Removal incomplete (UAC declined?).'}else{'Marz Monitor removed.'}),'Marz Monitor')
