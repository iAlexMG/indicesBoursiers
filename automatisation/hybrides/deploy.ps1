# Compile la DLL Hybrides (net10.0, obligatoire face à la v1.146.14) et la déploie dans
# Settings\Scripts\Strategies de Quantower. UNE DLL = les 3 stratégies hybrides + la sonde
# « Ordres Probe (SIM) » -> un seul deploy, un seul redémarrage de Quantower.
$ErrorActionPreference = "Stop"
$root = "C:\Quantower\TradingPlatform"
$latest = Get-ChildItem $root -Directory -Filter "v*" |
    Sort-Object { [version]($_.Name.TrimStart('v')) } -Descending |
    Select-Object -First 1
if ($null -eq $latest) { throw "Aucun dossier v* sous $root" }
$bin = Join-Path $latest.FullName "bin"

dotnet build "$PSScriptRoot\Hybrides.csproj" -c Release -p:QuantowerBin="$bin"
if ($LASTEXITCODE -ne 0) { throw "Build en échec" }

$out  = Join-Path $PSScriptRoot "bin\Release\net10.0"
# Settings est le FRÈRE de TradingPlatform (C:\Quantower\{TradingPlatform,Settings}) : on
# remonte de DEUX niveaux depuis v*. ⚠ Les anciens deploy.ps1 (« ..\..\.. ») visaient
# C:\Settings — un dossier fantôme. On exige le dossier Scripts existant, jamais de mkdir
# aveugle (leçon REPRISE : dériver, et throw si absent).
$scripts = [System.IO.Path]::GetFullPath((Join-Path $latest.FullName "..\..\Settings\Scripts\Strategies"))
if (-not (Test-Path $scripts)) { throw "Dossier introuvable : $scripts (structure Quantower inattendue)" }
$dest = Join-Path $scripts "Hybrides"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
foreach ($f in "Hybrides.dll","Hybrides.deps.json","Hybrides.pdb") {
    Copy-Item (Join-Path $out $f) $dest -Force
}
Write-Host "Déployé dans : $dest"
Write-Host "⚠ Quantower ne recharge les DLL qu'au REDÉMARRAGE (piège 5 du REPRISE)."
Write-Host "Panneau Strategies -> Hybride H1 ORB (NQ) / H2 SMA Suiveur / H3 RSI Bracket / Ordres Probe (SIM)."
