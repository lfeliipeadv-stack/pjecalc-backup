# ---------------------------------------------------------------------------
#  PJe-Calc - Backup de TODOS os cálculos em arquivos .PJC
#
#  Faz exatamente o que você faria à mão, para cada cálculo:
#     Buscar -> Abrir -> Exportar -> Fechar
#  usando o Microsoft Edge (ou Chrome) em modo invisível, e grava os .PJC em
#     <Destino>\<data-hora> (PJe-Calc <versão>)\
#
#  Não altera nenhum cálculo. Efeito colateral: os cálculos passam pela lista
#  "Abrir Cálculos Recentes" da tela inicial.
#
#  Se o PJe-Calc não estiver aberto, o script abre (e deixa aberto).
#
#  Uso: dois cliques em backup-calculos.bat
#       ou: powershell -ExecutionPolicy Bypass -File backup-calculos.ps1 [-Destino "C:\pasta"]
# ---------------------------------------------------------------------------
param(
    [string]$Destino = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'BKP PJC PJECALC'),
    [string]$PjeCalc = (Split-Path -Parent $PSScriptRoot),
    [string]$Url     = 'http://127.0.0.1:9257/pjecalc',
    [int]$PortaDebug = 9333
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Log([string]$m, [string]$cor = 'Gray') { Write-Host $m -ForegroundColor $cor }

# ------------------------------------------------------------ PJe-Calc no ar
function PjeCalcNoAr {
    try { (Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 "$Url/logon.jsf").StatusCode -eq 200 } catch { $false }
}

if (-not (PjeCalcNoAr)) {
    $bat = Join-Path $PjeCalc 'iniciarPjeCalc.bat'
    if (-not (Test-Path $bat)) { throw "PJe-Calc não está aberto e não achei $bat. Abra o PJe-Calc e rode de novo." }
    Log "Abrindo o PJe-Calc ($PjeCalc)..." 'Yellow'
    Start-Process -FilePath $bat -WorkingDirectory $PjeCalc -WindowStyle Hidden
    $limite = (Get-Date).AddMinutes(4)
    while (-not (PjeCalcNoAr)) {
        if ((Get-Date) -gt $limite) { throw 'O PJe-Calc não respondeu em 4 minutos.' }
        Start-Sleep 3
    }
}
Log 'PJe-Calc respondendo.' 'Green'

# ------------------------------------------------------------ navegador invisível
$navegadores = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe"
)
$exe = $navegadores | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $exe) { throw 'Não encontrei o Microsoft Edge nem o Google Chrome.' }

$perfil = Join-Path $env:TEMP ('pjecalc-backup-' + [guid]::NewGuid().ToString('N'))
$proc = Start-Process -FilePath $exe -PassThru -ArgumentList @(
    '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
    '--disable-extensions', "--remote-debugging-port=$PortaDebug", "--user-data-dir=`"$perfil`"", 'about:blank')

$ws = $null
try {
    $alvo = $null
    for ($i = 0; $i -lt 40 -and -not $alvo; $i++) {
        Start-Sleep -Milliseconds 500
        try {
            $lista = Invoke-RestMethod -TimeoutSec 3 "http://127.0.0.1:$PortaDebug/json"
            $alvo = $lista | ForEach-Object { $_ } | Where-Object { $_.type -eq 'page' } | Select-Object -First 1
        } catch {}
    }
    if (-not $alvo) { throw 'O navegador invisível não iniciou.' }

    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $ws.ConnectAsync([Uri]$alvo.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()
    $script:idMsg = 0

    function Receber {
        $buf = New-Object byte[] 65536
        $ms = New-Object IO.MemoryStream
        do {
            $seg = New-Object 'ArraySegment[byte]' -ArgumentList (, $buf)
            $t = $ws.ReceiveAsync($seg, [Threading.CancellationToken]::None)
            if (-not $t.Wait(120000)) { throw 'Navegador parou de responder.' }
            $ms.Write($buf, 0, $t.Result.Count)
        } while (-not $t.Result.EndOfMessage)
        [Text.Encoding]::UTF8.GetString($ms.ToArray())
    }

    function Cdp([string]$metodo, $params = @{}) {
        $script:idMsg++
        $id = $script:idMsg
        $json = @{ id = $id; method = $metodo; params = $params } | ConvertTo-Json -Depth 10 -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($json)
        $seg = New-Object 'ArraySegment[byte]' -ArgumentList (, $bytes)
        $ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).Wait()
        while ($true) {
            $o = Receber | ConvertFrom-Json
            if ($o.id -eq $id) {
                if ($o.error) { throw "CDP ${metodo}: $($o.error.message)" }
                return $o.result
            }
        }
    }

    function JS([string]$expr, [switch]$Aguardar) {
        $r = Cdp 'Runtime.evaluate' @{ expression = $expr; awaitPromise = [bool]$Aguardar; returnByValue = $true }
        if ($r.exceptionDetails) { throw ('Erro na página: ' + $r.exceptionDetails.exception.description) }
        $r.result.value
    }

    function Esperar([string]$condicao, [int]$segundos = 60) {
        $limite = (Get-Date).AddSeconds($segundos)
        while ((Get-Date) -lt $limite) {
            try { if (JS "!!(function(){ try { return $condicao } catch(e) { return false } })()") { Start-Sleep -Milliseconds 300; return } } catch {}
            Start-Sleep -Milliseconds 400
        }
        throw "Tempo esgotado esperando: $condicao"
    }

    function Clicar([string]$seletorJs) { JS "(function(){ var e = $seletorJs; if(!e) throw new Error('elemento nao encontrado'); e.click(); return 1 })()" | Out-Null }

    $MENU   = "Array.prototype.slice.call(document.querySelectorAll('#menupainel a'))"
    $LINHAS = "Array.prototype.slice.call(document.querySelectorAll('#formulario\\:listagem tr.rich-table-row'))"

    function IrParaBusca {
        Cdp 'Page.navigate' @{ url = "$Url/pages/principal.jsf" } | Out-Null
        Esperar "document.readyState=='complete' && document.querySelector('#menupainel')"
        if (JS "!!document.querySelector('#menupainel a.menuImageClose')") {
            Clicar "document.querySelector('#menupainel a.menuImageClose')"
            Esperar "!document.querySelector('#menupainel a.menuImageClose') && $MENU.some(function(a){return a.textContent.trim()=='Buscar'})"
        }
        Clicar "$MENU.filter(function(a){return a.textContent.trim()=='Buscar'})[0]"
        Esperar "document.getElementById('formulario:buscar') && document.getElementById('formulario:numeroCalculoBusca')"
    }

    # Lê as linhas visíveis da busca: [{num, rec}]
    $LER_LINHAS = @"
$LINHAS.map(function(r){
  var c = Array.prototype.slice.call(r.cells).map(function(td){ return td.innerText.replace(/\s+/g,' ').trim(); });
  var i = c.findIndex(function(t){ return /^\d+$/.test(t); });
  return { num: c[i], rec: c[i+2] || '' };
})
"@

    function Buscar([string]$numero) {
        JS ("document.getElementById('formulario:numeroCalculoBusca').value = '$numero';" +
            "$LINHAS.forEach(function(r){ r.setAttribute('data-velha','1') }); 1") | Out-Null
        Clicar "document.getElementById('formulario:buscar')"
        Esperar "($LINHAS.length > 0 && !$LINHAS.some(function(r){ return r.getAttribute('data-velha') })) || /Nenhum registro|nenhum resultado/i.test(document.body.innerText)"
    }

    # ---------------------------------------------- 1) lista todos os cálculos
    Log 'Listando os cálculos...' 'Cyan'
    IrParaBusca
    $versao = JS "(document.getElementById('formulario:versaoSistema')||{}).textContent || ''"
    $versao = ($versao -replace '[^\d\.]', '')
    Buscar ''
    $calculos = @()
    for ($pag = 1; $pag -le 500; $pag++) {
        $calculos += @(JS $LER_LINHAS)
        $temProxima = JS "!!document.querySelector('img[id*=pagProximolistagem]:not([id*=Off])')"
        if (-not $temProxima) { break }
        JS "$LINHAS.forEach(function(r){ r.setAttribute('data-velha','1') }); 1" | Out-Null
        Clicar "document.querySelector('img[id*=pagProximolistagem]:not([id*=Off])')"
        Esperar "$LINHAS.length > 0 && !$LINHAS.some(function(r){ return r.getAttribute('data-velha') })"
    }
    $calculos = @($calculos | Where-Object { $_.num } | Sort-Object { [long]$_.num } -Unique)
    if ($calculos.Count -eq 0) { Log 'Nenhum cálculo encontrado.' 'Yellow'; return }

    $pasta = Join-Path $Destino ((Get-Date -Format 'yyyy-MM-dd_HHmm') + " (PJe-Calc $versao)")
    New-Item -ItemType Directory -Force $pasta | Out-Null
    Log ("{0} cálculo(s) encontrados. Salvando em: {1}" -f $calculos.Count, $pasta) 'Cyan'

    # ---------------------------------------------- 2) exporta um por um
    $EXPORTAR = @'
(async function(){
  window.__pjc = null;
  var f = document.getElementById('formulario');
  f.submit = function(){
    var fd = new URLSearchParams(new FormData(this));
    fetch(this.action, { method:'POST', body:fd, credentials:'same-origin' }).then(async function(r){
      var buf = new Uint8Array(await r.arrayBuffer()), s = '';
      for (var i = 0; i < buf.length; i += 8192) s += String.fromCharCode.apply(null, buf.subarray(i, i + 8192));
      window.__pjc = { b64: btoa(s), disp: r.headers.get('content-disposition') || '', len: buf.length, zip: buf[0] == 0x50 && buf[1] == 0x4B };
    });
  };
  document.getElementById('formulario:exportar').click();
  for (var i = 0; i < 240 && !window.__pjc; i++) await new Promise(function(r){ setTimeout(r, 500) });
  if (!window.__pjc) return null;
  return { disp: window.__pjc.disp, len: window.__pjc.len, zip: window.__pjc.zip, tam: window.__pjc.b64.length };
})()
'@

    $relatorio = @()
    $ok = 0
    foreach ($c in $calculos) {
        $rotulo = "Cálculo $($c.num) - $($c.rec)"
        try {
            IrParaBusca
            Buscar $c.num
            $achados = @(JS $LER_LINHAS)
            $idx = [array]::IndexOf(@($achados | ForEach-Object { $_.num }), [string]$c.num)
            if ($idx -lt 0) { throw 'não apareceu na busca' }
            Clicar "$LINHAS[$idx].querySelector('a.linkSelecionar')"
            Esperar "document.querySelector('#menupainel a.menuImageExport')"
            Clicar "document.querySelector('#menupainel a.menuImageExport')"
            Esperar "document.getElementById('formulario:exportar')"
            $num = JS "((document.querySelector('#conteudo fieldset')||{}).innerText||'').replace(/\s+/g,' ')"
            if ($num -notmatch "C.lculo:\s*$($c.num)\b") { throw "abriu o cálculo errado ($num)" }

            $r = JS $EXPORTAR -Aguardar
            if (-not $r) { throw 'o PJe-Calc não gerou o arquivo' }
            if (-not $r.zip) { throw 'a resposta não é um arquivo .PJC' }

            $b64 = New-Object Text.StringBuilder
            for ($p = 0; $p -lt $r.tam; $p += 400000) { [void]$b64.Append((JS "window.__pjc.b64.substr($p, 400000)")) }
            $bytes = [Convert]::FromBase64String($b64.ToString())

            $nomeOrig = if ($r.disp -match 'filename="?([^";]+)') { $Matches[1] } else { "CALCULO_$($c.num).PJC" }
            $rec = ($c.rec -replace '[\\/:*?"<>|$]', '').Trim()
            $nome = $nomeOrig -replace '^(CALCULO_\d+)_', ('$1_' + $rec + '_')
            $arq = Join-Path $pasta $nome
            [IO.File]::WriteAllBytes($arq, $bytes)

            $z = [IO.Compression.ZipFile]::OpenRead($arq); $n = $z.Entries.Count; $z.Dispose()
            if ($n -lt 1) { throw 'arquivo .PJC vazio' }

            Log ("  [ok] {0}  ({1:N0} KB)" -f $rotulo, ($bytes.Length / 1KB)) 'Green'
            $relatorio += "OK    $rotulo -> $nome"
            $ok++
        } catch {
            Log "  [ERRO] $rotulo : $($_.Exception.Message)" 'Red'
            $relatorio += "ERRO  $rotulo : $($_.Exception.Message)"
        } finally {
            try { JS "window.__pjc = null; 1" | Out-Null } catch {}
        }
    }

    # fecha o último cálculo aberto
    try { IrParaBusca } catch {}

    $resumo = "Backup de $(Get-Date -Format 'dd/MM/yyyy HH:mm') - PJe-Calc $versao - $ok de $($calculos.Count) cálculo(s) exportados"
    Set-Content -Path (Join-Path $pasta 'relatorio.txt') -Value (@($resumo, '') + $relatorio) -Encoding UTF8
    Log ''
    Log $resumo $(if ($ok -eq $calculos.Count) { 'Green' } else { 'Yellow' })
    Log "Pasta: $pasta"
}
finally {
    if ($ws) { try { Cdp 'Browser.close' | Out-Null } catch {}; $ws.Dispose() }
    if ($proc -and -not $proc.HasExited) { try { $proc.Kill() } catch {} }
    Start-Sleep 1
    Remove-Item -Recurse -Force $perfil -ErrorAction SilentlyContinue
}
