# Compile l'indicateur visuel H1 (net10.0) et le déploie dans Settings\Scripts\Indicators.
$ErrorActionPreference = "Stop"
$root = "C:\Quantower\TradingPlatform"
$latest = Get-ChildItem $root -Directory -Filter "v*" |
    Sort-Object { [version]($_.Name.TrimStart('v')) } -Descending |
    Select-Object -First 1
if ($null -eq $latest) { throw "Aucun dossier v* sous $root" }
$bin = Join-Path $latest.FullName "bin"

dotnet build "$PSScriptRoot\OrbNqVisuel.csproj" -c Release -p:QuantowerBin="$bin"
if ($LASTEXITCODE -ne 0) { throw "Build en échec" }

$out = Join-Path $PSScriptRoot "bin\Release\net10.0"
# Settings est le FRÈRE de TradingPlatform : deux niveaux au-dessus de v* (jamais trois —
# l'ancien « ..\..\.. » visait le fantôme C:\Settings).
$scripts = [System.IO.Path]::GetFullPath((Join-Path $latest.FullName "..\..\Settings\Scripts\Indicators"))
if (-not (Test-Path $scripts)) { throw "Dossier introuvable : $scripts (structure Quantower inattendue)" }
$dest = Join-Path $scripts "OrbNqVisuel"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
foreach ($f in "OrbNqVisuel.dll","OrbNqVisuel.deps.json","OrbNqVisuel.pdb") {
    Copy-Item (Join-Path $out $f) $dest -Force
}
Write-Host "Déployé dans : $dest"
Write-Host "⚠ Redémarrer Quantower, puis : graphe NQ 1 m -> clic droit -> Add indicator -> « Hybride H1 ORB (visuel) »."
