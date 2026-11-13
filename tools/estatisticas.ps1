# Mostra quantas pessoas estão vendo e baixando o Hertzo, usando os dados do próprio GitHub (precisa do `gh` logado
# com acesso ao repositório). Nada é coletado dos usuários além do que o GitHub já conta.
#
#   .\tools\estatisticas.ps1             -> resumo
#   .\tools\estatisticas.ps1 -Dias 30    -> histórico dos últimos 30 dias
#
# O GitHub só guarda visitas e clones dos últimos 14 dias. Cada execução junta esses dias num histórico local
# (%LocalAppData%\HertzoEstatisticas\historico.csv), então rode pelo menos uma vez a cada duas semanas para não perder dias.
param(
    [string]$Repo = 'ryan-hugo/hertzo',
    [int]$Dias = 14
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

function Api([string]$path) {
    $json = gh api $path 2>$null
    if ($LASTEXITCODE -ne 0) { throw "Falha ao consultar $path. Confira se o 'gh' está logado (gh auth status)." }
    return $json | ConvertFrom-Json
}

function Linha([string]$rotulo, $valor, [string]$extra = '') {
    Write-Host ('  {0,-28}' -f $rotulo) -NoNewline -ForegroundColor DarkGray
    Write-Host ('{0,8}' -f $valor) -NoNewline -ForegroundColor White
    if ($extra) { Write-Host "   $extra" -ForegroundColor DarkGray } else { Write-Host '' }
}

Write-Host ''
Write-Host "  HERTZO / ESTATÍSTICAS  ·  $Repo  ·  $(Get-Date -Format 'dd/MM/yyyy HH:mm')" -ForegroundColor DarkYellow
Write-Host ('  ' + ('-' * 64)) -ForegroundColor DarkGray

# ---------------- downloads ----------------
$releases = Api "repos/$Repo/releases?per_page=100"
$total = 0
Write-Host "`n  DOWNLOADS (total desde a publicação)" -ForegroundColor White
foreach ($r in $releases) {
    foreach ($a in $r.assets) {
        $total += $a.download_count
        Linha "$($r.tag_name) · $($a.name)" $a.download_count ("publicado em " + ([datetime]$r.published_at).ToLocalTime().ToString('dd/MM/yyyy'))
    }
}
Linha 'TOTAL' $total

# ---------------- repositório ----------------
$repoInfo = Api "repos/$Repo"
$views = Api "repos/$Repo/traffic/views"
$clones = Api "repos/$Repo/traffic/clones"
Write-Host "`n  REPOSITÓRIO NO GITHUB (últimos 14 dias)" -ForegroundColor White
Linha 'Visitas' $views.count "$($views.uniques) pessoas diferentes"
Linha 'Clones do código' $clones.count "$($clones.uniques) pessoas diferentes"
Linha 'Estrelas' $repoInfo.stargazers_count
Linha 'Forks' $repoInfo.forks_count
Linha 'Acompanhando (watchers)' $repoInfo.subscribers_count

$refs = @(Api "repos/$Repo/traffic/popular/referrers" | ForEach-Object { $_ } | Where-Object { $_.referrer })
if ($refs.Count -gt 0) {
    Write-Host "`n  DE ONDE AS PESSOAS VIERAM" -ForegroundColor White
    foreach ($x in $refs) { Linha $x.referrer $x.count "$($x.uniques) pessoas" }
}
$paths = @(Api "repos/$Repo/traffic/popular/paths" | ForEach-Object { $_ } | Where-Object { $_.path })
if ($paths.Count -gt 0) {
    Write-Host "`n  PÁGINAS MAIS VISTAS NO GITHUB" -ForegroundColor White
    foreach ($x in $paths | Select-Object -First 6) {
        $nome = $x.path -replace "^/$Repo", '' ; if (-not $nome) { $nome = '/ (página inicial)' }
        Linha $nome $x.count "$($x.uniques) pessoas"
    }
}

# ---------------- histórico local ----------------
$dir = Join-Path $env:LOCALAPPDATA 'HertzoEstatisticas'
New-Item -ItemType Directory -Force $dir | Out-Null
$hist = Join-Path $dir 'historico.csv'
$porDia = @{}
if (Test-Path $hist) { Import-Csv $hist | ForEach-Object { $porDia[$_.Dia] = $_ } }
foreach ($v in $views.views) {
    $dia = ([datetime]$v.timestamp).ToString('yyyy-MM-dd')
    $linha = $porDia[$dia]; if (-not $linha) { $linha = [pscustomobject]@{ Dia = $dia; Visitas = 0; VisitantesUnicos = 0; Clones = 0; Downloads = '' } }
    $linha.Visitas = $v.count; $linha.VisitantesUnicos = $v.uniques
    $porDia[$dia] = $linha
}
foreach ($c in $clones.clones) {
    $dia = ([datetime]$c.timestamp).ToString('yyyy-MM-dd')
    $linha = $porDia[$dia]; if (-not $linha) { $linha = [pscustomobject]@{ Dia = $dia; Visitas = 0; VisitantesUnicos = 0; Clones = 0; Downloads = '' } }
    $linha.Clones = $c.count
    $porDia[$dia] = $linha
}
# downloads: o GitHub só dá o total; guarda o total do dia para ver a evolução
$hoje = (Get-Date).ToString('yyyy-MM-dd')
if (-not $porDia[$hoje]) { $porDia[$hoje] = [pscustomobject]@{ Dia = $hoje; Visitas = 0; VisitantesUnicos = 0; Clones = 0; Downloads = '' } }
$porDia[$hoje].Downloads = $total
$porDia.Values | Sort-Object Dia | Export-Csv $hist -NoTypeInformation -Encoding UTF8

Write-Host "`n  HISTÓRICO (últimos $Dias dias)" -ForegroundColor White
Write-Host ('  {0,-12}{1,9}{2,10}{3,8}{4,14}' -f 'DIA', 'VISITAS', 'PESSOAS', 'CLONES', 'DOWNLOADS*') -ForegroundColor DarkGray
$desde = (Get-Date).AddDays(-$Dias).ToString('yyyy-MM-dd')
foreach ($l in $porDia.Values | Where-Object { $_.Dia -ge $desde } | Sort-Object Dia) {
    $barra = '#' * [Math]::Min(30, [int]$l.Visitas)
    Write-Host ('  {0,-12}{1,9}{2,10}{3,8}{4,14}  ' -f $l.Dia, $l.Visitas, $l.VisitantesUnicos, $l.Clones, $l.Downloads) -NoNewline
    Write-Host $barra -ForegroundColor DarkYellow
}
Write-Host "  * total acumulado de downloads no dia em que o script rodou" -ForegroundColor DarkGray
Write-Host "  Histórico salvo em $hist" -ForegroundColor DarkGray
Write-Host ''
Write-Host '  Visitas ao site (ryan-hugo.github.io/hertzo) não aparecem aqui: o GitHub Pages não tem contador próprio.' -ForegroundColor DarkGray
Write-Host ''
