<#
.SYNOPSIS
  Suiveur lisible du journal NDJSON des hybrides — pour les captures / la vidéo Apex.

.DESCRIPTION
  Lit EN DIRECT le journal de décisions d'une stratégie hybride et affiche des
  lignes propres et colorées, au lieu du mur de JSON brut. Le fichier reste un
  vrai .ndjson lu tel quel (Get-Content -Wait) : c'est le MÊME format que le
  jumeau LEAN, donc la preuve de parité tient — on n'ajoute qu'une mise en forme.

  Fait trois choses que le tail brut ne fait pas :
    1. masque le compte Apex (numéro + nom légal) qui fuit dans la ligne « demarrage » ;
    2. force l'UTF-8 (sinon PowerShell 5.1 casse tous les accents à l'écran) ;
    3. affiche l'heure en ET (la séance et le nom du fichier sont en ET).

  Ctrl+C pour arrêter.

.PARAMETER Strategie
  H1 | H2 | H3 (alias) ou un slug direct (sma_bracket_nq / sma_suiveur_nq / sma_annule_nq).

.EXAMPLE
  .\suivre-journal.ps1 H1
  .\suivre-journal.ps1 -Strategie sma_suiveur_nq -Tail 20
  .\suivre-journal.ps1 -Fichier 'H:\...\sma_bracket_nq\2026-07-22.ndjson'
#>
param(
    [Parameter(Position = 0)]
    [string]$Strategie = 'H1',
    [string]$Base = 'H:\IndicesBoursiers\automatisation\journaux',
    [string]$Fichier = '',
    [int]$Tail = 12,
    [switch]$Instantane
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$Inv = [System.Globalization.CultureInfo]::InvariantCulture
$Et  = [System.TimeZoneInfo]::FindSystemTimeZoneById('Eastern Standard Time')

$slugs = @{ 'H1' = 'sma_bracket_nq'; 'H2' = 'sma_suiveur_nq'; 'H3' = 'sma_annule_nq' }
$cle = $Strategie.ToUpper()
if ($slugs.ContainsKey($cle)) { $slug = $slugs[$cle] } else { $slug = $Strategie }

function Trouver-Fichier($dir) {
    Get-ChildItem $dir -Filter *.ndjson -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
}

if ($Fichier) {
    $cible = $Fichier
}
else {
    $dir = Join-Path $Base $slug
    if (-not (Test-Path $dir)) {
        Write-Host "Dossier introuvable : $dir" -ForegroundColor Red
        Write-Host "Slugs : sma_bracket_nq (H1), sma_suiveur_nq (H2), sma_annule_nq (H3)." -ForegroundColor DarkGray
        return
    }
    $f = Trouver-Fichier $dir
    while (-not $f) {
        Write-Host "En attente du fichier du jour dans $dir  (lance la stratégie...)" -ForegroundColor DarkGray
        Start-Sleep -Seconds 1
        $f = Trouver-Fichier $dir
    }
    $cible = $f.FullName
}

Write-Host ''
Write-Host "  Journal : $cible" -ForegroundColor Gray
Write-Host "  $slug   .   heures en ET   .   compte masque   .   Ctrl+C pour arreter" -ForegroundColor DarkGray
Write-Host ('  ' + ('-' * 84)) -ForegroundColor DarkGray

function Masquer($txt) {
    if (-not $txt) { return $txt }
    # Politique « zéro Apex » : on n'affiche NI le numéro, NI le nom légal, NI le mot « Apex ».
    #   1) compte complet « APEX-1234567 (Nom Legal) »  ->  « (compte masqué) »
    #   2) numéro nu résiduel « APEX-1234567 »          ->  « (compte masqué) »
    #   3) filet de sécurité : le mot « Apex/APEX » seul ->  « (compte masqué) »
    $t = [regex]::Replace($txt, 'APEX-\d+\s*\([^)]*\)', '(compte masqué)')
    $t = [regex]::Replace($t,   'APEX-\d+',             '(compte masqué)')
    $t = [regex]::Replace($t,   '(?i)\bapex\b',         '(compte masqué)')
    return $t
}

function Fmt($v) {
    if ($null -eq $v) { return '' }
    return ([double]$v).ToString('0.##', $Inv)
}

function Afficher-Ligne($ligne) {
    if ([string]::IsNullOrWhiteSpace($ligne)) { return }
    try { $o = $ligne | ConvertFrom-Json }
    catch { Write-Host "  $ligne" -ForegroundColor DarkGray; return }

    $tsUtc = [datetime]::Parse($o.ts, $Inv,
        [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)
    $h = [System.TimeZoneInfo]::ConvertTimeFromUtc($tsUtc, $Et).ToString('HH:mm:ss')

    $ev = "$($o.evenement)"
    $raison = Masquer "$($o.raison)"

    switch ($ev) {
        'signal'         { $lab = 'SIGNAL     '; $col = 'Cyan' }
        'proposition'    { if ($raison -match 'ACCEPT') { $lab = 'OK ACCEPTÉE'; $col = 'Green' } else { $lab = 'PROPOSITION'; $col = 'Yellow' } }
        'entree_envoyee' { $lab = 'ENTRÉE     '; $col = 'White' }
        'fill'           { $lab = 'FILL       '; $col = 'Green' }
        'bracket_pose'   { $lab = 'BRACKET    '; $col = 'DarkCyan' }
        'stop_modifie'   { $lab = 'STOP ^     '; $col = 'Magenta' }
        'sortie_envoyee' { $lab = 'SORTIE     '; $col = 'Red' }
        'annulation'     { $lab = 'ANNULATION '; $col = 'DarkYellow' }
        'flat_force'     { $lab = 'FLAT       '; $col = 'Red' }
        'garde_fou'      { $lab = 'GARDE-FOU  '; $col = 'Red' }
        'kill'           { $lab = 'KILL       '; $col = 'Red' }
        'demarrage'      { $lab = 'DÉMARRAGE  '; $col = 'Gray' }
        'arret'          { $lab = 'ARRÊT      '; $col = 'DarkGray' }
        default          { $lab = $ev.PadRight(11); $col = 'White' }
    }

    $ind = $o.indicateurs
    $extra = ''
    if ($ind) {
        $p = @()
        if ($null -ne $ind.sma_rapide -and $null -ne $ind.sma_lente) { $p += ('sma {0}/{1}' -f (Fmt $ind.sma_rapide), (Fmt $ind.sma_lente)) }
        if ($null -ne $ind.atr)  { $p += ('atr {0}'  -f (Fmt $ind.atr)) }
        if ($null -ne $ind.stop) { $p += ('stop {0}' -f (Fmt $ind.stop)) }
        if ($null -ne $ind.take) { $p += ('take {0}' -f (Fmt $ind.take)) }
        if ($p.Count) { $extra = '   [ ' + ($p -join '  ') + ' ]' }
    }

    if ($null -ne $o.prix) { $prix = '@' + (Fmt $o.prix) } else { $prix = '' }

    Write-Host ('  {0}  ' -f $h) -NoNewline -ForegroundColor DarkGray
    Write-Host ('{0}' -f $lab) -NoNewline -ForegroundColor $col
    Write-Host ('  {0} {1}{2}' -f $prix, $raison, $extra) -ForegroundColor Gray
}

if ($Instantane) {
    Get-Content -Path $cible -Tail $Tail -Encoding UTF8 | ForEach-Object { Afficher-Ligne $_ }
}
else {
    Get-Content -Path $cible -Wait -Tail $Tail -Encoding UTF8 | ForEach-Object { Afficher-Ligne $_ }
}
