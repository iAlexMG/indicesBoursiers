# Compile le pont/sonde du pilier Affichage et le déploie dans le dossier Scripts de Quantower.
# Résout dynamiquement le bin v* le plus récent (jamais de chemin en dur) — même logique que
# historique\NqExtractor\deploy.ps1.
$ErrorActionPreference = "Stop"
$root = "C:\Quantower\TradingPlatform"
$latest = Get-ChildItem $root -Directory -Filter "v*" |
    Sort-Object { [version]($_.Name.TrimStart('v')) } -Descending |
    Select-Object -First 1
if ($null -eq $latest) { throw "Aucun dossier v* sous $root" }
$bin = Join-Path $latest.FullName "bin"

dotnet build "$PSScriptRoot\NqFeed.csproj" -c Release -p:QuantowerBin="$bin"
# $ErrorActionPreference ne capte PAS le code de sortie d'un exe natif : sans ce test, un build en
# echec laissait le script deployer la DLL PRECEDENTE, et on testait du code fantome.
if ($LASTEXITCODE -ne 0) { throw "Build en echec (code $LASTEXITCODE) : deploiement annule." }

# TFM auto (net8.0 pour v1.145.x, net10.0 pour v1.146.14+) : prendre le build le plus récent.
$out = Get-ChildItem (Join-Path $PSScriptRoot "bin\Release") -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName "NqFeed.dll") } |
    Sort-Object { (Get-Item (Join-Path $_.FullName "NqFeed.dll")).LastWriteTime } -Descending |
    Select-Object -First 1 -ExpandProperty FullName
# Les strategies vivent sous <racine Quantower>\Settings, soit le PARENT de TradingPlatform.
# On derive depuis $root plutot que d'empiler des "..\" : un niveau de trop fabriquait
# silencieusement C:\Settings\... (New-Item -Force ne bronche pas) et la strategie n'etait
# jamais vue par la plateforme.
$strategies = Join-Path (Split-Path $root -Parent) "Settings\Scripts\Strategies"
if (-not (Test-Path $strategies)) {
    throw "Dossier des strategies Quantower introuvable : $strategies (installation inattendue ?)"
}
$dest = Join-Path $strategies "NqFeed"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
foreach ($f in "NqFeed.dll","NqFeed.deps.json","NqFeed.pdb") {
    $src = Join-Path $out $f
    if (Test-Path $src) { Copy-Item $src $dest -Force }
}
Write-Host "Déployé dans : $dest (depuis $out)"
Write-Host ""
Write-Host "Dans Quantower (connecté Rithmic), panneau Strategies :"
Write-Host ""
Write-Host "  - NQ Feed : LE PONT. Sert trades + carnet au tableau de bord Python sur"
Write-Host "    127.0.0.1:5555. Symbole = NQ front, puis Start, et le laisser en Working."
Write-Host "    Côté Python :  python quantower_feed.py --seconds 15"
Write-Host ""
Write-Host "  - NQ Feed Probe : sonde Phase 0. Mesure l'entitlement L2, la profondeur du carnet,"
Write-Host "    la couverture de l'agresseur et les cadences. Symbole = NQ front, puis Start."
Write-Host "    Rapport complet dans l'onglet Logs (une ligne par message)."
Write-Host ""
Write-Host "Note : la grille de logs de Quantower tronque un message multi-ligne. Le texte"
Write-Host "intégral est dans Settings\Scripts\ScriptsData\<stratégie> (<guid>)\logs\*.slog"
