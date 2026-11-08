#Requires -Version 5.1
# Marz Monitor - graphical installer (not elevated). Asks for UAC only for the step that writes to Program Files.
[CmdletBinding()]
param([switch]$VerifyOnly, [switch]$Silent, [switch]$NoDesktop, [switch]$NoStartup, [switch]$NoOpen, [switch]$KeepGpuApp)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$AppName = 'Marz Monitor'
$Version = '__VERSION__'
$ManifestHash = '__MANIFEST_SHA256__'
$stage = 'start'

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$Bg=[Drawing.Color]::FromArgb(24,25,27); $Fg=[Drawing.Color]::FromArgb(245,245,248); $Muted=[Drawing.Color]::FromArgb(163,163,168); $Accent=[Drawing.Color]::FromArgb(46,125,255)

function New-Form([string]$Title,[int]$H) {
    $f=New-Object Windows.Forms.Form
    $f.Text=$Title; $f.ClientSize=New-Object Drawing.Size(520,$H); $f.BackColor=$Bg; $f.ForeColor=$Fg
    $f.Font=New-Object Drawing.Font('Segoe UI',10); $f.StartPosition='CenterScreen'; $f.FormBorderStyle='FixedDialog'
    $f.MaximizeBox=$false; $f.MinimizeBox=$false; $f.AutoScaleMode='Dpi'
    $ico=Join-Path $PSScriptRoot 'publish\MonitorTray.exe'
    if(Test-Path $ico){ try { $f.Icon=[Drawing.Icon]::ExtractAssociatedIcon($ico) } catch {} }
    return $f
}
function New-Label([string]$T,[int]$Y,[int]$H,[float]$Size=10,[bool]$Bold=$false,[bool]$Dim=$false) {
    $l=New-Object Windows.Forms.Label; $l.Text=$T; $l.SetBounds(24,$Y,472,$H)
    $l.Font=New-Object Drawing.Font('Segoe UI',$Size,$(if($Bold){[Drawing.FontStyle]::Bold}else{[Drawing.FontStyle]::Regular}))
    if($Dim){$l.ForeColor=$Muted}; return $l
}
function New-Button([string]$T,[int]$X,[int]$Y,[bool]$Primary=$false) {
    $b=New-Object Windows.Forms.Button; $b.Text=$T; $b.SetBounds($X,$Y,140,36); $b.FlatStyle='Flat'; $b.FlatAppearance.BorderSize=0
    $b.BackColor=$(if($Primary){$Accent}else{[Drawing.Color]::FromArgb(44,45,49)}); $b.ForeColor=[Drawing.Color]::White; return $b
}
function New-Check([string]$T,[int]$Y,[bool]$On) {
    $c=New-Object Windows.Forms.CheckBox; $c.Text=$T; $c.Checked=$On; $c.SetBounds(24,$Y,472,26); $c.FlatStyle='Flat'; return $c
}
function Show-Result([string]$Title,[string]$Msg,[string]$Details,[bool]$Fail) {
    if($Silent){ Write-Host "$Title`n$Msg`n$Details"; return }
    $f=New-Form $Title 400
    try {
        $f.Controls.Add((New-Label $(if($Fail){'Installation stopped'}else{'Done!'}) 20 36 16 $true))
        $f.Controls.Add((New-Label $Msg 64 70))
        $box=New-Object Windows.Forms.TextBox; $box.Multiline=$true; $box.ReadOnly=$true; $box.ScrollBars='Vertical'; $box.Text=$Details
        $box.BackColor=[Drawing.Color]::FromArgb(34,35,38); $box.ForeColor=$Muted; $box.BorderStyle='FixedSingle'; $box.SetBounds(24,140,472,190); $f.Controls.Add($box)
        $copy=New-Button 'Copy details' 200 344; $copy.Add_Click({[Windows.Forms.Clipboard]::SetText($box.Text)}.GetNewClosure())
        $ok=New-Button 'Close' 356 344 $true; $ok.DialogResult='OK'
        $f.Controls.AddRange(@($copy,$ok)); $f.AcceptButton=$ok; [void]$f.ShowDialog()
    } finally { $f.Dispose() }
}
function Hash([string]$p){ (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }

# Closes the running instance the normal way (returns the fans to automatic). Never kills a process with a fan in manual.
function Request-AppExit([int]$Timeout=20) {
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if(@(Get-Process MonitorTray -ErrorAction SilentlyContinue).Count -eq 0){ return 'none' }
    $h=$null
    if([Threading.EventWaitHandle]::TryOpenExisting("Local\MonitorTray.Quit.$sid",[Security.AccessControl.EventWaitHandleRights]::Modify,[ref]$h)){ try{[void]$h.Set()} finally {$h.Dispose()} }
    $end=(Get-Date).AddSeconds($Timeout)
    while((Get-Date) -lt $end){ if(@(Get-Process MonitorTray -ErrorAction SilentlyContinue).Count -eq 0){return 'closed'}; Start-Sleep -Milliseconds 400 }
    return 'still-running'
}

# GPU by MarzOps (CmpInf): Marz Monitor replaces it (fans + keyboard). Closes it through its own signal (which returns the fans to automatic).
function Get-GpuAppTask { Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like "CmpInf-OLED-Rodinha-$sid" } | Select-Object -First 1 }
function Request-GpuAppExit([int]$Timeout=20) {
    if(@(Get-Process CmpInf -ErrorAction SilentlyContinue).Count -eq 0){ return 'none' }
    $h=$null
    if([Threading.EventWaitHandle]::TryOpenExisting("Local\CmpInf.Quit.$sid",[Security.AccessControl.EventWaitHandleRights]::Modify,[ref]$h)){ try{[void]$h.Set()} finally {$h.Dispose()} }
    $end=(Get-Date).AddSeconds($Timeout)
    while((Get-Date) -lt $end){ if(@(Get-Process CmpInf -ErrorAction SilentlyContinue).Count -eq 0){return 'closed'}; Start-Sleep -Milliseconds 400 }
    return 'still-running'
}

try {
    $stage='check environment'
    if(-not [Environment]::Is64BitProcess){ throw 'Use 64-bit PowerShell (Windows x64).' }
    $id=[Security.Principal.WindowsIdentity]::GetCurrent(); $sid=$id.User.Value
    if(([Security.Principal.WindowsPrincipal]::new($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
        throw 'Open INSTALL.cmd normally (without "Run as administrator"). UAC is requested at the right step, so the tasks are registered under YOUR account.'
    }
    $stage='check package integrity'
    $manifest=Join-Path $PSScriptRoot 'payload-manifest.json'
    if((Hash $manifest) -ne $ManifestHash){ throw 'Package manifest modified (SHA256 does not match).' }
    $m=Get-Content $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
    if($m.Version -ne $Version){ throw 'Manifest version does not match the installer.' }
    $pub=Join-Path $PSScriptRoot 'publish'
    foreach($e in $m.Files){
        $p=Join-Path $pub ($e.Name -replace '/','\')
        if(-not(Test-Path -LiteralPath $p -PathType Leaf)){ throw "Missing file: $($e.Name)" }
        if((Hash $p) -ne $e.SHA256){ throw "Modified file: $($e.Name)" }
    }
    if(@(Get-ChildItem $pub -Recurse -File).Count -ne @($m.Files).Count){ throw 'Extra or missing files in the package.' }
    if($VerifyOnly){ Write-Host "Package $Version intact ($(@($m.Files).Count) files)."; return }

    $installRoot=Join-Path $env:ProgramFiles 'MonitorTray'
    $dir=Join-Path $installRoot $sid
    $existing=$null
    $exe=Join-Path $dir 'MonitorTray.exe'
    if(Test-Path $exe){ $existing=(Get-Item $exe).VersionInfo.ProductVersion }

    if($true){
        $stage='consent'
        $gpuTask=Get-GpuAppTask; $gpuRunning=@(Get-Process CmpInf -ErrorAction SilentlyContinue).Count -gt 0
        $gpuPresent=($gpuTask -ne $null -and $gpuTask.State -ne 'Disabled') -or $gpuRunning
        if($Silent){ $choice=@{Mode='Install'; Startup=-not $NoStartup; Hidden=$true; Desktop=-not $NoDesktop; Open=-not $NoOpen; ReplaceGpu=($gpuPresent -and -not $KeepGpuApp)} } else {
        $f=New-Form "$AppName $Version - setup" $(if($gpuPresent){390}else{360})
        $f.Controls.Add((New-Label $AppName 18 36 18 $true))
        $f.Controls.Add((New-Label $(if($existing){"Update from version $existing to $Version"}else{"Install version $Version"}) 56 24 10 $false $true))
        $f.Controls.Add((New-Label "Hardware monitor in the tray: GPU and motherboard fans (Armoury Crate modes, game mode), Apex keyboard, local network vs internet.`r`nInstalls to Program Files, for this account only. Closes the running app by itself (the fans go back to automatic)." 90 60 9.5))
        $cbStart=New-Check 'Start with Windows (elevated, no UAC at each logon)' 160 $true
        $cbHidden=New-Check 'Start hidden in the tray' 188 $true
        $cbDesk=New-Check 'Desktop shortcut' 216 $true
        $cbOpen=New-Check 'Open Marz Monitor when finished' 244 $true
        $f.Controls.AddRange(@($cbStart,$cbHidden,$cbDesk,$cbOpen))
        $cbGpu=New-Check 'Replace GPU by MarzOps (closes it and turns off its automatic startup)' 272 $true
        $by=300; if($gpuPresent){ $f.Controls.Add($cbGpu); $by=330 }
        $cancel=New-Button 'Cancel' 200 $by; $cancel.DialogResult='Cancel'
        $go=New-Button $(if($existing){'Update'}else{'Install'}) 356 $by $true; $go.DialogResult='OK'
        $f.Controls.AddRange(@($cancel,$go)); $f.AcceptButton=$go; $f.CancelButton=$cancel
        $ok=$f.ShowDialog() -eq 'OK'
        $choice=@{Mode='Install'; Startup=$cbStart.Checked; Hidden=$cbHidden.Checked; Desktop=$cbDesk.Checked; Open=$cbOpen.Checked; ReplaceGpu=($gpuPresent -and $cbGpu.Checked)}
        $f.Dispose()
        if(-not $ok){ Write-Host 'Cancelled.'; return }
        }
    }

    $stage='close the running instance'
    $closed=Request-AppExit
    if($closed -eq 'still-running'){
        # Only treated as safe if the status proves no fan is in manual (recent status, Policy 0 on all of them).
        $st="$env:APPDATA\MonitorTray\state\fan-status.json"
        $safe=$false
        if(Test-Path $st){
            $j=Get-Content $st -Raw | ConvertFrom-Json
            $age=((Get-Date).ToUniversalTime()-[DateTime]::Parse($j.WrittenAtUtc).ToUniversalTime()).TotalSeconds
            $safe=($age -le 10) -and (-not $j.Gpu.HoldingManual) -and (@($j.Gpu.Fans | Where-Object { $_.Policy -ne 0 }).Count -eq 0)
        }
        if(-not $safe){ throw 'Marz Monitor did not close and it could not be proven that the fans are in automatic. Quit it from the tray menu and run again.' }
    }

    if($choice.ReplaceGpu){
        $stage='close GPU by MarzOps'
        if((Request-GpuAppExit) -eq 'still-running'){ throw 'GPU by MarzOps did not close. Quit it from its tray menu and run again (or uncheck the replace option).' }
    }

    $stage='elevated step (UAC)'
    $tmp=Join-Path $env:LOCALAPPDATA ('MonitorTray-Setup\'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $tmp | Out-Null
    $req=Join-Path $tmp 'request.json'; $res=Join-Path $tmp 'result.json'
    @{ Mode=$choice.Mode; Sid=$sid; Session=(Get-Process -Id $PID).SessionId; Source=$pub; Version=$Version; Manifest=$manifest; ManifestHash=$ManifestHash;
       Startup=[bool]$choice.Startup; DisableTask=$(if($choice.ReplaceGpu -and $gpuTask){$gpuTask.TaskName}else{''}); User="$env:USERDOMAIN\$env:USERNAME"; Result=$res } | ConvertTo-Json | Set-Content $req -Encoding UTF8
    $elev=Join-Path $PSScriptRoot 'scripts\Elevated.ps1'
    $elevHash=Hash $elev
    $boot = "`$b=[IO.File]::ReadAllBytes('$($elev -replace "'","''")'); `$h=([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash(`$b)) -replace '-','').ToLower(); if(`$h -ne '$elevHash'){throw 'Elevated installer modified.'}; & ([scriptblock]::Create([Text.Encoding]::UTF8.GetString(`$b).TrimStart([char]0xFEFF))) -RequestPath '$($req -replace "'","''")'"
    $enc=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($boot))
    $p=Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $enc" -Wait -PassThru
    if(-not(Test-Path $res)){ throw "The elevated step returned no result (UAC declined? code $($p.ExitCode))." }
    $r=Get-Content $res -Raw | ConvertFrom-Json
    if(-not $r.Success){ throw $r.Error }

    $stage='shortcuts and preferences'
    $settings="$env:APPDATA\MonitorTray\settings.json"
    New-Item -ItemType Directory (Split-Path $settings) -Force | Out-Null
    $s=if(Test-Path $settings){ Get-Content $settings -Raw | ConvertFrom-Json }else{ [pscustomobject]@{version=1} }
    foreach($kv in @(@('autostart',[bool]$choice.Startup),@('startHidden',[bool]$choice.Hidden))){ $s | Add-Member -NotePropertyName $kv[0] -NotePropertyValue $kv[1] -Force }
    ($s | ConvertTo-Json -Depth 6) | Set-Content $settings -Encoding UTF8
    # Renamed (0.1.6): removes old "Monitor Tray" shortcuts.
    foreach($old in @("$([Environment]::GetFolderPath('Programs'))\Monitor Tray.lnk","$([Environment]::GetFolderPath('Desktop'))\Monitor Tray.lnk")){ Remove-Item $old -ErrorAction SilentlyContinue }
    $ws=New-Object -ComObject WScript.Shell
    $targets=@("$([Environment]::GetFolderPath('Programs'))\Marz Monitor.lnk")
    if($choice.Desktop){ $targets+="$([Environment]::GetFolderPath('Desktop'))\Marz Monitor.lnk" }
    foreach($t in $targets){ $l=$ws.CreateShortcut($t); $l.TargetPath=$r.Exe; $l.WorkingDirectory=$r.InstallDir; $l.IconLocation="$($r.Exe),0"; $l.Description='Hardware and GPU fan monitor'; $l.Save() }

    if($choice.Open){ & "$env:SystemRoot\System32\schtasks.exe" /Run /TN "MonitorTray-$sid" | Out-Null }
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    $details="Version: $($r.Version)`r`nFolder: $($r.InstallDir)`r`nBackup: $($r.Backup)`r`nStart with Windows: $($choice.Startup)`r`nTasks: $($r.Tasks -join ', ')$(if($r.DisabledTask){"`r`nGPU by MarzOps: automatic startup turned off ($($r.DisabledTask))"})"
    Show-Result "$AppName $Version - installed" 'Installed. The icon sits near the clock (it may be under the ^ arrow). Start menu shortcut: "Marz Monitor".' $details $false
} catch {
    $d="FAILED: $($_.Exception.Message)`r`nStep: $stage"
    Write-Host $d -ForegroundColor Red
    try { Show-Result "$AppName - error" 'Nothing was left half-done: the previous installation is still in effect. Copy the details if you need help.' $d $true } catch {}
    exit 1
}
