#Requires -Version 5.1
# Marz Monitor - instalador grafico (nao elevado). Pede UAC so para a etapa que grava em Program Files.
[CmdletBinding()]
param([switch]$VerifyOnly, [switch]$Silent, [switch]$NoDesktop, [switch]$NoStartup, [switch]$NoOpen, [switch]$KeepGpuApp)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$AppName = 'Marz Monitor'
$Version = '__VERSION__'
$ManifestHash = '__MANIFEST_SHA256__'
$stage = 'iniciar'

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
        $f.Controls.Add((New-Label $(if($Fail){'Instalacao interrompida'}else{'Pronto!'}) 20 36 16 $true))
        $f.Controls.Add((New-Label $Msg 64 70))
        $box=New-Object Windows.Forms.TextBox; $box.Multiline=$true; $box.ReadOnly=$true; $box.ScrollBars='Vertical'; $box.Text=$Details
        $box.BackColor=[Drawing.Color]::FromArgb(34,35,38); $box.ForeColor=$Muted; $box.BorderStyle='FixedSingle'; $box.SetBounds(24,140,472,190); $f.Controls.Add($box)
        $copy=New-Button 'Copiar detalhes' 200 344; $copy.Add_Click({[Windows.Forms.Clipboard]::SetText($box.Text)}.GetNewClosure())
        $ok=New-Button 'Fechar' 356 344 $true; $ok.DialogResult='OK'
        $f.Controls.AddRange(@($copy,$ok)); $f.AcceptButton=$ok; [void]$f.ShowDialog()
    } finally { $f.Dispose() }
}
function Hash([string]$p){ (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }

# Fecha a instancia aberta pelo caminho normal (devolve as fans ao automatico). Nunca mata processo com fan manual.
function Request-AppExit([int]$Timeout=20) {
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if(@(Get-Process MonitorTray -ErrorAction SilentlyContinue).Count -eq 0){ return 'none' }
    $h=$null
    if([Threading.EventWaitHandle]::TryOpenExisting("Local\MonitorTray.Quit.$sid",[Security.AccessControl.EventWaitHandleRights]::Modify,[ref]$h)){ try{[void]$h.Set()} finally {$h.Dispose()} }
    $end=(Get-Date).AddSeconds($Timeout)
    while((Get-Date) -lt $end){ if(@(Get-Process MonitorTray -ErrorAction SilentlyContinue).Count -eq 0){return 'closed'}; Start-Sleep -Milliseconds 400 }
    return 'still-running'
}

# GPU by MarzOps (CmpInf): o Marz Monitor substitui (fans + teclado). Fecha pelo sinal dele (que devolve as fans ao automatico).
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
    $stage='validar ambiente'
    if(-not [Environment]::Is64BitProcess){ throw 'Use o PowerShell 64 bits (Windows x64).' }
    $id=[Security.Principal.WindowsIdentity]::GetCurrent(); $sid=$id.User.Value
    if(([Security.Principal.WindowsPrincipal]::new($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
        throw 'Abra o INSTALAR.cmd normalmente (sem "Executar como administrador"). O UAC e pedido na etapa certa, para as tarefas ficarem na SUA conta.'
    }
    $stage='conferir integridade do pacote'
    $manifest=Join-Path $PSScriptRoot 'payload-manifest.json'
    if((Hash $manifest) -ne $ManifestHash){ throw 'Manifesto do pacote alterado (SHA256 nao confere).' }
    $m=Get-Content $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
    if($m.Version -ne $Version){ throw 'Versao do manifesto diverge do instalador.' }
    $pub=Join-Path $PSScriptRoot 'publish'
    foreach($e in $m.Files){
        $p=Join-Path $pub ($e.Name -replace '/','\')
        if(-not(Test-Path -LiteralPath $p -PathType Leaf)){ throw "Arquivo ausente: $($e.Name)" }
        if((Hash $p) -ne $e.SHA256){ throw "Arquivo alterado: $($e.Name)" }
    }
    if(@(Get-ChildItem $pub -Recurse -File).Count -ne @($m.Files).Count){ throw 'Arquivos extras ou faltando no pacote.' }
    if($VerifyOnly){ Write-Host "Pacote $Version integro ($(@($m.Files).Count) arquivos)."; return }

    $installRoot=Join-Path $env:ProgramFiles 'MonitorTray'
    $dir=Join-Path $installRoot $sid
    $existing=$null
    $exe=Join-Path $dir 'MonitorTray.exe'
    if(Test-Path $exe){ $existing=(Get-Item $exe).VersionInfo.ProductVersion }

    if($true){
        $stage='consentimento'
        $gpuTask=Get-GpuAppTask; $gpuRunning=@(Get-Process CmpInf -ErrorAction SilentlyContinue).Count -gt 0
        $gpuPresent=($gpuTask -ne $null -and $gpuTask.State -ne 'Disabled') -or $gpuRunning
        if($Silent){ $choice=@{Mode='Install'; Startup=-not $NoStartup; Hidden=$true; Desktop=-not $NoDesktop; Open=-not $NoOpen; ReplaceGpu=($gpuPresent -and -not $KeepGpuApp)} } else {
        $f=New-Form "$AppName $Version - instalacao" $(if($gpuPresent){390}else{360})
        $f.Controls.Add((New-Label $AppName 18 36 18 $true))
        $f.Controls.Add((New-Label $(if($existing){"Atualizar da versao $existing para $Version"}else{"Instalar a versao $Version"}) 56 24 10 $false $true))
        $f.Controls.Add((New-Label "Monitor de hardware na bandeja: fans da GPU e da placa (modos do Armoury Crate, modo game), teclado Apex, rede local x internet.`r`nInstala em Program Files, so para esta conta. Fecha sozinho o app aberto (as fans voltam ao automatico)." 90 60 9.5))
        $cbStart=New-Check 'Iniciar com o Windows (como administrador, sem UAC a cada logon)' 160 $true
        $cbHidden=New-Check 'Iniciar oculto na bandeja' 188 $true
        $cbDesk=New-Check 'Atalho na Area de Trabalho' 216 $true
        $cbOpen=New-Check 'Abrir o Marz Monitor ao concluir' 244 $true
        $f.Controls.AddRange(@($cbStart,$cbHidden,$cbDesk,$cbOpen))
        $cbGpu=New-Check 'Substituir o GPU by MarzOps (fecha e desliga o inicio automatico dele)' 272 $true
        $by=300; if($gpuPresent){ $f.Controls.Add($cbGpu); $by=330 }
        $cancel=New-Button 'Cancelar' 200 $by; $cancel.DialogResult='Cancel'
        $go=New-Button $(if($existing){'Atualizar'}else{'Instalar'}) 356 $by $true; $go.DialogResult='OK'
        $f.Controls.AddRange(@($cancel,$go)); $f.AcceptButton=$go; $f.CancelButton=$cancel
        $ok=$f.ShowDialog() -eq 'OK'
        $choice=@{Mode='Install'; Startup=$cbStart.Checked; Hidden=$cbHidden.Checked; Desktop=$cbDesk.Checked; Open=$cbOpen.Checked; ReplaceGpu=($gpuPresent -and $cbGpu.Checked)}
        $f.Dispose()
        if(-not $ok){ Write-Host 'Cancelado.'; return }
        }
    }

    $stage='fechar a instancia aberta'
    $closed=Request-AppExit
    if($closed -eq 'still-running'){
        # So encerra a forca se o status provar que nenhuma fan esta manual (status recente, Policy 0 em todas).
        $st="$env:APPDATA\MonitorTray\state\fan-status.json"
        $safe=$false
        if(Test-Path $st){
            $j=Get-Content $st -Raw | ConvertFrom-Json
            $age=((Get-Date).ToUniversalTime()-[DateTime]::Parse($j.WrittenAtUtc).ToUniversalTime()).TotalSeconds
            $safe=($age -le 10) -and (-not $j.Gpu.HoldingManual) -and (@($j.Gpu.Fans | Where-Object { $_.Policy -ne 0 }).Count -eq 0)
        }
        if(-not $safe){ throw 'O Marz Monitor nao fechou e nao foi possivel provar que as fans estao no automatico. Saia pelo menu da bandeja e rode de novo.' }
    }

    if($choice.ReplaceGpu){
        $stage='fechar o GPU by MarzOps'
        if((Request-GpuAppExit) -eq 'still-running'){ throw 'O GPU by MarzOps nao fechou. Saia dele pelo menu da bandeja e rode de novo (ou desmarque a substituicao).' }
    }

    $stage='etapa elevada (UAC)'
    $tmp=Join-Path $env:LOCALAPPDATA ('MonitorTray-Setup\'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $tmp | Out-Null
    $req=Join-Path $tmp 'request.json'; $res=Join-Path $tmp 'result.json'
    @{ Mode=$choice.Mode; Sid=$sid; Session=(Get-Process -Id $PID).SessionId; Source=$pub; Version=$Version; Manifest=$manifest; ManifestHash=$ManifestHash;
       Startup=[bool]$choice.Startup; DisableTask=$(if($choice.ReplaceGpu -and $gpuTask){$gpuTask.TaskName}else{''}); User="$env:USERDOMAIN\$env:USERNAME"; Result=$res } | ConvertTo-Json | Set-Content $req -Encoding UTF8
    $elev=Join-Path $PSScriptRoot 'scripts\Elevated.ps1'
    $elevHash=Hash $elev
    $boot = "`$b=[IO.File]::ReadAllBytes('$($elev -replace "'","''")'); `$h=([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash(`$b)) -replace '-','').ToLower(); if(`$h -ne '$elevHash'){throw 'Instalador elevado alterado.'}; & ([scriptblock]::Create([Text.Encoding]::UTF8.GetString(`$b).TrimStart([char]0xFEFF))) -RequestPath '$($req -replace "'","''")'"
    $enc=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($boot))
    $p=Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $enc" -Wait -PassThru
    if(-not(Test-Path $res)){ throw "A etapa elevada nao retornou resultado (UAC recusado? codigo $($p.ExitCode))." }
    $r=Get-Content $res -Raw | ConvertFrom-Json
    if(-not $r.Success){ throw $r.Error }

    $stage='atalhos e preferencias'
    $settings="$env:APPDATA\MonitorTray\settings.json"
    New-Item -ItemType Directory (Split-Path $settings) -Force | Out-Null
    $s=if(Test-Path $settings){ Get-Content $settings -Raw | ConvertFrom-Json }else{ [pscustomobject]@{version=1} }
    foreach($kv in @(@('autostart',[bool]$choice.Startup),@('startHidden',[bool]$choice.Hidden))){ $s | Add-Member -NotePropertyName $kv[0] -NotePropertyValue $kv[1] -Force }
    ($s | ConvertTo-Json -Depth 6) | Set-Content $settings -Encoding UTF8
    # Renomeado (0.1.6): remove atalhos antigos "Monitor Tray".
    foreach($old in @("$([Environment]::GetFolderPath('Programs'))\Monitor Tray.lnk","$([Environment]::GetFolderPath('Desktop'))\Monitor Tray.lnk")){ Remove-Item $old -ErrorAction SilentlyContinue }
    $ws=New-Object -ComObject WScript.Shell
    $targets=@("$([Environment]::GetFolderPath('Programs'))\Marz Monitor.lnk")
    if($choice.Desktop){ $targets+="$([Environment]::GetFolderPath('Desktop'))\Marz Monitor.lnk" }
    foreach($t in $targets){ $l=$ws.CreateShortcut($t); $l.TargetPath=$r.Exe; $l.WorkingDirectory=$r.InstallDir; $l.IconLocation="$($r.Exe),0"; $l.Description='Monitor de hardware e fans da GPU'; $l.Save() }

    if($choice.Open){ & "$env:SystemRoot\System32\schtasks.exe" /Run /TN "MonitorTray-$sid" | Out-Null }
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    $details="Versao: $($r.Version)`r`nPasta: $($r.InstallDir)`r`nBackup: $($r.Backup)`r`nIniciar com o Windows: $($choice.Startup)`r`nTarefas: $($r.Tasks -join ', ')$(if($r.DisabledTask){"`r`nGPU by MarzOps: inicio automatico desligado ($($r.DisabledTask))"})"
    Show-Result "$AppName $Version - instalado" 'Instalado. O icone fica perto do relogio (pode estar na setinha ^). Atalho no menu Iniciar: "Marz Monitor".' $details $false
} catch {
    $d="FALHA: $($_.Exception.Message)`r`nEtapa: $stage"
    Write-Host $d -ForegroundColor Red
    try { Show-Result "$AppName - erro" 'Nada foi deixado pela metade: a instalacao anterior continua valendo. Copie os detalhes se precisar de ajuda.' $d $true } catch {}
    exit 1
}
