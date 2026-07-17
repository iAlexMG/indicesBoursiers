# Compile l'extracteur et le déploie dans le dossier Scripts de Quantower.
# Résout dynamiquement le bin v* le plus récent (jamais de chemin en dur).
$ErrorActionPreference = "Stop"
$root = "C:\Quantower\TradingPlatform"
$latest = Get-ChildItem $root -Directory -Filter "v*" |
    Sort-Object { [version]($_.Name.TrimStart('v')) } -Descending |
    Select-Object -First 1
if ($null -eq $latest) { throw "Aucun dossier v* sous $root" }
$bin = Join-Path $latest.FullName "bin"

dotnet build "$PSScriptRoot\NqExtractor.csproj" -c Release -p:QuantowerBin="$bin"
# $ErrorActionPreference ne capte PAS le code de sortie d'un exe natif : sans ce test, un build en
# echec laissait le script deployer la DLL PRECEDENTE, et on testait du code fantome.
if ($LASTEXITCODE -ne 0) { throw "Build en echec (code $LASTEXITCODE) : deploiement annule." }

# TFM auto (net8.0 pour v1.145.x, net10.0 pour v1.146.14+) : prendre le build le plus récent.
$out = Get-ChildItem (Join-Path $PSScriptRoot "bin\Release") -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName "NqTickExtractor.dll") } |
    Sort-Object { (Get-Item (Join-Path $_.FullName "NqTickExtractor.dll")).LastWriteTime } -Descending |
    Select-Object -First 1 -ExpandProperty FullName
# Les strategies vivent sous <racine Quantower>\Settings, soit le PARENT de TradingPlatform.
# On derive depuis $root plutot que d'empiler des "..\" : un niveau de trop visait C:\Settings\...
# que New-Item -Force creait sans broncher -- la DLL partait dans le vide et Quantower gardait
# l'ancienne. Constate le 2026-07-15 : bug present depuis le commit initial.
$strategies = Join-Path (Split-Path $root -Parent) "Settings\Scripts\Strategies"
if (-not (Test-Path $strategies)) {
    throw "Dossier des strategies Quantower introuvable : $strategies (installation inattendue ?)"
}
$dest = Join-Path $strategies "NqTickExtractor"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
foreach ($f in "NqTickExtractor.dll","NqTickExtractor.deps.json","NqTickExtractor.pdb") {
    Copy-Item (Join-Path $out $f) $dest -Force
}
Write-Host "Déployé dans : $dest (depuis $out)"
Write-Host "Dans Quantower, panneau Strategies :"
Write-Host "  - NQ-ES History Ticks : ticks (fenêtre ~2-3 semaines) -> NQ-<contrat>.db"
Write-Host "  - NQ-ES History Bars 1m : barres minute (toute la profondeur serveur) -> NQ-<contrat>-1m.db"
