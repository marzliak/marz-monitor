#Requires -Version 5.1
# Elevated step: copies the package to Program Files\MonitorTray\<SID>, with backup and rollback, and registers the account's tasks.
param([Parameter(Mandatory=$true)][string]$RequestPath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$rq=Get-Content $RequestPath -Raw | ConvertFrom-Json
$out=[ordered]@{ Success=$false; Error=$null; Version=$rq.Version; InstallDir=$null; Exe=$null; Backup=$null; Tasks=@(); DisabledTask=$null }
function Hash([string]$p){ (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }
function Save { ($out | ConvertTo-Json -Depth 5) | Set-Content $rq.Result -Encoding UTF8 }
try {
    if($rq.Sid -notmatch '^S-1-5-21-[\d-]+$'){ throw 'Invalid SID.' }
    $root=Join-Path $env:ProgramFiles 'MonitorTray'; $dir=Join-Path $root $rq.Sid
    $out.InstallDir=$dir; $out.Exe=Join-Path $dir 'MonitorTray.exe'
    $tLaunch="MonitorTray-$($rq.Sid)"; $tLogon="MonitorTray-Logon-$($rq.Sid)"
    $schtasks="$env:SystemRoot\System32\schtasks.exe"

    if($rq.Mode -eq 'Uninstall'){
        foreach($t in @($tLaunch,$tLogon)){ & $schtasks /Delete /TN $t /F 2>$null | Out-Null }
        if(Get-Process MonitorTray -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dir*" }){ throw 'The app is still running.' }
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
        if((Test-Path $root) -and @(Get-ChildItem $root -Force).Count -eq 0){ Remove-Item $root -Force }
        $out.Success=$true; Save; return
    }

    # Revalidates the package inside the elevated step (the request came from a non-elevated process).
    if((Hash $rq.Manifest) -ne $rq.ManifestHash){ throw 'Manifest modified.' }
    $m=Get-Content $rq.Manifest -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach($e in $m.Files){ if((Hash (Join-Path $rq.Source ($e.Name -replace '/','\'))) -ne $e.SHA256){ throw "Modified file: $($e.Name)" } }

    if(Get-Process MonitorTray -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dir*" }){ throw 'The installed app is still running.' }
    New-Item -ItemType Directory $root -Force | Out-Null
    $stage=Join-Path $root (".staging-"+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $stage | Out-Null
    Copy-Item (Join-Path $rq.Source '*') $stage -Recurse -Force
    if(-not(Test-Path (Join-Path $stage 'MonitorTray.exe'))){ throw 'Package copy incomplete.' }
    $backup=$null
    if(Test-Path $dir){
        $bdir=Join-Path $root '_backups'; New-Item -ItemType Directory $bdir -Force | Out-Null
        $backup=Join-Path $bdir ($rq.Sid+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
        Move-Item $dir $backup
    }
    try {
        Move-Item $stage $dir
        # ACL: Administrators/SYSTEM full control; the user only reads and executes (the app does not rewrite itself).
        # ACL only on the root folder (inheritable); the files go back to inheriting. Applying (OI)(CI) file by file left them without ACEs.
        & icacls.exe $dir /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' "*$($rq.Sid):(OI)(CI)RX" /C /Q | Out-Null
        if($LASTEXITCODE -ne 0){ throw 'Failed to apply the folder ACL.' }
        & icacls.exe (Join-Path $dir '*') /reset /T /C /Q | Out-Null
        $exe=$out.Exe
        foreach($t in @(@{N=$tLaunch;Logon=$false},@{N=$tLogon;Logon=$true})){
            $trig = if($t.Logon){ "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>$($rq.Sid)</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>" } else { '<Triggers/>' }
            $args = if($t.Logon){ '--tray' } else { '' }
            $xml=@"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Author>MarzOps</Author><Description>Marz Monitor $($rq.Version)</Description></RegistrationInfo>
  $trig
  <Principals><Principal id="Author"><UserId>$($rq.Sid)</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>6</Priority><Enabled>$(if($t.Logon){([string][bool]$rq.Startup).ToLower()}else{'true'})</Enabled></Settings>
  <Actions Context="Author"><Exec><Command>$exe</Command><Arguments>$args</Arguments><WorkingDirectory>$dir</WorkingDirectory></Exec></Actions>
</Task>
"@
            $xf=Join-Path $env:TEMP ("mt-task-"+[guid]::NewGuid().ToString('N')+'.xml')
            [IO.File]::WriteAllText($xf,$xml,[Text.Encoding]::Unicode)
            & $schtasks /Create /TN $t.N /XML $xf /F | Out-Null
            $code=$LASTEXITCODE; Remove-Item $xf -Force
            if($code -ne 0){ throw "Failed to register task $($t.N)." }
            $out.Tasks += $t.N
        }
        $v=[string](Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
        if(-not $v -or -not $v.StartsWith($rq.Version)){ throw "Installed exe version ($v) does not match $($rq.Version)." }
    } catch {
        # Rollback: restores the previous version.
        if(Test-Path $dir){ Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
        if($backup -and (Test-Path $backup) -and -not (Test-Path $dir)){ Move-Item $backup $dir }
        throw
    }
    # Keeps only the 2 most recent backups of this account.
    if($backup){ Get-ChildItem (Join-Path $root '_backups') -Directory | Where-Object Name -like "$($rq.Sid)-*" | Sort-Object Name -Descending | Select-Object -Skip 2 | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }
    $out.Backup=$backup
    # Entry in Windows "Installed apps".
    $u="HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MonitorTray-$($rq.Sid)"
    New-Item $u -Force | Out-Null
    $vals=@{DisplayName="Marz Monitor ($($rq.User))";DisplayVersion=$rq.Version;Publisher='MarzOps';DisplayIcon="$($out.Exe),0";InstallLocation=$dir;NoModify=1;NoRepair=1;
            UninstallString="powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$dir\Uninstall.ps1`""}
    foreach($k in $vals.Keys){ New-ItemProperty $u -Name $k -Value $vals[$k] -Force | Out-Null }
    # Replace GPU by MarzOps: only disables its logon task (reversible; nothing is deleted).
    if($rq.DisableTask){
        if($rq.DisableTask -ne "CmpInf-OLED-Rodinha-$($rq.Sid)"){ throw 'Unexpected GPU by MarzOps task.' }
        & $schtasks /Change /TN $rq.DisableTask /DISABLE | Out-Null
        if($LASTEXITCODE -eq 0){ $out.DisabledTask=$rq.DisableTask }
    }
    $out.Success=$true
} catch { $out.Error="$($_.Exception.Message) [line $($_.InvocationInfo.ScriptLineNumber): $($_.InvocationInfo.Line.Trim())]" }
Save
